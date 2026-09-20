#!/usr/bin/env python3
"""Validate Android ARM64 native provenance, ELF alignment and APK native payloads."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys
import zipfile

from nativeaot_elf import inspect_linkage


def inspect_elf(data: bytes) -> dict:
    if len(data) < 64 or data[:6] != b'\x7fELF\x02\x01':
        raise ValueError('Expected a little-endian ELF64 library')
    header = struct.unpack_from('<HHIQQQIHHHHHH', data, 16)
    kind, machine, version = header[:3]
    phoff, phsize, phcount = header[4], header[8], header[9]
    if kind != 3 or machine != 183 or version != 1:
        raise ValueError('Expected an Android ARM64 shared object (ET_DYN, EM_AARCH64)')
    if phsize < 56 or phcount == 0 or phoff + phsize * phcount > len(data):
        raise ValueError('Invalid or truncated ELF program header table')
    alignments = []
    for i in range(phcount):
        segment = struct.unpack_from('<IIQQQQQQ', data, phoff + i * phsize)
        if segment[0] != 1:
            continue
        offset, address, file_size, memory_size, alignment = segment[2], segment[3], segment[5], segment[6], segment[7]
        if file_size > memory_size or offset + file_size > len(data):
            raise ValueError('Truncated or invalid ELF load segment')
        if alignment < 16384 or alignment & (alignment - 1) or address % 16384 != offset % 16384:
            raise ValueError('ELF load segment does not support 16 KB pages')
        alignments.append(alignment)
    if not alignments:
        raise ValueError('ELF has no load segments')
    return {'machine': 'aarch64', 'loadSegmentAlignments': alignments, 'bytes': len(data)}


def verify_native(directory: Path, manifest: dict) -> dict:
    record = json.loads((directory / 'dawn-build.json').read_text())
    for key in ('bindingVersion', 'revision', 'androidAbi', 'androidApi', 'ndkVersion'):
        if record.get(key) != manifest[key]:
            raise ValueError('Native provenance mismatch: ' + key)
    header = (directory / 'webgpu.h').read_bytes().replace(b'\r\n', b'\n')
    if hashlib.sha256(header).hexdigest() != manifest['normalizedHeaderSha256']:
        raise ValueError('Dawn header does not match WebGPUSharp')
    data = (directory / 'libwebgpu_dawn.so').read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != record.get('librarySha256'):
        raise ValueError('Dawn library checksum mismatch')
    return dict(inspect_elf(data), sha256=digest, revision=record['revision'])


def verify_apk(path: Path, native: dict | None, *, runtime: str = 'nativeaot') -> dict:
    if runtime not in ('nativeaot', 'mono'):
        raise ValueError('Unknown Android runtime: ' + runtime)
    libraries = {}
    payloads = {}
    with zipfile.ZipFile(path) as archive, path.open('rb') as raw:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate APK entries')
        runtime_library = 'libparadise_android.so' if runtime == 'nativeaot' else 'libassemblies.arm64-v8a.blob.so'
        for required in ('AndroidManifest.xml', 'classes.dex', 'lib/arm64-v8a/libSDL3.so', 'lib/arm64-v8a/libwebgpu_dawn.so',
                         'lib/arm64-v8a/' + runtime_library):
            if required not in names:
                raise ValueError('APK is missing ' + required)
        if runtime == 'nativeaot':
            for name in names:
                lower = name.lower()
                filename = lower.rsplit('/', 1)[-1]
                if (lower.endswith(('.dll', '.dll.so', '.deps.json', '.runtimeconfig.json')) or
                        filename.startswith(('libmono', 'libassemblies', 'libcoreclr', 'libmonodroid')) or
                        lower.startswith('assemblies/')):
                    raise ValueError('Managed/Mono payload in NativeAOT APK: ' + name)
        for info in archive.infolist():
            if not (info.filename.startswith('lib/') and info.filename.endswith('.so')):
                continue
            if not info.filename.startswith('lib/arm64-v8a/'):
                raise ValueError('Unexpected ABI in ARM64-only APK: ' + info.filename)
            data = archive.read(info)
            libraries[info.filename] = inspect_elf(data)
            payloads[info.filename.rsplit('/', 1)[-1]] = data
            if info.compress_type == zipfile.ZIP_STORED:
                raw.seek(info.header_offset)
                local = raw.read(30)
                if len(local) != 30 or local[:4] != b'PK\x03\x04':
                    raise ValueError('Invalid ZIP local header')
                name_len, extra_len = struct.unpack_from('<HH', local, 26)
                offset = info.header_offset + 30 + name_len + extra_len
                if offset % 16384:
                    raise ValueError('Uncompressed native library is not ZIP-aligned to 16 KB: ' + info.filename)
            if native and info.filename.endswith('/libwebgpu_dawn.so'):
                if hashlib.sha256(data).hexdigest() != native['sha256']:
                    raise ValueError('APK Dawn library differs from the verified build artifact')
        if runtime == 'nativeaot':
            verify_nativeaot_payloads(archive, payloads, libraries)
    return {'path': str(path), 'runtime': runtime, 'nativeLibraries': libraries,
            'managedAssemblyStore': runtime == 'mono', 'deviceExecutionVerified': False}


def verify_nativeaot_payloads(archive: zipfile.ZipFile, payloads: dict, libraries: dict) -> None:
    manifest = json.loads(Path(__file__).with_name('nativeaot.manifest.json').read_text())
    receipt_name = 'assets/native/nativeaot-build.json'
    if receipt_name not in archive.namelist():
        raise ValueError('NativeAOT APK is missing its build receipt')
    record = json.loads(archive.read(receipt_name))
    for key in ('runtimeIdentifier', 'runtimeVersion', 'androidAbi', 'androidApi', 'targetApi',
                'applicationLibrary', 'entryPoint', 'sdlVersion', 'sdlBindingCommit', 'sdlBridgeSha256'):
        if record.get(key) != manifest[key]:
            raise ValueError('NativeAOT provenance mismatch: ' + key)
    expected = {'libSDL3.so', 'libwebgpu_dawn.so', manifest['applicationLibrary']}
    if set(payloads) != expected or set(record.get('librarySha256', {})) != expected:
        raise ValueError('Unexpected NativeAOT library inventory')
    if hashlib.sha256(archive.read('classes.dex')).hexdigest() != record.get('dexSha256'):
        raise ValueError('NativeAOT launcher checksum mismatch')
    system = {'libc.so', 'libm.so', 'libdl.so', 'liblog.so', 'libandroid.so', 'libz.so',
              'libOpenSLES.so', 'libGLESv1_CM.so', 'libGLESv2.so', 'libEGL.so', 'libvulkan.so'}
    for name, data in payloads.items():
        if hashlib.sha256(data).hexdigest() != record['librarySha256'][name]:
            raise ValueError('NativeAOT library checksum mismatch: ' + name)
        linkage = inspect_linkage(data)
        if linkage['soname'] != name:
            raise ValueError('ELF SONAME does not match packaged name: ' + name)
        unresolved = set(linkage['needed']) - system - expected
        if unresolved:
            raise ValueError('Unpackaged or non-Android dependencies: ' + ', '.join(sorted(unresolved)))
        exports = set(linkage.pop('exports'))
        if name == manifest['applicationLibrary']:
            required = {manifest['entryPoint'], 'DotNetRuntimeDebugHeader'}
            if not required <= exports:
                raise ValueError('Missing NativeAOT/SDL exports: ' + ', '.join(sorted(required - exports)))
            linkage['verifiedExports'] = sorted(required)
        elif name == 'libSDL3.so' and 'JNI_OnLoad' not in exports:
            raise ValueError('SDL native library is missing JNI_OnLoad')
        libraries['lib/arm64-v8a/' + name].update(linkage)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-dir', type=Path)
    parser.add_argument('--apk', type=Path)
    parser.add_argument('--runtime', choices=('nativeaot', 'mono'), default='nativeaot')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    if not args.native_dir and not args.apk:
        parser.error('Specify --native-dir and/or --apk')
    manifest = json.loads(Path(__file__).with_name('dawn.manifest.json').read_text())
    native = verify_native(args.native_dir, manifest) if args.native_dir else None
    result = {'native': native, 'apk': verify_apk(args.apk, native, runtime=args.runtime) if args.apk else None}
    text = json.dumps(result, indent=2) + '\n'
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(text)
    print(text)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, struct.error, zipfile.BadZipFile) as error:
        print(f'Android artifact validation failed: {error}', file=sys.stderr)
        sys.exit(1)
