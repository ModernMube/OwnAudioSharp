using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;

namespace OwnaudioNET.Features.OwnChordDetect.Analysis
{
    /// <summary>
    /// Represents a detected chord with timing information.
    /// </summary>
    public class TimedChord
    {
        /// <summary>
        /// Gets the start time of the chord in seconds.
        /// </summary>
        public float StartTime { get; }

        /// <summary>
        /// Gets the end time of the chord in seconds.
        /// </summary>
        public float EndTime { get; }

        /// <summary>
        /// Gets the name of the detected chord.
        /// </summary>
        public string ChordName { get; }

        /// <summary>
        /// Gets the confidence level of the chord detection (0.0 to 1.0).
        /// </summary>
        public float Confidence { get; }

        /// <summary>
        /// Gets the array of note names that form the chord.
        /// </summary>
        public string[] Notes { get; }

        /// <summary>
        /// Initializes a new instance of the TimedChord class.
        /// </summary>
        /// <param name="startTime">The start time of the chord in seconds.</param>
        /// <param name="endTime">The end time of the chord in seconds.</param>
        /// <param name="chordName">The name of the detected chord.</param>
        /// <param name="confidence">The confidence level of the chord detection (0.0 to 1.0).</param>
        /// <param name="notes">The array of note names that form the chord.</param>
        public TimedChord(float startTime, float endTime, string chordName, float confidence, string[] notes)
        {
            StartTime = startTime;
            EndTime = endTime;
            ChordName = chordName;
            Confidence = confidence;
            Notes = notes;
        }

        /// <summary>
        /// Returns a string representation of the timed chord.
        /// </summary>
        /// <returns>A formatted string containing timing, chord name, confidence, and notes.</returns>
        public override string ToString()
        {
            return $"{StartTime:F1}s-{EndTime:F1}s: {ChordName} ({Confidence:F2}) [{string.Join(", ", Notes)}]";
        }
    }

    /// <summary>
    /// Analyzes a complete song and extracts timed chord progressions with key awareness.
    /// Uses BPM-derived quarter-note windows and progressive note pruning to build a
    /// per-window candidate lattice, then decodes the most plausible chord sequence with
    /// Viterbi dynamic programming (<see cref="ChordProgressionDecoder"/>) so the result
    /// is temporally stable and musically coherent instead of frame-wise greedy.
    /// </summary>
    public class SongChordAnalyzer
    {
        /// <summary>
        /// Number of ranked chord hypotheses kept per analysis window for the Viterbi lattice.
        /// Must be wide: on chord-transition mixture windows the union of two chords fully
        /// supports many extended "chimera" templates that all outscore the plain triads,
        /// and the decoder can only stay on the true chord if that chord is still a state
        /// in the window. 32 keeps the plain triads present in practice while the decoding
        /// cost stays trivial (windows × 33² transitions).
        /// </summary>
        private const int CandidatesPerWindow = 32;

        /// <summary>
        /// The chord detector used for analyzing individual chord segments.
        /// </summary>
        private readonly ChordDetector _detector;

        /// <summary>
        /// The size of the analysis window in seconds (one quarter note when BPM is provided).
        /// </summary>
        private readonly float _windowSize;

        /// <summary>
        /// The hop size between analysis windows in seconds (half a quarter note when BPM is provided).
        /// </summary>
        private readonly float _hopSize;

        /// <summary>
        /// The minimum duration required for a chord to be included in the result.
        /// </summary>
        private readonly float _minimumChordDuration;

        /// <summary>
        /// The minimum confidence threshold; also serves as the emission score of the
        /// no-chord state in the Viterbi decoder, so windows whose best candidate falls
        /// below it resolve to no-chord exactly as they previously fell below the threshold.
        /// </summary>
        private readonly float _confidence;

        /// <summary>
        /// Reusable buffer holding the notes active in the current analysis window.
        /// Reused across windows to avoid a per-window list allocation on the hot path.
        /// </summary>
        private readonly List<Note> _windowNotes = new List<Note>();

        /// <summary>
        /// Reusable working set for progressive note pruning, populated only when the
        /// full-window detection fails. Reused across windows to avoid allocations.
        /// </summary>
        private readonly List<Note> _workingSet = new List<Note>();

        /// <summary>
        /// Gets the detected musical key of the song. With modulation tracking this is the
        /// dominant key (the one active for the longest total duration).
        /// </summary>
        public MusicalKey? DetectedKey { get; private set; }

        /// <summary>
        /// Gets the modulation-aware key timeline of the last analyzed song: one segment per
        /// key region in chronological order. Songs without modulation have a single segment.
        /// </summary>
        internal IReadOnlyList<TimedKey>? KeyTimeline { get; private set; }

        /// <summary>
        /// The key currently applied to the detector, tracked so segment lookups only pay
        /// the template-rebuild cost of <see cref="ChordDetector.SetKey"/> at segment changes.
        /// </summary>
        private MusicalKey? _appliedKey;

        /// <summary>
        /// Initializes a new instance of the SongChordAnalyzer class.
        /// When <paramref name="bpm"/> is greater than zero, the analysis window is derived
        /// from the tempo using an adaptive note-value strategy:
        /// <list type="bullet">
        ///   <item>BPM &lt; 100 → quarter note (60 / bpm s) – chords change frequently</item>
        ///   <item>BPM 100–150 → half note (120 / bpm s) – medium harmonic rhythm</item>
        ///   <item>BPM &gt; 150 → whole note (240 / bpm s) – fast tempos, slow chord changes</item>
        /// </list>
        /// This mirrors the concept of harmonic rhythm: in fast music chords tend to hold
        /// for longer beat-multiples, so a larger window captures more notes per chord.
        /// </summary>
        /// <param name="windowSize">Analysis window in seconds. Overridden when bpm &gt; 0.</param>
        /// <param name="hopSize">Hop between windows in seconds. Overridden when bpm &gt; 0.</param>
        /// <param name="minimumChordDuration">Minimum chord duration to include in results. Default 0.8 s.</param>
        /// <param name="confidence">Minimum confidence threshold for chord detection. Default 0.6.</param>
        /// <param name="bpm">Detected tempo in BPM. Drives adaptive window sizing when &gt; 0.</param>
        public SongChordAnalyzer(
            float windowSize = 1.0f,
            float hopSize = 0.5f,
            float minimumChordDuration = 0.8f,
            float confidence = 0.6f,
            int bpm = 0)
        {
            _detector = new ChordDetector(DetectionMode.KeyAware, confidence);
            _minimumChordDuration = minimumChordDuration;
            _confidence = confidence;

            if (bpm > 0)
            {
                // Adaptive window based on the harmonic rhythm of typical music:
                float quarterNote = 60f / bpm;
                _windowSize = bpm < 100 ? quarterNote
                            : bpm <= 150 ? quarterNote * 2f   // half note
                                         : quarterNote * 4f;  // whole note

                // Hop = half the window, so consecutive windows overlap by 50%
                _hopSize = _windowSize / 2f;
            }
            else
            {
                _windowSize = windowSize;
                _hopSize = hopSize;
            }
        }

        /// <summary>
        /// Analyzes a complete song and returns timed chord progression with key-appropriate naming.
        /// </summary>
        /// <param name="songNotes">The list of notes that make up the song.</param>
        /// <returns>A list of timed chords representing the chord progression of the song.</returns>
        public List<TimedChord> AnalyzeSong(List<Note> songNotes)
        {
            if (!songNotes.Any())
                return new List<TimedChord>();

            var sortedNotes = songNotes.OrderBy(n => n.StartTime).ToList();

            var timeline = _detector.DetectKeyTimelineFromNotes(sortedNotes);
            if (timeline.Count == 0)
                timeline.Add(new TimedKey(0f, float.MaxValue, _detector.DetectKeyFromNotes(sortedNotes)));

            KeyTimeline = timeline;
            DetectedKey = GetDominantKey(timeline);
            ApplyKey(timeline[0].Key);

            var chords = AnalyzeWindows(sortedNotes);
            return MergeAdjacentChords(chords);
        }

        /// <summary>
        /// Analyzes a song with a manually specified key instead of auto-detection.
        /// </summary>
        /// <param name="songNotes">The list of notes that make up the song.</param>
        /// <param name="key">The musical key to use for chord analysis.</param>
        /// <returns>A list of timed chords representing the chord progression of the song in the specified key.</returns>
        public List<TimedChord> AnalyzeSongInKey(List<Note> songNotes, MusicalKey key)
        {
            if (!songNotes.Any())
                return new List<TimedChord>();

            var sortedNotes = songNotes.OrderBy(n => n.StartTime).ToList();

            DetectedKey = key;
            KeyTimeline = new List<TimedKey> { new TimedKey(0f, float.MaxValue, key) };
            ApplyKey(key);

            var chords = AnalyzeWindows(sortedNotes);
            return MergeAdjacentChords(chords);
        }

        /// <summary>
        /// Returns the key active for the longest total duration across the timeline,
        /// aggregating segments of the same key name and mode.
        /// </summary>
        /// <param name="timeline">The key timeline of the song.</param>
        /// <returns>The dominant musical key.</returns>
        private static MusicalKey GetDominantKey(IReadOnlyList<TimedKey> timeline)
        {
            var durations = new Dictionary<string, float>();
            var keys = new Dictionary<string, MusicalKey>();

            foreach (var segment in timeline)
            {
                string label = segment.Key.ToString();
                durations.TryGetValue(label, out float total);
                durations[label] = total + (segment.EndTime - segment.StartTime);
                keys[label] = segment.Key;
            }

            string? bestLabel = null;
            float bestDuration = float.MinValue;

            foreach (var (label, total) in durations)
            {
                if (total > bestDuration)
                {
                    bestDuration = total;
                    bestLabel = label;
                }
            }

            return keys[bestLabel!];
        }

        /// <summary>
        /// Applies a key to the detector when it differs from the currently applied one,
        /// avoiding redundant template rebuilds inside the window loop.
        /// </summary>
        /// <param name="key">The key to apply.</param>
        private void ApplyKey(MusicalKey key)
        {
            if (ReferenceEquals(_appliedKey, key))
                return;

            _appliedKey = key;
            _detector.SetKey(key);
        }

        /// <summary>
        /// Applies the key active at the given song position according to the key timeline.
        /// Positions before the first or after the last segment use the nearest segment.
        /// </summary>
        /// <param name="time">The song position in seconds.</param>
        private void ApplyKeyForTime(float time)
        {
            var timeline = KeyTimeline;
            if (timeline == null || timeline.Count == 0)
                return;

            for (int i = 0; i < timeline.Count; i++)
            {
                if (time < timeline[i].EndTime || i == timeline.Count - 1)
                {
                    ApplyKey(timeline[i].Key);
                    return;
                }
            }
        }

        /// <summary>
        /// Analyzes the song in quarter-note-aligned windows and decodes the chord progression.
        /// <para>
        /// Algorithm:
        /// <list type="number">
        ///   <item>For every window, collect the active notes and build a ranked candidate
        ///         list (top <see cref="CandidatesPerWindow"/> hypotheses, no threshold),
        ///         using progressive pruning to clean noisy windows.</item>
        ///   <item>Run Viterbi decoding over the whole lattice so chord choices are
        ///         temporally smoothed and musically weighted.</item>
        ///   <item>Emit a <see cref="TimedChord"/> for every window not resolved to no-chord.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <returns>A list of timed chords decoded from the analysis windows.</returns>
        private List<TimedChord> AnalyzeWindows(List<Note> notes)
        {
            float songDuration = 0f;
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].EndTime > songDuration)
                    songDuration = notes[i].EndTime;
            }

            var windows = new List<ChordWindow>();

            for (float time = 0; time < songDuration; time += _hopSize)
            {
                var windowEnd = Math.Min(time + _windowSize, songDuration);
                ApplyKeyForTime((time + windowEnd) * 0.5f);
                CollectWindowNotes(notes, time, windowEnd);

                ChordCandidate[] candidates;
                if (_windowNotes.Count < 3)
                {
                    candidates = Array.Empty<ChordCandidate>();
                }
                else
                {
                    var noteSet = SelectWindowNoteSet(time, windowEnd);
                    candidates = _detector.GetChordCandidates(noteSet, CandidatesPerWindow, time, windowEnd).ToArray();
                }

                windows.Add(new ChordWindow(time, windowEnd, candidates));
            }

            var decoder = new ChordProgressionDecoder(_confidence);
            int[] selection = decoder.Decode(windows);

            var chords = new List<TimedChord>();

            for (int i = 0; i < windows.Count; i++)
            {
                if (selection[i] == ChordProgressionDecoder.NoChordState)
                    continue;

                var window = windows[i];
                var candidate = window.Candidates[selection[i]];

                ApplyKeyForTime((window.StartTime + window.EndTime) * 0.5f);
                CollectWindowNotes(notes, window.StartTime, window.EndTime);
                var noteNames = _detector.GetChordNoteNames(candidate.Name, _windowNotes);

                chords.Add(new TimedChord(
                    window.StartTime, window.EndTime,
                    candidate.Name, candidate.Cosine, noteNames));
            }

            return chords;
        }

        /// <summary>
        /// Fills the reusable window buffer with all notes whose playback overlaps the window.
        /// </summary>
        /// <param name="allNotes">All notes in the song.</param>
        /// <param name="windowStart">Start of the analysis window (seconds).</param>
        /// <param name="windowEnd">End of the analysis window (seconds).</param>
        private void CollectWindowNotes(List<Note> allNotes, float windowStart, float windowEnd)
        {
            _windowNotes.Clear();
            for (int i = 0; i < allNotes.Count; i++)
            {
                var note = allNotes[i];
                if (note.StartTime < windowEnd && note.EndTime > windowStart)
                    _windowNotes.Add(note);
            }
        }

        /// <summary>
        /// Selects the note set used for candidate ranking in the current window.
        /// <para>
        /// Algorithm:
        /// <list type="number">
        ///   <item>Try to detect a chord with all notes; if it succeeds, use the full set.</item>
        ///   <item>If not, repeatedly remove the shortest-overlap note from the lowest-pitch group
        ///         until a chord is detected or only 3 notes remain, and use the pruned set.</item>
        ///   <item>If pruning never succeeds, fall back to the full set — the candidate lattice
        ///         carries the low scores and the decoder can still resolve the window to no-chord.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="windowStart">Start of the analysis window (seconds).</param>
        /// <param name="windowEnd">End of the analysis window (seconds).</param>
        /// <returns>The note list to rank candidates from.</returns>
        private List<Note> SelectWindowNoteSet(float windowStart, float windowEnd)
        {
            var analysis = _detector.TryAnalyzeChord(_windowNotes);
            if (analysis != null)
                return _windowNotes;

            _workingSet.Clear();
            _workingSet.AddRange(_windowNotes);
            _workingSet.Sort((a, b) =>
            {
                int byPitch = a.Pitch.CompareTo(b.Pitch);
                if (byPitch != 0)
                    return byPitch;
                return GetOverlap(a, windowStart, windowEnd).CompareTo(GetOverlap(b, windowStart, windowEnd));
            });

            while (_workingSet.Count > 3)
            {
                _workingSet.RemoveAt(0);

                analysis = _detector.TryAnalyzeChord(_workingSet);
                if (analysis != null)
                    return _workingSet;
            }

            return _windowNotes;
        }

        /// <summary>
        /// Calculates the overlap duration between a note and a time window.
        /// </summary>
        /// <param name="note">The note to calculate overlap for.</param>
        /// <param name="windowStart">The start time of the window in seconds.</param>
        /// <param name="windowEnd">The end time of the window in seconds.</param>
        /// <returns>The duration of overlap between the note and the window in seconds.</returns>
        private float GetOverlap(Note note, float windowStart, float windowEnd)
        {
            return Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart);
        }

        /// <summary>
        /// Merges adjacent chords with the same name to create longer, more stable chord segments.
        /// </summary>
        /// <param name="rawChords">The list of raw chord detections to merge.</param>
        /// <returns>A list of merged chord segments with minimum duration filtering applied.</returns>
        private List<TimedChord> MergeAdjacentChords(List<TimedChord> rawChords)
        {
            if (!rawChords.Any())
                return new List<TimedChord>();

            var merged = new List<TimedChord>();
            var current = rawChords.First();

            for (int i = 1; i < rawChords.Count; i++)
            {
                var next = rawChords[i];

                if (current.ChordName == next.ChordName &&
                    Math.Abs(current.EndTime - next.StartTime) <= _hopSize * 1.5f)
                {
                    float currentDuration = current.EndTime - current.StartTime;
                    float nextDuration = next.EndTime - next.StartTime;
                    float totalDuration = currentDuration + nextDuration;

                    float mergedConfidence = totalDuration > 0f
                        ? (current.Confidence * currentDuration + next.Confidence * nextDuration) / totalDuration
                        : (current.Confidence + next.Confidence) / 2f;

                    current = new TimedChord(current.StartTime, next.EndTime, current.ChordName,
                                           mergedConfidence, current.Notes);
                }
                else
                {
                    if (current.EndTime - current.StartTime >= _minimumChordDuration)
                    {
                        merged.Add(current);
                    }
                    current = next;
                }
            }

            if (current.EndTime - current.StartTime >= _minimumChordDuration)
            {
                merged.Add(current);
            }

            return merged;
        }
    }
}
