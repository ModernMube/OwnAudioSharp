using System;
using System.Threading;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Synchronization
{
    /// <summary>
    /// Master clock for timeline-based audio synchronization.
    /// Provides sample-accurate timing for multi-track audio mixing.
    /// </summary>
    public sealed class MasterClock : IDisposable
    {
        private long _currentSamplePosition;      // Interlocked - lock-free
        private double _currentTimestamp;         // lock-protected
        private readonly int _sampleRate;
        private readonly int _channels;
        private volatile ClockMode _mode;
        private readonly object _positionLock = new object();
        private bool _disposed;

        /// <summary>
        /// Gets the current sample position (sample-accurate, lock-free read).
        /// </summary>
        public long CurrentSamplePosition => Interlocked.Read(ref _currentSamplePosition);

        /// <summary>
        /// Gets the current timestamp in seconds.
        /// </summary>
        public double CurrentTimestamp
        {
            get
            {
                lock (_positionLock)
                {
                    return _currentTimestamp;
                }
            }
        }

        /// <summary>
        /// Gets or sets the current rendering mode.
        /// </summary>
        public ClockMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        /// <summary>
        /// Gets the sample rate in Hz.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Gets the number of channels.
        /// </summary>
        public int Channels => _channels;

        /// <summary>
        /// Gets or sets whether the clock is controlled by network synchronization.
        /// When true, the clock is synchronized from network (client mode).
        /// When false, the clock advances normally (server or standalone mode).
        /// </summary>
        public bool IsNetworkControlled { get; set; }

        /// <summary>
        /// Initializes a new instance of the MasterClock class.
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz (typically 48000)</param>
        /// <param name="channels">Number of audio channels (typically 2 for stereo)</param>
        /// <param name="mode">Initial rendering mode (Realtime or Offline)</param>
        public MasterClock(int sampleRate, int channels, ClockMode mode = ClockMode.Realtime)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive");
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be positive");

            _sampleRate = sampleRate;
            _channels = channels;
            _mode = mode;
            _currentSamplePosition = 0;
            _currentTimestamp = 0.0;
        }

        /// <summary>
        /// Advances the master clock by the specified number of frames.
        /// Called by the MixThread after each buffer is processed.
        /// Thread-safe: Uses Interlocked operations for lock-free performance.
        /// </summary>
        /// <param name="frameCount">Number of frames to advance</param>
        public void Advance(int frameCount)
        {
            ThrowIfDisposed();

            if (frameCount <= 0)
                return;

            // Atomic increment of sample position (lock-free)
            long newSamplePosition = Interlocked.Add(ref _currentSamplePosition, frameCount);

            // Update timestamp (requires lock)
            lock (_positionLock)
            {
                _currentTimestamp = SamplePositionToTimestamp(newSamplePosition);
            }
        }

        /// <summary>
        /// Seeks the master clock to the specified timestamp.
        /// Called by the UI thread for user-initiated seeks.
        /// Thread-safe: Uses Interlocked.Exchange for atomic update.
        /// </summary>
        /// <param name="timestamp">Target timestamp in seconds</param>
        public void SeekTo(double timestamp)
        {
            ThrowIfDisposed();

            if (timestamp < 0.0)
                throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp cannot be negative");

            long targetSamplePosition = TimestampToSamplePosition(timestamp);

            // Atomic exchange for sample position
            Interlocked.Exchange(ref _currentSamplePosition, targetSamplePosition);

            // Update timestamp (requires lock)
            lock (_positionLock)
            {
                _currentTimestamp = timestamp;
            }
        }

        /// <summary>
        /// Resets the master clock to zero position.
        /// Thread-safe.
        /// </summary>
        public void Reset()
        {
            ThrowIfDisposed();

            Interlocked.Exchange(ref _currentSamplePosition, 0);

            lock (_positionLock)
            {
                _currentTimestamp = 0.0;
            }
        }

        /// <summary>
        /// Converts a timestamp (in seconds) to a sample position.
        /// </summary>
        /// <param name="timestamp">Timestamp in seconds</param>
        /// <returns>Sample position (frame count)</returns>
        public long TimestampToSamplePosition(double timestamp)
        {
            return (long)(timestamp * _sampleRate);
        }

        /// <summary>
        /// Converts a sample position to a timestamp (in seconds).
        /// </summary>
        /// <param name="samplePosition">Sample position (frame count)</param>
        /// <returns>Timestamp in seconds</returns>
        public double SamplePositionToTimestamp(long samplePosition)
        {
            return samplePosition / (double)_sampleRate;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MasterClock));
        }

        /// <summary>
        /// Disposes the MasterClock instance.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Rendering mode for the master clock.
    /// </summary>
    public enum ClockMode
    {
        /// <summary>
        /// Realtime mode: Non-blocking, dropouts result in silence and events.
        /// Used for live playback.
        /// </summary>
        Realtime,

        /// <summary>
        /// Offline mode: Blocking, waits for all tracks to be ready.
        /// Used for deterministic rendering to file.
        /// </summary>
        Offline,

        /// <summary>
        /// Network server mode: Acts as timing source for network synchronization.
        /// Uses internal timer and broadcasts to clients.
        /// </summary>
        NetworkServer,

        /// <summary>
        /// Network client mode: Synchronizes with network server.
        /// Receives timing updates from server.
        /// </summary>
        NetworkClient
    }
}
