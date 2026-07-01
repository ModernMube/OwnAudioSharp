using OwnaudioNET.RustNext.Features.Extensions;
using OwnaudioNET.RustNext.Features.OwnChordDetect.Analysis;
using OwnaudioNET.RustNext.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OwnaudioNET.RustNext.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// Detection modes for the chord detector.
    /// </summary>
    public enum DetectionMode
    {
        /// <summary>
        /// Basic chord detection with triads and 7th chords only.
        /// </summary>
        Basic,

        /// <summary>
        /// Extended chord detection including 9th, 11th, and 13th chords.
        /// </summary>
        Extended,

        /// <summary>
        /// Key-aware chord detection with appropriate note naming.
        /// </summary>
        KeyAware,

        /// <summary>
        /// Optimized detection with ambiguity analysis and stability checking.
        /// </summary>
        Optimized
    }

    /// <summary>
    /// Unified chord detector with multiple detection modes.
    /// </summary>
    public class ChordDetector
    {
        /// <summary>
        /// Dictionary of chord templates mapping chord names to their normalized chromagram patterns.
        /// Kept for the public template API; the matching hot path uses <see cref="_templateEntries"/>.
        /// </summary>
        private Dictionary<string, float[]> _templates;

        /// <summary>
        /// Pre-computed, immutable view of every template used by the allocation-free matching path.
        /// Caches the inverse magnitude and tone count so cosine similarity and the parsimony bias
        /// can be evaluated without per-call square roots or LINQ allocations.
        /// </summary>
        private TemplateEntry[] _templateEntries;

        /// <summary>
        /// Number of top candidates retained during ranking. Small and fixed so the ranking
        /// buffer can be stack-allocated by callers, keeping the hot path GC-free.
        /// </summary>
        private const int TopCandidateCount = 5;

        /// <summary>
        /// Reference chord size (a triad) against which the parsimony penalty is measured.
        /// Templates larger than a triad are penalised proportionally to their extra tones.
        /// </summary>
        private const int TriadToneCount = 3;

        /// <summary>
        /// Score penalty applied per chord tone beyond a triad. Small enough that a genuine
        /// seventh or extension still wins on cosine similarity, but large enough to break
        /// near-ties in favour of the simpler chord, suppressing spurious extended-chord labels.
        /// </summary>
        private const float ComplexityPenaltyPerTone = 0.012f;

        /// <summary>
        /// Score bonus applied to chords whose tones all fit the detected key's scale.
        /// Acts only as a tie-breaker: it nudges harmonically plausible (diatonic) chords ahead
        /// of enharmonic rivals without overriding a clearly stronger cosine match.
        /// </summary>
        private const float DiatonicBonus = 0.02f;

        /// <summary>
        /// Pitch-class bitmask of the currently active key's scale, or all twelve bits set
        /// when no key is known. Used to flag templates as diatonic when building entries.
        /// </summary>
        private int _scaleMask = 0xFFF;

        /// <summary>
        /// The minimum confidence threshold for chord detection (0.0 to 1.0).
        /// </summary>
        private readonly float _confidenceThreshold;

        /// <summary>
        /// The threshold for determining chord ambiguity in optimized mode.
        /// </summary>
        private readonly float _ambiguityThreshold;

        /// <summary>
        /// The key detector instance for musical key analysis.
        /// </summary>
        private readonly KeyDetector _keyDetector;

        /// <summary>
        /// The currently active musical key for chord naming.
        /// </summary>
        private MusicalKey _currentKey;

        /// <summary>
        /// The detection mode used for chord analysis.
        /// </summary>
        private readonly DetectionMode _mode;

        /// <summary>
        /// Rolling buffer of recently detected chord names for real-time stability analysis.
        /// A null entry marks a frame whose confidence was below the acceptance threshold,
        /// preserving the original stability denominator while avoiding per-frame re-detection.
        /// </summary>
        private readonly Queue<string?> _detectedChords = new Queue<string?>();

        /// <summary>
        /// The maximum size of the note buffer for real-time processing.
        /// </summary>
        private readonly int _bufferSize;

        /// <summary>
        /// Initializes a new instance of the ChordDetector class.
        /// </summary>
        /// <param name="mode">The detection mode to use for chord analysis.</param>
        /// <param name="confidenceThreshold">The minimum confidence threshold for chord detection (0.0 to 1.0).</param>
        /// <param name="ambiguityThreshold">The threshold for determining chord ambiguity in optimized mode.</param>
        /// <param name="bufferSize">The maximum size of the note buffer for real-time processing.</param>
        #nullable disable
        public ChordDetector(DetectionMode mode = DetectionMode.Extended,
                            float confidenceThreshold = 0.6f,
                            float ambiguityThreshold = 0.1f,
                            int bufferSize = 5)
        {
            _mode = mode;
            _confidenceThreshold = confidenceThreshold;
            _ambiguityThreshold = ambiguityThreshold;
            _keyDetector = new KeyDetector();
            _bufferSize = bufferSize;
            UpdateTemplates();
        }
        #nullable restore

        /// <summary>
        /// Analyzes a list of notes and returns the detected chord with analysis details.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A ChordAnalysis object containing the detected chord, confidence, and detailed analysis.</returns>
        public ChordAnalysis AnalyzeChord(List<Note> notes)
        {
            if (!notes.Any())
                return new ChordAnalysis("N", 0.9f, "No notes detected", new string[0]);

            var chromagram = ComputeChromagram(notes);
            var (chord, confidence, isAmbiguous, alternatives) = DetectChordAdvancedBase(chromagram);

            int presentMask = 0;
            foreach (var note in notes)
                presentMask |= 1 << (note.Pitch % 12);

            if (chord != "Unknown" && chord != "N" && _templates.TryGetValue(chord, out var chordTemplate))
            {
                int filteredMask = 0;
                for (int pc = 0; pc < 12; pc++)
                {
                    if ((presentMask & (1 << pc)) != 0 && chordTemplate[pc] > 0f)
                        filteredMask |= 1 << pc;
                }
                presentMask = filteredMask;
            }

            int pitchCount = BitOperations.PopCount((uint)presentMask);
            var pitchClasses = new int[pitchCount];
            var noteNames = new string[pitchCount];
            for (int pc = 0, w = 0; pc < 12; pc++)
            {
                if ((presentMask & (1 << pc)) != 0)
                {
                    pitchClasses[w] = pc;
                    noteNames[w] = ChordTemplates.GetNoteName(pc, _currentKey);
                    w++;
                }
            }

            return new ChordAnalysis(chord, confidence, GenerateExplanation(pitchClasses, chord, confidence, isAmbiguous), noteNames)
            {
                IsAmbiguous = isAmbiguous,
                Alternatives = alternatives,
                PitchClasses = pitchClasses,
                Chromagram = chromagram
            };
        }

        /// <summary>
        /// Tries to analyze a list of notes as a chord. Returns null if no chord is confidently detected.
        /// Used by the progressive pruning loop in SongChordAnalyzer.
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <returns>A ChordAnalysis if a chord was found, or null if the result is Unknown.</returns>
        public ChordAnalysis? TryAnalyzeChord(List<Note> notes)
        {
            if (notes.Count < 3)
                return null;

            var analysis = AnalyzeChord(notes);
            return (analysis.ChordName == "Unknown" || analysis.ChordName == "N")
                ? null
                : analysis;
        }

        /// <summary>
        /// Analyzes chord with key-aware naming.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for chord detection.</param>
        /// <returns>A ChordAnalysis object with key-appropriate note naming.</returns>
        public ChordAnalysis AnalyzeChordWithKey(List<Note> notes)
        {
            return AnalyzeChord(notes);
        }

        /// <summary>
        /// Analyzes the entire song to detect its key and then analyzes individual chords.
        /// </summary>
        /// <param name="allSongNotes">The complete list of notes from the entire song for key detection.</param>
        /// <param name="chordNotes">The specific notes to analyze for chord detection.</param>
        /// <returns>A ChordAnalysis object with key-appropriate chord naming based on the song context.</returns>
        public ChordAnalysis AnalyzeChordInSongContext(List<Note> allSongNotes, List<Note> chordNotes)
        {
            if (_currentKey == null)
            {
                _currentKey = _keyDetector.DetectKey(allSongNotes);
                UpdateTemplates();
            }

            return AnalyzeChord(chordNotes);
        }

        /// <summary>
        /// Sets the musical key explicitly.
        /// </summary>
        /// <param name="key">The musical key to use for chord naming.</param>
        public void SetKey(MusicalKey key)
        {
            _currentKey = key;
            UpdateTemplates();
        }

        /// <summary>
        /// Gets the currently detected or set musical key.
        /// </summary>
        /// <returns>The current musical key, or null if no key has been detected or set.</returns>
        public MusicalKey GetCurrentKey() => _currentKey;

        /// <summary>
        /// Detects the key from a collection of notes.
        /// </summary>
        /// <param name="notes">The list of notes to analyze for key detection.</param>
        /// <returns>The detected musical key.</returns>
        public MusicalKey DetectKeyFromNotes(List<Note> notes)
        {
            return _keyDetector.DetectKey(notes);
        }

        /// <summary>
        /// Processes notes for real-time detection with stability analysis.
        /// </summary>
        /// <param name="newNotes">The new notes to add to the processing buffer.</param>
        /// <returns>A tuple containing the most stable chord name and its stability score (0.0 to 1.0).</returns>
        public (string chord, float stability) ProcessNotes(List<Note> newNotes)
        {
            var (chord, confidence, _, _) = DetectChordAdvancedBase(ComputeChromagram(newNotes));

            _detectedChords.Enqueue(confidence > 0.5f ? chord : null);
            if (_detectedChords.Count > _bufferSize)
                _detectedChords.Dequeue();

            string? bestChord = null;
            int bestCount = 0;

            foreach (var candidate in _detectedChords)
            {
                if (candidate == null)
                    continue;

                int candidateCount = 0;
                foreach (var other in _detectedChords)
                {
                    if (other == candidate)
                        candidateCount++;
                }

                if (candidateCount > bestCount)
                {
                    bestCount = candidateCount;
                    bestChord = candidate;
                }
            }

            if (bestChord == null)
                return ("Unknown", 0.0f);

            float stability = (float)bestCount / _detectedChords.Count;
            return (bestChord, stability);
        }

        /// <summary>
        /// Gets top N chord matches.
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <param name="topN">The number of top matches to return.</param>
        /// <returns>A list of tuples containing chord names and their confidence scores, ordered by confidence.</returns>
        public List<(string chord, float confidence)> GetTopMatches(List<Note> notes, int topN = 5)
        {
            if (!notes.Any())
                return new List<(string, float)> { ("N", 0.9f) };

            if (topN < 1)
                topN = 1;

            var chromagram = ComputeChromagram(notes);

            Span<ScoredChord> top = topN <= 32
                ? stackalloc ScoredChord[topN]
                : new ScoredChord[topN];

            int count = RankChords(chromagram, top);

            var result = new List<(string chord, float confidence)>(count);
            for (int i = 0; i < count; i++)
                result.Add((_templateEntries[top[i].TemplateIndex].Name, top[i].Cosine));

            return result;
        }

        /// <summary>
        /// Detects chord from chromagram with advanced analysis.
        /// </summary>
        /// <param name="chromagram">The 12-element chromagram array representing pitch class distribution.</param>
        /// <returns>A tuple containing the detected chord name and confidence score.</returns>
        public (string chord, float confidence) DetectChordFromChromagram(float[] chromagram)
        {
            var (chord, confidence, _, _) = DetectChordAdvancedBase(chromagram);
            return (chord, confidence);
        }

        /// <summary>
        /// Adds a custom chord template.
        /// </summary>
        /// <param name="chordName">The name of the custom chord.</param>
        /// <param name="pitchClasses">The pitch classes that form the chord (0-11).</param>
        public void AddChordTemplate(string chordName, int[] pitchClasses)
        {
            _templates[chordName] = ChordTemplates.CreateTemplate(pitchClasses);
        }

        /// <summary>
        /// Gets all chord templates.
        /// </summary>
        /// <returns>A dictionary copy of all chord templates mapping chord names to their chromagram patterns.</returns>
        public Dictionary<string, float[]> GetChordTemplates()
        {
            return new Dictionary<string, float[]>(_templates);
        }

        /// <summary>
        /// Updates templates based on current mode and key, then rebuilds the pre-computed
        /// matching entries (inverse magnitude, tone count, diatonic flag) used by the hot path.
        /// </summary>
        private void UpdateTemplates()
        {
            var includeExtended = _mode != DetectionMode.Basic;
            _templates = ChordTemplates.CreateAllTemplates(_currentKey, includeExtended);
            RebuildTemplateEntries();
        }

        /// <summary>
        /// Rebuilds <see cref="_templateEntries"/> from <see cref="_templates"/> and the current key.
        /// Each entry caches the inverse Euclidean magnitude, the active tone count and whether the
        /// chord is diatonic, so the matching loop performs no square roots, allocations or LINQ.
        /// </summary>
        private void RebuildTemplateEntries()
        {
            _scaleMask = BuildScaleMask(_currentKey);

            var entries = new TemplateEntry[_templates.Count];
            int index = 0;

            foreach (var (chordName, template) in _templates)
            {
                float magnitudeSquared = 0f;
                int toneCount = 0;
                int activeMask = 0;

                for (int pc = 0; pc < 12; pc++)
                {
                    float value = template[pc];
                    if (value > 0f)
                    {
                        magnitudeSquared += value * value;
                        toneCount++;
                        activeMask |= 1 << pc;
                    }
                }

                float inverseMagnitude = magnitudeSquared > 0f
                    ? (float)(1.0 / Math.Sqrt(magnitudeSquared))
                    : 0f;

                bool isDiatonic = (activeMask & ~_scaleMask) == 0;

                entries[index++] = new TemplateEntry(chordName, template, inverseMagnitude, toneCount, isDiatonic);
            }

            _templateEntries = entries;
        }

        /// <summary>
        /// Builds a 12-bit pitch-class mask of the scale belonging to the given key.
        /// Returns all twelve bits set when no key is supplied so the diatonic bonus stays neutral.
        /// Minor keys use the natural-minor scale; the small bonus weight keeps occasional
        /// borrowed (e.g. dominant) chords from being unfairly demoted.
        /// </summary>
        /// <param name="key">The active musical key, or null when unknown.</param>
        /// <returns>A bitmask where bit <c>n</c> is set when pitch class <c>n</c> is in the scale.</returns>
        private static int BuildScaleMask(MusicalKey? key)
        {
            if (key == null)
                return 0xFFF;

            string tonicName = key.IsMajor ? key.KeyName : key.KeyName.TrimEnd('m');
            int tonic = Array.IndexOf(key.PreferredNoteNames, tonicName);
            if (tonic < 0)
                return 0xFFF;

            ReadOnlySpan<int> intervals = key.IsMajor
                ? stackalloc int[] { 0, 2, 4, 5, 7, 9, 11 }
                : stackalloc int[] { 0, 2, 3, 5, 7, 8, 10 };

            int mask = 0;
            foreach (int interval in intervals)
                mask |= 1 << ((tonic + interval) % 12);

            return mask;
        }

        /// <summary>
        /// Advanced chord detection with ambiguity analysis.
        /// Candidates are ranked by a perceptual score (cosine similarity plus a parsimony penalty
        /// and a diatonic bonus), but the reported confidence is always the winner's raw cosine
        /// similarity so the meaning of <see cref="_confidenceThreshold"/> stays unchanged.
        /// </summary>
        /// <param name="chromagram">The chromagram to analyze for chord detection.</param>
        /// <returns>A tuple containing the chord name, confidence, ambiguity flag, and alternative chord names.</returns>
        protected (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvancedBase(float[]? chromagram)
        {
            if (chromagram == null || _templateEntries.Length == 0)
                return ("Unknown", 0f, false, new string[0]);

            Span<ScoredChord> top = stackalloc ScoredChord[TopCandidateCount];
            int count = RankChords(chromagram, top);

            if (count == 0)
                return ("Unknown", 0f, false, new string[0]);

            ScoredChord best = top[0];

            if (_mode == DetectionMode.Optimized)
            {
                int ambiguousCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (Math.Abs(top[i].Cosine - best.Cosine) <= _ambiguityThreshold)
                        ambiguousCount++;
                }

                bool isAmbiguous = ambiguousCount > 1;

                if (best.Cosine >= _confidenceThreshold)
                {
                    if (isAmbiguous)
                    {
                        var ambiguous = new string[ambiguousCount];
                        for (int i = 0, w = 0; i < count && w < ambiguousCount; i++)
                        {
                            if (Math.Abs(top[i].Cosine - best.Cosine) <= _ambiguityThreshold)
                                ambiguous[w++] = _templateEntries[top[i].TemplateIndex].Name;
                        }

                        int take = Math.Min(3, ambiguous.Length);
                        var combinedName = string.Join("/", ambiguous, 0, take);
                        return (combinedName, best.Cosine, true, ambiguous);
                    }

                    return (_templateEntries[best.TemplateIndex].Name, best.Cosine, false, new string[0]);
                }

                int altCount = Math.Min(3, count);
                var alternatives = new string[altCount];
                for (int i = 0; i < altCount; i++)
                    alternatives[i] = _templateEntries[top[i].TemplateIndex].Name;

                return ("Unknown", best.Cosine, false, alternatives);
            }

            var chord = best.Cosine >= _confidenceThreshold
                ? _templateEntries[best.TemplateIndex].Name
                : "Unknown";
            return (chord, best.Cosine, false, new string[0]);
        }

        /// <summary>
        /// Computes chromagram from notes, weighting each pitch class by amplitude × overlap duration.
        /// Longer-held notes contribute proportionally more than brief passing notes, which improves
        /// chord accuracy in windows that contain melodic ornaments or short non-chord tones.
        /// </summary>
        /// <param name="notes">The list of notes to convert to chromagram.</param>
        /// <param name="windowStart">Optional window start time for overlap calculation. Pass -1 to use note duration directly.</param>
        /// <param name="windowEnd">Optional window end time for overlap calculation. Pass -1 to use note duration directly.</param>
        /// <returns>A normalized 12-element array representing the pitch class distribution.</returns>
        public float[] ComputeChromagram(List<Note> notes, float windowStart = -1f, float windowEnd = -1f)
        {
            var chroma = new float[12];

            foreach (var note in notes)
            {
                var pitchClass = note.Pitch % 12;

                float duration;
                if (windowStart >= 0 && windowEnd > windowStart)
                {
                    duration = Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart);
                    duration = Math.Max(duration, 0f);
                }
                else
                {
                    duration = note.EndTime - note.StartTime;
                }

                chroma[pitchClass] += note.Amplitude * duration;
            }

            float sum = 0f;
            for (int i = 0; i < 12; i++)
                sum += chroma[i];

            if (sum > 0f)
            {
                float inverseSum = 1f / sum;
                for (int i = 0; i < 12; i++)
                    chroma[i] *= inverseSum;
            }

            return chroma;
        }

        /// <summary>
        /// Ranks all chord templates against a chromagram and fills the supplied buffer with the
        /// best candidates in descending score order. Performs no allocations or LINQ: the caller
        /// provides a (typically stack-allocated) span whose length defines how many candidates are kept.
        /// <para>
        /// The ranking score is the cosine similarity adjusted by two perceptual priors:
        /// a parsimony penalty proportional to the number of tones beyond a triad, and a diatonic
        /// bonus when every chord tone belongs to the active key. Each candidate still carries its
        /// raw cosine similarity so callers can report unbiased confidence.
        /// </para>
        /// </summary>
        /// <param name="chromagram">The 12-element chromagram to match.</param>
        /// <param name="top">A buffer receiving the top candidates; its length is the retention count.</param>
        /// <returns>The number of candidates written to <paramref name="top"/>.</returns>
        private int RankChords(float[] chromagram, Span<ScoredChord> top)
        {
            float chromaMagnitudeSquared = 0f;
            for (int i = 0; i < 12; i++)
                chromaMagnitudeSquared += chromagram[i] * chromagram[i];

            if (chromaMagnitudeSquared <= 0f)
                return 0;

            float inverseChromaMagnitude = (float)(1.0 / Math.Sqrt(chromaMagnitudeSquared));

            int capacity = top.Length;
            int filled = 0;
            var entries = _templateEntries;

            for (int e = 0; e < entries.Length; e++)
            {
                ref readonly TemplateEntry entry = ref entries[e];
                float[] vector = entry.Vector;

                float dot = 0f;
                for (int pc = 0; pc < 12; pc++)
                    dot += chromagram[pc] * vector[pc];

                float cosine = dot * inverseChromaMagnitude * entry.InverseMagnitude;
                if (cosine <= 0f)
                    continue;

                float score = cosine - ComplexityPenaltyPerTone * (entry.ToneCount - TriadToneCount);
                if (entry.IsDiatonic)
                    score += DiatonicBonus;

                if (filled == capacity && score <= top[capacity - 1].Score)
                    continue;

                int j = filled < capacity ? filled : capacity - 1;
                while (j > 0 && top[j - 1].Score < score)
                {
                    top[j] = top[j - 1];
                    j--;
                }

                top[j] = new ScoredChord(e, cosine, score);

                if (filled < capacity)
                    filled++;
            }

            return filled;
        }

        /// <summary>
        /// Generates explanation for chord analysis.
        /// </summary>
        /// <param name="pitchClasses">The pitch classes present in the chord.</param>
        /// <param name="chord">The detected chord name.</param>
        /// <param name="confidence">The confidence score of the detection.</param>
        /// <param name="isAmbiguous">Whether the chord detection is ambiguous.</param>
        /// <returns>A human-readable explanation of the chord analysis.</returns>
        private string GenerateExplanation(int[] pitchClasses, string chord, float confidence, bool isAmbiguous)
        {
            var noteNames = pitchClasses.Select(pc => ChordTemplates.GetNoteName(pc, _currentKey)).ToArray();
            var keyInfo = _currentKey != null ? $" (Key: {_currentKey})" : "";

            if (pitchClasses.Length < 2)
                return $"Too few notes ({pitchClasses.Length}) for reliable chord detection{keyInfo}.";

            if (isAmbiguous)
                return $"Ambiguous chord with notes [{string.Join(", ", noteNames)}]{keyInfo}. Multiple interpretations possible.";

            return confidence switch
            {
                >= 0.9f => $"Clear {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}.",
                >= 0.7f => $"Likely {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}.",
                >= 0.5f => $"Possible {chord} chord with notes [{string.Join(", ", noteNames)}]{keyInfo}, but uncertain.",
                _ => $"Unclear chord with notes [{string.Join(", ", noteNames)}]{keyInfo}. Consider adding more notes."
            };
        }

        /// <summary>
        /// Immutable, pre-computed view of a chord template optimised for matching.
        /// Caches values that are constant for a given template so the ranking loop avoids
        /// per-call square roots, tone counting and LINQ.
        /// </summary>
        private readonly struct TemplateEntry
        {
            /// <summary>
            /// The chord name this template represents (e.g. "Cmaj7").
            /// </summary>
            public readonly string Name;

            /// <summary>
            /// The 12-element weighted chromagram pattern of the chord.
            /// </summary>
            public readonly float[] Vector;

            /// <summary>
            /// The reciprocal of the template's Euclidean magnitude, pre-computed for cosine similarity.
            /// </summary>
            public readonly float InverseMagnitude;

            /// <summary>
            /// The number of distinct chord tones, used by the parsimony penalty.
            /// </summary>
            public readonly int ToneCount;

            /// <summary>
            /// Whether every chord tone lies within the active key's scale.
            /// </summary>
            public readonly bool IsDiatonic;

            /// <summary>
            /// Initializes a new template entry with its pre-computed matching metadata.
            /// </summary>
            /// <param name="name">The chord name.</param>
            /// <param name="vector">The weighted 12-element chromagram pattern.</param>
            /// <param name="inverseMagnitude">The reciprocal of the template's Euclidean magnitude.</param>
            /// <param name="toneCount">The number of distinct chord tones.</param>
            /// <param name="isDiatonic">Whether the chord is diatonic to the active key.</param>
            public TemplateEntry(string name, float[] vector, float inverseMagnitude, int toneCount, bool isDiatonic)
            {
                Name = name;
                Vector = vector;
                InverseMagnitude = inverseMagnitude;
                ToneCount = toneCount;
                IsDiatonic = isDiatonic;
            }
        }

        /// <summary>
        /// A ranked chord candidate carrying both its perceptual ranking score and its
        /// raw cosine similarity, so callers can rank by one and report confidence with the other.
        /// Stores the template index rather than the name so the struct is unmanaged and the
        /// ranking buffer can be stack-allocated; the name is resolved from <see cref="_templateEntries"/>.
        /// </summary>
        private readonly struct ScoredChord
        {
            /// <summary>
            /// Index of the matched template in <see cref="_templateEntries"/>.
            /// </summary>
            public readonly int TemplateIndex;

            /// <summary>
            /// The raw cosine similarity between the chromagram and this chord's template.
            /// </summary>
            public readonly float Cosine;

            /// <summary>
            /// The perceptual ranking score (cosine adjusted by parsimony and diatonic priors).
            /// </summary>
            public readonly float Score;

            /// <summary>
            /// Initializes a new scored chord candidate.
            /// </summary>
            /// <param name="templateIndex">Index of the matched template.</param>
            /// <param name="cosine">The raw cosine similarity.</param>
            /// <param name="score">The perceptual ranking score.</param>
            public ScoredChord(int templateIndex, float cosine, float score)
            {
                TemplateIndex = templateIndex;
                Cosine = cosine;
                Score = score;
            }
        }
    }
}
