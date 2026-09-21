#!/usr/bin/env python3
"""Publish the sample as a NativeAOT shared library and build a test-signed Android APK."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from verify_android import inspect_elf, verify_apk, verify_native


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(*args: str | Path, cwd: Path | None = None, env: dict | None = None) -> None:
    command = [str(arg) for arg in args]
    print('+ ' + subprocess.list2cmdline(command), flush=True)
    subprocess.run(command, cwd=cwd, env=env, check=True)


def download(url: str, path: Path, expected: str | None = None) -> Path:
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        # The NDK's bundled Python can lack SSL; curl retains certificate verification.
        temporary = path.with_suffix(path.suffix + '.tmp')
        try:
            curl = shutil.which('curl')
            if curl:
                run(curl, '--fail', '--location', '--retry', '2', '--max-time', '60', '--output', temporary, url)
            else:
                with urllib.request.urlopen(url, timeout=60) as response:
                    temporary.write_bytes(response.read())
            if expected and digest(temporary) != expected:
                raise ValueError('Downloaded checksum mismatch: ' + url)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)
    if expected and digest(path) != expected:
        raise ValueError('Cached checksum mismatch: ' + str(path))
    return path


def package_path(assets: dict, package: str, version: str) -> Path:
    relative = package.lower() + '/' + version.lower()
    for folder in assets['packageFolders']:
        candidate = Path(folder) / relative
        if candidate.is_dir():
            return candidate
    raise ValueError('Restored package not found: ' + relative)


def require_package(assets: dict, package: str, version: str) -> None:
    expected = (package + '/' + version).lower()
    if expected not in {name.lower() for name in assets['libraries']}:
        raise ValueError('Sample dependency differs from the native manifest: ' + expected)


def validate_android_manifest(path: Path, manifest: dict) -> ET.ElementTree:
    """Reject launcher/SDK drift before recording pinned metadata in an APK receipt."""
    document = ET.parse(path)
    root = document.getroot()
    attribute = '{http://schemas.android.com/apk/res/android}'
    sdk = root.find('uses-sdk')
    if sdk is None or sdk.get(attribute + 'minSdkVersion') != str(manifest['androidApi']):
        raise ValueError('Android manifest minimum API differs from nativeaot.manifest.json')
    if sdk.get(attribute + 'targetSdkVersion') != str(manifest['targetApi']):
        raise ValueError('Android manifest target API differs from nativeaot.manifest.json')
    application = root.find('application')
    if application is None or application.get(attribute + 'extractNativeLibs') != 'true':
        raise ValueError('SDL nativeLibraryDir loading requires extractNativeLibs=true')
    if application.get(attribute + 'hasCode') != 'true':
        raise ValueError('The SDL Java launcher requires hasCode=true')
    activity = application.find("activity[@" + attribute + "name='.MainActivity']")
    if activity is None:
        raise ValueError('The SDL Java launcher requires MainActivity')
    # SDL fullscreen hides system bars, not an Activity's theme-provided title bar.
    theme = activity.get(attribute + 'theme', application.get(attribute + 'theme'))
    if theme != '@android:style/Theme.Material.NoActionBar':
        raise ValueError('The Android game launcher requires Theme.Material.NoActionBar')
    return document


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--samples', required=True, type=Path)
    parser.add_argument('--version', required=True)
    parser.add_argument('--feed', type=Path)
    parser.add_argument('--native-dir', required=True, type=Path)
    parser.add_argument('--sdk', required=True, type=Path)
    parser.add_argument('--jdk', required=True, type=Path)
    parser.add_argument('--ndk', type=Path)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--configuration', choices=('Debug', 'Release'), default='Release')
    parser.add_argument('--cache', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    tools = Path(__file__).resolve().parent
    manifest = json.loads((tools / 'nativeaot.manifest.json').read_text())
    dawn_manifest = json.loads((tools / 'dawn.manifest.json').read_text())
    native_dir = args.native_dir.resolve()
    native = verify_native(native_dir, dawn_manifest)
    samples, sdk, jdk = args.samples.resolve(), args.sdk.resolve(), args.jdk.resolve()
    ndk = (args.ndk or sdk / 'ndk' / manifest['ndkVersion']).resolve()
    if 'Pkg.Revision = ' + manifest['ndkVersion'] not in (ndk / 'source.properties').read_text().splitlines():
        raise ValueError('NDK version does not match nativeaot.manifest.json')
    cache, output = args.cache.resolve(), args.out.resolve()
    cache.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    suffix = '.exe' if os.name == 'nt' else ''
    host = 'windows-x86_64' if os.name == 'nt' else ('darwin-x86_64' if sys.platform == 'darwin' else 'linux-x86_64')
    ndk_bin = ndk / 'toolchains/llvm/prebuilt' / host / 'bin'
    build_tools = sdk / 'build-tools' / manifest['buildToolsVersion']
    android_jar = sdk / 'platforms' / ('android-' + str(manifest['targetApi'])) / 'android.jar'
    java, javac, keytool = (jdk / 'bin' / (name + suffix) for name in ('java', 'javac', 'keytool'))
    aapt, zipalign, strip = build_tools / ('aapt2' + suffix), build_tools / ('zipalign' + suffix), ndk_bin / ('llvm-strip' + suffix)
    for tool in (java, javac, keytool, aapt, zipalign, strip, android_jar):
        if not tool.is_file():
            raise ValueError('Missing build prerequisite: ' + str(tool))
    env = dict(os.environ, PATH=str(ndk_bin) + os.pathsep + os.environ.get('PATH', ''), JAVA_HOME=str(jdk))
    project = samples / 'src/Paradise.Rendering.Android.Sample/Paradise.Rendering.Android.Sample.csproj'
    project_dir = project.parent
    document = validate_android_manifest(project_dir / 'Android/AndroidManifest.xml', manifest)
    publish = output / 'native' / args.configuration
    command = [args.dotnet, 'publish', project, '-c', args.configuration, '-o', publish,
               '-p:ParadiseVersion=' + args.version, '-p:RuntimeFrameworkVersion=' + manifest['runtimeVersion'],
               '-p:NuGetAudit=false', '-v:minimal']
    if args.feed:
        command.append('-p:RestoreAdditionalProjectSources=' + str(args.feed.resolve()))
    run(*command, cwd=samples, env=env)
    symbol = project_dir / 'bin' / args.configuration / 'net10.0' / manifest['runtimeIdentifier'] / 'native/paradise_android.so.dbg'
    if symbol.is_file():
        shutil.copy2(symbol, publish / symbol.name)
    assets = json.loads((project_dir / 'obj/project.assets.json').read_text())
    require_package(assets, manifest['sdlPackage'], manifest['sdlVersion'])
    require_package(assets, dawn_manifest['bindingPackage'], dawn_manifest['bindingVersion'])
    if any('mono.android' in name.lower() or 'java.interop' in name.lower() for name in assets['libraries']):
        raise ValueError('Mono/Java.Interop leaked into the native application dependency graph')
    sdl_package = package_path(assets, manifest['sdlPackage'], manifest['sdlVersion'])
    sdl_native = sdl_package / 'runtimes/android-arm64/native/libSDL3.so'
    if digest(sdl_native) != manifest['sdlLibrarySha256']:
        raise ValueError('SDL native binary does not match the pinned binding release')
    bridge = download(manifest['sdlBridgeUrl'], cache / 'SDL3AndroidBridge.jar', manifest['sdlBridgeSha256'])
    application = publish / 'paradise_android.so'
    inspect_elf(application.read_bytes())
    runtime_package = package_path(assets, 'Microsoft.NETCore.App.Runtime.NativeAOT.linux-bionic-arm64', manifest['runtimeVersion'])
    prefix = 'ParadiseAndroid-NativeAOT-' + args.configuration
    apk = output / (prefix + '.apk')
    with tempfile.TemporaryDirectory(prefix='.nativeaot-build-', dir=output) as temporary:
        work = Path(temporary)
        classes, dex, asset_dir, libs = (work / name for name in ('classes', 'dex', 'assets/native', 'lib/arm64-v8a'))
        for directory in (classes, dex, asset_dir, libs):
            directory.mkdir(parents=True)
        shutil.copy2(application, libs / manifest['applicationLibrary'])
        shutil.copy2(native_dir / 'libwebgpu_dawn.so', libs / 'libwebgpu_dawn.so')
        shutil.copy2(sdl_native, libs / 'libSDL3.so')
        run(strip, '--strip-unneeded', libs / 'libSDL3.so')
        # Original native/ILC outputs and any .dbg files stay outside the APK for symbolication.
        for name in ('dawn-build.json', 'LICENSE.Dawn'):
            shutil.copy2(native_dir / name, asset_dir / name)
        shutil.copytree(native_dir / 'licenses', asset_dir / 'licenses')
        for name in ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
            shutil.copy2(runtime_package / name, asset_dir / 'licenses' / ('NativeAOT-' + name))
        for name, url in {
            'SDL3-CS.LICENCE': 'https://raw.githubusercontent.com/ppy/SDL3-CS/' + manifest['sdlBindingCommit'] + '/LICENCE',
            'SDL3.LICENSE.txt': 'https://raw.githubusercontent.com/libsdl-org/SDL/' + manifest['sdlCommit'] + '/LICENSE.txt',
        }.items():
            shutil.copy2(download(url, cache / name), asset_dir / 'licenses' / name)
        run(javac, '--release', '8', '-encoding', 'UTF-8', '-classpath', str(android_jar) + os.pathsep + str(bridge),
            '-d', classes, project_dir / 'Android/MainActivity.java')
        run(java, '-cp', build_tools / 'lib/d8.jar', 'com.android.tools.r8.D8',
            '--debug' if args.configuration == 'Debug' else '--release', '--min-api', str(manifest['androidApi']),
            '--lib', android_jar, '--output', dex, bridge, *sorted(classes.rglob('*.class')))
        receipt = dict(manifest, configuration=args.configuration, engineVersion=args.version,
                       dotnetSdk=subprocess.check_output([args.dotnet, '--version'], cwd=samples, env=env, text=True).strip(),
                       librarySha256={path.name: digest(path) for path in libs.iterdir()},
                       dexSha256=digest(dex / 'classes.dex'), deviceExecutionVerified=False)
        (asset_dir / 'nativeaot-build.json').write_text(json.dumps(receipt, indent=2) + '\n')
        android_ns = 'http://schemas.android.com/apk/res/android'
        ET.register_namespace('android', android_ns)
        application_element = document.getroot().find('application')
        if application_element is None:
            raise ValueError('Android manifest has no application')
        application_element.set('{' + android_ns + '}debuggable', str(args.configuration == 'Debug').lower())
        resolved_manifest = work / 'AndroidManifest.xml'
        document.write(resolved_manifest, encoding='utf-8', xml_declaration=True)
        resources = work / 'resources.apk'
        unsigned = work / 'unsigned.apk'
        run(aapt, 'link', '-I', android_jar, '--manifest', resolved_manifest, '-A', work / 'assets', '-o', resources)
        # Rewrite aapt's streaming ZIP records instead of appending to its data-descriptor headers.
        with zipfile.ZipFile(resources) as source, zipfile.ZipFile(unsigned, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for info in source.infolist():
                archive.writestr(info.filename, source.read(info), compress_type=info.compress_type)
            for path in sorted(dex.glob('*.dex')):
                archive.write(path, path.name)
            for path in sorted(libs.iterdir()):
                archive.write(path, 'lib/arm64-v8a/' + path.name)
        aligned = work / 'aligned.apk'
        run(zipalign, '-P', '16', '-f', '4', unsigned, aligned)
        keystore = cache / 'debug.keystore'
        if not keystore.exists():
            run(keytool, '-genkeypair', '-noprompt', '-keystore', keystore, '-storetype', 'PKCS12',
                '-storepass', 'android', '-keypass', 'android', '-alias', 'androiddebugkey',
                '-dname', 'CN=Android Debug,O=Android,C=US', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000')
        signer = build_tools / 'lib/apksigner.jar'
        run(java, '-jar', signer, 'sign', '--ks', keystore, '--ks-key-alias', 'androiddebugkey',
            '--ks-pass', 'pass:android', '--key-pass', 'pass:android', '--out', apk, aligned)
        run(java, '-jar', signer, 'verify', '--verbose', apk)
        run(zipalign, '-c', '-P', '16', '4', apk)
    report = {'native': native, 'apk': verify_apk(apk, native, runtime='nativeaot'),
              'apkSha256': digest(apk), 'bytes': apk.stat().st_size, 'testSigned': True}
    (output / (prefix + '-validation.json')).write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(f'Android NativeAOT build failed: {error}', file=sys.stderr)
        sys.exit(1)
