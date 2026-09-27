# OwnAudio Multitrack Player

A small Avalonia multitrack player on top of OwnAudioSharp 4.x. It is a trimmed-down take on how a
full backing-track player drives the library: one mixer, every track on the master clock, the
transport talking to the mixer, and the master bus carrying a plugin and the Smart Master.

## What it does

- **Tracks** — load WAV, MP3 or FLAC files; each track has a volume fader, mute, solo, a stereo
  level meter and a remove button. A track added while playing joins in at the current position.
- **Transport** — Play, Pause, Stop and a seek slider. The song stops by itself when every track
  has run out.
- **Master** — master volume and meter, global tempo (the library's allowed range) and pitch
  (±12 semitones) for every track, and a Reset button.
- **Plugin** — one effect plugin on the master bus: VST3 on every platform, AudioUnit on macOS as
  well. Load, bypass, open its editor, remove.
- **Smart Master** — on/off, speaker preset, and saving or loading its settings as
  `*.smartmaster.json`.
- **DSP load** — the status bar shows how much of each render block the engine uses. A peak at
  100 % means a block was late, which is a dropout.

## Layout

```
Services/AudioService.cs                   engine + mixer lifecycle, fault and end-of-playback events
ViewModels/TrackViewModel.cs               one track: FileSource, fader, mute, solo, meters
ViewModels/MainWindowViewModel.cs          startup, events, shutdown order
ViewModels/MainWindowViewModel.Transport.cs  play / pause / stop / seek, position, DSP load
ViewModels/MainWindowViewModel.Tracks.cs   add / remove, solo, level meters
ViewModels/MainWindowViewModel.Master.cs   master volume, tempo, pitch
ViewModels/MainWindowViewModel.Plugin.cs   master plugin (AudioPluginBrowser, VST3PluginHost)
ViewModels/MainWindowViewModel.SmartMaster.cs  Smart Master
MainWindow.axaml(.cs)                      the UI, file pickers, seek slider handling
```

## How the audio side works

**Startup.** `OwnaudioNet.InitializeAsync` + `OwnaudioNet.Start`, then one `AudioMixer` on
`OwnaudioNet.Engine.UnderlyingEngine`, started right away. The UI never calls the engine directly.

**Play from a stopped state.** Every track is registered with `AddSourcePrepared`, which also puts
it on the mixer's master clock. The mixer is paused while `StartPreparedSources(position)` starts
them, then `Start` releases them together, so all tracks enter on the same block.

```csharp
mixer.Seek(start);
foreach (var t in tracks) mixer.AddSourcePrepared(t.Source);
mixer.Pause();
mixer.StartPreparedSources(start);
mixer.Start();
```

**Pause / resume.** `mixer.Pause()` plus `Pause()` on each source; everything stays on the mixer.
Resume is `Play()` on each source and `mixer.Start()`.

**Stop.** The sources stop, `mixer.Seek(0)`, and they come off the mixer with `RemoveSource`.
They stay loaded on their tracks for the next Play.

**Seek.** Always `mixer.Seek(seconds)`. It moves the master clock together with every native
track. Moving one source or the clock by hand does not work: in native mode the clock follows the
tracks and is overwritten on the next tick.

**Position.** `mixer.MasterClock.CurrentTimestamp`, in project time, so a tempo change is already
in it. The duration shown is the longest file divided by the tempo.

**Tempo and pitch.** `SetTempoSmooth` / `SetPitchSmooth` on each `FileSource`. They glide over
without flushing buffers, so they can follow the slider on every step.

**Solo and mute.** One solo anywhere sets `SoloActive` on every track, and each track works out its
own level (`Level`) from volume, mute and solo in one place.

**End of song and device loss.** `AudioMixer.PlaybackEnded` stops the transport when every source
ran out; `AudioMixer.StreamFaulted` stops it when the output device goes away.

**Master chain.** The plugin is loaded with `VST3PluginHost.CreateAsync`, checked with `IsEffect`,
prepared with `InitializeAudioAsync`, and its processor goes on with `AddMasterEffect`. The on/off
switch is a bypass (`Enabled`), not a removal, so the chain latency never changes under the mix.
The Smart Master is created the first time it is switched on and always stays last in the chain.

**Shutdown.** The mixer is disposed first (it disposes the master effects it holds), then the
engine, then the plugin host, and the track sources last.

## Build and run

```bash
dotnet run --project OwnAudio/Examples/Ownaudio.Example.MultitrackPlayer/Ownaudio.Example.MultitrackPlayer.csproj
```

Requires the .NET 10 SDK. The native engine comes prebuilt from `OwnAudioEngine/OwnAudioRust/runtimes`.

## Not included

Room measurement and microphone monitoring for the Smart Master are left out of this example.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/docs/assets/tools/rider.svg) | **JetBrains** — Rider |
