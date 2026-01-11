using System;
using System.Collections.Generic;

namespace OwnaudioNET.Monitoring
{
    /// <summary>
    /// Tracks performance metrics for individual audio tracks in the mixer.
    /// Provides CPU usage tracking, buffer fill monitoring, and dropout statistics.
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
        /// Gets the unique identifier of the track being monitored.
        /// </summary>
        public Guid TrackId { get; }

        /// <summary>
        /// Gets the name of the track being monitored.
        /// </summary>
        public string TrackName { get; }

        /// <summary>
        /// Gets the current buffer fill percentage (0.0 to 100.0).
        /// Higher values indicate better buffering.
        /// </summary>
        public double BufferFillPercentage
        {
            get
            {
                lock (_lock)
                {
                    return _bufferFillPercentage;
                }
            }
        }

        /// <summary>
        /// Gets the average CPU usage percentage (0.0 to 100.0) based on recent samples.
        /// </summary>
        public double AverageCpuUsage
        {
            get
            {
                lock (_lock)
                {
                    return _averageCpuUsage;
                }
            }
        }

        /// <summary>
        /// Gets the total number of dropouts that have occurred.
        /// </summary>
        public int TotalDropoutCount
        {
            get
            {
                lock (_lock)
                {
                    return _totalDropoutCount;
                }
            }
        }

        /// <summary>
        /// Gets a copy of the dropout history.
        /// </summary>
        public IReadOnlyList<DropoutRecord> DropoutHistory
        {
            get
            {
                lock (_lock)
                {
                    return _dropoutHistory.ToArray();
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the TrackPerformanceMetrics class.
        /// </summary>
        /// <param name="trackId">The unique identifier of the track</param>
        /// <param name="trackName">The name of the track</param>
        /// <param name="maxCpuSamples">Maximum number of CPU samples to keep for averaging (default: 100)</param>
        /// <param name="maxDropoutHistory">Maximum number of dropout records to keep (default: 50)</param>
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
            _bufferFillPercentage = 0.0;
            _totalDropoutCount = 0;
            _averageCpuUsage = 0.0;
        }

        /// <summary>
        /// Records a CPU usage sample.
        /// Maintains a moving average based on recent samples.
        /// </summary>
        /// <param name="cpuPercentage">CPU usage percentage (0.0 to 100.0)</param>
        public void RecordCpuSample(double cpuPercentage)
        {
            lock (_lock)
            {
                _cpuSamples.Enqueue(cpuPercentage);

                // Keep only the most recent samples
                while (_cpuSamples.Count > _maxCpuSamples)
                {
                    _cpuSamples.Dequeue();
                }

                // Calculate moving average
                double sum = 0.0;
                foreach (var sample in _cpuSamples)
                {
                    sum += sample;
                }
                _averageCpuUsage = _cpuSamples.Count > 0 ? sum / _cpuSamples.Count : 0.0;
            }
        }

        /// <summary>
        /// Updates the buffer fill percentage.
        /// </summary>
        /// <param name="fillPercentage">Fill percentage (0.0 to 100.0)</param>
        public void UpdateBufferFill(double fillPercentage)
        {
            lock (_lock)
            {
                _bufferFillPercentage = Math.Clamp(fillPercentage, 0.0, 100.0);
            }
        }

        /// <summary>
        /// Records a dropout event.
        /// </summary>
        /// <param name="timestamp">The master clock timestamp when the dropout occurred</param>
        /// <param name="missedFrames">The number of frames that were dropped</param>
        /// <param name="reason">The reason for the dropout</param>
        public void RecordDropout(double timestamp, int missedFrames, string? reason = null)
        {
            lock (_lock)
            {
                _totalDropoutCount++;

                var record = new DropoutRecord
                {
                    Timestamp = timestamp,
                    MissedFrames = missedFrames,
                    Reason = reason ?? "Unknown",
                    EventTime = DateTime.UtcNow
                };

                _dropoutHistory.Add(record);

                // Keep only the most recent dropout records
                while (_dropoutHistory.Count > _maxDropoutHistory)
                {
                    _dropoutHistory.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Resets all metrics to their initial state.
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
        /// Returns a summary string of the current metrics.
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
    /// Represents a single dropout event record.
    /// </summary>
    public struct DropoutRecord
    {
        /// <summary>
        /// The master clock timestamp when the dropout occurred.
        /// </summary>
        public double Timestamp { get; set; }

        /// <summary>
        /// The number of frames that were missed.
        /// </summary>
        public int MissedFrames { get; set; }

        /// <summary>
        /// The reason for the dropout.
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// The UTC time when this event was recorded.
        /// </summary>
        public DateTime EventTime { get; set; }

        /// <summary>
        /// Returns a string representation of the dropout record.
        /// </summary>
        public override string ToString()
        {
            return $"Dropout at {Timestamp:F3}s: {MissedFrames} frames ({Reason})";
        }
    }
}
