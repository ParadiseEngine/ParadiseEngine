"""Offline checks for the native validation gate; these do not claim device compatibility."""
import copy
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

from verify_android import inspect_elf, verify_native, verify_apk


def elf(alignment=16384, machine=183):
    data = bytearray(120)
    data[:6] = b'\x7fELF\x02\x01'
    struct.pack_into('<HHIQQQIHHHHHH', data, 16, 3, machine, 1, 0, 64, 0, 0, 64, 56, 1, 0, 0, 0)
    struct.pack_into('<IIQQQQQQ', data, 64, 1, 5, 0, 0, 0, len(data), len(data), alignment)
    return bytes(data)


class ArtifactTests(unittest.TestCase):
    def test_accepts_aligned_arm64(self):
        self.assertEqual(inspect_elf(elf())['loadSegmentAlignments'], [16384])

    def test_rejects_wrong_machine(self):
        with self.assertRaisesRegex(ValueError, 'ARM64'):
            inspect_elf(elf(machine=62))

    def test_rejects_4k(self):
        with self.assertRaisesRegex(ValueError, '16 KB'):
            inspect_elf(elf(alignment=4096))

    def test_rejects_truncated_headers(self):
        for size in (0, 6, 63, 100):
            with self.subTest(size=size), self.assertRaises(ValueError):
                inspect_elf(elf()[:size])

    def test_rejects_truncated_segments(self):
        data = bytearray(elf())
        struct.pack_into('<Q', data, 64 + 32, 256)
        with self.assertRaisesRegex(ValueError, 'segment'):
            inspect_elf(data)

    def test_checks_native_provenance_and_hash(self):
        manifest = {'bindingVersion': '0.5.7', 'revision': 'test', 'androidAbi': 'arm64-v8a',
                    'androidApi': 26, 'ndkVersion': 'test',
                    'normalizedHeaderSha256': hashlib.sha256(b'header\n').hexdigest()}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'webgpu.h').write_bytes(b'header\r\n')
            (root / 'libwebgpu_dawn.so').write_bytes(elf())
            record = dict(manifest, librarySha256=hashlib.sha256(elf()).hexdigest())
            (root / 'dawn-build.json').write_text(json.dumps(record))
            self.assertEqual(verify_native(root, manifest)['machine'], 'aarch64')
            altered = copy.copy(manifest)
            altered['bindingVersion'] = 'different'
            with self.assertRaisesRegex(ValueError, 'provenance'):
                verify_native(root, altered)
            (root / 'libwebgpu_dawn.so').write_bytes(elf() + b'changed')
            with self.assertRaisesRegex(ValueError, 'checksum'):
                verify_native(root, manifest)

    def test_rejects_incomplete_apk(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'missing.apk'
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('AndroidManifest.xml', b'manifest')
            with self.assertRaisesRegex(ValueError, 'classes.dex'):
                verify_apk(path, None, runtime='mono')

    def test_checks_all_libraries_not_only_dawn(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'test.apk'
            with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('AndroidManifest.xml', b'manifest')
                archive.writestr('classes.dex', b'dex')
                archive.writestr('lib/arm64-v8a/libwebgpu_dawn.so', elf())
                archive.writestr('lib/arm64-v8a/libSDL3.so', elf(alignment=4096))
                archive.writestr('lib/arm64-v8a/libassemblies.arm64-v8a.blob.so', elf())
            with self.assertRaisesRegex(ValueError, '16 KB'):
                verify_apk(path, None, runtime='mono')


    def test_rejects_fast_deployment_apk_without_assembly_store(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'no-assemblies.apk'
            with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                archive.writestr('AndroidManifest.xml', b'manifest')
                archive.writestr('classes.dex', b'dex')
                archive.writestr('lib/arm64-v8a/libwebgpu_dawn.so', elf())
                archive.writestr('lib/arm64-v8a/libSDL3.so', elf())
            with self.assertRaisesRegex(ValueError, 'libassemblies'):
                verify_apk(path, None, runtime='mono')


if __name__ == '__main__':
    unittest.main()
