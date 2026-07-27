# OwnAudio iOS Demo

The iOS counterpart of the Android sample: four synchronized tracks through `AudioMixer`, master
effects (30-band EQ, compressor, dynamic amp), a vocal chain (compressor → delay → reverb), live
peak meters and a tempo-accuracy readout on stop.

The UI is built in code — no storyboard — so the whole demo is `MainViewController.cs`.

## Building

> **`-p:BuildMobile=true` is required**, exactly as for the Android sample: without it the engine
> projects only expose their desktop `net10.0` target framework and the app links the wrong
> assemblies. See the [repository README](../../../README.md#installation).

```bash
# Simulator (Apple Silicon)
dotnet build OwnAudio/Examples/Ownaudio.Example.iOS/Ownaudio.Example.iOS.csproj \
    -c Debug -p:BuildMobile=true -p:RuntimeIdentifier=iossimulator-arm64

# Device
dotnet build OwnAudio/Examples/Ownaudio.Example.iOS/Ownaudio.Example.iOS.csproj \
    -c Release -p:BuildMobile=true -p:RuntimeIdentifier=ios-arm64
```

A `RuntimeIdentifier` has to be passed: the static engine library is picked per RID, and there is
no single `.a` that covers device and simulator at once.

If your Xcode is newer than the one the installed .NET iOS workload pins, the build stops with
`E0191`. Add `-p:ValidateXcodeVersion=false` to get past it, or install the matching Xcode.

## Running in the simulator

```bash
xcrun simctl boot "iPhone 17 Pro"
xcrun simctl install booted bin/Debug/net10.0-ios/iossimulator-arm64/Ownaudio.Example.iOS.app
xcrun simctl launch --console-pty booted com.ownaudio.iostest
```

## What the sample shows that Android does not

**iOS will not make a sound until something claims an audio session.** The engine deliberately
does not do this for you — the category is an application-level decision. `_configureAudioSession`
does it here:

```csharp
AVAudioSession session = AVAudioSession.SharedInstance();
session.SetCategory(AVAudioSessionCategory.Playback);
session.SetPreferredSampleRate(48000, out _);
session.SetPreferredIOBufferDuration(512.0 / 48000.0, out _);
session.SetActive(true, out _);
```

Without it the stream opens and runs, but the audio goes nowhere and the ringer switch mutes the
app. `Info.plist` also declares the `audio` background mode so playback survives backgrounding.

## Notes

Playback is verified on the simulator: four tracks in sync, peaks moving, zero underruns.

Two things had to change in the library before iOS worked at all, both worth knowing about if you
touch the interop layer:

- The P/Invoke library name is `__Internal` on iOS, not `ownaudio_ffi`. The engine is a static
  `.a` linked into the app, and only `__Internal` makes the AOT compiler emit direct references to
  the symbols. Going through a `DllImportResolver` and `dlsym` does not work — nothing references
  the Rust symbols statically, so `-dead_strip` removes every one of them during the native link.
- The audio callbacks are static `[UnmanagedCallersOnly]` entry points that recover their instance
  from the engine's `user_data` pointer. A delegate would need a native-to-managed thunk built at
  run time, and iOS is AOT-only — that fails with "Attempting to JIT compile method".
