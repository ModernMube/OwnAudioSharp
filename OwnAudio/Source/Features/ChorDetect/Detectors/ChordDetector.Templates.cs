using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Analysis;
using OwnaudioNET.Features.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace OwnaudioNET.Features.OwnChordDetect.Detectors
{
    /// <summary>
    /// The chord template table and the penalties that shape how it is scored.
    /// </summary>
    public partial class ChordDetector
    {
        /// <summary>
        /// Chords added through <see cref="AddChordTemplate"/>, kept so a key change that rebuilds
        /// the built-in set doesn't wipe them.
        /// </summary>
        private readonly Dictionary<string, int[]> _customChords = new Dictionary<string, int[]>();

        /// <summary>
        /// Drops in your own template. pitchClasses must start with the root. Takes part in
        /// detection right away and survives later key changes.
        /// </summary>
        public void AddChordTemplate(string chordName, int[] pitchClasses)
        {
            var copy = (int[])pitchClasses.Clone();
            _customChords[chordName] = copy;
            _templates[chordName] = ChordTemplates.CreateTemplate(copy);
            _rebuildEntries();
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

            foreach (var (chordName, pitchClasses) in _customChords)
                _templates[chordName] = ChordTemplates.CreateTemplate(pitchClasses);

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
    }
}
