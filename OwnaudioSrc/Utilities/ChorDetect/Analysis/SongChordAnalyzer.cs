using Ownaudio.Utilities.Extensions;
using Ownaudio.Utilities.OwnChordDetect.Detectors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ownaudio.Utilities.OwnChordDetect.Analysis
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
        /// Initializes a new instance of the <see cref="TimedChord"/> class.
        /// </summary>
        /// <param name="startTime">The start time of the chord in seconds.</param>
        /// <param name="endTime">The end time of the chord in seconds.</param>
        /// <param name="chordName">The name of the detected chord.</param>
        /// <param name="confidence">The confidence level of the detection (0.0 to 1.0).</param>
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
        /// Returns a string representation of the timed chord including timing, name, confidence, and notes.
        /// </summary>
        /// <returns>A formatted string containing all chord information.</returns>
        public override string ToString()
        {
            return $"{StartTime:F1}s-{EndTime:F1}s: {ChordName} ({Confidence:F2}) [{string.Join(", ", Notes)}]";
        }
    }

    /// <summary>
    /// Analyzes a complete song and extracts timed chord progressions.
    /// </summary>
    public class SongChordAnalyzer
    {
        /// <summary>
        /// The chord detector used for analyzing individual chord segments.
        /// </summary>
        private readonly BaseChordDetector _detector;

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
        /// Initializes a new instance of the <see cref="SongChordAnalyzer"/> class.
        /// </summary>
        /// <param name="windowSize">The size of the analysis window in seconds. Default is 1.0f.</param>
        /// <param name="hopSize">The hop size between analysis windows in seconds. Default is 0.5f.</param>
        /// <param name="minimumChordDuration">The minimum duration for a chord to be included. Default is 0.8f.</param>
        /// <param name="confidence">The confidence threshold for chord detection. Default is 0.6f.</param>
        public SongChordAnalyzer(float windowSize = 1.0f, float hopSize = 0.5f, float minimumChordDuration = 0.8f, float confidence = 0.6f)
        {
            _detector = new OptimizedChordDetector(confidenceThreshold: confidence);
            _windowSize = windowSize;
            _hopSize = hopSize;
            _minimumChordDuration = minimumChordDuration;
        }

        /// <summary>
        /// Analyzes a complete song and returns timed chord progression.
        /// </summary>
        /// <param name="songNotes">All notes from the song to be analyzed.</param>
        /// <returns>A list of timed chords representing the chord progression.</returns>
        public List<TimedChord> AnalyzeSong(List<Note> songNotes)
        {
            if (!songNotes.Any())
                return new List<TimedChord>();

            var sortedNotes = songNotes.OrderBy(n => n.StartTime).ToList();

            var songDuration = sortedNotes.Max(n => n.EndTime);

            // Analyze in windows
            var rawChords = AnalyzeInWindows(sortedNotes, songDuration);

            // Post-process to merge similar adjacent chords
            var mergedChords = MergeAdjacentChords(rawChords, songDuration);

            return mergedChords;
        }

        /// <summary>
        /// Analyzes the song in sliding windows to detect chords at different time positions.
        /// </summary>
        /// <param name="sortedNotes">The notes sorted by start time.</param>
        /// <param name="songDuration">The total duration of the song in seconds.</param>
        /// <returns>A list of raw timed chords before merging.</returns>
        private List<TimedChord> AnalyzeInWindows(List<Note> sortedNotes, float songDuration)
        {
            var chords = new List<TimedChord>();

            // Use integer-based iteration to avoid float precision issues
            var totalSteps = (int)Math.Ceiling(songDuration / _hopSize);

            for (int step = 0; step < totalSteps; step++)
            {
                var time = step * _hopSize;

                // Safety check - don't exceed song duration
                if (time >= songDuration)
                    break;

                var windowEnd = Math.Min(time + _windowSize, songDuration);

                // Get the most characteristic notes for this window
                var characteristicNotes = GetCharacteristicNotes(sortedNotes, time, windowEnd);

                if (characteristicNotes.Any())
                {
                    var analysis = _detector.AnalyzeChord(characteristicNotes);

                    // Only add if confidence is reasonable
                    if (analysis.Confidence > 0.4f)
                    {
                        chords.Add(new TimedChord(
                            time,
                            windowEnd,
                            analysis.ChordName,
                            analysis.Confidence,
                            analysis.NoteNames
                        ));
                    }
                }
            }

            return chords;
        }

        /// <summary>
        /// Extracts the most characteristic notes from a time window for chord analysis.
        /// </summary>
        /// <param name="sortedNotes">The notes sorted by start time.</param>
        /// <param name="startTime">The start time of the window in seconds.</param>
        /// <param name="endTime">The end time of the window in seconds.</param>
        /// <returns>A list of the most characteristic notes for the window.</returns>
        private List<Note> GetCharacteristicNotes(List<Note> sortedNotes, float startTime, float endTime)
        {
            // Get notes that are active in this time window
            var windowNotes = sortedNotes
                .Where(note => note.StartTime < endTime && note.EndTime > startTime)
                .ToList();

            if (!windowNotes.Any())
                return new List<Note>();

            // Calculate characteristics for each note
            var noteCharacteristics = windowNotes.Select(note => new
            {
                Note = note,
                Duration = Math.Min(note.EndTime, endTime) - Math.Max(note.StartTime, startTime),
                Amplitude = note.Amplitude,
                Score = CalculateNoteScore(note, startTime, endTime)
            }).ToList();

            // Sort by score (descending) and select top 3-5
            var selectedNotes = noteCharacteristics
                .OrderByDescending(nc => nc.Score)
                .Take(5)
                .Select(nc => nc.Note)
                .ToList();

            // If we have less than 3 notes, supplement from adjacent windows
            if (selectedNotes.Count < 3)
            {
                var additionalNotes = GetAdditionalNotes(sortedNotes, startTime, endTime, selectedNotes);
                selectedNotes.AddRange(additionalNotes);
            }

            return selectedNotes.Take(5).ToList();
        }

        /// <summary>
        /// Calculates a score for a note based on its relevance to the analysis window.
        /// </summary>
        /// <param name="note">The note to score.</param>
        /// <param name="windowStart">The start time of the analysis window.</param>
        /// <param name="windowEnd">The end time of the analysis window.</param>
        /// <returns>A score value representing the note's importance in the window.</returns>
        private float CalculateNoteScore(Note note, float windowStart, float windowEnd)
        {
            // Calculate overlap duration with window
            var overlapDuration = Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart);

            // Calculate relative overlap (0-1)
            var relativeOverlap = overlapDuration / (windowEnd - windowStart);

            // Amplitude is already normalized (0.0-1.0)
            var normalizedAmplitude = note.Amplitude;

            // Combined score (weighted sum)
            return (relativeOverlap * 0.7f) + (normalizedAmplitude * 0.3f);
        }

        /// <summary>
        /// Gets additional notes from adjacent windows when the current window has insufficient notes.
        /// </summary>
        /// <param name="sortedNotes">The notes sorted by start time.</param>
        /// <param name="windowStart">The start time of the current window.</param>
        /// <param name="windowEnd">The end time of the current window.</param>
        /// <param name="existingNotes">The notes already selected for the current window.</param>
        /// <returns>A list of additional notes from adjacent windows.</returns>
        private List<Note> GetAdditionalNotes(List<Note> sortedNotes, float windowStart, float windowEnd, List<Note> existingNotes)
        {
            var additionalNotes = new List<Note>();
            var neededCount = 3 - existingNotes.Count;

            if (neededCount <= 0)
                return additionalNotes;

            // Get existing pitches to avoid duplicates
            var existingPitches = existingNotes.Select(n => n.Pitch).ToHashSet();

            // Look in previous window
            var prevWindowStart = windowStart - _hopSize;
            var prevWindowEnd = windowStart;
            var prevNotes = sortedNotes
                .Where(note => note.StartTime < prevWindowEnd && note.EndTime > prevWindowStart)
                .Where(note => !existingPitches.Contains(note.Pitch))
                .OrderByDescending(note => CalculateNoteScore(note, prevWindowStart, prevWindowEnd))
                .Take(neededCount)
                .ToList();

            additionalNotes.AddRange(prevNotes);

            // If still need more, look in next window
            if (additionalNotes.Count < neededCount)
            {
                var nextWindowStart = windowEnd;
                var nextWindowEnd = windowEnd + _hopSize;
                var nextNotes = sortedNotes
                    .Where(note => note.StartTime < nextWindowEnd && note.EndTime > nextWindowStart)
                    .Where(note => !existingPitches.Contains(note.Pitch) &&
                                   !additionalNotes.Any(an => an.Pitch == note.Pitch))
                    .OrderByDescending(note => CalculateNoteScore(note, nextWindowStart, nextWindowEnd))
                    .Take(neededCount - additionalNotes.Count)
                    .ToList();

                additionalNotes.AddRange(nextNotes);
            }

            return additionalNotes;
        }

        /// <summary>
        /// Merges adjacent chords with the same name to create longer, more stable chord segments.
        /// </summary>
        /// <param name="rawChords">The raw list of detected chords before merging.</param>
        /// <param name="songDuration">The maximum duration of the song to prevent time overflow.</param>
        /// <returns>A list of merged chords with minimum duration requirements applied.</returns>
        private List<TimedChord> MergeAdjacentChords(List<TimedChord> rawChords, float songDuration)
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
                    // Create merged chord with clamped end time
                    var avgConfidence = (current.Confidence + next.Confidence) / 2;
                    var endTime = Math.Min(next.EndTime, songDuration);
                    current = new TimedChord(
                        current.StartTime,
                        endTime,
                        current.ChordName,
                        avgConfidence,
                        current.Notes
                    );
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

            // Don't forget the last chord - clamp its end time to song duration
            var finalEndTime = Math.Min(current.EndTime, songDuration);
            var finalChord = new TimedChord(
                current.StartTime,
                finalEndTime,
                current.ChordName,
                current.Confidence,
                current.Notes
            );

            if (finalChord.EndTime - finalChord.StartTime >= _minimumChordDuration)
            {
                merged.Add(finalChord);
            }

            return merged;
        }
    }
}
