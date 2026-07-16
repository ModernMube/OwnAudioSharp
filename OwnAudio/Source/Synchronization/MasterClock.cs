using System;
using System.Threading;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;

namespace OwnaudioNET.Synchronization
{
    /// <summary>
    /// Sample-accurate timeline clock the MixThread advances; drives multi-track sync.
    /// </summary>
    public sealed class MasterClock : IDisposable
    {
        private long _samplePos;        // Interlocked, lock-free read
        private double _timestamp;      // guarded by _lock
        private readonly int _sampleRate;
        private readonly int _channels;
        private volatile ClockMode _mode;
        private readonly object _lock = new object();
        private bool _disposed;

        // lock-free frame counter
        public long CurrentSamplePosition => Interlocked.Read(ref _samplePos);

        // seconds; mirrors the sample pos
        public double CurrentTimestamp { get { lock (_lock) return _timestamp; } }

        public ClockMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        public int SampleRate => _sampleRate;

        public int Channels => _channels;

        // when true the clock is driven from the network (client), not advanced locally
        public bool IsNetworkControlled { get; set; }

        public MasterClock(int sampleRate, int channels, ClockMode mode = ClockMode.Realtime)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            _sampleRate = sampleRate;
            _channels = channels;
            _mode = mode;
        }

        // MixThread calls this after each buffer; lock-free bump + timestamp refresh
        public void Advance(int frameCount)
        {
            ThrowIfDisposed();
            if (frameCount <= 0) return;

            long pos = Interlocked.Add(ref _samplePos, frameCount);
            lock (_lock) _timestamp = SamplePositionToTimestamp(pos);
        }

        // user seek from the UI thread
        public void SeekTo(double timestamp)
        {
            ThrowIfDisposed();
            if (timestamp < 0.0) throw new ArgumentOutOfRangeException(nameof(timestamp));

            Interlocked.Exchange(ref _samplePos, TimestampToSamplePosition(timestamp));
            lock (_lock) _timestamp = timestamp;
        }

        public void Reset()
        {
            ThrowIfDisposed();
            Interlocked.Exchange(ref _samplePos, 0);
            lock (_lock) _timestamp = 0.0;
        }

        public long TimestampToSamplePosition(double timestamp) => (long)(timestamp * _sampleRate);

        public double SamplePositionToTimestamp(long samplePosition) => samplePosition / (double)_sampleRate;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MasterClock));
        }

        public void Dispose() => _disposed = true;
    }

    /// <summary>
    /// How the master clock is driven.
    /// </summary>
    public enum ClockMode
    {
        // live playback: non-blocking, dropouts turn into silence
        Realtime,
        // render to file: blocking, waits for every track
        Offline,
        // timing source, broadcasts to clients
        NetworkServer,
        // follows a server's timing
        NetworkClient
    }
}
