#!/usr/bin/env python3
"""Build the exact Dawn revision paired with WebGPUSharp; never substitute a daily binary."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from verify_android import inspect_elf


def run(*args: str | Path, cwd: Path | None = None) -> None:
    command = [str(arg) for arg in args]
    print('+ ' + subprocess.list2cmdline(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True)


def sha256(path: Path, *, text: bool = False) -> str:
    data = path.read_bytes()
    if text:
        data = data.replace(b'\r\n', b'\n')
    return hashlib.sha256(data).hexdigest()


def stage_notices(source: Path, ndk: Path, output: Path) -> None:
    roots = ('third_party/abseil-cpp', 'third_party/spirv-headers/src',
             'third_party/vulkan-headers/src', 'third_party/vulkan-utility-libraries/src')
    for relative in roots:
        root = source / relative
        for directory, children, files in os.walk(root):
            children[:] = [name for name in children if name != '.git']
            for name in files:
                file = Path(directory) / name
                if not (name.lower().startswith(('license', 'notice', 'copying'))
                        or 'LICENSES' in file.relative_to(root).parts):
                    continue
                destination = output / 'licenses' / file.relative_to(source)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, destination)
    for name in ('NOTICE', 'NOTICE.toolchain'):
        destination = output / 'licenses/android-ndk' / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ndk / name, destination)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ndk', required=True, type=Path)
    parser.add_argument('--cmake', default='cmake')
    parser.add_argument('--ninja', default='ninja')
    parser.add_argument('--cache', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    parser.add_argument('--jobs', type=int, default=4)
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error('--jobs must be positive')
    manifest = json.loads(Path(__file__).with_name('dawn.manifest.json').read_text())
    ndk = args.ndk.resolve()
    properties = (ndk / 'source.properties').read_text()
    if not any(line.strip() == 'Pkg.Revision = ' + manifest['ndkVersion'] for line in properties.splitlines()):
        raise RuntimeError('NDK revision does not match dawn.manifest.json')
    source = args.cache.resolve() / ('dawn-' + manifest['revision'])
    build = args.cache.resolve() / ('build-' + manifest['revision'] + '-' + manifest['androidAbi'])
    output = args.out.resolve()
    source.mkdir(parents=True, exist_ok=True)
    if not (source / '.git').exists():
        if any(source.iterdir()):
            raise RuntimeError(f'Refusing to initialize a nonempty source directory: {source}')
        run('git', 'init', source)
        run('git', 'remote', 'add', 'origin', manifest['repository'], cwd=source)
    remote = subprocess.check_output(['git', 'remote', 'get-url', 'origin'], cwd=source, text=True).strip()
    if remote != manifest['repository']:
        raise RuntimeError('Source cache has an unexpected remote')
    if os.name == 'nt':
        run('git', 'config', 'core.longpaths', 'true', cwd=source)
    head = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=source, capture_output=True, text=True)
    if head.returncode or head.stdout.strip() != manifest['revision']:
        if subprocess.check_output(['git', 'status', '--porcelain'], cwd=source, text=True).strip():
            raise RuntimeError('Source cache is dirty; refusing to change its revision')
        run('git', 'fetch', '--depth=1', 'origin', manifest['revision'], cwd=source)
        run('git', 'checkout', '--detach', manifest['revision'], cwd=source)
    run(args.cmake, '-S', source, '-B', build, '-G', 'Ninja',
        '-DCMAKE_MAKE_PROGRAM=' + str(Path(args.ninja).resolve() if Path(args.ninja).exists() else args.ninja),
        '-DCMAKE_TOOLCHAIN_FILE=' + str(ndk / 'build/cmake/android.toolchain.cmake'),
        '-DANDROID_ABI=' + manifest['androidAbi'],
        '-DANDROID_PLATFORM=android-' + str(manifest['androidApi']),
        '-DANDROID_STL=c++_static', '-DCMAKE_BUILD_TYPE=Release',
        '-DPython3_EXECUTABLE=' + sys.executable,
        '-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384',
        '-DDAWN_FETCH_DEPENDENCIES=ON', '-DDAWN_BUILD_MONOLITHIC_LIBRARY=SHARED',
        '-DDAWN_BUILD_SAMPLES=OFF', '-DDAWN_BUILD_TESTS=OFF', '-DDAWN_USE_GLFW=OFF',
        '-DDAWN_USE_X11=OFF', '-DDAWN_USE_WAYLAND=OFF', '-DDAWN_ENABLE_NULL=OFF',
        '-DDAWN_ENABLE_VULKAN=ON', '-DDAWN_ENABLE_D3D11=OFF', '-DDAWN_ENABLE_D3D12=OFF',
        '-DDAWN_ENABLE_METAL=OFF', '-DDAWN_ENABLE_DESKTOP_GL=OFF', '-DDAWN_ENABLE_OPENGLES=OFF',
        '-DDAWN_ENABLE_SPIRV_VALIDATION=OFF', '-DDAWN_FORCE_SYSTEM_COMPONENT_LOAD=ON',
        '-DTINT_BUILD_TESTS=OFF', '-DTINT_BUILD_CMD_TOOLS=OFF',
        '-DTINT_BUILD_IR_BINARY=OFF', '-DDAWN_BUILD_PROTOBUF=OFF', '-DTINT_BUILD_SPV_READER=OFF',
        '-DTINT_BUILD_WGSL_READER=ON', '-DTINT_BUILD_SPV_WRITER=ON')
    run(args.cmake, '--build', build, '--target', 'webgpu_dawn', '--parallel', str(args.jobs))
    header = build / 'gen/include/dawn/webgpu.h'
    if sha256(header, text=True) != manifest['normalizedHeaderSha256']:
        raise RuntimeError('Generated C header differs from the pinned binding reference; refusing to stage native code')
    library = build / 'src/dawn/native/libwebgpu_dawn.so'
    if not library.is_file():
        raise RuntimeError(f'Expected shared library was not produced: {library}')
    host = 'windows-x86_64' if os.name == 'nt' else ('darwin-x86_64' if sys.platform == 'darwin' else 'linux-x86_64')
    suffix = '.exe' if os.name == 'nt' else ''
    readelf = ndk / 'toolchains/llvm/prebuilt' / host / 'bin' / ('llvm-readelf' + suffix)
    symbols = subprocess.check_output([str(readelf), '--dyn-syms', '--wide', str(library)], text=True)
    for symbol in ('wgpuCreateInstance', 'wgpuInstanceCreateSurface', 'wgpuInstanceWaitAny', 'wgpuDeviceCreateShaderModule'):
        if not any(line.split()[-1:] == [symbol] and ' UND ' not in line for line in symbols.splitlines()):
            raise RuntimeError('Missing exported WebGPU symbol: ' + symbol)
    output.mkdir(parents=True, exist_ok=True)
    staged = output / library.name
    shutil.copy2(library, staged)
    # Keep the unstripped library in the build cache for native crash symbolication.
    strip = readelf.with_name('llvm-strip' + suffix)
    run(strip, '--strip-unneeded', staged)
    inspect_elf(staged.read_bytes())
    shutil.copy2(header, output / 'webgpu.h')
    shutil.copy2(source / 'LICENSE', output / 'LICENSE.Dawn')
    stage_notices(source, ndk, output)
    record = dict(manifest, librarySha256=sha256(staged), headerSha256=sha256(header, text=True))
    (output / 'dawn-build.json').write_text(json.dumps(record, indent=2) + '\n')
    print(f'Native artifact staged at {output}. Device execution is a separate acceptance gate.', flush=True)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f'Android Dawn build failed: {error}', file=sys.stderr)
        sys.exit(1)
