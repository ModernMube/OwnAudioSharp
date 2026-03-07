using System.Runtime.CompilerServices;
using OwnaudioNET.Core;

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

        _shouldStop = false;
        _isRunning = true;
        _pauseEvent.Set();

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

        // Set running flag to false but keep thread alive
        // The thread loop will wait on _pauseEvent
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

        // Wait for mix thread to exit
        if (_mixThread.IsAlive)
        {
            if (!_mixThread.Join(TimeSpan.FromSeconds(2)))
            {
                // Thread didn't exit gracefully
            }
        }

        // Stop all sources
        foreach (var source in _sources.Values)
        {
            try
            {
                source.Stop();
            }
            catch
            {
                // Ignore errors when stopping sources
            }
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

        // Unregister from OwnaudioNet API
        OwnaudioNet.UnregisterAudioMixer(this);

        // Stop mixer
        if (_isRunning)
        {
            try
            {
                Stop();
            }
            catch
            {
                // Ignore errors
            }
        }

        // Stop recording
        StopRecording();

        // Clear all sources
        ClearSources();

        // Dispose all effects
        lock (_effectsLock)
        {
            foreach (var effect in _masterEffects)
            {
                try
                {
                    effect?.Dispose();
                }
                catch
                {
                    // Ignore errors during disposal
                }
            }
            _masterEffects.Clear();
        }

        // Dispose synchronization primitives
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

        // 1. Move the master clock — Playing sources self-correct via Three-Zone drift correction.
        _masterClock.SeekTo(positionInSeconds);

        // 2. Reactivate EndOfStream sources that have content at the new position.
        //    Use a fresh snapshot to avoid stale cached array.
        var sources = _sources.Values.ToArray();
        foreach (var source in sources)
        {
            if (source.State != AudioState.EndOfStream)
                continue;

            if (positionInSeconds >= source.Duration)
                continue;

            try
            {
                source.Seek(positionInSeconds);
                source.Play();
            }
            catch
            {
                // Reactivation failure is non-fatal; source stays silent.
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
