# Android NativeAOT bring-up

Android is the first platform implementation slice of #323. iOS #324 and browser WASM/WebGPU remain required targets. This sample establishes a build and packaging path, not a device-qualified mobile port.

## Runtime architecture

The Android application uses **NativeAOT**, not Mono AOT:

```text
Java MainActivity extends SDLActivity
  -> SDL's existing native JNI/application-thread bridge
  -> SDL_main exported by libparadise_android.so
  -> SdlAndroidHost.Run -> game/application loop
  -> SDL window + Dawn WebGPU surface
```

`Paradise.Hosting.Android` now targets ordinary `net10.0` and is AOT-compatible. It supplies `SdlAndroidHost.Run(Action<ILogger>)` and a logcat logger using source-generated P/Invoke to `liblog.so`. It has no `Mono.Android`, `Java.Interop`, or managed Activity dependency. The host translates application exceptions to exit code 1, attempts a critical log, and contains logger failures. Applications must also catch startup failures inside their exported unmanaged entry point. No managed exception may cross SDL/JNI.

The application targets `net10.0` / **`linux-bionic-arm64`**, with `PublishAot=true`, `NativeLib=Shared`, and `PublishAotUsingRuntimePack=true`. The documented Bionic route requires `DisableUnsupportedError=true`; this is an explicit experimental-platform opt-in, not suppression of trimming/AOT warnings. Those warnings remain errors. Android NativeAOT is experimental upstream and has no built-in Java interop; SDL supplies that boundary here. Do not confuse the Bionic RID with glibc `linux-arm64` or the Mono-hosted `android-arm64` application target.

An initial attempt to enable NativeAOT on the earlier `net10.0-android` host reached ILC but pulled in Mono-only exception methods and emitted `Mono.Android` AOT warnings. Rather than suppressing them, the managed Activity layer was removed. **`SdlAndroidActivity` is replaced by `SdlAndroidHost`**; sample launch metadata is now Java/XML, not C# Activity attributes.

The app remains managed C# at source level and retains NativeAOT's GC/runtime support inside its native library. It does not carry a Mono VM, JIT, assembly store, or separately deployed managed assemblies. It is not a runtime-free C++ conversion. The sample logs its native entry and rejects dynamic-code support at runtime.

## Activity title-bar policy

Android game launchers must select `@android:style/Theme.Material.NoActionBar` in their Activity manifest before SDL creates its content view. Immersive fullscreen only hides system status/navigation bars; it does not remove a theme-provided ActionBar. Keep this manifest policy alongside the engine fullscreen default, rather than hiding a title view after launch. The theme deliberately does not force `windowFullscreen`: SDL and `WindowOptions.Fullscreen` still control system bars, including the explicit windowed opt-out.

The APK recipe validates the effective MainActivity theme (Activity overrides Application), rejecting a missing theme or an override that restores the title bar. The ParadiseSamples Android launcher is the matching template. Android framework styles require no extra Java dependency or native rebuild.

## Platform boundaries

The sample lives in **ParadiseSamples** at `src/Paradise.Rendering.Android.Sample`, consuming a versioned engine NuGet feed, not source overrides. It is a foreground clear-color/touch probe without PBR, KTX, ImGui, Noesis or Wwise. Full mobile-session/surface recovery remains unfinished.

`SDLActivity` owns Android's UI/JNI lifecycle and starts the application thread. The final element of its `getLibraries()` list is `paradise_android`, so SDL locates `SDL_main` there. Its existing `getMainSharedObject()` resolves `nativeLibraryDir/libparadise_android.so`; the manifest deliberately uses `extractNativeLibs=true`. Do not disable extraction without adapting and validating this loader contract.

The surface descriptor borrows `ANativeWindow*`; drawable replacement still requires retiring presentation resources and acquiring a new surface. Resize is not a substitute. SDL window setup uses `SDL_GetPlatform()` for Android detection because an NDK/Bionic runtime must not depend on the managed OS predicate selecting mobile behavior. Creating a mobile window does not automatically activate the soft keyboard.

Ordinary desktop/browser solutions are unchanged. `ParadiseEngine.Android.slnx` builds/tests the host without an Android managed workload. Browser rendering continues through `Paradise.Rendering.Browser`; native Dawn, the Java launcher and Bionic runtime must never enter WASM dependencies.

## Fullscreen defaults

Android SDL windows default to **immersive fullscreen**. `new WindowOptions("Game", 1280, 720)` selects fullscreen on Android, while retaining the existing windowed desktop default. `Fullscreen = true` or `false` explicitly overrides the SDL platform default; null means platform-selected. In fullscreen, the actual drawable follows the display rather than the requested startup dimensions.

The SDL backend adds `SDL_WINDOW_FULLSCREEN` during creation. The matching SDLActivity bridge owns hiding the status/navigation bars, edge-to-edge layout, transient edge-swipe access and re-hiding. Do not add a second per-frame Java system-bar controller or change device-wide navigation settings. Fullscreen is not kiosk mode: Android navigation remains accessible.

```csharp
// Android: fullscreen by default. Desktop: windowed by default.
var window = new WindowOptions("Game", 1280, 720);
// An explicit opt-out for Android tooling or embedded test applications.
var windowed = window with { Fullscreen = false };
```

This is an SDL windowing policy, not browser fullscreen authorization: WASM/WebGPU canvas behavior is unchanged. Render using the live window size; keep interactive UI inside safe areas rather than assuming the display has no cutout or transient system bars.

## Pinned dependencies

`tools/android/dawn.manifest.json` pairs WebGPUSharp **0.5.7** with Dawn **4178cb7e771b700743bc489037390777b98be39d**, whose generated header must match the pinned reference hash. Nearby native daily binaries are not ABI substitutes.

`nativeaot.manifest.json` additionally pins .NET runtime **10.0.12**, SDL3-CS **2026.722.0**, the original Android SDL library hash, the Java bridge's source revision/hash, NDK **28.2.13676358**, SDK platform **36**, build-tools **36.0.0**, and minimum API **26**. The Java bridge and native SDL come from the same binding release. This API/ABI floor is not a measured device support matrix.

The application explicitly excludes native assets from both WebGPUSharp and SDL3-CS. The APK recipe takes the neutral managed bindings, selects only Android natives, and rejects version/hash mismatches. In particular, NuGet's Linux ARM64 fallback must not silently provide a glibc Dawn or SDL binary.

## Build

Install .NET SDK 10.0.400 or newer, Python 3.10+, Git, CMake/Ninja, curl (or an SSL-enabled Python), JDK 21, Android SDK platform/build tools 36, and the pinned NDK. **The .NET Android workload and Gradle are not required for this route.** Initial verification used Windows/.NET SDK 10.0.401; Linux CI is configured separately.

Build native Dawn once from the engine root:

```sh
python3 tools/android/build_dawn.py \
  --ndk "$ANDROID_HOME/ndk/28.2.13676358" \
  --cache ./artifacts/dawn-cache \
  --out ./artifacts/native/android-arm64 --jobs 4
```

The script verifies the exact source/header pair, native exports and 16 KB ELF alignment, retains the unstripped native build for symbolication, and stages a stripped library with notices and a checksum receipt. Explicit `--cmake` and `--ninja` paths are accepted on Windows.

Pack `Paradise.Features`, `Paradise.Windowing`, `Paradise.Rendering`, `Paradise.Windowing.Sdl`, `Paradise.Rendering.WebGPU`, and `Paradise.Hosting.Android` into one local feed using a unique prerelease version, for example:

```sh
dotnet pack src/Paradise.Hosting.Android/Paradise.Hosting.Android.csproj \
  -c Release -p:Version=0.48.0-android.2 -o ./artifacts/feed
```

Repeat for the other five projects. Never overwrite an already-consumed package version after changing code; use a new prerelease suffix.

Build and test-sign the complete sample APK:

```sh
python3 tools/android/build_nativeaot.py \
  --samples ../ParadiseSamples \
  --version 0.48.0-android.2 --feed ./artifacts/feed \
  --native-dir ./artifacts/native/android-arm64 \
  --sdk "$ANDROID_HOME" --jdk "$JAVA_HOME" \
  --cache ./artifacts/android-cache --out ./artifacts/android \
  --configuration Release
```

Repeat with `--configuration Debug`. `--dotnet` and `--ndk` accept explicit paths. PowerShell uses backticks instead of backslashes for line continuation. The recipe rejects manifest minimum/target API drift and disabled native-library extraction before publishing, then publishes NativeAOT with the NDK toolchain, compiles the Java launcher, runs D8/aapt2, assembles the explicit native payload, aligns, test-signs, and validates the APK. It copies available `.so.dbg` files alongside the published native output, outside the APK. Keep those outputs for native debugging; source-level Mono debugging is no longer applicable.

Outputs are `ParadiseAndroid-NativeAOT-<Debug|Release>.apk`, a validation JSON, and native outputs under `native/<configuration>/`. Both configurations use true NativeAOT. Signing uses a generated **development key** in the private cache; neither configuration is production/store signed. Persist that key to update an installed test app. An older Mono APK signed with a different development key cannot be updated in place; uninstalling it removes its app data.

## Validation and CI

```sh
dotnet test --solution ParadiseEngine.Android.slnx --output normal
python3 -m unittest discover -s tools/android -p 'test_*.py' -v
python3 tools/android/verify_android.py \
  --native-dir ./artifacts/native/android-arm64 \
  --apk ./artifacts/android/ParadiseAndroid-NativeAOT-Release.apk \
  --report ./artifacts/android/verified.json
```

NativeAOT is the validator's default. It requires exactly the app, SDL and Dawn libraries, matching receipts/checksums, ELF SONAMEs, Android/system dependencies, SDL's JNI export, and the app's `SDL_main`/`DotNetRuntimeDebugHeader` exports. It rejects Mono/CoreCLR libraries, managed DLLs, assembly stores, unexpected ABIs and non-16-KB-compatible segments across all libraries. APK signatures and ZIP alignment are independently checked with Android build tools. `--runtime mono` exists only to validate pre-migration baseline artifacts; it is not a supported application fallback.

The engine workflow runs host/tool tests, NativeAOT-publishes a small probe rooting the actual host/window/renderer, validates its ELF, builds pinned Dawn, and packs the six-package feed. The probe is compile-only, not device-rendering evidence. The full sample APK recipe is separately validated against that package boundary. Workflow source changes do not imply a successful hosted CI run. Browser unit/resource-lifetime tests remain independent and do not require Android tools.

## Device acceptance remains open

Install on a connected ARM64 test device and confirm the visible animated clear color and touch response. The native-entry and first-frame logs are observations, not proof of displayed pixels. Physical-device startup, pause/resume, native drawable replacement, orientation/safe areas, keyboard focus, device loss, thermal/memory measurements, full PBR/assets/UI/audio, and real-browser scene parity remain separate acceptance gates. This migration does not claim they passed.

References: [NativeAOT support/limitations](https://learn.microsoft.com/dotnet/core/deploying/native-aot/), [Bionic NDK route](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/android-bionic.md), [native exports](https://learn.microsoft.com/dotnet/core/deploying/native-aot/interop), [SDL Android](https://wiki.libsdl.org/SDL3/README-android), [16 KB page sizes](https://developer.android.com/guide/practices/page-sizes).

## Texture-cache cryptography boundary

The PBR texture cache uses managed `System.IO.Hashing.XxHash128` for its process-local
content-and-usage key. This key is not persisted identity, authentication or content-integrity
verification. Removing its former SHA-256 call avoids initializing platform cryptography merely
to load a material. Cryptographic verification elsewhere remains SHA-256; an application that
uses .NET cryptography on the Bionic route must still package its OpenSSL dependency.

The compile-only NativeAOT probe now roots the material-cache path with an invalid KTX2
payload, which exercises hashing and the managed fallback without invoking libktx.
Portability and material-lifetime tests cover complete payloads, slices, source mutation,
usage separation and reference retirement. This is not a device or real-browser execution test.

## Rendering performance follow-up

See [mobile shader specialization](android-shader-specialization.md) for pipeline constants,
material specialization, hard-shadow variants, measurements and remaining device gates.
