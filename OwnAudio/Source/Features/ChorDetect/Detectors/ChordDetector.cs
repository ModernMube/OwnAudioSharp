using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
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
        /// Templates larger than a triad are penalised progressively per extra tone.
        /// </summary>
        private const int TriadToneCount = 3;

        /// <summary>
        /// Score penalty for the fourth chord tone (sevenths, sixths, add9). Small, so a
        /// genuine tetrad still wins on cosine similarity over its parent triad.
        /// </summary>
        private const float FourthTonePenalty = 0.012f;

        /// <summary>
        /// Score penalty for the fifth chord tone (9th chords). Noticeably higher than the
        /// fourth-tone step: five-tone templates cover so many pitch classes that they match
        /// mixed or noisy windows spuriously unless the voicing evidence is strong.
        /// </summary>
        private const float FifthTonePenalty = 0.06f;

        /// <summary>
        /// Score penalty for the sixth and any further chord tone (11th/13th chords).
        /// Six-tone templates span near-complete scales and therefore match the chimera
        /// chromagram of chord-transition windows almost perfectly; this steep step means
        /// they are only reported when they beat every simpler reading decisively, matching
        /// industry practice where recognition vocabularies stop at tetrads.
        /// </summary>
        private const float SixthTonePenalty = 0.12f;

        /// <summary>
        /// Weight of the missing-tone penalty: the score cost of template tones that have
        /// no supporting energy in the chromagram, as a fraction of the template's total
        /// weight. This is evidence-based parsimony — a seventh chord whose seventh is
        /// actually sounding pays nothing, while one hallucinated onto a bare triad is
        /// demoted well below the triad.
        /// </summary>
        private const float MissingTonePenaltyWeight = 0.4f;

        /// <summary>
        /// A template tone counts as missing when the chromagram energy at its pitch class
        /// is below this fraction of the chromagram's maximum bin.
        /// </summary>
        private const float MissingToneThresholdRatio = 0.05f;

        /// <summary>
        /// Score bonus applied to chords whose tones all fit the detected key's scale.
        /// Acts only as a tie-breaker: it nudges harmonically plausible (diatonic) chords ahead
        /// of enharmonic rivals without overriding a clearly stronger cosine match.
        /// </summary>
        private const float DiatonicBonus = 0.02f;

        /// <summary>
        /// Score bonus applied to chords whose root matches the lowest sounding pitch class.
        /// The bass note is the strongest root evidence in real music, so this prior resolves
        /// shared-subset ambiguities (e.g. C6 vs Am7, Cmaj7 vs Em/C) in favour of the chord
        /// whose root is actually in the bass, without overriding a clearly stronger match.
        /// </summary>
        private const float BassRootBonus = 0.03f;

        /// <summary>
        /// Minimum duration of the bass note relative to the longest note in the analysed set
        /// for it to count as root evidence. Filters out brief low passing tones that would
        /// otherwise mislead the bass-root prior.
        /// </summary>
        private const float BassMinimumDurationRatio = 0.25f;

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
            var (chord, confidence, isAmbiguous, alternatives) = DetectChordAdvanced(chromagram, ComputeBassPitchClass(notes));

            var (pitchClasses, noteNames) = BuildPresentNotes(notes, chord);

            return new ChordAnalysis(chord, confidence, GenerateExplanation(pitchClasses, chord, confidence, isAmbiguous), noteNames)
            {
                IsAmbiguous = isAmbiguous,
                Alternatives = alternatives,
                PitchClasses = pitchClasses,
                Chromagram = chromagram
            };
        }

        /// <summary>
        /// Builds the present pitch classes and their key-aware note names from a note list.
        /// When a known chord is supplied, the result is filtered to the chord's tones so
        /// non-chord (passing/ornament) pitches do not appear in the reported notes.
        /// </summary>
        /// <param name="notes">The notes whose pitch classes should be collected.</param>
        /// <param name="chord">The detected chord name used for filtering, or "Unknown"/"N" to skip filtering.</param>
        /// <returns>A tuple of the present pitch classes and their note names.</returns>
        private (int[] pitchClasses, string[] noteNames) BuildPresentNotes(List<Note> notes, string chord)
        {
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

            return (pitchClasses, noteNames);
        }

        /// <summary>
        /// Returns the key-aware note names of the pitches present in a note list, filtered
        /// to the tones of the given chord. Used by the song analyzer to label a window with
        /// the notes of the chord chosen by the progression decoder, which may differ from
        /// the locally best-matching chord.
        /// </summary>
        /// <param name="chordName">The chord whose tones act as the filter.</param>
        /// <param name="notes">The notes present in the analysed window.</param>
        /// <returns>An array of note names present in both the window and the chord.</returns>
        internal string[] GetChordNoteNames(string chordName, List<Note> notes)
        {
            return BuildPresentNotes(notes, chordName).noteNames;
        }

        /// <summary>
        /// Returns the duration a note contributes to an analysis window: the overlap with
        /// the window when bounds are supplied, otherwise the note's full duration.
        /// Mirrors the weighting rule of <see cref="ComputeChromagram"/>.
        /// </summary>
        /// <param name="note">The note whose effective duration is measured.</param>
        /// <param name="windowStart">Window start in seconds, or -1 to use the full duration.</param>
        /// <param name="windowEnd">Window end in seconds, or -1 to use the full duration.</param>
        /// <returns>The effective duration in seconds, never negative.</returns>
        private static float GetEffectiveDuration(Note note, float windowStart, float windowEnd)
        {
            if (windowStart >= 0f && windowEnd > windowStart)
            {
                float overlap = Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart);
                return Math.Max(overlap, 0f);
            }

            return note.EndTime - note.StartTime;
        }

        /// <summary>
        /// Determines the pitch class of the bass (lowest) note in a note list.
        /// Only notes lasting at least <see cref="BassMinimumDurationRatio"/> of the longest
        /// note are considered, so short low passing tones cannot masquerade as the bass.
        /// When window bounds are supplied, durations are clipped to the window so a note
        /// barely overlapping the window cannot dominate.
        /// </summary>
        /// <param name="notes">The notes to inspect.</param>
        /// <param name="windowStart">Optional window start for duration clipping. Pass -1 to use full durations.</param>
        /// <param name="windowEnd">Optional window end for duration clipping. Pass -1 to use full durations.</param>
        /// <returns>The pitch class (0-11) of the qualifying lowest note, or -1 when none qualifies.</returns>
        internal static int ComputeBassPitchClass(List<Note> notes, float windowStart = -1f, float windowEnd = -1f)
        {
            if (notes == null || notes.Count == 0)
                return -1;

            float maxDuration = 0f;
            foreach (var note in notes)
            {
                float duration = GetEffectiveDuration(note, windowStart, windowEnd);
                if (duration > maxDuration)
                    maxDuration = duration;
            }

            if (maxDuration <= 0f)
                return -1;

            float minimumDuration = maxDuration * BassMinimumDurationRatio;
            int bassPitch = int.MaxValue;

            foreach (var note in notes)
            {
                float duration = GetEffectiveDuration(note, windowStart, windowEnd);
                if (duration >= minimumDuration && note.Pitch < bassPitch)
                    bassPitch = note.Pitch;
            }

            return bassPitch == int.MaxValue ? -1 : bassPitch % 12;
        }

        /// <summary>
        /// Ranks the chord templates against the given notes and returns the best candidates
        /// with their raw cosine similarity, perceptual score and root pitch class.
        /// This is the lattice-building entry point used by the Viterbi progression decoder:
        /// unlike single-chord detection it applies no confidence threshold, so weak windows
        /// still yield candidates and the decoder can weigh them against the no-chord state.
        /// </summary>
        /// <param name="notes">The notes of the analysis window.</param>
        /// <param name="topN">The maximum number of candidates to return.</param>
        /// <param name="windowStart">Optional window start for duration clipping. Pass -1 to use full note durations.</param>
        /// <param name="windowEnd">Optional window end for duration clipping. Pass -1 to use full note durations.</param>
        /// <returns>The candidates in descending score order; empty when the window has no usable energy.</returns>
        internal List<ChordCandidate> GetChordCandidates(List<Note> notes, int topN = 8, float windowStart = -1f, float windowEnd = -1f)
        {
            var result = new List<ChordCandidate>();

            if (notes == null || notes.Count == 0 || topN < 1)
                return result;

            var chromagram = ComputeChromagram(notes, windowStart, windowEnd);
            int bassPitchClass = ComputeBassPitchClass(notes, windowStart, windowEnd);

            Span<ScoredChord> top = topN <= 32
                ? stackalloc ScoredChord[topN]
                : new ScoredChord[topN];

            int count = RankChords(chromagram, bassPitchClass, top);

            for (int i = 0; i < count; i++)
            {
                ref readonly TemplateEntry entry = ref _templateEntries[top[i].TemplateIndex];
                result.Add(new ChordCandidate(entry.Name, top[i].Cosine, top[i].Score, entry.Root));
            }

            return result;
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
        /// Detects the modulation-aware key timeline from a collection of notes.
        /// Songs without modulation yield a single segment; genuine modulations produce
        /// a new segment at the change point.
        /// </summary>
        /// <param name="notes">The list of notes to analyze.</param>
        /// <returns>The chronological list of key segments; empty when there are no notes.</returns>
        internal List<TimedKey> DetectKeyTimelineFromNotes(List<Note> notes)
        {
            return _keyDetector.DetectKeyTimeline(notes);
        }

        /// <summary>
        /// Processes notes for real-time detection with stability analysis.
        /// </summary>
        /// <param name="newNotes">The new notes to add to the processing buffer.</param>
        /// <returns>A tuple containing the most stable chord name and its stability score (0.0 to 1.0).</returns>
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

            int count = RankChords(chromagram, ComputeBassPitchClass(notes), top);

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
                int root = 0;
                float rootWeight = 0f;

                for (int pc = 0; pc < 12; pc++)
                {
                    float value = template[pc];
                    if (value > 0f)
                    {
                        magnitudeSquared += value * value;
                        toneCount++;
                        activeMask |= 1 << pc;

                        if (value > rootWeight)
                        {
                            rootWeight = value;
                            root = pc;
                        }
                    }
                }

                float inverseMagnitude = magnitudeSquared > 0f
                    ? (float)(1.0 / Math.Sqrt(magnitudeSquared))
                    : 0f;

                float weightSum = 0f;
                for (int pc = 0; pc < 12; pc++)
                    weightSum += template[pc];

                float inverseWeightSum = weightSum > 0f ? 1f / weightSum : 0f;

                bool isDiatonic = (activeMask & ~_scaleMask) == 0;

                entries[index++] = new TemplateEntry(
                    chordName, template, inverseMagnitude,
                    ComputeComplexityPenalty(toneCount), inverseWeightSum, isDiatonic, root);
            }

            _templateEntries = entries;
        }

        /// <summary>
        /// Computes the progressive parsimony penalty of a template from its tone count.
        /// The fourth tone is cheap (genuine tetrads must stay detectable), the fifth is
        /// noticeably more expensive, and the sixth and beyond are steep, because templates
        /// spanning near-complete scales match mixed windows spuriously.
        /// </summary>
        /// <param name="toneCount">The number of distinct chord tones in the template.</param>
        /// <returns>The cumulative score penalty for all tones beyond a triad.</returns>
        private static float ComputeComplexityPenalty(int toneCount)
        {
            float penalty = 0f;

            for (int tone = TriadToneCount + 1; tone <= toneCount; tone++)
            {
                penalty += tone switch
                {
                    4 => FourthTonePenalty,
                    5 => FifthTonePenalty,
                    _ => SixthTonePenalty
                };
            }

            return penalty;
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
        /// Candidates are ranked by a perceptual score (cosine similarity adjusted by the
        /// parsimony, missing-tone, diatonic and bass-root priors), but the reported confidence
        /// is always the winner's raw cosine similarity so the meaning of
        /// <see cref="_confidenceThreshold"/> stays unchanged. Ambiguity likewise compares
        /// perceptual scores, so candidates separated by the priors (e.g. a bare triad versus
        /// its unsupported seventh extension) are no longer reported as ambiguous.
        /// </summary>
        /// <param name="chromagram">The chromagram to analyze for chord detection.</param>
        /// <returns>A tuple containing the chord name, confidence, ambiguity flag, and alternative chord names.</returns>
        protected (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvancedBase(float[]? chromagram)
        {
            return DetectChordAdvanced(chromagram, -1);
        }

        /// <summary>
        /// Bass-aware variant of <see cref="DetectChordAdvancedBase(float[])"/>.
        /// The bass pitch class feeds the root prior in ranking, resolving shared-subset
        /// ambiguities in favour of the chord whose root is actually in the bass.
        /// Kept internal so the frozen public API surface stays unchanged.
        /// </summary>
        /// <param name="chromagram">The chromagram to analyze for chord detection.</param>
        /// <param name="bassPitchClass">The pitch class of the bass note, or -1 when unknown.</param>
        /// <returns>A tuple containing the chord name, confidence, ambiguity flag, and alternative chord names.</returns>
        internal (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvanced(float[]? chromagram, int bassPitchClass)
        {
            if (chromagram == null || _templateEntries.Length == 0)
                return ("Unknown", 0f, false, new string[0]);

            Span<ScoredChord> top = stackalloc ScoredChord[TopCandidateCount];
            int count = RankChords(chromagram, bassPitchClass, top);

            if (count == 0)
                return ("Unknown", 0f, false, new string[0]);

            ScoredChord best = top[0];

            if (_mode == DetectionMode.Optimized)
            {
                int ambiguousCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (best.Score - top[i].Score <= _ambiguityThreshold)
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
                            if (best.Score - top[i].Score <= _ambiguityThreshold)
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
        /// The ranking score is the cosine similarity adjusted by four perceptual priors:
        /// a progressive parsimony penalty for tones beyond a triad, an evidence-based
        /// missing-tone penalty for template tones with no support in the chromagram,
        /// a diatonic bonus when every chord tone belongs to the active key, and a bass-root
        /// bonus when the chord's root matches the lowest sounding pitch class. Each candidate
        /// still carries its raw cosine similarity so callers can report unbiased confidence.
        /// </para>
        /// </summary>
        /// <param name="chromagram">The 12-element chromagram to match.</param>
        /// <param name="bassPitchClass">The pitch class of the bass note for the root prior, or -1 when unknown.</param>
        /// <param name="top">A buffer receiving the top candidates; its length is the retention count.</param>
        /// <returns>The number of candidates written to <paramref name="top"/>.</returns>
        private int RankChords(float[] chromagram, int bassPitchClass, Span<ScoredChord> top)
        {
            float chromaMagnitudeSquared = 0f;
            float chromaMax = 0f;
            for (int i = 0; i < 12; i++)
            {
                float value = chromagram[i];
                chromaMagnitudeSquared += value * value;
                if (value > chromaMax)
                    chromaMax = value;
            }

            if (chromaMagnitudeSquared <= 0f)
                return 0;

            float inverseChromaMagnitude = (float)(1.0 / Math.Sqrt(chromaMagnitudeSquared));
            float missingThreshold = chromaMax * MissingToneThresholdRatio;

            int capacity = top.Length;
            int filled = 0;
            var entries = _templateEntries;

            for (int e = 0; e < entries.Length; e++)
            {
                ref readonly TemplateEntry entry = ref entries[e];
                float[] vector = entry.Vector;

                float dot = 0f;
                float missingWeight = 0f;
                for (int pc = 0; pc < 12; pc++)
                {
                    float templateWeight = vector[pc];
                    float chromaValue = chromagram[pc];

                    dot += chromaValue * templateWeight;

                    if (templateWeight > 0f && chromaValue < missingThreshold)
                        missingWeight += templateWeight;
                }

                float cosine = dot * inverseChromaMagnitude * entry.InverseMagnitude;
                if (cosine <= 0f)
                    continue;

                float score = cosine
                    - entry.ComplexityPenalty
                    - MissingTonePenaltyWeight * missingWeight * entry.InverseWeightSum;
                if (entry.IsDiatonic)
                    score += DiatonicBonus;
                if (bassPitchClass >= 0 && entry.Root == bassPitchClass)
                    score += BassRootBonus;

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
            /// The pre-computed progressive parsimony penalty of the template.
            /// </summary>
            public readonly float ComplexityPenalty;

            /// <summary>
            /// The reciprocal of the sum of all template weights, pre-computed so the
            /// missing-tone fraction needs no division in the matching loop.
            /// </summary>
            public readonly float InverseWeightSum;

            /// <summary>
            /// Whether every chord tone lies within the active key's scale.
            /// </summary>
            public readonly bool IsDiatonic;

            /// <summary>
            /// The root pitch class of the chord (0-11), identified as the template tone
            /// with the highest perceptual weight. Used by the bass-root prior.
            /// </summary>
            public readonly int Root;

            /// <summary>
            /// Initializes a new template entry with its pre-computed matching metadata.
            /// </summary>
            /// <param name="name">The chord name.</param>
            /// <param name="vector">The weighted 12-element chromagram pattern.</param>
            /// <param name="inverseMagnitude">The reciprocal of the template's Euclidean magnitude.</param>
            /// <param name="complexityPenalty">The pre-computed progressive parsimony penalty.</param>
            /// <param name="inverseWeightSum">The reciprocal of the sum of all template weights.</param>
            /// <param name="isDiatonic">Whether the chord is diatonic to the active key.</param>
            /// <param name="root">The root pitch class of the chord (0-11).</param>
            public TemplateEntry(string name, float[] vector, float inverseMagnitude, float complexityPenalty, float inverseWeightSum, bool isDiatonic, int root)
            {
                Name = name;
                Vector = vector;
                InverseMagnitude = inverseMagnitude;
                ComplexityPenalty = complexityPenalty;
                InverseWeightSum = inverseWeightSum;
                IsDiatonic = isDiatonic;
                Root = root;
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
