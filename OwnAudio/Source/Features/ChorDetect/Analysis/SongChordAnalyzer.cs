using OwnaudioNET.Features.Extensions;
using OwnaudioNET.Features.OwnChordDetect.Core;
using OwnaudioNET.Features.OwnChordDetect.Detectors;

namespace OwnaudioNET.Features.OwnChordDetect.Analysis
{
    /// <summary>
    /// A chord with the span it covers.
    /// </summary>
    public class TimedChord
    {
        /// <summary>
        /// Start, seconds.
        /// </summary>
        public float StartTime { get; }

        /// <summary>
        /// End, seconds.
        /// </summary>
        public float EndTime { get; }

        /// <summary>
        /// The chord we called it.
        /// </summary>
        public string ChordName { get; }

        /// <summary>
        /// 0..1.
        /// </summary>
        public float Confidence { get; }

        /// <summary>
        /// The notes behind it, spelled for the key.
        /// </summary>
        public string[] Notes { get; }

        /// <summary>
        /// All read-only, set once.
        /// </summary>
        public TimedChord(float startTime, float endTime, string chordName, float confidence, string[] notes)
        {
            StartTime = startTime;
            EndTime = endTime;
            ChordName = chordName;
            Confidence = confidence;
            Notes = notes;
        }

        /// <summary>
        /// Span, name, confidence and notes on one line.
        /// </summary>
        public override string ToString()
        {
            return $"{StartTime:F1}s-{EndTime:F1}s: {ChordName} ({Confidence:F2}) [{string.Join(", ", Notes)}]";
        }
    }

    /// <summary>
    /// Whole-song chord analysis. Cuts the song into tempo-derived windows, builds a candidate
    /// lattice per window (with progressive note pruning for the messy ones) and lets
    /// <see cref="ChordProgressionDecoder"/> pick the path — so the result is stable instead of
    /// window-by-window greedy.
    /// </summary>
    public class SongChordAnalyzer
    {
        /// <summary>
        /// How many hypotheses per window go into the lattice. Has to be generous: on a window
        /// straddling a chord change the union of two chords feeds all sorts of extended chimera
        /// templates that outscore the plain triads, and the decoder can only stay on the real
        /// chord if it's still among the states. 32 keeps the triads in, and 33² transitions
        /// per window costs nothing.
        /// </summary>
        private const int CandidatesPerWindow = 32;

        private readonly ChordDetector _detector;
        private readonly float _windowSize;
        private readonly float _hopSize;
        private readonly float _minimumChordDuration;

        /// <summary>
        /// Doubles as the no-chord emission in the decoder, so a window whose best guess is
        /// under this drops out the same way it used to fail the threshold.
        /// </summary>
        private readonly float _confidence;

        /// <summary>
        /// Notes alive in the current window. Reused, we're in the window loop.
        /// </summary>
        private readonly List<Note> _windowNotes = new List<Note>();

        /// <summary>
        /// Scratch list for the pruning pass, only touched when the full window fails.
        /// </summary>
        private readonly List<Note> _workingSet = new List<Note>();

        /// <summary>
        /// The song's key — with modulation, the one that holds for the longest total time.
        /// </summary>
        public MusicalKey? DetectedKey { get; private set; }

        /// <summary>
        /// Key segments of the last analysis, in order. One segment if nothing modulates.
        /// </summary>
        internal IReadOnlyList<TimedKey>? KeyTimeline { get; private set; }

        /// <summary>
        /// What the detector is currently set to, so we only pay for a template rebuild
        /// when we actually cross a segment boundary.
        /// </summary>
        private MusicalKey? _appliedKey;

        /// <summary>
        /// With bpm &gt; 0 the beat sets everything and windowSize/hopSize/minimumChordDuration are
        /// ignored: one quarter note per window, an eighth of hop, and nothing shorter than a
        /// quarter survives the merge. Anything faster than that isn't harmonic rhythm any more,
        /// anything slower smears chord changes together.
        /// </summary>
        public SongChordAnalyzer(
            float windowSize = 1.0f,
            float hopSize = 0.5f,
            float minimumChordDuration = 0.8f,
            float confidence = 0.6f,
            int bpm = 0)
        {
            _detector = new ChordDetector(DetectionMode.KeyAware, confidence);
            _confidence = confidence;

            if (bpm > 0)
            {
                float quarterNote = 60f / bpm;

                _windowSize = quarterNote;
                _hopSize = quarterNote / 2f;
                _minimumChordDuration = quarterNote;
            }
            else
            {
                _windowSize = windowSize;
                _hopSize = hopSize;
                _minimumChordDuration = minimumChordDuration;
            }
        }

        /// <summary>
        /// The full run: key timeline first, then the windows, then merge what belongs together.
        /// </summary>
        public List<TimedChord> AnalyzeSong(List<Note> songNotes)
        {
            if (songNotes.Count == 0) return new List<TimedChord>();

            var sortedNotes = songNotes.OrderBy(n => n.StartTime).ToList();

            var timeline = _detector.DetectKeyTimelineFromNotes(sortedNotes);
            if (timeline.Count == 0)
                timeline.Add(new TimedKey(0f, float.MaxValue, _detector.DetectKeyFromNotes(sortedNotes)));

            KeyTimeline = timeline;
            DetectedKey = _dominantKey(timeline);
            _applyKey(timeline[0].Key);

            return _mergeAdjacent(_analyzeWindows(sortedNotes));
        }

        /// <summary>
        /// Same, but you tell us the key and we skip detection.
        /// </summary>
        public List<TimedChord> AnalyzeSongInKey(List<Note> songNotes, MusicalKey key)
        {
            if (songNotes.Count == 0) return new List<TimedChord>();

            var sortedNotes = songNotes.OrderBy(n => n.StartTime).ToList();

            DetectedKey = key;
            KeyTimeline = new List<TimedKey> { new TimedKey(0f, float.MaxValue, key) };
            _applyKey(key);

            return _mergeAdjacent(_analyzeWindows(sortedNotes));
        }

        /// <summary>
        /// The key holding for the most total time, segments of the same key added up.
        /// </summary>
        private static MusicalKey _dominantKey(IReadOnlyList<TimedKey> timeline)
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

        private void _applyKey(MusicalKey key)
        {
            if (ReferenceEquals(_appliedKey, key)) return;

            _appliedKey = key;
            _detector.SetKey(key);
        }

        /// <summary>
        /// Picks the key segment covering the given position. Before the first or past the last
        /// segment we just take the nearest one.
        /// </summary>
        private void _applyKeyForTime(float time)
        {
            var timeline = KeyTimeline;
            if (timeline == null || timeline.Count == 0) return;

            for (int i = 0; i < timeline.Count; i++)
            {
                if (time < timeline[i].EndTime || i == timeline.Count - 1)
                {
                    _applyKey(timeline[i].Key);
                    return;
                }
            }
        }

        /// <summary>
        /// Window loop: collect the active notes, rank the top candidates with no threshold at all,
        /// then Viterbi the whole lattice and emit a chord for every window that didn't land on
        /// no-chord. Windows with fewer than 3 notes go in empty.
        /// </summary>
        private List<WindowChord> _analyzeWindows(List<Note> notes)
        {
            float songDuration = 0f;
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].EndTime > songDuration) songDuration = notes[i].EndTime;
            }

            var windows = new List<ChordWindow>();

            for (float time = 0; time < songDuration; time += _hopSize)
            {
                var windowEnd = Math.Min(time + _windowSize, songDuration);
                _applyKeyForTime((time + windowEnd) * 0.5f);
                _collectWindowNotes(notes, time, windowEnd);

                ChordCandidate[] candidates;
                if (_windowNotes.Count < 3)
                {
                    candidates = Array.Empty<ChordCandidate>();
                }
                else
                {
                    var noteSet = _selectNoteSet(time, windowEnd);
                    candidates = _detector.GetChordCandidates(noteSet, CandidatesPerWindow, time, windowEnd).ToArray();
                }

                windows.Add(new ChordWindow(time, windowEnd, candidates));
            }

            var decoder = new ChordProgressionDecoder(_confidence);
            int[] selection = decoder.Decode(windows);

            var chords = new List<WindowChord>();

            for (int i = 0; i < windows.Count; i++)
            {
                if (selection[i] == ChordProgressionDecoder.NoChordState) continue;

                var window = windows[i];
                var candidate = window.Candidates[selection[i]];

                _applyKeyForTime((window.StartTime + window.EndTime) * 0.5f);
                _collectWindowNotes(notes, window.StartTime, window.EndTime);
                var noteNames = _detector.GetChordNoteNames(candidate.Name, _windowNotes);

                chords.Add(new WindowChord(
                    _regionStart(windows, i), _regionEnd(windows, i),
                    window.StartTime, window.EndTime,
                    candidate.Name, candidate.Cosine, noteNames));
            }

            return chords;
        }

        /// <summary>
        /// Where the part of the timeline a window speaks for begins: halfway between its centre
        /// and the previous window's. The first window reaches back to its own start.
        /// </summary>
        private static float _regionStart(List<ChordWindow> windows, int index)
        {
            if (index == 0) return windows[0].StartTime;

            return (_center(windows[index - 1]) + _center(windows[index])) * 0.5f;
        }

        /// <summary>
        /// Where it ends: halfway to the next window's centre, the last one runs to its own end.
        /// Consecutive regions tile the song without overlapping, one hop each.
        /// </summary>
        private static float _regionEnd(List<ChordWindow> windows, int index)
        {
            if (index == windows.Count - 1) return windows[index].EndTime;

            return (_center(windows[index]) + _center(windows[index + 1])) * 0.5f;
        }

        private static float _center(in ChordWindow window)
        {
            return (window.StartTime + window.EndTime) * 0.5f;
        }

        private void _collectWindowNotes(List<Note> allNotes, float windowStart, float windowEnd)
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
        /// Which notes to rank from. Full set if it already detects as a chord; otherwise we keep
        /// throwing away the weakest note — least amplitude times time inside the window, the
        /// passing tones and ornaments — until something clicks or only 3 are left. The bass note
        /// is never thrown away, it's the best root evidence the window has. If nothing clicks,
        /// the full set goes in anyway — the scores will be low and the decoder can still call it
        /// no-chord. Every check sees the same window-clipped chromagram the candidates are built from.
        /// </summary>
        private List<Note> _selectNoteSet(float windowStart, float windowEnd)
        {
            if (_detector.MatchesChord(_windowNotes, windowStart, windowEnd)) return _windowNotes;

            int bassPitch = ChordDetector.ComputeBassPitch(_windowNotes, windowStart, windowEnd);

            _workingSet.Clear();
            _workingSet.AddRange(_windowNotes);
            _workingSet.Sort((a, b) =>
                _weight(a, windowStart, windowEnd).CompareTo(_weight(b, windowStart, windowEnd)));

            while (_workingSet.Count > 3)
            {
                int victim = _workingSet.FindIndex(note => note.Pitch != bassPitch);
                if (victim < 0) break;

                _workingSet.RemoveAt(victim);

                if (_detector.MatchesChord(_workingSet, windowStart, windowEnd)) return _workingSet;
            }

            return _windowNotes;
        }

        /// <summary>
        /// What a note contributes to the window's chromagram: amplitude times clipped overlap.
        /// </summary>
        private static float _weight(Note note, float windowStart, float windowEnd)
        {
            float overlap = Math.Min(note.EndTime, windowEnd) - Math.Max(note.StartTime, windowStart);
            return note.Amplitude * Math.Max(overlap, 0f);
        }

        /// <summary>
        /// How far apart two regions may sit and still count as touching. Regions of consecutive
        /// windows share the exact same boundary, this only soaks up float noise.
        /// </summary>
        private const float RegionTouchTolerance = 0.0001f;

        /// <summary>
        /// Glues neighbouring windows with the same chord into one segment, hands segments shorter
        /// than the minimum over to the chords they sit between, and drops only the short ones
        /// with no chord next to them. Adjacency, the averaging and the minimum all go by the
        /// windows' evidence span; only the reported times come from the non-overlapping regions.
        /// </summary>
        private List<TimedChord> _mergeAdjacent(List<WindowChord> rawChords)
        {
            var segments = _joinRuns(rawChords);
            _absorbShortSegments(segments);

            var merged = new List<TimedChord>(segments.Count);
            foreach (var segment in segments)
            {
                if (_longEnough(segment)) merged.Add(segment.ToTimedChord());
            }

            return merged;
        }

        /// <summary>
        /// Runs of the same chord become one segment, confidence averaged by span.
        /// </summary>
        private List<WindowChord> _joinRuns(List<WindowChord> rawChords)
        {
            var segments = new List<WindowChord>();
            if (rawChords.Count == 0) return segments;

            var current = rawChords[0];

            for (int i = 1; i < rawChords.Count; i++)
            {
                var next = rawChords[i];

                if (current.ChordName == next.ChordName &&
                    Math.Abs(current.SpanEnd - next.SpanStart) <= _hopSize * 1.5f)
                {
                    current = _combine(current, next);
                }
                else
                {
                    segments.Add(current);
                    current = next;
                }
            }

            segments.Add(current);
            return segments;
        }

        /// <summary>
        /// Too-short segments that touch a chord give their time away instead of leaving a hole,
        /// shortest first. Between two different chords the boundary moves to the middle of the
        /// short one, between two of the same chord the three become one, next to a single chord
        /// that chord takes it all. Isolated short segments stay for the final filter to drop.
        /// </summary>
        private void _absorbShortSegments(List<WindowChord> segments)
        {
            while (true)
            {
                int shortest = -1;
                float shortestSpan = float.MaxValue;

                for (int i = 0; i < segments.Count; i++)
                {
                    var segment = segments[i];
                    float span = segment.SpanEnd - segment.SpanStart;

                    if (!_longEnough(segment)
                        && (_touches(segments, i - 1, i) || _touches(segments, i, i + 1))
                        && span < shortestSpan)
                    {
                        shortest = i;
                        shortestSpan = span;
                    }
                }

                if (shortest < 0) return;

                _absorb(segments, shortest);
            }
        }

        private void _absorb(List<WindowChord> segments, int index)
        {
            var segment = segments[index];
            bool hasPrevious = _touches(segments, index - 1, index);
            bool hasNext = _touches(segments, index, index + 1);

            if (hasPrevious && hasNext)
            {
                var previous = segments[index - 1];
                var next = segments[index + 1];

                if (previous.ChordName == next.ChordName)
                {
                    segments[index - 1] = _combine(previous, next);
                    segments.RemoveRange(index, 2);
                    return;
                }

                float regionMiddle = (segment.RegionStart + segment.RegionEnd) * 0.5f;
                float spanMiddle = (segment.SpanStart + segment.SpanEnd) * 0.5f;

                segments[index - 1] = previous.WithEnd(regionMiddle, Math.Max(previous.SpanEnd, spanMiddle));
                segments[index + 1] = next.WithStart(regionMiddle, Math.Min(next.SpanStart, spanMiddle));
            }
            else if (hasPrevious)
            {
                var previous = segments[index - 1];
                segments[index - 1] = previous.WithEnd(segment.RegionEnd, Math.Max(previous.SpanEnd, segment.SpanEnd));
            }
            else
            {
                var next = segments[index + 1];
                segments[index + 1] = next.WithStart(segment.RegionStart, Math.Min(next.SpanStart, segment.SpanStart));
            }

            segments.RemoveAt(index);
        }

        /// <summary>
        /// True when both indices exist and the second segment starts right where the first ends.
        /// A no-chord window between them leaves a gap, and silence doesn't get painted over.
        /// </summary>
        private static bool _touches(List<WindowChord> segments, int first, int second)
        {
            if (first < 0 || second >= segments.Count) return false;

            return segments[second].RegionStart - segments[first].RegionEnd <= RegionTouchTolerance;
        }

        /// <summary>
        /// Two segments of the same chord as one, from the first's start to the second's end,
        /// confidence averaged by span.
        /// </summary>
        private static WindowChord _combine(in WindowChord first, in WindowChord second)
        {
            float firstDuration = first.SpanEnd - first.SpanStart;
            float secondDuration = second.SpanEnd - second.SpanStart;
            float totalDuration = firstDuration + secondDuration;

            float confidence = totalDuration > 0f
                ? (first.Confidence * firstDuration + second.Confidence * secondDuration) / totalDuration
                : (first.Confidence + second.Confidence) / 2f;

            return new WindowChord(
                first.RegionStart, second.RegionEnd,
                first.SpanStart, second.SpanEnd,
                first.ChordName, confidence, first.Notes);
        }

        /// <summary>
        /// On the tempo path the minimum is exactly one window long, and the float drift the window
        /// loop accumulates would shave a hair off some of them — so we let a millisecond slide.
        /// </summary>
        private bool _longEnough(in WindowChord chord)
        {
            return chord.SpanEnd - chord.SpanStart >= _minimumChordDuration - 0.001f;
        }

        /// <summary>
        /// A decoded window, or a run of them merged. Region is the stretch of the timeline it
        /// gets reported for, span is the audio its evidence came from — overlapping windows make
        /// the span wider than the region.
        /// </summary>
        private readonly struct WindowChord
        {
            /// <summary>
            /// Reported start, seconds.
            /// </summary>
            internal readonly float RegionStart;

            /// <summary>
            /// Reported end, seconds.
            /// </summary>
            internal readonly float RegionEnd;

            /// <summary>
            /// Start of the first window behind it.
            /// </summary>
            internal readonly float SpanStart;

            /// <summary>
            /// End of the last window behind it.
            /// </summary>
            internal readonly float SpanEnd;

            /// <summary>
            /// The decoded chord.
            /// </summary>
            internal readonly string ChordName;

            /// <summary>
            /// Raw cosine, span-weighted once merged.
            /// </summary>
            internal readonly float Confidence;

            /// <summary>
            /// Note names of the first window.
            /// </summary>
            internal readonly string[] Notes;

            /// <summary>
            /// Straight assignment.
            /// </summary>
            internal WindowChord(float regionStart, float regionEnd, float spanStart, float spanEnd,
                string chordName, float confidence, string[] notes)
            {
                RegionStart = regionStart;
                RegionEnd = regionEnd;
                SpanStart = spanStart;
                SpanEnd = spanEnd;
                ChordName = chordName;
                Confidence = confidence;
                Notes = notes;
            }

            /// <summary>
            /// Same chord, reaching to a new end.
            /// </summary>
            internal WindowChord WithEnd(float regionEnd, float spanEnd)
            {
                return new WindowChord(RegionStart, regionEnd, SpanStart, spanEnd, ChordName, Confidence, Notes);
            }

            /// <summary>
            /// Same chord, starting earlier.
            /// </summary>
            internal WindowChord WithStart(float regionStart, float spanStart)
            {
                return new WindowChord(regionStart, RegionEnd, spanStart, SpanEnd, ChordName, Confidence, Notes);
            }

            /// <summary>
            /// The public shape, with the region as its time span.
            /// </summary>
            internal TimedChord ToTimedChord()
            {
                return new TimedChord(RegionStart, RegionEnd, ChordName, Confidence, Notes);
            }
        }
    }
}
