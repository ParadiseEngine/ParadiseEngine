"""NativeAOT packaging regressions; fabricated ELFs exercise validation, never device execution."""
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

from nativeaot_elf import inspect_linkage
from verify_android import inspect_elf, verify_apk

MANIFEST = json.loads(Path(__file__).with_name('nativeaot.manifest.json').read_text())


def library(name, exports=(), needed=('libc.so',), alignment=16384):
    strings = bytearray(b'\0')

    def add(text):
        offset = len(strings)
        strings.extend(text.encode() + b'\0')
        return offset

    soname = add(name)
    dependencies = [add(value) for value in needed]
    symbols = [add(value) for value in exports]
    dynamic = b''.join(struct.pack('<qQ', 1, value) for value in dependencies)
    dynamic += struct.pack('<qQ', 14, soname) + bytes(16)
    dynsym = bytes(24) + b''.join(struct.pack('<IBBHQQ', value, 0x12, 0, 1, 0x1000, 4) for value in symbols)
    data = bytearray(120)
    string_offset = len(data)
    data.extend(strings)
    dynamic_offset = len(data)
    data.extend(dynamic)
    symbol_offset = len(data)
    data.extend(dynsym)
    section_offset = len(data)
    data.extend(bytes(64))
    for kind, offset, size, link, entry in (
        (3, string_offset, len(strings), 0, 0), (6, dynamic_offset, len(dynamic), 1, 16),
        (11, symbol_offset, len(dynsym), 1, 24),
    ):
        data.extend(struct.pack('<IIQQQQIIQQ', 0, kind, 0, 0, offset, size, link, 0, 8, entry))
    data[:6] = b'\x7fELF\x02\x01'
    struct.pack_into('<HHIQQQIHHHHHH', data, 16, 3, 183, 1, 0, 64, section_offset, 0, 64, 56, 1, 64, 4, 0)
    struct.pack_into('<IIQQQQQQ', data, 64, 1, 5, 0, 0, 0, len(data), len(data), alignment)
    return bytes(data)


class NativeAotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.apk = Path(self.temp.name) / 'sample.apk'

    def payloads(self):
        return {
            'libparadise_android.so': library('libparadise_android.so', ('SDL_main', 'DotNetRuntimeDebugHeader')),
            'libSDL3.so': library('libSDL3.so', ('JNI_OnLoad',)),
            'libwebgpu_dawn.so': library('libwebgpu_dawn.so'),
        }

    def write_apk(self, payloads=None, extra=None, mutate_receipt=None):
        payloads = self.payloads() if payloads is None else payloads
        receipt = dict(MANIFEST, librarySha256={name: hashlib.sha256(data).hexdigest() for name, data in payloads.items()},
                       dexSha256=hashlib.sha256(b'dex').hexdigest())
        if mutate_receipt:
            mutate_receipt(receipt)
        with zipfile.ZipFile(self.apk, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr('AndroidManifest.xml', b'manifest')
            archive.writestr('classes.dex', b'dex')
            archive.writestr('assets/native/nativeaot-build.json', json.dumps(receipt))
            for name, data in payloads.items():
                archive.writestr('lib/arm64-v8a/' + name, data)
            for name, data in (extra or {}).items():
                archive.writestr(name, data)

    def test_linkage_reader(self):
        data = library('libtest.so', ('SDL_main',), ('libc.so', 'libm.so'))
        self.assertEqual(inspect_elf(data)['machine'], 'aarch64')
        self.assertEqual(inspect_linkage(data), {'soname': 'libtest.so', 'needed': ['libc.so', 'libm.so'], 'exports': ['SDL_main']})

    def test_accepts_nativeaot_without_managed_assemblies(self):
        self.write_apk()
        report = verify_apk(self.apk, None)
        self.assertEqual(report['runtime'], 'nativeaot')
        self.assertFalse(report['managedAssemblyStore'])
        self.assertFalse(report['deviceExecutionVerified'])
        self.assertEqual(len(report['nativeLibraries']), 3)

    def test_rejects_mono_and_managed_payloads(self):
        for name in ('libmonosgen-2.0.so', 'libassemblies.arm64-v8a.blob.so', 'libcoreclr.so', 'Example.dll', 'Example.dll.so'):
            with self.subTest(name=name):
                self.write_apk(extra={'lib/arm64-v8a/' + name: b'not permitted'})
                with self.assertRaisesRegex(ValueError, 'Managed/Mono'):
                    verify_apk(self.apk, None)

    def test_rejects_missing_application(self):
        payloads = self.payloads()
        del payloads['libparadise_android.so']
        self.write_apk(payloads)
        with self.assertRaisesRegex(ValueError, 'missing.*libparadise'):
            verify_apk(self.apk, None)

    def test_rejects_wrong_entry_point(self):
        payloads = self.payloads()
        payloads['libparadise_android.so'] = library('libparadise_android.so', ('DotNetRuntimeDebugHeader',))
        self.write_apk(payloads)
        with self.assertRaisesRegex(ValueError, 'exports.*SDL_main'):
            verify_apk(self.apk, None)

    def test_rejects_non_nativeaot_application(self):
        payloads = self.payloads()
        payloads['libparadise_android.so'] = library('libparadise_android.so', ('SDL_main',))
        self.write_apk(payloads)
        with self.assertRaisesRegex(ValueError, 'DotNetRuntimeDebugHeader'):
            verify_apk(self.apk, None)

    def test_rejects_mismatched_soname(self):
        payloads = self.payloads()
        payloads['libSDL3.so'] = library('libdifferent.so', ('JNI_OnLoad',))
        self.write_apk(payloads)
        with self.assertRaisesRegex(ValueError, 'SONAME'):
            verify_apk(self.apk, None)

    def test_rejects_desktop_or_unbundled_dependency(self):
        payloads = self.payloads()
        payloads['libSDL3.so'] = library('libSDL3.so', ('JNI_OnLoad',), ('libc.so.6',))
        self.write_apk(payloads)
        with self.assertRaisesRegex(ValueError, 'non-Android'):
            verify_apk(self.apk, None)

    def test_rejects_checksum_mismatch(self):
        self.write_apk(mutate_receipt=lambda record: record['librarySha256'].update({'libSDL3.so': 'incorrect'}))
        with self.assertRaisesRegex(ValueError, 'checksum'):
            verify_apk(self.apk, None)

    def test_rejects_wrong_runtime_receipt(self):
        self.write_apk(mutate_receipt=lambda record: record.update(runtimeIdentifier='linux-arm64'))
        with self.assertRaisesRegex(ValueError, 'provenance'):
            verify_apk(self.apk, None)

    def test_rejects_wrong_abi(self):
        self.write_apk(extra={'lib/x86_64/libSDL3.so': library('libSDL3.so')})
        with self.assertRaisesRegex(ValueError, 'Unexpected ABI'):
            verify_apk(self.apk, None)

    def test_rejects_misaligned_application(self):
        payloads = self.payloads()
        payloads['libparadise_android.so'] = library('libparadise_android.so', alignment=4096)
        self.write_apk(payloads)
        with self.assertRaisesRegex(ValueError, '16 KB'):
            verify_apk(self.apk, None)

    def test_rejects_truncated_section_tables(self):
        data = library('libtest.so')
        for size in (0, 63, len(data) - 1):
            with self.subTest(size=size), self.assertRaises(ValueError):
                inspect_linkage(data[:size])

    def test_rejects_corrupt_string_link(self):
        data = bytearray(library('libtest.so'))
        section_offset = struct.unpack_from('<Q', data, 40)[0]
        struct.pack_into('<I', data, section_offset + 2 * 64 + 40, 99)
        with self.assertRaisesRegex(ValueError, 'string-table'):
            inspect_linkage(data)


if __name__ == '__main__':
    unittest.main()
