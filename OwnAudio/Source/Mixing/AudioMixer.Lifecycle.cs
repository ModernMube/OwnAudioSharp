using System.Runtime.CompilerServices;
using OwnaudioNET.Core;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Starts or resumes the mixer. Sources may still be added afterwards.
    /// </summary>
    public void Start()
    {
        _throwIfDisposed();

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

        _isRunning = true;
        _pauseEvent.Set();

        _startRustOutput();
        _startRustSyncTick();
    }

    /// <summary>
    /// Pauses playback without tearing anything down. Start() resumes.
    /// </summary>
    public void Pause()
    {
        _throwIfDisposed();

        if (!_isRunning)
            return;

        if (_rustNative) _pauseRustOutput();

        _isRunning = false;
        _pauseEvent.Reset();
    }

    /// <summary>
    /// Stops the mixer. Sources are stopped but stay registered.
    /// </summary>
    public void Stop()
    {
        _throwIfDisposed();

        if (!_isRunning)
            return;

        _pauseEvent.Reset();

        _stopRustOutput();
        _stopRustSyncTick();

        foreach (var source in _sources.Values)
        {
            try { source.Stop(); }
            catch {}
        }

        _isRunning = false;
    }

    /// <summary>
    /// Tears the mixer down and releases everything it owns.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        OwnaudioNet.UnregisterAudioMixer(this);

        if (_isRunning)
        {
            try { Stop(); }
            catch {}
        }

        StopRecording();
        ClearSources();

        if (_rustNative) _disposeRustSession();

        lock (_effectsLock)
        {
            foreach (var effect in _masterEffects)
            {
                try { effect?.Dispose(); }
                catch {}
            }
            _masterEffects.Clear();
        }

        _pauseEvent?.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Jumps the mixer to a position. Playing sources riding the clock pull themselves
    /// back via drift correction; EndOfStream ones covering the new spot get revived.
    /// </summary>
    /// <param name="positionInSeconds"></param>
    public void Seek(double positionInSeconds)
    {
        _throwIfDisposed();

        if (positionInSeconds < 0) positionInSeconds = 0;

        //No managed MixThread on the rust chain, so every native track has to be moved by hand
        if (_rustNative)
        {
            SeekRustNative(positionInSeconds);
            return;
        }

        _masterClock.SeekTo(positionInSeconds);

        foreach (var source in _sources.Values.ToArray())
        {
            if (source.State != AudioState.EndOfStream)
                continue;

            double _target = positionInSeconds;
            double _duration = source.Duration;

            if (source is IMasterClockSource mcs)
            {
                float _tempo = source is FileSource fs ? fs.Tempo : 1.0f;

                if (positionInSeconds >= mcs.StartOffset + _duration / _tempo)
                    continue;

                _target = Math.Max(0.0, (positionInSeconds - mcs.StartOffset) * _tempo);
            }
            else if (positionInSeconds >= _duration) continue;

            try
            {
                source.Seek(_target);
                source.Play();
            }
            catch {}
        }
    }

    /// <summary>
    /// Parks the clock at startPosition and kicks off every source that isn't playing yet.
    /// Pair it with AddSourcePrepared + PreBuffer for a drift-free multitrack start.
    /// </summary>
    /// <param name="startPosition"></param>
    public void StartPreparedSources(double startPosition = 0.0)
    {
        _throwIfDisposed();

        _masterClock.SeekTo(startPosition);

        foreach (var source in _sources.Values.ToArray())
        {
            if (source.State != AudioState.Playing)
            {
                try { source.Play(); }
                catch {}
            }
        }
    }

    /// <summary>
    /// Guard at the top of the public methods.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _throwIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioMixer));
    }
}
