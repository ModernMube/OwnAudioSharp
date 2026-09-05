using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// Key-aware analysis: detecting the key, holding on to it, and the scale mask the scoring
    /// uses to favour diatonic chords.
    /// </summary>
    public partial class ChordDetector
    {
        /// <summary>
        /// Same as AnalyzeChord, the naming already follows the key.
        /// </summary>
        public ChordAnalysis AnalyzeChordWithKey(List<Note> notes)
        {
            return AnalyzeChord(notes);
        }

        /// <summary>
        /// Detects the key from the whole song once, then analyzes the chord in that context.
        /// allSongNotes is only used for the key, chordNotes is what gets named.
        /// </summary>
        public ChordAnalysis AnalyzeChordInSongContext(List<Note> allSongNotes, List<Note> chordNotes)
        {
            if (_currentKey == null)
            {
                _currentKey = _keyDetector.DetectKey(allSongNotes);
                _updateTemplates();
            }

            return AnalyzeChord(chordNotes);
        }

        /// <summary>
        /// Sets the key by hand. Rebuilds every template, so don't call it in a tight loop.
        /// </summary>
        public void SetKey(MusicalKey key)
        {
            _currentKey = key;
            _updateTemplates();
        }

        /// <summary>
        /// Null until something set or detected a key.
        /// </summary>
        public MusicalKey GetCurrentKey() => _currentKey;

        /// <summary>
        /// One key for the whole note list.
        /// </summary>
        public MusicalKey DetectKeyFromNotes(List<Note> notes)
        {
            return _keyDetector.DetectKey(notes);
        }

        /// <summary>
        /// Key segments with modulation tracking. One segment if the song stays put.
        /// </summary>
        internal List<TimedKey> DetectKeyTimelineFromNotes(List<Note> notes)
        {
            return _keyDetector.DetectKeyTimeline(notes);
        }

        /// <summary>
        /// Realtime call: detect, push into the history, then report whichever chord shows up most.
        /// </summary>
        /// <returns>The winner and how much of the history it holds, 0..1.</returns>
        public (string chord, float stability) ProcessNotes(List<Note> newNotes)
        {
            var (chord, confidence, _, _) = DetectChordAdvanced(ComputeChromagram(newNotes), ComputeBassPitchClass(newNotes));

            _detectedChords.Enqueue(confidence > 0.5f ? chord : null);
            if (_detectedChords.Count > _bufferSize)
                _detectedChords.Dequeue();

            string? bestChord = null;
            int bestCount = 0;

            foreach (var candidate in _detectedChords)
            {
                if (candidate == null) continue;

                int candidateCount = 0;
                foreach (var other in _detectedChords)
                {
                    if (other == candidate) candidateCount++;
                }

                if (candidateCount > bestCount)
                {
                    bestCount = candidateCount;
                    bestChord = candidate;
                }
            }

            if (bestChord == null) return ("Unknown", 0.0f);

            return (bestChord, (float)bestCount / _detectedChords.Count);
        }

        /// <summary>
        /// The topN best matches with their raw cosine, no threshold applied.
        /// </summary>
        public List<(string chord, float confidence)> GetTopMatches(List<Note> notes, int topN = 5)
        {
            if (notes.Count == 0)
                return new List<(string, float)> { ("N", 0.9f) };

            if (topN < 1) topN = 1;

            var chromagram = ComputeChromagram(notes);

            Span<ScoredChord> top = topN <= 32
                ? stackalloc ScoredChord[topN]
                : new ScoredChord[topN];

            int count = _rankChords(chromagram, ComputeBassPitchClass(notes), top);

            var result = new List<(string chord, float confidence)>(count);
            for (int i = 0; i < count; i++)
                result.Add((_templateEntries[top[i].TemplateIndex].Name, top[i].Cosine));

            return result;
        }

        /// <summary>
        /// Chord straight from a 12 bin chromagram, no bass information.
        /// </summary>
        public (string chord, float confidence) DetectChordFromChromagram(float[] chromagram)
        {
            var (chord, confidence, _, _) = DetectChordAdvancedBase(chromagram);
            return (chord, confidence);
        }

        /// <summary>
        /// 12 bit mask of the key's scale, all ones without a key so the diatonic bonus goes flat.
        /// Minor keys use natural minor — the bonus is small enough that a borrowed dominant
        /// doesn't get punished for it.
        /// </summary>
        private static int _buildScaleMask(MusicalKey? key)
        {
            if (key == null) return 0xFFF;

            string tonicName = key.IsMajor ? key.KeyName : key.KeyName.TrimEnd('m');
            int tonic = Array.IndexOf(key.PreferredNoteNames, tonicName);
            if (tonic < 0) return 0xFFF;

            ReadOnlySpan<int> intervals = key.IsMajor
                ? stackalloc int[] { 0, 2, 4, 5, 7, 9, 11 }
                : stackalloc int[] { 0, 2, 3, 5, 7, 8, 10 };

            int mask = 0;
            foreach (int interval in intervals)
                mask |= 1 << ((tonic + interval) % 12);

            return mask;
        }

        /// <summary>
        /// Detection without bass information.
        /// </summary>
        protected (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvancedBase(float[]? chromagram)
        {
            return DetectChordAdvanced(chromagram, -1);
        }
    }
}
