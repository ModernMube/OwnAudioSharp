# ChordDetect

Chord and key recognition for OwnAudioSharp. The module takes raw audio (or a
list of already-transcribed notes) and produces a timed chord progression, the
song's musical key, and its tempo. It also offers a real-time detector for
streaming note input.

Namespace root: `OwnaudioNET.Features.OwnChordDetect`

---

## Pipeline overview

```
audio file(s)
   │  AudioDecoderFactory  →  mono, 22050 Hz PCM
   ▼
Model.Predict  (note-transcription model, in Features/Extensions)
   ▼
NotesConverter  →  List<Note>   (pitch, start/end time, amplitude)
   ▼
┌───────────────────────────────────────────────┐
│ SongChordAnalyzer                              │
│   KeyDetector      → MusicalKey                │
│   sliding windows  → per-window ChordDetector  │
│   merge + filter   → List<TimedChord>          │
└───────────────────────────────────────────────┘
   ▼
(List<TimedChord>, MusicalKey, int bpm)
```

BPM is detected in parallel with `BpmDetect` (SoundTouch auto-correlation) and
is used to size the analysis window (see [Harmonic-rhythm window sizing](#harmonic-rhythm-window-sizing)).

---

## Public entry point — `ChordDetect`

`ChordDetect` is a static facade ([ChordDetect.cs](ChordDetect.cs)). Most callers
only need this class.

| Method | Purpose |
| --- | --- |
| `DetectFromFile(string audioFile, float intervalSecond = 1.0f)` | Decode, transcribe and analyze a single file. |
| `DetectFromFiles(IReadOnlyList<string> audioFiles, float intervalSecond = 1.0f)` | Mix several files into one mono stream (peak-normalized) and analyze the mix — useful for multi-track projects. |
| `DetectRealtime(List<Note> notes, DetectionMode mode = Optimized, int buffersize = 5)` | One step of streaming detection; returns the most stable chord and a stability score. |

All three offline calls return `(List<TimedChord>, MusicalKey, int bpm)`.

```csharp
var (chords, key, bpm) = ChordDetect.DetectFromFile("song.wav");

Console.WriteLine($"Key: {key}  Tempo: {bpm} BPM");
foreach (var c in chords)
    Console.WriteLine(c);   // e.g. "12.0s-14.5s: Am7 (0.83) [A, C, E, G]"
```

Notes:

- `intervalSecond` is only a **fallback** window size. When BPM detection
  succeeds (`bpm > 0`), the window is derived from the tempo instead and this
  argument is ignored.
- Decoding targets **22050 Hz mono** — the sample rate the transcription model
  expects. Do not change this without retuning the frequency limits in
  `NotesConvertOptions`.
- `DetectBpmFromSamples` falls back to **120 BPM** when detection fails (e.g.
  speech or non-rhythmic content).

---

## Data model

### `Note` (in `Features/Extensions`, [AudioReaderNote.cs](../Extensions/AudioReaderNote.cs))

The transcription output. Immutable, sorted by start time.

| Field | Meaning |
| --- | --- |
| `StartTime`, `EndTime` | seconds |
| `Pitch` | MIDI pitch 0–127 (`Pitch % 12` gives the pitch class) |
| `Amplitude` | 0.0–1.0 |
| `PitchBend` | optional |

### `TimedChord` ([Analysis/SongChordAnalyzer.cs](Analysis/SongChordAnalyzer.cs))

A chord placed on the timeline: `StartTime`, `EndTime`, `ChordName`,
`Confidence` (0–1), and `Notes` (the constituent note names).

### `ChordAnalysis` ([Analysis/ChordAnalysis.cs](Analysis/ChordAnalysis.cs))

The richer single-window result: `ChordName`, `Confidence`, `Explanation`
(human-readable), `NoteNames`, `IsAmbiguous`, `Alternatives`, `PitchClasses`,
and the raw `Chromagram`.

### `MusicalKey` ([Core/KeyDetector.cs](Core/KeyDetector.cs))

`KeyName`, `IsMajor`, `Sharps`, `Flats`, and `PreferredNoteNames` — the last of
which decides whether pitch classes are spelled with sharps or flats.

---

## Components

### `KeyDetector` — [Core/KeyDetector.cs](Core/KeyDetector.cs)

Detects the key with the **Krumhansl–Schmuckler** algorithm: it builds a
duration/amplitude-weighted 12-bin chromagram, then correlates (Pearson) it
against the 24 rotated major/minor key profiles and returns the best match.
Flat vs. sharp spelling comes from the `KeyDefinitions` table.

### `ChordTemplates` — [Core/ChordTemplates.cs](Core/ChordTemplates.cs)

Builds the chord dictionary matched against. Each chord is a 12-element template
where tones are weighted by harmonic importance (`ToneWeights`: root 1.0, third
0.85, fifth 0.65, extensions taper off) — grounded in Krumhansl–Kessler probe-tone
research. Two vocabularies:

- **Basic** — triads + 7ths (major, minor, dom7, maj7, m7).
- **Extended** — adds sus2/4, dim, aug, 6/m6, 9/11/13 families, altered and
  half-diminished chords.

`GetNoteName(pitchClass, key)` performs key-aware note spelling.

### `ChordDetector` — [Detectors/ChordDetector.cs](Detectors/ChordDetector.cs)

The matching engine. Four `DetectionMode`s:

| Mode | Behavior |
| --- | --- |
| `Basic` | triads + 7ths only |
| `Extended` | full vocabulary |
| `KeyAware` | full vocabulary + key-appropriate naming (used by the song analyzer) |
| `Optimized` | adds ambiguity analysis, alternatives, and real-time stability |

How a window is scored:

1. `ComputeChromagram` weights each pitch class by `Amplitude × overlap
   duration`, so sustained chord tones outweigh brief passing notes, then
   normalizes.
2. `RankChords` computes **cosine similarity** against every template and keeps
   the top candidates. The ranking score is cosine adjusted by two perceptual
   priors:
   - a **parsimony penalty** (`ComplexityPenaltyPerTone`) per chord tone beyond a
     triad, so a plain triad wins near-ties over spurious extended labels;
   - a **diatonic bonus** (`DiatonicBonus`) when every tone fits the active key's
     scale — a tie-breaker only.
   The **reported confidence is always the raw cosine similarity**, so the
   meaning of `confidenceThreshold` is independent of the priors.
3. In `Optimized` mode, candidates within `ambiguityThreshold` of the best are
   reported as ambiguous (names joined with `/`) plus an `Alternatives` list.

**Performance note:** the hot path is allocation-free. Templates are pre-computed
into an immutable `TemplateEntry[]` (caching inverse magnitude, tone count,
diatonic flag), and ranking uses a stack-allocated `Span<ScoredChord>` with no
LINQ or per-call `sqrt`. Keep it that way when editing `RankChords` /
`DetectChordAdvancedBase`.

The wrapper classes in [Detectors/ChordDetectorTypes.cs](Detectors/ChordDetectorTypes.cs)
(`BaseChordDetector`, `ExtendedChordDetector`, `OptimizedChordDetector`,
`KeyAwareChordDetector`) are thin legacy-compatibility constructors that preset a
mode.

### `SongChordAnalyzer` — [Analysis/SongChordAnalyzer.cs](Analysis/SongChordAnalyzer.cs)

Turns a full note list into a `List<TimedChord>`:

1. Sort notes, detect the key, and hand it to a `KeyAware` `ChordDetector`.
2. Slide a window (`_windowSize`, hop `_hopSize`) across the song. For each
   window, collect overlapping notes and run **progressive pruning**
   (`GetAndPruneNotes`): try all notes first, then repeatedly drop the
   shortest-duration note from the lowest-pitch group until a chord is found or
   only 3 notes remain. This strips bass runs and melodic ornaments that would
   otherwise mask the chord.
3. `MergeAdjacentChords` fuses consecutive identical labels (duration-weighted
   confidence) and drops anything shorter than `minimumChordDuration`.

The window-note and pruning buffers (`_windowNotes`, `_workingSet`) are reused
across windows to avoid per-window allocations.

Use `AnalyzeSongInKey(notes, key)` to skip auto-detection and force a key.

#### Harmonic-rhythm window sizing

When `bpm > 0` the window adapts to the tempo, mirroring how faster music tends
to hold chords over more beats:

| Tempo | Window |
| --- | --- |
| BPM < 100 | quarter note (`60/bpm` s) |
| 100 ≤ BPM ≤ 150 | half note (`120/bpm` s) |
| BPM > 150 | whole note (`240/bpm` s) |

The hop is always half the window (50% overlap).

### `RealTimeChordDetector` — [Detectors/RealTimeChordDetector.cs](Detectors/RealTimeChordDetector.cs)

A thin wrapper over an `Optimized` `ChordDetector` for streaming input. Each call
to `ProcessNotes` pushes the detected chord into a rolling buffer of size
`bufferSize` and returns the most frequent (most stable) chord along with a
stability score = `occurrences / bufferSize`. Low-confidence frames are enqueued
as `null` so they still count against the stability denominator.

```csharp
var rt = new RealTimeChordDetector(bufferSize: 5);
var (chord, stability) = rt.ProcessNotes(latestNotes);
if (stability > 0.6f) { /* accept */ }
```

---

## Tuning cheat-sheet

| Knob | Where | Effect |
| --- | --- | --- |
| `confidenceThreshold` | `ChordDetector` ctor | Minimum cosine to accept a chord; below it → `"Unknown"`. |
| `ambiguityThreshold` | `ChordDetector` ctor (Optimized) | How close rivals must be to be reported as ambiguous. |
| `ComplexityPenaltyPerTone` | `ChordDetector` const | Bias toward simpler chords. |
| `DiatonicBonus` | `ChordDetector` const | Tie-break toward in-key chords. |
| `minimumChordDuration` | `SongChordAnalyzer` ctor | Drops fleeting chord labels. |
| `OnsetThreshold` / `FrameThreshold` / `MinNoteLength` | `NotesConvertOptions` in `ChordDetect` | Transcription sensitivity feeding the whole pipeline. |
| `bufferSize` | real-time API | Stability window length. |

---

## Extending the vocabulary

To add a chord type, append a `(suffix, intervals)` entry to
`GetAllChordDefinitions` (or `GetBasicChordDefinitions`) in
[Core/ChordTemplates.cs](Core/ChordTemplates.cs). Order intervals by harmonic
importance — root first — because `CreateTemplate` weights by position. For a
one-off custom chord at runtime use `ChordDetector.AddChordTemplate(name,
pitchClasses)`.

## Special chord labels

- `"N"` — no notes / silence.
- `"Unknown"` — notes present but no template cleared the confidence threshold.

---

## Development Tools

This project is developed with the following tools:

| | |
|:--:|:--|
| ![Claude Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/claude.svg) | **Anthropic** — Claude Code |
| ![Visual Studio Code](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/vscode.svg) | **Microsoft** — Visual Studio Code |
| ![Visual Studio 2022](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/visualstudio.svg) | **Microsoft** — Visual Studio 2022 |
| ![Rider](https://raw.githubusercontent.com/ModernMube/OwnAudioSharp/master/assets/tools/rider.svg) | **JetBrains** — Rider |
