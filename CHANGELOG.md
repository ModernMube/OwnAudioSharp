# Changelog

All notable changes to OwnAudioSharp are documented here.
Releases before 4.0.0 are documented on the [GitHub Releases](https://github.com/ModernMube/OwnAudioSharp/releases) page.

## 4.0.5-preview.1 — 2026-08-20

### Added

- **The buffer size the driver granted is readable.** `FramesPerBuffer` echoed the size that was
  requested even when the driver rounded it, while its own documentation promised "what the device
  actually gave us" — so the contract said one thing and the code did another, and a host had to
  infer the real size from callback drain sizes. cpal treats a fixed buffer size as a request and
  never reports back what was settled on, and `FramesPerBuffer` is read at init (the engine wrapper
  and the mixer size their buffers from it), so it has to stay the request. The callback length —
  the only ground truth — is now recorded on both directions and surfaced as
  `AudioEngineWrapper.OutputCallbackFrames` and `InputCallbackFrames`, 0 until audio has run. Two
  properties rather than one because outside ASIO the render and capture sides need not agree, and
  they report the most recent callback rather than a fixed contract: ASIO holds one size, WASAPI
  shared and CoreAudio may vary the block. No extra work on the audio thread — the frame count was
  already being computed for the load counters.

- **The output render ring depth is configurable.** It was a fixed 0.1 s in the FFI layer, and
  because the producer keeps the ring topped up, that depth was paid as output latency on every
  buffer no matter how small the device buffer was — for live monitoring it was the single biggest
  term. `AudioConfig.OutputRingMilliseconds` sets it, defaulting to 100 so nothing moves for
  existing users. Underneath it is a new additive export, `ownaudio_v1_open_output_stream_ex`,
  taking the depth in frames; the old entry point delegates to it with 0 = default, so the ABI
  version stays put and C consumers are untouched. A request shallower than three device buffers
  is pulled back up — a ring under one callback period underruns every time — and
  `AudioEngineWrapper.OutputRingFrames` reports the depth that was actually used, so a clamped
  request is visible rather than silent. That floor also fixes a latent case: an 8192-frame device
  buffer used to get a 4800-frame ring and partial-read on every callback.

- **Matchering can drive a live chain instead of rendering a file.** The whole matching pipeline
  was only reachable as an offline renderer — file in, 24-bit wav out — with the interesting part,
  the settings it computed on the way, private. Three additions expose it without touching the
  offline path: `AnalyzeAudioBuffer(float[], int, int)` runs the same segmented analysis on samples
  already in memory (no `FileSource`, no static lock, no decode round trip — `AnalyzeAudioFile` now
  delegates to it, so the two cannot drift apart), `CalculateProfile` returns a `MatcheringProfile`
  carrying the 30 band gains, Q factors, compressor and AGC settings, and `GetPresetTargetSpectrum`
  builds a preset's target spectrum entirely in memory instead of writing two temp wavs, cached per
  system.
- `CalculateProfile` defaults its `fixedQ` to `AudioAnalyzer.NativeBandQ` (4.318474), the constant Q
  of the native 30-band equalizer, because the engine has no per-band Q parameter — solving the
  filter bank against the optimized per-band Qs would model a bank that is not the one making the
  sound. `fixedQ: 0` still gets the per-band Qs, which is what the offline render uses.
- `cutOnly` (on by default) drops the whole curve so no band is boosted, for chains with no gain
  stage in front of the EQ; the AGC behind it takes the level back. The shift stops short of
  pushing the deepest cut into the ±9 dB clamp, and `CutOnlyShiftDb` reports what was applied.

### Fixed

- **The equalizer's "soft" limiter was a step, not a knee.** Above 0.95 it computed
  `0.95 * tanh(x)` instead of scaling into the headroom, so a sample crossing the threshold
  did not saturate — it dropped from 0.95 to 0.70, a 2.6 dB notch cut straight out of the
  waveform, and the output could never reach past 0.95 however hard it was driven. It now
  squeezes everything past the threshold into what is left below unity, joining the linear
  part with a continuous knee (same value and same slope at 0.95) and asymptoting at 1.0.
  **This changes how the EQ sounds** on anything it boosts past 0.95; below that nothing
  moves. Both the managed `EqualizerEffect` and the native port carried it, as did the f64
  reference the native port is tested against — which is why comparing the two to each other
  never caught it. Both sides now have a test that measures the knee itself.
- That step is also what made the native reference comparison platform-dependent: one sample
  landing on the far side of the discontinuity is worth −54.3 dB of RMS error against a
  −60 dB budget, so 1 ulp of libm difference between glibc and Apple's decided whether the
  test passed. It was green on aarch64 and red on x86_64 for the same commit.
- **The test suite read the developer's own SmartMaster presets.** `SmartMasterEffect` loads
  presets from `~/.ownaudio/smartmasterpresets` and only falls back to the factory curves when
  that fails, so any test calling `LoadSpeakerPreset` measured whatever happened to be saved on
  the machine. A Club preset saved months earlier held +4 dB at 20 Hz where the factory curve
  has +1.5, which kept a stale expectation green locally and red on CI — and it masked a second
  failure entirely. Presets now take an internal directory override (the public surface is
  unchanged) and every test run gets its own empty folder.
- The SmartMaster long-duration stability test compared the very first block against the tenth.
  The chain starts from zeroed filter states with the compressor not yet engaged, so those
  blocks are it settling in — it converges monotonically and is within 0.05 dB by the tenth.
  Reading the transient as drift, the test measured 1.47 dB against a 0.5 dB budget. Both
  readings are now taken after the chain has arrived, which is what "maintains stability" means.
- The `sine_wave_output_smoke` integration test is `#[ignore]`d: it opens a real output device,
  which a headless CI runner does not have, so it failed the whole Rust job. Run it with
  `--ignored` on a machine with a sound card.
- **The phaser barely did anything.** Its all-pass coefficient is built correctly —
  `a = (t−1)/(t+1)` for `t = tan(πf/fs)`, which comes out negative anywhere well under Nyquist —
  but the difference equation applied `−a`, and flipping that sign moves the stage's corner from
  the nominal frequency to about 23 kHz. Six cascaded stages shifted roughly 3° across the audio
  band instead of 540°, so the dry signal and the phase-shifted copy never cancelled and there was
  no notch to sweep: a steady tone moved 0.007 dB where a working phaser moves several dB. Fixed
  on both the managed and the native side, which carried the same equation. **This changes how
  every phaser preset sounds** — the effect is audible now, so settings dialled in against the
  broken version will be far stronger than before.
- **The delay crashed on the real-time thread for most delay times.** A time that is not a whole
  number of samples — 150 ms at 48 kHz is 7200.0005 — leaves the read position a hair below zero,
  and the negative wrap rounds it straight back up to exactly the buffer length in f32. The
  interpolation's second index was clamped, the first was not, so it indexed one past the end the
  moment the write pointer caught up with the delay length. Both the managed and the native delay
  carried it, the native one on all three code paths.
- **The native flanger had the same overrun**, and hit it far more easily: its LFO makes the delay
  fractional on every single sample. The managed flanger uses an integer delay and was never
  affected.
- **`DynamicAmpEffect.Reset()` did not restore the state the constructor set.** The preset
  constructor primes the level estimate at −20 dBFS so the AGC does not slam the gain on the first
  block; reset zeroed it instead, so an effect that had been reset behaved differently from a fresh
  one for about a second.
- **SmartMaster measured the low end against an absolute level.** The subwoofer step compared a
  broadband RMS to a fixed −40 dBFS, so the verdict moved with playback volume, mic gain and
  distance, and room rumble could carry it on its own. It now falls out of the pink noise pass in
  three tiers: a capture-level gate so a dead mic cannot read as a healthy system, a weak-low case
  left to the EQ, and a subharmonic case limited to systems that carry 40–80 Hz but run out under
  it — the synth is an octave divider, so on a box already down at 60 Hz it would only write
  energy further down. `SubharmonicMix` ramps 0.08–0.18 with the deficit instead of snapping to a
  fixed 0.15. The separate 2 second low-frequency pass is gone, which also makes the measurement
  shorter.
- **The spectrum analyzer read the wrong frequencies.** The captured audio went into the FFT
  interleaved, which drags every band down an octave, and each band averaged over its bins, which
  tilted the whole readout by 1.5 dB per octave. Both skewed the EQ correction, not just the low
  end verdict. The per-band deviation is now taken against the same noise run through the same
  analyzer, so the window's low-frequency smearing cancels too and a perfect system reads 0 dB at
  every band.

### Changed

- The `HiFi` speaker preset drops its subharmonic mix from 0.12 to 0.06 — a bookshelf pair may not
  reach where the octave divider writes.

### Documentation

- `SmartMasterConfig` in the effects API page listed a `SubharmonicFreqRange` field that does not
  exist; replaced with the real `SubharmonicLowLevel` / `SubharmonicHighLevel` band levels.
- **The FFmpeg fallback described everywhere was removed in 4.0 and never came back.** There is no
  FFmpeg code left in the product — no library loading, no process invocation, in either the
  managed or the native layer — but the package READMEs still told users to `brew install ffmpeg`
  for "any format the native backend cannot handle", and the reference page documented an
  `FFmpegConfig.CustomLibraryPath` / `IsAvailable` API that does not exist. Corrected in
  `README.Desktop.md`, `README.Basic.md`, `Ownaudio.Core/README.md`, `index.html`,
  `api-reference.html` and `troubleshooting.html`; the misleading comments in `RustNativeDecoder`
  and in four places on the Rust side, all claiming FFmpeg decoding lived in the other layer, are
  gone too. A format outside the Symphonia list now honestly fails.
- **The Matchering page called every entry point static and named four `PlaybackSystem` values that
  do not exist.** `AudioAnalyzer`'s methods are instance methods, the namespace is
  `OwnaudioNET.Features.Matchering` rather than `OwnaudioNET.Features`, and the enum has ten members
  (`ConcertPA`, `ClubPA`, `HiFiSpeakers`, `StudioMonitors`, `Headphones`, `Earbuds`, `CarStereo`,
  `Television`, `RadioBroadcast`, `Smartphone`) — not `Club` / `Car` / `Studio` / `HomeTheater`. None
  of the samples in `api-advanced.html` compiled as printed.
- The desktop and mobile package READMEs advertised vocal removal as a feature of the package and
  opened their quick start with `using OwnaudioNET.Features.Vocalremover;` — a namespace neither
  package contains, so the sample did not compile. Vocal separation now points at the separate
  `OwnVocalRemover` add-on, as the site already did; the mobile README says outright that it is
  desktop only.

## 4.0.4-preview.2 — 2026-08-09

### Added

- **Effect chain tap.** `mixer.CreateEffectTap(sourceId)` and `mixer.CreateMasterEffectTap()` hand
  back an `EffectTap` carrying the same block of audio twice — as the chain received it and as the
  chain left it. The Rust mixer mirrors each rendered block into a pair of lock-free rings on both
  sides of the chain; a slow drain drops whole pre/post pairs rather than one side alone, so the
  two streams can never slide apart. The tap sits ahead of gain, pan and delay compensation, and
  holds the dry side back by the running effects' latency, so a look-ahead limiter or a hosted
  VST3 does not smear the comparison.
- `EffectSpectrumAnalyzer` — a live before/after spectrum over a tap. Hann-windowed, sine
  calibrated (a full-scale tone reads 0 dBFS), with the magnitudes exposed as dBFS spans and the
  bin centres in Hz.
- `SourceWithEffects.ActiveEffectLatencySamples` — the latency of the effects actually running,
  as opposed to `EffectLatencySamples`, which counts bypassed ones too so PDC alignment stays put
  across a bypass toggle.

### Documentation

- The site claimed 17 built-in DSP effects on the pages that describe the C# API, but only 15 of
  the Rust engine's 17 are surfaced there — `Gate` and `PitchShift` have no managed wrapper, and
  the effect table on the same page always listed 15. Corrected everywhere the count refers to the
  managed API; the architecture table still says 17 for `ownaudio-core`, now with the split spelled
  out.

## 4.0.4-preview.1 — 2026-08-09

### Added

- SmartMaster is laid out like a dbx DriveRack style PA processor now: everything that shapes
  the program runs before the crossover, everything that protects the drivers runs after it,
  per band. New stages are a 24 dB/oct subsonic high-pass, an 8-band sweepable input parametric
  EQ, and an output section with per-band trim, alignment, polarity and driver-protection
  limiters behind an explicit `CrossoverEnabled` switch. The native parameter map grew to 0–91
  with full managed mirroring.
- `CompressorEffect.KneeWidth` — the soft-knee width, what dbx calls OverEasy.
- `SmartMasterEffect.SanitizedSampleCount` — how many NaN/Inf samples the chain has had to zero
  out, since `Process` can no longer log from the audio thread.

### Fixed

- **30-band EQ transfer.** The band Q ran 0.6–1.4 on 1/3-octave centres, so every bell was about
  1.4 octaves wide and a drawn curve summed to roughly 5× its written values. It is constant-Q
  (4.318) now, and the built-in preset curves were redrawn against the corrected transfer.
- **Limiter gain law.** The required gain divided by the excess twice (`thr²/peak²`), so a peak
  6 dB over threshold was pulled 12 dB down and the output ducked 6 dB *under* the threshold.
- **Limiter look-ahead.** The ring buffer was exactly the look-ahead long, so the read index
  landed back on the write index: no delay at all, while `LatencySamples` reported one — and
  reported it in interleaved samples, double the real figure on stereo. The limiter is
  frame-based and stereo-linked now, with a window-minimum deque replacing a per-sample linear
  scan, and `LatencySamples` is in frames.
- **Subharmonic synth.** It band-passed 40–120 Hz into a waveshaper, which makes harmonics
  rather than subharmonics, and crossfaded the result over the program — at mix 0.4 the whole
  mix dropped 4.4 dB. Replaced with a two-band octave divider (48–72 → 24–36 Hz, 72–112 →
  36–56 Hz) added in parallel.
- **Compressor detection.** A single envelope walked the interleaved samples, so stereo channels
  drifted apart and the configured attack/release were effectively halved. Detection is per
  frame off the loudest channel.
- **SmartMaster compressor threshold** was fed the config's linear 0–1 value straight into a
  dB-facing property, pinning it at 0 dBFS in managed mode while the native side converted it
  properly.
- **Matchering spectrum measurement.** Band readings averaged bin magnitudes and normalised by
  the window's coherent gain — correct for an isolated sine, wrong for broadband material, and
  scaling as `1/sqrt(fftSize)`, so a 44.1k source and a 48k reference came out about 3 dB apart
  before comparison. Band power is now normalised by the window's noise power, Flat-Top gave way
  to Hann, band edges are geometric 1/3-octave ones widened where the transform cannot resolve
  them (the 20 Hz band spanned one bin at 48k and none at 44.1k), the FFT size comes from a
  fixed 0.35 s window, and outlier statistics moved to dB.
- **Matchering EQ curve.** The per-band delta carried the broadband level difference with it, so
  a source 6 dB down got +6 dB on every band — a gain change dressed as EQ, which the AGC then
  corrected a second time. The offset is removed and handled as gain, the per-band limit is
  ±9 dB rather than ±18, and the wanted curve is deconvolved against the filter bank's own
  response so the realised curve lands within 0.11–0.35 dB of the request instead of 1.31–2.41.
- **Matchering chain order.** The compressor ran before the EQ, controlling the uncorrected
  signal while the EQ boosted bands underneath it, and its threshold was derived from the
  original RMS while the audio was pushed down by up to 12 dB of pre-gain.
- **Segment position weight** read `StartTime / (StartTime + Duration)`, which is not a position
  in the track — past about 40 s every segment scored above 0.8.
- 16 and 24 bit WAV writes round instead of truncating toward zero, and carry highpass TPDF
  dither.

### Changed

- SmartMaster's AutoEQ smooths the measured deviation over three bands, aims at a house target
  curve and applies 65 % of it, with short boost caps — a room is not minimum phase, and filling
  a null costs headroom without filling it.
- Matchering analysis caches its window and FFT context per sample rate, treats segments as
  views over the shared buffer instead of copies, and runs across cores.
- The managed SmartMaster chain no longer calls `Parallel.Invoke` or logs from `Process`, and
  every IIR state is denormal-flushed.

### Breaking

- `SmartMasterConfig.SubharmonicFreqRange` and `ParametricEQGains` are removed. Neither did
  anything; the parametric stage is `ParametricEQ` (8 `ParametricBand` entries) now, and
  `ParametricBands` is 8.
- Existing 30-band EQ presets need retuning: with constant Q the same gain values produce about
  a third of the boost they used to.
- `LimiterEffect` genuinely delays by its look-ahead now. Offline callers should flush and shift
  by `LatencySamples`, as `Matchering` does.

## 4.0.3 — 2026-08-07

### API

- `ChordDetect.DetectFromFile` and `DetectFromFiles` now take an optional
  `Action<double>? progress` callback that reports 0..1 over the whole job — decoding, note
  transcription, tempo detection and chord analysis, weighted by phase. With the MT3
  transcriber an analysis is minutes of work on a full song and there was no way to show that
  to the user. The parameter is last on both overloads, so existing calls are unaffected, and
  without a callback the transcription percentage still goes to the log as before.

## 4.0.2 — 2026-08-04

### Fixed

- BPM detection was inaccurate in several ways at once. The spectral flux was computed in the
  power domain instead of magnitude, which gave an accented beat a squared weight and locked the
  autocorrelation onto the accent period — tempi from about 112 BPM up came back halved on
  ordinary kick/snare material. On top of that the log-Gaussian tempo prior is symmetric, so above
  `PREFERRED_BPM * sqrt(2)` (169.7 BPM with the old centre of 120) the half tempo won on prior
  weight alone and everything fast was reported at half speed.
- The estimate only ever looked at the last 8 seconds of input. Since `ChordDetect` feeds a whole
  track and asks once at the end, the outro decided the tempo: a fade, a reverb tail or trailing
  silence produced a confident wrong answer (silence came back as 190 BPM). Every analysis window
  now folds into an accumulated autocorrelation, so the whole track has a say.
- No confidence check at all, and the final `clamp` made failures look like plausible readings —
  white noise reported 152 BPM, digital silence 190. Below a minimum peak correlation the detector
  now returns 0, and the "enough data" threshold went from 1.7 to 4 seconds, which was also the
  point below which slow tempi could not be represented at all and came back doubled.
- Onset frames are twice as dense (64-sample hop instead of 128) for finer lag resolution near
  `MAX_BPM`, and a candidate is compared against its twice-as-fast reading, so a pattern accented
  on every other beat no longer reads as half tempo. `get_bpm` is now allocation-free as well.

## 4.0.1 — 2026-08-03

### Diagnostics

- Every `catch` block in the audio package now reports through `Ownaudio.Core`'s `Log` instead of
  discarding the exception, and the silent failure returns beside them (`-1` / `false` / `null` on
  the device and stream paths) report the same way.
- Initialization, teardown and parameter changes are logged across the engine, mixer, sources,
  network sync and VST3 hosting.
- Real-time code stays off the logger. Audio-rate handlers count their failures and emit one line
  on the first hit plus a total on reset or dispose; control-rate and network loops report the
  first occurrence and every Nth after it, so a stuck fault stays visible without flooding.

### API

- `Log.LoggerLevel` now starts at `Disabled` instead of `Info`, so the library is silent unless
  asked otherwise.
- `OwnaudioNet.Initialize` and `InitializeAsync` take an optional `logLevel` argument that turns
  the console logger on. It sits last on every overload, so existing positional calls keep
  compiling; `Log.LoggerLevel` can still be changed at any time while the API is running.

## 4.0.0 — 2026-07-27

The whole audio engine was rewritten in **Rust**. Everything below the public C# API — device
I/O, decoding, resampling, mixing, effects and the real-time thread — is now native code reached
through a versioned C ABI. The managed platform engines (WASAPI / PulseAudio / CoreAudio written
in C#) are gone, and so is the managed mix thread: audio data never travels through the GC heap
any more.

The public C# API was kept as close to 3.x as possible, but this is a major version and there are
breaking changes — see [Breaking changes](#breaking-changes).

### Engine

- New Rust workspace: `ownaudio-core` (engine, mixer, decoders, resampler, effects, ring buffer,
  RT guards), `ownaudio-ffi` (the C ABI surface) and `ownaudio-soundtouch` (time-stretch / pitch).
- Native-only real-time path — the managed mix thread and `OnSamplesRead` pipeline were removed;
  the Rust mixer is the only mixing path.
- Host API selection: **WASAPI**, **ASIO**, **CoreAudio**, **ALSA** and **AAudio** (Android).
- Symphonia-backed streaming decoder with prefetch is the primary decoder; the managed decoders
  and managed DSP were removed and `AudioDecoderFactory` is native-only.
- Resampler switched from `SincFixedIn` to `FftFixedIn` for SIMD acceleration, and made
  allocation-free on the RT path.
- cpal audio callbacks and every FFI `destroy` function are panic-guarded, so a Rust panic can
  never unwind across the real-time or FFI boundary.
- ABI versioning (`ownaudio_v1_get_abi_version`) with two-sided struct-size parity tests, so a
  mismatched native binary fails loudly instead of corrupting memory.
- Library version is read from the assembly instead of a hardcoded constant.

### Mixing and sources

- Native per-track chain: volume, pan, tempo, pitch, effects and plugin delay compensation all run
  in Rust.
- Equal-power stereo pan for both sources and master.
- Native master volume and peak metering.
- Native per-track start offset (silence pre-roll plus content seek).
- `SampleSource`, `InputSource` and `FileSource` were ported fully to the native chain;
  `FileSource.ReadSamples` decodes synchronously and the managed source threads are gone.
- New `StreamingSource` with callback-driven native playback.
- Sample-accurate `play_all`, additive multitrack mixing, atomic parameter updates and
  anti-zipper gain ramps.
- Lock-free mixer command queue with a self-draining controller, so the retire queue can't back up.

### Effects

- All built-in effects ported to Rust with denormal protection: Equalizer, Compressor, Limiter,
  Gate, Delay, Reverb, Overdrive, Distortion, Chorus, Flanger, Phaser, Rotary, AutoGain,
  DynamicAmp, Enhancer and PitchShift.
- Stereo-linked, frame-based dynamics with RT-safe preallocation.
- Per-effect wet/dry ramping to remove zipper noise, block-rate EQ band-gain smoothing,
  look-ahead attack ramp on the limiter, and threshold hysteresis plus detector smoothing on
  the gate.
- Pitch shift reports a constant latency and aligns the dry path accordingly.
- **SmartMaster** runs as a native composite effect in the Rust chain.
- **VST3 plugins are hosted natively** inside the Rust effect chain; hosted plugins stay warm
  (soft bypass) so toggling them doesn't stall the stream.
- Default and preset parameters were retuned for real-world material.
- SoundTouch's WSOLA engine was ported to Rust; the managed SoundTouch implementation was removed.

### MIDI

- `OwnAudioSharp.Midi` is now Rust-backed, with native packaging and its own CI pipeline.
- Hot-plug port monitoring, hybrid clock timing and a capacity-aware parser.
- iOS support plus Android file and clock handling.
- FFI crossings are batched and the clock backlog handling was hardened.

### Features

- **Chord and key detection** rewritten to be musically informed, allocation-free on the hot path
  and AOT-safe.
- **BPM detection** moved to the Rust side over FFI.
- **ALAC** support, plus a fix for mono AAC/ALAC-in-M4A channel detection (#38).
- Hardware latency is exposed by the engine and compensated for in recordings.
- Mono capture channel adaptation and native master-output recording.

### Mobile

- `OwnAudioSharp.Mobile` ships **Android (arm, arm64, x64)** and **iOS (device + simulator)**
  natives, with the engine assemblies packed into the package.
- Device enumeration survives a missing JNI context on Android instead of throwing.
- An Android example app and a documented `BuildMobile` flag for building against the engine.

### Packaging and CI

- Multi-platform native builds in GitHub Actions: win-x64, win-arm64 (both with optional ASIO),
  linux-x64, linux-arm64, osx-x64, osx-arm64, android-arm/arm64/x64, ios-arm64 and the iOS
  simulator RIDs.
- Packages are packed from the real `OwnaudioNET*` projects and verified after packing, closing
  the regression where truncated, API-less packages could ship.
- Built natives are committed back into `runtimes/` so local development works without a Rust
  toolchain.
- `OwnAudioSharp.Basic` is a deliberately minimal audio in/out package — no ONNX, no AI features.

### Breaking changes

- **`VocalRemover` was removed** from the API along with its model asset.
- The managed platform engines, managed decoders, managed DSP and the managed SoundTouch
  implementation were removed. Code that depended on the managed mix path (`OnSamplesRead`-style
  interception) no longer receives audio.
- `MathNet.Numerics` was dropped from the full package in favour of the in-house `OwnAudioFft`;
  it remains a dependency of `OwnAudioSharp.Basic`.
- Target framework moved to **net10.0** (`net10.0-android` / `net10.0-ios` for the mobile package).
- Several device/channel properties are now backward-compatibility shims only and are documented
  as such.

### Fixes

- ASIO: one cpal device is shared between capture and playback for true duplex; device lists are
  served from a snapshot instead of fighting over the driver; engine-scoped enumeration, I32
  sample format and a 256-channel limit.
- Time-stretch stays transparent at unity, which removes the phaser artefact on tempo changes.
- Live tempo/pitch changes on file tracks no longer click, and stretch is only latched on real
  tempo/pitch use.
- `FileSource.Position` reports content time again and seeking is tempo-corrected.
- Master and track effect mirroring no longer floods the command queue.
- SmartMaster: EQ band count aligned, limiter release wired up, `ApplyConfiguration` added, and
  the subharmonic band-pass kernel rejects DC.
- Equalizer preset bypass and the `Receives` state guard were fixed.
