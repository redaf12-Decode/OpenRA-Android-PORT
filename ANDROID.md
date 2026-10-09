# OpenRA on Android

![OpenRA main menu running natively on Android](screenshots/openra-android-mainmenu.png)

Native Android port of OpenRA, built with .NET 9 (`net9.0-android`) and raw EGL/GLES — no SDL2, no emulation layer.

## Status

- ✅ Engine + all mods compile and run natively on `arm64-v8a`
- ✅ OpenGL ES 3.2 rendering via EGL (Adreno/Mali tested)
- ✅ Touch input: tap (left-click), double-tap, long-press (right-click), two-finger pan, pinch-to-zoom
- ✅ Soft keyboard for text fields (player name, etc.)
- ✅ Audio via OpenAL-Soft (OpenSL ES backend)
- ✅ Freeware game content auto-download from openra.net mirrors
- ✅ Red Alert main menu + shellmap rendering with live animation

## Architecture

```
OpenRA.Android (net9.0-android, the APK app)
 ├─ OpenRA.Game              (multi-targets net8.0;net9.0-android)
 ├─ OpenRA.Mods.Common/Cnc/D2k (multi-target, compiled-in, no disk loading)
 └─ OpenRA.Platforms.Android (EGL + GLES + touch + OpenAL audio)
```

The Android platform layer (`OpenRA.Platforms.Android`) implements OpenRA's `IPlatform`/`IPlatformWindow`/`IGraphicsContext` interfaces using:
- **EGL** via P/Invoke to `libEGL.so` (context, surface, swap)
- **GLES 3** function-pointer loader (reuses the engine's `OpenGL.cs` with `eglGetProcAddress`)
- **SurfaceView** + `ISurfaceHolderCallback` for the window surface
- **MotionEvent** translation for multi-touch input
- **OpenAL-Soft** for audio (`libsoft_oal.so`, OpenSL ES backend)
- **FreeType** for font rendering (`libfreetype6.so`)

## Build prerequisites

- .NET 9 SDK + `dotnet workload install android`
- Android SDK (API 34+), NDK r27
- JDK 17
- CMake (Android SDK CMake 3.22.1 works)

## Building the native libraries

The three native dependencies (FreeType, Lua, OpenAL-Soft) don't ship Android binaries, so they're built from source with the NDK:

```bash
# All scripts are in thirdparty/. They download source, cross-compile for arm64-v8a,
# and output 16KB-page-aligned .so files to the app's jniLibs dir.
export PATH="$HOME/Library/Android/sdk/cmake/3.22.1/bin:$PATH"
export ANDROID_NDK_ROOT="$HOME/Library/Android/sdk/ndk/27.1.12297006"

./thirdparty/build-freetype-android.sh   # → libfreetype6.so
./thirdparty/build-lua-android.sh        # → liblua51.so
./thirdparty/build-openal-android.sh     # → libsoft_oal.so
```

## Building and deploying the APK

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export JAVA_HOME="/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home"
export ANDROID_SDK_ROOT="$HOME/Library/Android/sdk"
export ANDROID_NDK_ROOT="$HOME/Library/Android/sdk/ndk/27.1.12297006"

# Build, align, sign, install, and launch on a connected device
./thirdparty/deploy-android.sh
```

Or manually:
```bash
dotnet build OpenRA.Android/OpenRA.Android.csproj -c Release -f net9.0-android -p:BuildForAndroid=true

# Sign with debug keystore
APK=OpenRA.Android/obj/Release/android/bin/net.openra.android.apk
zipalign -f -p 4 "$APK" "${APK%.apk}-aligned.apk"
apksigner sign --ks ~/.android/debug.keystore --ks-pass pass:android \
  --out "${APK%.apk}-Signed.apk" "${APK%.apk}-aligned.apk"

adb install -r "${APK%.apk}-Signed.apk"
adb shell am start -n net.openra.android/$(adb shell dumpsys package net.openra.android | grep -oE 'crc[0-9a-f]+\.MainActivity' | head -1)
```

## Touch controls

| Gesture | Action |
|---|---|
| Tap | Left click |
| Double-tap | Double-click |
| Long-press (>500ms) | Right-click |
| Drag | Move / scroll map / drag-select |
| Two-finger drag | Pan map |
| Pinch | Zoom in/out |

## Engine modifications

Changes to the core engine are minimal and guarded by `OperatingSystem.IsAndroid()`:
- `ObjectCreator.cs` — resolves mod assemblies from the default load context (compiled-in)
- `Game.cs` — instantiates `AndroidPlatform` directly; skips LOH compaction (unsupported on Mono)
- `PlatformInterfaces.cs` — added `StartTextInput()`/`StopTextInput()` for soft keyboard
- `Widget.cs` — calls `StartTextInput`/`StopTextInput` on focus gain/loss
- `*.csproj` — multi-target `net8.0;net9.0-android` via `BuildForAndroid` property

Desktop builds are completely unaffected.

## Red Alert 2 (RA2) mod

This port bundles the [OpenRA2](https://github.com/hel1o-wor1d/OpenRA2) mod (GPLv3):

- `OpenRA.Mods.RA2/` - the mod's C# assembly, adapted to this engine's API
  (docking notifications, `[VerifySync]`, sequence loader hooks, ...).
- `mods/ra2/` - mod rules, sequences, weapons, chrome, tilesets and maps,
  migrated from engine `release-20230225` with the engine's official
  `--update-mod release-20230225` rules plus a few documented manual fixes.
- The launch screen now offers a mod chooser (Red Alert, Tiberian Dawn, ...
  and Red Alert 2); the last choice is remembered.

### RA2 game data (required, not included)

Red Alert 2 artwork/audio is copyrighted and is **not** redistributed with the
app. Copy the mix files from your own legally obtained copy of the game to the
app's support directory before launching the RA2 mod:

```
Android/data/net.openra.android/files/Support/Content/ra2/
```

Required files (from `mods/ra2/mod.yaml`): `ra2.mix`, `language.mix`,
`multi.mix`, `audio.mix`, `cache.mix`, `cameo.mix`, `conquer.mix`,
`generic.mix`, `isogen.mix`, `isosnow.mix`, `isotemp.mix`, `isourb.mix`,
`load.mix`, `local.mix`, `neutral.mix`, `sidec01.mix`, `sidec02.mix`,
`sno.mix`, `snow.mix`, `tem.mix`, `temperat.mix`, `theme.mix`, `urb.mix`,
`urban.mix`, `audio.bag`.

Without this data the RA2 mod starts but can not enter a game; the bundled
maps and mod definitions are still included so the menu works.

## GitHub Actions setup (CI & signed releases)

All Android workflows are **manual-only** (`workflow_dispatch`) — nothing runs on
`push` or `pull_request`. Run them from the **Actions** tab on the `Main` branch:

- **Android CI** (`.github/workflows/android-ci.yml`) — builds an unsigned Release APK
  and uploads it as a workflow artifact for testing. Optional `branch` input (default `Main`).
  Never publishes a Release.
- **Android Release** (`.github/workflows/android-release.yml`) — builds, signs, verifies
  and publishes a GitHub Release in one run. Inputs:
  - `tag_name` — release tag, e.g. `v1.0.0`
  - `release_name` — release title (defaults to `tag_name`)
  - `prerelease` — mark as pre-release
  - `source_branch` — branch to build from (default `Main`)

### Required repository secrets

Add them under **GitHub → Settings → Secrets and variables → Actions → New repository secret**.
Never paste them into chat, workflow files, or the repository.

| Secret | Expected value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | The release keystore file, Base64-encoded (one line, no `data:` prefix) |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password (`keytool --storepass`) |
| `ANDROID_KEY_ALIAS` | Key alias inside the keystore (`keytool --alias`) |
| `ANDROID_KEY_PASSWORD` | Password for that specific key (`keytool --keypass`) |

If any of these is missing, the release workflow **stops before building** and explains
which secret is absent — it will never publish an unsigned APK as an official release.

### Creating a release keystore (only if you don't have one)

```bash
keytool -genkeypair -v -keystore release.keystore -alias openra-android \
  -keyalg RSA -keysize 4096 -validity 10000
base64 -w0 release.keystore   # value for ANDROID_KEYSTORE_BASE64 (macOS: base64 -i release.keystore)
```

- **Back the keystore up safely (password manager / offline storage) and keep the
  passwords with it.** An APK can only be updated over Android if the new build is signed
  with the same key, so **never lose or replace** a keystore that users already have
  installed builds from.
- The workflow decodes the keystore into the runner's temp directory with `umask 077`,
  never logs passwords (GitHub masks secret values), and deletes the temporary files
  after signing — even when a step fails.
- Signing uses `zipalign` + `apksigner` (the same mechanism as `thirdparty/deploy-android.sh`)
  and every release APK is verified with `apksigner verify` before it is uploaded.
