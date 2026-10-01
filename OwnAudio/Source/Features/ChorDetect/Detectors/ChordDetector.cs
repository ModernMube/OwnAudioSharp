using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// The chord matcher. Builds weighted templates for the current key, then scores a chromagram
    /// against all of them — cosine similarity nudged by a handful of musical priors. Key
    /// handling, the templates and the scoring pass live in the sibling partials.
    /// </summary>
    public partial class ChordDetector
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
        /// Highest MIDI pitch (E5) that still counts with full weight in the chromagram.
        /// </summary>
        private const int MelodyRegisterStart = 76;

        /// <summary>
        /// Semitones above <see cref="MelodyRegisterStart"/> where a note's weight has halved.
        /// </summary>
        private const float MelodyRegisterHalfLife = 12f;

        /// <summary>
        /// γ of the chromagram's log compression. Mild on purpose: doubled roots and fifths stop
        /// dominating, but a quiet passing tone stays well below the chord tones.
        /// </summary>
        private const double ChromaCompression = 3.0;

        /// <summary>
        /// Joins the tied readings of an ambiguous call in Optimized mode. Not a slash, that one
        /// means an inversion.
        /// </summary>
        private const string AmbiguitySeparator = " | ";

        /// <summary>
        /// Sampling step for following the lowest sounding note through a span.
        /// </summary>
        private const float BassLineStepSeconds = 0.02f;

        /// <summary>
        /// Share of the time one pitch class has to hold the bottom before it counts as the bass.
        /// An alternating root–fifth bass never gets there, a held inversion does.
        /// </summary>
        private const float BassLineDominance = 0.6f;

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

            if (!isAmbiguous)
                chord = NameWithBass(chord, ComputeBassLine(notes));

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
            int bassPitch = ComputeBassPitch(notes, windowStart, windowEnd);
            return bassPitch < 0 ? -1 : bassPitch % 12;
        }

        /// <summary>
        /// MIDI pitch of the lowest note that lasts long enough to be taken seriously, -1 when
        /// nothing qualifies. Same rule as <see cref="ComputeBassPitchClass"/>, without folding
        /// to a pitch class, so callers can tell the bass note itself apart from its octaves.
        /// </summary>
        internal static int ComputeBassPitch(List<Note> notes, float windowStart = -1f, float windowEnd = -1f)
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

            return bassPitch == int.MaxValue ? -1 : bassPitch;
        }

        /// <summary>
        /// The pitch class holding the bottom of the texture across a span: at every step the
        /// lowest sounding note gets the vote, weighted by its amplitude. -1 unless one pitch class
        /// holds at least <see cref="BassLineDominance"/> of it. Bounds of -1 take the span of the
        /// notes themselves.
        /// </summary>
        internal static int ComputeBassLine(List<Note> notes, float spanStart = -1f, float spanEnd = -1f)
        {
            if (notes == null || notes.Count == 0) return -1;

            if (spanStart < 0f || spanEnd <= spanStart)
            {
                spanStart = float.MaxValue;
                spanEnd = float.MinValue;
                foreach (var note in notes)
                {
                    if (note.StartTime < spanStart) spanStart = note.StartTime;
                    if (note.EndTime > spanEnd) spanEnd = note.EndTime;
                }

                if (spanEnd <= spanStart) return -1;
            }

            Span<float> votes = stackalloc float[12];
            votes.Clear();
            float total = 0f;

            int steps = Math.Max(1, (int)Math.Ceiling((spanEnd - spanStart) / BassLineStepSeconds));
            float step = (spanEnd - spanStart) / steps;

            for (int s = 0; s < steps; s++)
            {
                float time = spanStart + (s + 0.5f) * step;
                int lowestPitch = int.MaxValue;
                float lowestAmplitude = 0f;

                foreach (var note in notes)
                {
                    if (note.StartTime <= time && time < note.EndTime && note.Pitch < lowestPitch)
                    {
                        lowestPitch = note.Pitch;
                        lowestAmplitude = note.Amplitude;
                    }
                }

                if (lowestPitch == int.MaxValue) continue;

                votes[lowestPitch % 12] += lowestAmplitude;
                total += lowestAmplitude;
            }

            if (total <= 0f) return -1;

            int bass = 0;
            for (int pc = 1; pc < 12; pc++)
            {
                if (votes[pc] > votes[bass]) bass = pc;
            }

            return votes[bass] >= total * BassLineDominance ? bass : -1;
        }

        /// <summary>
        /// The chord name with its inversion: "C/E" when the bass is a chord tone other than the
        /// root, the plain name otherwise — root position, no clear bass, a bass outside the
        /// chord, or a name we have no template for. The bass is spelled for the current key.
        /// </summary>
        internal string NameWithBass(string chordName, int bassPitchClass)
        {
            if (bassPitchClass < 0 || !_templates.TryGetValue(chordName, out var template))
                return chordName;

            int root = 0;
            for (int pc = 1; pc < 12; pc++)
            {
                if (template[pc] > template[root]) root = pc;
            }

            if (bassPitchClass == root || template[bassPitchClass] <= 0f)
                return chordName;

            return chordName + "/" + ChordTemplates.GetNoteName(bassPitchClass, _currentKey);
        }

        /// <summary>
        /// Whether the notes name a chord above the confidence threshold inside the given window.
        /// Chromagram and bass are clipped to the window exactly like the lattice candidates, and
        /// only the best match is ranked — no names, explanations or allocations beyond the chroma.
        /// </summary>
        internal bool MatchesChord(List<Note> notes, float windowStart, float windowEnd)
        {
            if (notes == null || notes.Count < 3) return false;

            var chromagram = ComputeChromagram(notes, windowStart, windowEnd);

            Span<ScoredChord> top = stackalloc ScoredChord[1];
            int count = _rankChords(chromagram, ComputeBassPitchClass(notes, windowStart, windowEnd), top);

            return count > 0 && top[0].Cosine >= _confidenceThreshold;
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
