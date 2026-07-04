using System.Runtime.CompilerServices;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Starts or resumes the audio mixer.
    /// Sources can be added dynamically after starting.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Start()
    {
        ThrowIfDisposed();

        if (_isRunning)
            return;

        lock (_effectsLock)
        {
            foreach (var effect in _masterEffects)
            {
                if (!effect.IsReady)
                    throw new InvalidOperationException(
                        $"Cannot start mixer: effect '{effect.Name}' is not ready for audio processing. " +
                        $"For VST3 effects call and await VST3PluginHost.InitializeAudioAsync() first.");
            }
        }

        _shouldStop = false;
        _isRunning = true;
        _pauseEvent.Set();

        if (_rustNative)
        {
            StartRustOutput();
            StartRustSyncTick();
            return;
        }

        if (!_mixThread.IsAlive)
            _mixThread.Start();
    }

    /// <summary>
    /// Pauses the audio mixer without stopping the thread.
    /// Use <see cref="Start"/> to resume.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Pause()
    {
        ThrowIfDisposed();

        if (!_isRunning)
            return;

        if (_rustNative)
            PauseRustOutput();

        _isRunning = false;
        _pauseEvent.Reset();
    }

    /// <summary>
    /// Stops the audio mixer.
    /// All sources will be stopped but not removed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Stop()
    {
        ThrowIfDisposed();

        if (!_isRunning)
            return;

        _shouldStop = true;
        _pauseEvent.Reset();

        if (_rustNative)
        {
            StopRustOutput();
            StopRustSyncTick();
        }

        // if (_mixThread.IsAlive)
        // {
        //     if (!_mixThread.Join(TimeSpan.FromSeconds(2)))
        //     {
        //         // Thread didn't exit gracefully
        //     }
        // }

        // Stop all sources
        foreach (var source in _sources.Values)
        {
            try
            {
                source.Stop();
            }
            catch {}
        }

        _isRunning = false;
    }

    /// <summary>
    /// Disposes the mixer and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        OwnaudioNet.UnregisterAudioMixer(this);

        if (_isRunning)
        {
            try
            {
                Stop();
            }
            catch {}
        }

        StopRecording();

        ClearSources();

        if (_rustNative)
            DisposeRustSession();

        lock (_effectsLock)
        {
            foreach (var effect in _masterEffects)
            {
                try
                {
                    effect?.Dispose();
                }
                catch {}
            }
            _masterEffects.Clear();
        }

        _pauseEvent?.Dispose();

        _disposed = true;
    }

    /// <summary>
    /// Seeks the mixer to the specified position.
    /// Advances the MasterClock and automatically reactivates any EndOfStream sources
    /// whose content covers the new position.
    /// Sources attached to the MasterClock that are still playing self-correct via
    /// their built-in Three-Zone drift correction — no explicit seek needed for them.
    /// </summary>
    /// <param name="positionInSeconds">Target position in seconds (clamped to 0 if negative).</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void Seek(double positionInSeconds)
    {
        ThrowIfDisposed();

        if (positionInSeconds < 0)
            positionInSeconds = 0;

        // In Rust-native mode the managed MixThread / soft-sync path (which legacy Playing sources
        // rely on to self-correct toward the clock) does not run, so the seek must be applied to each
        // native track explicitly.
        if (_rustNative)
        {
            SeekRustNative(positionInSeconds);
            return;
        }

        // 1. Move the master clock — Playing sources self-correct via Three-Zone drift correction.
        _masterClock.SeekTo(positionInSeconds);

        // 2. Reactivate EndOfStream sources that have content at the new position.
        var sources = _sources.Values.ToArray();
        foreach (var source in sources)
        {
            if (source.State != AudioState.EndOfStream)
                continue;

            double targetPos = positionInSeconds;
            double sourceDuration = source.Duration;

            if (source is IMasterClockSource mcs)
            {
                double tempo = 1.0;
                if (source is FileSource fs)
                    tempo = fs.Tempo;

                if (positionInSeconds >= mcs.StartOffset + sourceDuration / tempo)
                    continue;

                targetPos = Math.Max(0.0, (positionInSeconds - mcs.StartOffset) * tempo);
            }
            else
            {
                if (positionInSeconds >= sourceDuration)
                    continue;
            }

            try
            {
                source.Seek(targetPos);
                source.Play();
            }
            catch {}
        }
    }

    /// <summary>
    /// Seeks the MasterClock to <paramref name="startPosition"/> and simultaneously
    /// starts all sources that are not yet in Playing state.
    /// Call this after adding sources via <see cref="AddSourcePrepared"/> and
    /// pre-buffering them with <see cref="FileSource.PreBuffer"/>.
    /// </summary>
    /// <param name="startPosition">The clock position (in seconds) to start from. Default: 0.0</param>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void StartPreparedSources(double startPosition = 0.0)
    {
        ThrowIfDisposed();

        // 1. Reset clock to ensure all sources start from the same reference point
        _masterClock.SeekTo(startPosition);

        // 2. Start all non-playing sources atomically (sequential loop is fast: no blocking)
        var sources = _sources.Values.ToArray();
        foreach (var source in sources)
        {
            if (source.State != AudioState.Playing)
            {
                try { source.Play(); }
                catch {}
            }
        }
    }

    /// <summary>
    /// Throws ObjectDisposedException if disposed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioMixer));
    }
}
