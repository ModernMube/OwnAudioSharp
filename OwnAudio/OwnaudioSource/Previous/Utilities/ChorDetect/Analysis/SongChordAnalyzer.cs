using OwnaudioLegacy.Utilities.Extensions;
using OwnaudioLegacy.Utilities.OwnChordDetect.Core;
using OwnaudioLegacy.Utilities.OwnChordDetect.Detectors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OwnaudioLegacy.Utilities.OwnChordDetect.Analysis
{
    /// <summary>
    /// Represents a detected chord with timing information.
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
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
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public class SongChordAnalyzer
    {
        /// <summary>
        /// The chord detector used for analyzing individual chord segments.
        /// </summary>
        private readonly ChordDetector _detector;

        /// <summary>
        /// The size of the analysis window in seconds.
        /// </summary>
        private readonly float _windowSize;

        /// <summary>
        /// The hop size between analysis windows in seconds.
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
        /// </summary>
        /// <param name="windowSize">The size of the analysis window in seconds. Default is 1.0f.</param>
        /// <param name="hopSize">The hop size between analysis windows in seconds. Default is 0.5f.</param>
        /// <param name="minimumChordDuration">The minimum duration required for a chord to be included in the result. Default is 0.8f.</param>
        /// <param name="confidence">The minimum confidence threshold for chord detection. Default is 0.6f.</param>
        public SongChordAnalyzer(float windowSize = 1.0f, float hopSize = 0.5f, float minimumChordDuration = 0.8f, float confidence = 0.6f)
        {
            _detector = new ChordDetector(DetectionMode.KeyAware, confidence);
            _windowSize = windowSize;
            _hopSize = hopSize;
            _minimumChordDuration = minimumChordDuration;
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
        /// Analyzes the song in sliding windows to detect chords at different time positions.
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
                var windowNotes = GetNotesInWindow(notes, time, windowEnd);

                if (windowNotes.Count >= 2)
                {
                    var analysis = _detector.AnalyzeChord(windowNotes);
                    if (analysis.Confidence > 0.4f)
                    {
                        chords.Add(new TimedChord(time, windowEnd, analysis.ChordName,
                                                analysis.Confidence, analysis.NoteNames));
                    }
                }
            }

            return chords;
        }

        /// <summary>
        /// Gets notes that are active in the specified time window.
        /// </summary>
        /// <param name="notes">The complete list of notes to filter from.</param>
        /// <param name="start">The start time of the window in seconds.</param>
        /// <param name="end">The end time of the window in seconds.</param>
        /// <returns>A list of notes that are active during the specified time window, ordered by relevance.</returns>
        private List<Note> GetNotesInWindow(List<Note> notes, float start, float end)
        {
            return notes.Where(n => n.StartTime < end && n.EndTime > start)
                       .OrderByDescending(n => n.Amplitude * GetOverlap(n, start, end))
                       .Take(5)
                       .ToList();
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
                    // Create merged chord
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
