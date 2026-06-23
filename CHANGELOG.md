# Changelog

All notable changes to **OwnAudioSharp** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.1.7] - 2026-06-23

### Fixed
- **FileSource `Position` double-tempo regression** — `Position` advanced by
  `framesRead * tempo / sampleRate` instead of wall-clock time. SoundTouch
  already applies the tempo conversion, so the extra factor double-counted it.
  `Position` now tracks wall-clock time in both standalone and synchronized
  read paths.
- `OutputDeviceId` initialization on startup.
- `AudioDeviceInfo.DefaultSampleRate` reporting.
- Additional FileSource synchronization drift fixes.

## [3.1.6] - 2026-06-23

### Changed
- Chord detection rewritten to be allocation-free on the hot path and fully
  AOT-compatible.

## [3.1.5] - 2026-06-22

### Changed
- `BpmDetect` rewritten around normalised autocorrelation with a perceptual
  tempo prior for more accurate, octave-error-resistant tempo estimates.
- `AudioMixer` now supports dynamic (re)configuration of its output format and
  source set, with a more robust shutdown path.

## [3.1.3] - 2026-06-14

### Added
- Adaptive synchronization tolerance in the MasterClock sync engine.
- `SyncDiagnostics` runtime health-check property on `FileSource`.

### Changed
- Tighter default sync tolerances (Green Zone 5 ms, Yellow Zone 25 ms).

[3.1.7]: https://github.com/ModernMube/OwnAudioSharp/releases/tag/V3.1.7
[3.1.6]: https://github.com/ModernMube/OwnAudioSharp/compare/V3.1.5...V3.1.6
[3.1.5]: https://github.com/ModernMube/OwnAudioSharp/compare/V3.1.3...V3.1.5
[3.1.3]: https://github.com/ModernMube/OwnAudioSharp/releases/tag/V3.1.3
