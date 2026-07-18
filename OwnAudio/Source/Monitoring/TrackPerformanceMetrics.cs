using System;
using System.Collections.Generic;

namespace OwnaudioNET.Monitoring
{
    /// <summary>
    /// Per-track health counters for the mixer: rolling CPU average, buffer fill and dropout log.
    /// </summary>
    public class TrackPerformanceMetrics
    {
        private readonly object _lock = new object();
        private readonly Queue<double> _cpuSamples;
        private readonly List<DropoutRecord> _dropoutHistory;
        private readonly int _maxCpuSamples;
        private readonly int _maxDropoutHistory;

        private double _bufferFillPercentage;
        private int _totalDropoutCount;
        private double _averageCpuUsage;

        /// <summary>
        /// Which track this belongs to.
        /// </summary>
        public Guid TrackId { get; }

        /// <summary>
        /// Track name, handy for the ToString dump.
        /// </summary>
        public string TrackName { get; }

        /// <summary>
        /// How full the track buffer is, 0..100. Higher is healthier.
        /// </summary>
        public double BufferFillPercentage
        {
            get { lock (_lock) { return _bufferFillPercentage; } }
        }

        /// <summary>
        /// Moving average CPU load over the recent samples, 0..100.
        /// </summary>
        public double AverageCpuUsage
        {
            get { lock (_lock) { return _averageCpuUsage; } }
        }

        /// <summary>
        /// How many dropouts we've seen in total.
        /// </summary>
        public int TotalDropoutCount
        {
            get { lock (_lock) { return _totalDropoutCount; } }
        }

        /// <summary>
        /// Snapshot copy of the dropout log.
        /// </summary>
        public IReadOnlyList<DropoutRecord> DropoutHistory
        {
            get { lock (_lock) { return _dropoutHistory.ToArray(); } }
        }

        /// <summary>
        /// New metrics bucket for a track. The two caps bound the CPU window and dropout log size.
        /// </summary>
        /// <param name="trackId"></param>
        /// <param name="trackName"></param>
        /// <param name="maxCpuSamples"></param>
        /// <param name="maxDropoutHistory"></param>
        public TrackPerformanceMetrics(
            Guid trackId,
            string trackName,
            int maxCpuSamples = 100,
            int maxDropoutHistory = 50)
        {
            TrackId = trackId;
            TrackName = trackName ?? string.Empty;
            _maxCpuSamples = maxCpuSamples;
            _maxDropoutHistory = maxDropoutHistory;

            _cpuSamples = new Queue<double>(maxCpuSamples);
            _dropoutHistory = new List<DropoutRecord>(maxDropoutHistory);
        }

        /// <summary>
        /// Pushes a CPU sample and recomputes the moving average.
        /// </summary>
        /// <param name="cpuPercentage"></param>
        public void RecordCpuSample(double cpuPercentage)
        {
            lock (_lock)
            {
                _cpuSamples.Enqueue(cpuPercentage);

                while (_cpuSamples.Count > _maxCpuSamples)
                    _cpuSamples.Dequeue();

                double sum = 0.0;
                foreach (var sample in _cpuSamples)
                    sum += sample;

                _averageCpuUsage = _cpuSamples.Count > 0 ? sum / _cpuSamples.Count : 0.0;
            }
        }

        /// <summary>
        /// Sets the buffer fill, clamped into 0..100.
        /// </summary>
        /// <param name="fillPercentage"></param>
        public void UpdateBufferFill(double fillPercentage)
        {
            lock (_lock) { _bufferFillPercentage = Math.Clamp(fillPercentage, 0.0, 100.0); }
        }

        /// <summary>
        /// Logs a dropout and trims the history back to the cap.
        /// </summary>
        /// <param name="timestamp"></param>
        /// <param name="missedFrames"></param>
        /// <param name="reason"></param>
        public void RecordDropout(double timestamp, int missedFrames, string? reason = null)
        {
            lock (_lock)
            {
                _totalDropoutCount++;

                _dropoutHistory.Add(new DropoutRecord
                {
                    Timestamp = timestamp,
                    MissedFrames = missedFrames,
                    Reason = reason ?? "Unknown",
                    EventTime = DateTime.UtcNow
                });

                while (_dropoutHistory.Count > _maxDropoutHistory)
                    _dropoutHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// Wipes everything back to zero.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _cpuSamples.Clear();
                _dropoutHistory.Clear();
                _bufferFillPercentage = 0.0;
                _totalDropoutCount = 0;
                _averageCpuUsage = 0.0;
            }
        }

        /// <summary>
        /// Quick one-glance dump of the current numbers.
        /// </summary>
        public override string ToString()
        {
            lock (_lock)
            {
                return $"Track: {TrackName} (ID: {TrackId})\n" +
                       $"  Buffer Fill: {_bufferFillPercentage:F1}%\n" +
                       $"  Avg CPU: {_averageCpuUsage:F2}%\n" +
                       $"  Dropouts: {_totalDropoutCount}";
            }
        }
    }

    /// <summary>
    /// One logged dropout - when it hit, how many frames went missing and why.
    /// </summary>
    public struct DropoutRecord
    {
        public double Timestamp { get; set; }
        public int MissedFrames { get; set; }
        public string Reason { get; set; }
        public DateTime EventTime { get; set; }

        /// <summary>
        /// Human-readable one-liner for the record.
        /// </summary>
        public override string ToString()
        {
            return $"Dropout at {Timestamp:F3}s: {MissedFrames} frames ({Reason})";
        }
    }
}
