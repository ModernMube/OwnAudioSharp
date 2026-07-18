using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// How wide a net the detector casts.
    /// </summary>
    public enum DetectionMode
    {
        /// <summary>
        /// Triads and 7ths only.
        /// </summary>
        Basic,

        /// <summary>
        /// Adds 9ths, 11ths, 13ths and the altered stuff.
        /// </summary>
        Extended,

        /// <summary>
        /// Extended set, names spelled to fit the key.
        /// </summary>
        KeyAware,

        /// <summary>
        /// Extended set plus ambiguity reporting.
        /// </summary>
        Optimized
    }

    /// <summary>
    /// The chord matcher. Builds weighted templates for the current key, then scores a chromagram
    /// against all of them — cosine similarity nudged by a handful of musical priors.
    /// </summary>
    public class ChordDetector
    {
        /// <summary>
        /// Name to 12 bin pattern. Only the public template API uses this, matching goes through
        /// the baked entries.
        /// </summary>
        private Dictionary<string, float[]> _templates;

        /// <summary>
        /// The same templates with everything precomputed that the matching loop would otherwise
        /// redo per call — inverse magnitude, tone count, diatonic flag.
        /// </summary>
        private TemplateEntry[] _templateEntries;

        /// <summary>
        /// Candidates kept while ranking. Small and fixed so the buffer can live on the stack.
        /// </summary>
        private const int TopCandidateCount = 5;

        /// <summary>
        /// A triad is the baseline, everything above it pays.
        /// </summary>
        private const int TriadToneCount = 3;

        /// <summary>
        /// Fourth tone (7ths, 6ths, add9). Cheap, a real tetrad still has to be able to win.
        /// </summary>
        private const float FourthTonePenalty = 0.012f;

        /// <summary>
        /// Fifth tone (9ths). Steeper — five tones cover so much of the octave that they latch
        /// onto noisy windows unless the voicing really backs them up.
        /// </summary>
        private const float FifthTonePenalty = 0.06f;

        /// <summary>
        /// Sixth tone and beyond (11ths, 13ths). Steepest: those templates are basically whole
        /// scales and match the chimera chromagram of a chord change almost perfectly, so they
        /// only get reported when they beat every simpler reading outright.
        /// </summary>
        private const float SixthTonePenalty = 0.12f;

        /// <summary>
        /// How hard we hit template tones that have no energy behind them, as a fraction of the
        /// template's total weight. Parsimony with evidence: a seventh that's actually sounding
        /// costs nothing, one hallucinated onto a bare triad drops below the triad.
        /// </summary>
        private const float MissingTonePenaltyWeight = 0.4f;

        /// <summary>
        /// Below this fraction of the loudest bin a tone counts as not there.
        /// </summary>
        private const float MissingToneThresholdRatio = 0.05f;

        /// <summary>
        /// Tie-breaker for chords that sit entirely inside the key's scale. Small on purpose —
        /// it nudges, it doesn't overrule a clearly better cosine.
        /// </summary>
        private const float DiatonicBonus = 0.02f;

        /// <summary>
        /// Bonus when the chord root is the lowest sounding pitch class. The bass is the strongest
        /// root evidence there is, this settles C6 vs Am7 and Cmaj7 vs Em/C.
        /// </summary>
        private const float BassRootBonus = 0.03f;

        /// <summary>
        /// The bass note has to last at least this much of the longest note to count, otherwise
        /// a quick low passing tone would hijack the root prior.
        /// </summary>
        private const float BassMinimumDurationRatio = 0.25f;

        /// <summary>
        /// Scale of the active key as a 12 bit mask, all ones when we don't know the key.
        /// </summary>
        private int _scaleMask = 0xFFF;

        private readonly float _confidenceThreshold;
        private readonly float _ambiguityThreshold;
        private readonly KeyDetector _keyDetector;
        private MusicalKey _currentKey;
        private readonly DetectionMode _mode;

        /// <summary>
        /// Recent calls for the realtime stability vote. A null means that frame was under the
        /// threshold — it still counts in the denominator, we just don't vote with it.
        /// </summary>
        private readonly Queue<string?> _detectedChords = new Queue<string?>();

        private readonly int _bufferSize;

        /// <summary>
        /// ambiguityThreshold is how close a runner-up may come before we call it ambiguous,
        /// bufferSize is the realtime stability history length.
        /// </summary>
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
            _updateTemplates();
        }
        #nullable restore

        /// <summary>
        /// The full story for one set of notes: chord, confidence, explanation, the notes behind it.
        /// </summary>
        public ChordAnalysis AnalyzeChord(List<Note> notes)
        {
            if (notes.Count == 0)
                return new ChordAnalysis("N", 0.9f, "No notes detected", new string[0]);

            var chromagram = ComputeChromagram(notes);
            var (chord, confidence, isAmbiguous, alternatives) = DetectChordAdvanced(chromagram, ComputeBassPitchClass(notes));

            var (pitchClasses, noteNames) = _buildPresentNotes(notes, chord);

            return new ChordAnalysis(chord, confidence, _explain(noteNames, chord, confidence, isAmbiguous), noteNames)
            {
                IsAmbiguous = isAmbiguous,
                Alternatives = alternatives,
                PitchClasses = pitchClasses,
                Chromagram = chromagram
            };
        }

        /// <summary>
        /// Pitch classes actually sounding, plus their key-aware names. With a known chord the
        /// list is filtered to that chord's tones, so passing notes don't show up in the result.
        /// </summary>
        private (int[] pitchClasses, string[] noteNames) _buildPresentNotes(List<Note> notes, string chord)
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
        /// Note names of a window filtered to one chord's tones. The song analyzer needs this
        /// because the decoder may pick a different chord than the locally best matching one.
        /// </summary>
        internal string[] GetChordNoteNames(string chordName, List<Note> notes)
        {
            return _buildPresentNotes(notes, chordName).noteNames;
        }

        /// <summary>
        /// How much a note counts in a window: the overlap when we have bounds, the full length
        /// otherwise. Same rule the chromagram uses. Pass -1 for both bounds to skip clipping.
        /// </summary>
        private static float _effectiveDuration(Note note, float windowStart, float windowEnd)
        {
            if (windowStart >= 0f && windowEnd > windowStart)
                return Math.Max(Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart), 0f);

            return note.EndTime - note.StartTime;
        }

        /// <summary>
        /// Pitch class of the lowest note that lasts long enough to be taken seriously.
        /// -1 when nothing qualifies. Bounds of -1 mean full note durations.
        /// </summary>
        internal static int ComputeBassPitchClass(List<Note> notes, float windowStart = -1f, float windowEnd = -1f)
        {
            if (notes == null || notes.Count == 0) return -1;

            float maxDuration = 0f;
            foreach (var note in notes)
            {
                float duration = _effectiveDuration(note, windowStart, windowEnd);
                if (duration > maxDuration) maxDuration = duration;
            }

            if (maxDuration <= 0f) return -1;

            float minimumDuration = maxDuration * BassMinimumDurationRatio;
            int bassPitch = int.MaxValue;

            foreach (var note in notes)
            {
                float duration = _effectiveDuration(note, windowStart, windowEnd);
                if (duration >= minimumDuration && note.Pitch < bassPitch)
                    bassPitch = note.Pitch;
            }

            return bassPitch == int.MaxValue ? -1 : bassPitch % 12;
        }

        /// <summary>
        /// Lattice input for the Viterbi decoder. No threshold at all here on purpose — even a
        /// weak window hands back candidates so the decoder can weigh them against no-chord.
        /// </summary>
        /// <returns>Best first, empty when the window has no usable energy.</returns>
        internal List<ChordCandidate> GetChordCandidates(List<Note> notes, int topN = 8, float windowStart = -1f, float windowEnd = -1f)
        {
            var result = new List<ChordCandidate>();

            if (notes == null || notes.Count == 0 || topN < 1) return result;

            var chromagram = ComputeChromagram(notes, windowStart, windowEnd);
            int bassPitchClass = ComputeBassPitchClass(notes, windowStart, windowEnd);

            Span<ScoredChord> top = topN <= 32
                ? stackalloc ScoredChord[topN]
                : new ScoredChord[topN];

            int count = _rankChords(chromagram, bassPitchClass, top);

            for (int i = 0; i < count; i++)
            {
                ref readonly TemplateEntry entry = ref _templateEntries[top[i].TemplateIndex];
                result.Add(new ChordCandidate(entry.Name, top[i].Cosine, top[i].Score, entry.Root));
            }

            return result;
        }

        /// <summary>
        /// Null when we couldn't name it — that's the signal the pruning loop in SongChordAnalyzer
        /// waits for.
        /// </summary>
        public ChordAnalysis? TryAnalyzeChord(List<Note> notes)
        {
            if (notes.Count < 3) return null;

            var analysis = AnalyzeChord(notes);
            return (analysis.ChordName == "Unknown" || analysis.ChordName == "N") ? null : analysis;
        }

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
        /// Drops in your own template. pitchClasses must start with the root.
        /// </summary>
        public void AddChordTemplate(string chordName, int[] pitchClasses)
        {
            _templates[chordName] = ChordTemplates.CreateTemplate(pitchClasses);
        }

        /// <summary>
        /// A copy, so callers can't scribble on our templates.
        /// </summary>
        public Dictionary<string, float[]> GetChordTemplates()
        {
            return new Dictionary<string, float[]>(_templates);
        }

        private void _updateTemplates()
        {
            _templates = ChordTemplates.CreateAllTemplates(_currentKey, _mode != DetectionMode.Basic);
            _rebuildEntries();
        }

        /// <summary>
        /// Bakes the templates into the matching entries: inverse magnitude, parsimony penalty,
        /// weight sum and diatonic flag, so the ranking loop has no square roots or allocations left.
        /// </summary>
        private void _rebuildEntries()
        {
            _scaleMask = _buildScaleMask(_currentKey);

            var entries = new TemplateEntry[_templates.Count];
            int index = 0;

            foreach (var (chordName, template) in _templates)
            {
                float magnitudeSquared = 0f;
                float weightSum = 0f;
                int toneCount = 0;
                int activeMask = 0;
                int root = 0;
                float rootWeight = 0f;

                for (int pc = 0; pc < 12; pc++)
                {
                    float value = template[pc];
                    weightSum += value;

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

                entries[index++] = new TemplateEntry(
                    chordName, template, inverseMagnitude,
                    _complexityPenalty(toneCount),
                    weightSum > 0f ? 1f / weightSum : 0f,
                    (activeMask & ~_scaleMask) == 0, root);
            }

            _templateEntries = entries;
        }

        /// <summary>
        /// Adds up the per-tone penalties above a triad.
        /// </summary>
        private static float _complexityPenalty(int toneCount)
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

        /// <summary>
        /// The real one. Ranking happens on the perceptual score, but the confidence we report is
        /// always the raw cosine, so the threshold keeps meaning what it always meant. Ambiguity
        /// compares scores too — a bare triad and its unsupported seventh aren't "ambiguous" anymore,
        /// the priors already separated them. bassPitchClass of -1 means we don't know the bass.
        /// </summary>
        internal (string chord, float confidence, bool isAmbiguous, string[] alternatives) DetectChordAdvanced(float[]? chromagram, int bassPitchClass)
        {
            if (chromagram == null || _templateEntries.Length == 0)
                return ("Unknown", 0f, false, new string[0]);

            Span<ScoredChord> top = stackalloc ScoredChord[TopCandidateCount];
            int count = _rankChords(chromagram, bassPitchClass, top);

            if (count == 0) return ("Unknown", 0f, false, new string[0]);

            ScoredChord best = top[0];

            if (_mode == DetectionMode.Optimized)
            {
                int ambiguousCount = 0;
                for (int i = 0; i < count; i++)
                {
                    if (best.Score - top[i].Score <= _ambiguityThreshold) ambiguousCount++;
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

                        var combinedName = string.Join("/", ambiguous, 0, Math.Min(3, ambiguous.Length));
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
        /// Pitch class histogram weighted by amplitude times sounding time, then normalized —
        /// a held note counts for more than an ornament. Pass -1 for the bounds to use full
        /// note durations instead of the window overlap.
        /// </summary>
        public float[] ComputeChromagram(List<Note> notes, float windowStart = -1f, float windowEnd = -1f)
        {
            var chroma = new float[12];

            foreach (var note in notes)
                chroma[note.Pitch % 12] += note.Amplitude * _effectiveDuration(note, windowStart, windowEnd);

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
        /// The hot loop. Scores every template and keeps the best ones in the caller's buffer
        /// (usually stack allocated) — no allocations, no LINQ, insertion sort on the way in.
        /// <para>
        /// Score is cosine minus the parsimony penalty, minus the missing-tone penalty, plus the
        /// diatonic and bass-root bonuses. The raw cosine travels along untouched so callers can
        /// report honest confidence.
        /// </para>
        /// </summary>
        /// <returns>How many candidates actually landed in the buffer.</returns>
        private int _rankChords(float[] chromagram, int bassPitchClass, Span<ScoredChord> top)
        {
            float chromaMagnitudeSquared = 0f;
            float chromaMax = 0f;
            for (int i = 0; i < 12; i++)
            {
                float value = chromagram[i];
                chromaMagnitudeSquared += value * value;
                if (value > chromaMax) chromaMax = value;
            }

            if (chromaMagnitudeSquared <= 0f) return 0;

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
                if (cosine <= 0f) continue;

                float score = cosine
                    - entry.ComplexityPenalty
                    - MissingTonePenaltyWeight * missingWeight * entry.InverseWeightSum;
                if (entry.IsDiatonic) score += DiatonicBonus;
                if (bassPitchClass >= 0 && entry.Root == bassPitchClass) score += BassRootBonus;

                if (filled == capacity && score <= top[capacity - 1].Score) continue;

                int j = filled < capacity ? filled : capacity - 1;
                while (j > 0 && top[j - 1].Score < score)
                {
                    top[j] = top[j - 1];
                    j--;
                }

                top[j] = new ScoredChord(e, cosine, score);

                if (filled < capacity) filled++;
            }

            return filled;
        }

        /// <summary>
        /// The sentence that ends up in ChordAnalysis.Explanation. noteNames comes from the
        /// present-notes pass, no point recomputing it.
        /// </summary>
        private string _explain(string[] noteNames, string chord, float confidence, bool isAmbiguous)
        {
            var keyInfo = _currentKey != null ? $" (Key: {_currentKey})" : "";

            if (noteNames.Length < 2)
                return $"Too few notes ({noteNames.Length}) for reliable chord detection{keyInfo}.";

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
        /// A template with everything precomputed that stays constant for it.
        /// </summary>
        private readonly struct TemplateEntry
        {
            /// <summary>
            /// Chord name, e.g. "Cmaj7".
            /// </summary>
            public readonly string Name;

            /// <summary>
            /// The 12 bin weighted pattern.
            /// </summary>
            public readonly float[] Vector;

            /// <summary>
            /// 1 / magnitude, for the cosine.
            /// </summary>
            public readonly float InverseMagnitude;

            /// <summary>
            /// Parsimony penalty, baked in.
            /// </summary>
            public readonly float ComplexityPenalty;

            /// <summary>
            /// 1 / total weight, so the missing-tone fraction needs no division.
            /// </summary>
            public readonly float InverseWeightSum;

            /// <summary>
            /// True when every tone fits the key's scale.
            /// </summary>
            public readonly bool IsDiatonic;

            /// <summary>
            /// Root pitch class — the tone carrying the biggest weight.
            /// </summary>
            public readonly int Root;

            /// <summary>
            /// Everything precomputed by _rebuildEntries.
            /// </summary>
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
        /// A ranked hit. Holds the template index rather than the name so the struct stays
        /// unmanaged and the ranking buffer can sit on the stack.
        /// </summary>
        private readonly struct ScoredChord
        {
            /// <summary>
            /// Index into _templateEntries.
            /// </summary>
            public readonly int TemplateIndex;

            /// <summary>
            /// Raw cosine, what we report as confidence.
            /// </summary>
            public readonly float Cosine;

            /// <summary>
            /// Cosine after the priors, what we rank by.
            /// </summary>
            public readonly float Score;

            /// <summary>
            /// Straight assignment.
            /// </summary>
            public ScoredChord(int templateIndex, float cosine, float score)
            {
                TemplateIndex = templateIndex;
                Cosine = cosine;
                Score = score;
            }
        }
    }
}
