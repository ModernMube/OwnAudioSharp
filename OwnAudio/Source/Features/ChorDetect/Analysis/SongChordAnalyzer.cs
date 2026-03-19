using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;
using System;
using System.Collections.Generic;
using System.Linq;

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
    /// Uses BPM-derived quarter-note windows and progressive note pruning for accurate results.
    /// </summary>
    public class SongChordAnalyzer
    {
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
        /// Gets the detected musical key of the song.
        /// </summary>
        public MusicalKey? DetectedKey { get; private set; }

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

            if (bpm > 0)
            {
                // Adaptive window based on the harmonic rhythm of typical music:
                //   < 100 BPM  → ♩ quarter note  (60 / bpm)
                //   100–150    → 𝅗𝅥 half note     (120 / bpm)
                //   > 150 BPM  → 𝅝 whole note    (240 / bpm)
                float quarterNote = 60f / bpm;
                _windowSize = bpm < 100 ? quarterNote
                            : bpm <= 150 ? quarterNote * 2f   // half note
                                         : quarterNote * 4f;  // whole note

                // Hop = half the window, so consecutive windows overlap by 50%
                // and no chord boundary falls entirely between two windows
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

            // First, detect the key of the entire song
            DetectedKey = _detector.DetectKeyFromNotes(sortedNotes);
            _detector.SetKey(DetectedKey);

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

            // Use the specified key
            DetectedKey = key;
            _detector.SetKey(key);

            var chords = AnalyzeWindows(sortedNotes);
            return MergeAdjacentChords(chords);
        }

        /// <summary>
        /// Analyzes the song in quarter-note-aligned windows.
        /// Each window first attempts detection with all available notes, then falls back to
        /// progressive pruning via <see cref="GetAndPruneNotes"/> if needed.
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <returns>A list of timed chords detected in each analysis window.</returns>
        private List<TimedChord> AnalyzeWindows(List<Note> notes)
        {
            var chords = new List<TimedChord>();
            var songDuration = notes.Max(n => n.EndTime);

            for (float time = 0; time < songDuration; time += _hopSize)
            {
                var windowEnd = Math.Min(time + _windowSize, songDuration);
                var result = GetAndPruneNotes(notes, time, windowEnd);

                if (result.HasValue)
                {
                    chords.Add(new TimedChord(
                        time, windowEnd,
                        result.Value.analysis.ChordName,
                        result.Value.analysis.Confidence,
                        result.Value.analysis.NoteNames));
                }
            }

            return chords;
        }

        /// <summary>
        /// Collects all notes active in a time window and attempts chord detection with progressive pruning.
        /// <para>
        /// Algorithm:
        /// <list type="number">
        ///   <item>Collect all notes whose playback overlaps the window.</item>
        ///   <item>Try to detect a chord with all notes.</item>
        ///   <item>If not found, repeatedly remove the shortest-duration note from the lowest-pitch group
        ///         until a chord is detected or only 3 notes remain.</item>
        ///   <item>Make one final attempt with exactly 3 notes; return null on failure.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="allNotes">All notes in the song.</param>
        /// <param name="windowStart">Start of the analysis window (seconds).</param>
        /// <param name="windowEnd">End of the analysis window (seconds).</param>
        /// <returns>
        ///   A tuple of the winning <see cref="ChordAnalysis"/> and the pruned note list, or null on failure.
        /// </returns>
        private (ChordAnalysis analysis, List<Note> notes)? GetAndPruneNotes(
            List<Note> allNotes, float windowStart, float windowEnd)
        {
            // Step 1: collect every note active in this window
            var windowNotes = allNotes
                .Where(n => n.StartTime < windowEnd && n.EndTime > windowStart)
                .ToList();

            if (windowNotes.Count < 3)
                return null;

            // Step 2: try with all notes
            var analysis = _detector.TryAnalyzeChord(windowNotes);
            if (analysis != null)
                return (analysis, windowNotes);

            // Step 3: progressive pruning
            // Build a removal order: shortest overlap-duration notes, from lowest to highest pitch.
            // Notes that are very brief relative to the window are considered least structurally important.
            var candidates = windowNotes
                .Select(n => new
                {
                    Note = n,
                    OverlapDuration = Math.Min(n.EndTime, windowEnd) - Math.Max(n.StartTime, windowStart)
                })
                .OrderBy(x => x.Note.Pitch)          // low to high pitch
                .ThenBy(x => x.OverlapDuration)       // shortest within same pitch group first
                .ToList();

            var workingSet = windowNotes.ToList();

            foreach (var candidate in candidates)
            {
                if (workingSet.Count <= 3)
                    break;

                workingSet.Remove(candidate.Note);

                analysis = _detector.TryAnalyzeChord(workingSet);
                if (analysis != null)
                    return (analysis, workingSet);
            }

            // Step 4: last attempt when exactly 3 notes remain
            if (workingSet.Count == 3)
            {
                analysis = _detector.TryAnalyzeChord(workingSet);
                if (analysis != null)
                    return (analysis, workingSet);
            }

            return null;    // No chord found even with minimum note count
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

                // If same chord and adjacent/overlapping, merge them
                if (current.ChordName == next.ChordName &&
                    Math.Abs(current.EndTime - next.StartTime) <= _hopSize * 1.5f)
                {
                    var avgConfidence = (current.Confidence + next.Confidence) / 2;
                    current = new TimedChord(current.StartTime, next.EndTime, current.ChordName,
                                           avgConfidence, current.Notes);
                }
                else
                {
                    // Different chord or too far apart, add current and start new
                    if (current.EndTime - current.StartTime >= _minimumChordDuration)
                    {
                        merged.Add(current);
                    }
                    current = next;
                }
            }

            // Don't forget the last chord
            if (current.EndTime - current.StartTime >= _minimumChordDuration)
            {
                merged.Add(current);
            }

            return merged;
        }
    }
}
