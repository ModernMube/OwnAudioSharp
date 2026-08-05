# OwnAudioSharp.Mt3

MT3 music transcription as an `INoteTranscriber` for OwnAudioSharp.

Where the built-in BasicPitch transcriber hears *pitches*, MT3 hears *instruments*: it is a
sequence-to-sequence transformer that emits MIDI-like events with a program number attached, so
a bass line and a piano voicing stay separate instead of collapsing into one smear on the
chromagram. For chord detection that separation is usually worth more than the extra pitch
accuracy.

This is a separate package on purpose. ONNX Runtime is linked into the native library, which
costs about 26 MB per platform — no reason to put that in everyone's `OwnAudioSharp` download.

## Install

```bash
dotnet add package OwnAudioSharp.Mt3
```

Desktop only: **win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64**.

Intel Macs are not covered — the `ort` crate ships no prebuilt ONNX Runtime for
`x86_64-apple-darwin`, and building the runtime from source in CI is not worth it for a
platform Apple stopped selling in 2023. Everything else in OwnAudioSharp still runs there.

## Getting the model files

The weights are not in the package — they are about 290 MB. **Download the four files below
into one folder** and give that folder's path to `Mt3ModelPaths.FromDirectory()`. Keep the
names as they are; that is what the helper looks for.

| File | Size |
| --- | --- |
| [`mt3_encoder.onnx`](https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/mt3-onnx/mt3_encoder.onnx?download=true) | 92 MB |
| [`mt3_decoder_init.onnx`](https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/mt3-onnx/mt3_decoder_init.onnx?download=true) | 101 MB |
| [`mt3_decoder_step.onnx`](https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/mt3-onnx/mt3_decoder_step.onnx?download=true) | 89 MB |
| [`vocab.json`](https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/mt3-onnx/vocab.json?download=true) | < 1 KB |

```bash
mkdir -p ~/models/mt3 && cd ~/models/mt3

BASE=https://huggingface.co/ModernMube/HTDemucs_onnx/resolve/main/mt3-onnx
for f in mt3_encoder.onnx mt3_decoder_init.onnx mt3_decoder_step.onnx vocab.json; do
  curl -L -o "$f" "$BASE/$f?download=true"
done
```

All four have to come from the same export run. Mixing them produces confident nonsense
rather than an error, so don't assemble a folder from different sources.

Exporting your own from a different MT3-family checkpoint works too — the scripts are in
`tools/mt3/` in the repository. Mind the checkpoint's licence if you do: YourMT3, the usual
source of PyTorch MT3 weights, is GPL-3.0. This package contains none of its code and only
reads exported weights at runtime, but what you may ship with those weights is between you
and that licence.

## Use

```csharp
// The folder you downloaded the four files into
using var transcriber = new Mt3Transcriber(Mt3ModelPaths.FromDirectory("/Users/me/models/mt3"));

var (chords, key, bpm) = ChordDetect.DetectFromFile("song.wav", transcriber);
```

Or straight to notes:

```csharp
var notes = transcriber.Transcribe(samples, transcriber.PreferredSampleRate,
    p => Console.Write($"\r{p:P0}"));

foreach (var n in notes.Where(n => !n.IsDrum))
    Console.WriteLine($"{n.StartTime:F2}s pitch {n.Pitch} program {n.Program}");
```

## Expect it to be slow

MT3 decodes autoregressively — up to a thousand tokens per two seconds of audio. Even with
the KV cache the native side keeps, a full song is minutes of CPU work, not seconds. Run it
offline, off the UI thread, and use the progress callback. If you need something interactive,
`BasicPitchTranscriber` is still there and still the default.

## What runs where

Everything below the C# surface is Rust: the ONNX sessions, the greedy decode loop, the MT3
event codec and the note state machine all live in `ownaudio_mt3_ffi`. The managed side only
marshals a float buffer in and a note array out.

## Building the native library locally

The published package carries prebuilt natives, but they are **not** committed to the
repository — 26 MB per platform would bloat the history on every rebuild. Working from a
checkout, build the one for your machine and drop it where the loader looks:

```bash
cd OwnAudio/Source/Mt3/rust-mt3
cargo build --release -p ownaudio-mt3-ffi

RID=osx-arm64   # or win-x64, linux-x64, linux-arm64, win-arm64
mkdir -p ../runtimes/$RID/native
cp target/release/libownaudio_mt3_ffi.dylib ../runtimes/$RID/native/
```

`runtimes/` is gitignored, so this stays local. The token codec and the note state machine
build without ONNX Runtime at all — `cargo test -p ownaudio-mt3-core --no-default-features`
runs their tests in a couple of seconds and needs no native download.
