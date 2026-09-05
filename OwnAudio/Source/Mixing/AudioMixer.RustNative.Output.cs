using System;
using System.Collections.Generic;
using System.Threading;
using Logger;
using Ownaudio.Audio.Tracks;
using Ownaudio.Safe;
using OwnaudioNET.Core;
using OwnaudioNET.Effects.VST;
using OwnaudioNET.Engine;
using OwnaudioNET.Events;
using OwnaudioNET.Interfaces;
using OwnaudioNET.Sources;

namespace OwnaudioNET.Mixing;

/// <summary>
/// The sync tick thread, the pollers, seeking and the output stream itself.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// One network drift-correction pass over the attached file sources. No-op for anything
    /// not playing under a network-controlled clock.
    /// </summary>
    internal void DriveRustNativeSyncOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            _resolve<FileSource>(source)?.ApplyRustNativeSync();
        }
    }

    /// <summary>
    /// Starts the control tick unless it already runs.
    /// </summary>
    private void _startRustSyncTick()
    {
        lock (_rustSessionLock)
        {
            if (_rustSyncThread is not null)
                return;

            _rustSyncStop = false;
            _rustSyncThread = new Thread(_rustSyncLoop)
            {
                Name = "AudioMixer.RustControlSync",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _rustSyncThread.Start();
        }
    }

    /// <summary>
    /// Signals and joins the control tick. Idempotent.
    /// </summary>
    private void _stopRustSyncTick()
    {
        Thread? _thread;
        lock (_rustSessionLock)
        {
            _thread = _rustSyncThread;
            _rustSyncStop = true;
            _rustSyncThread = null;
        }

        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join();
    }

    /// <summary>
    /// The control tick itself. A transient error mustn't kill it, but a repeating one gets
    /// reported and eventually stops the loop instead of spinning for hours.
    /// </summary>
    private void _rustSyncLoop()
    {
        while (!_rustSyncStop)
        {
            try
            {
                SyncRustControlStateOnce();
                SyncRustMasterOnce();
                SyncRustStartOffsetsOnce();
                SyncRustChannelMapsOnce();
                SyncRustOutputRoutesOnce();
                SyncRustCaptureChannelsOnce();
                MirrorRustMasterEffectsOnce();
                ReconcileRustTrackEffectsOnce();
                DriveRustNativeSyncOnce();
                _advanceMasterClockFromTracks();
                PollRustPlaybackEndedOnce();
                PollRustStreamFaultOnce();

                _rustSyncConsecutiveErrors = 0;
                _rustApplyErrors = 0;
            }
            catch (Exception ex)
            {
                if (_handleLoopError("Rust-native control sync tick", ex, ref _rustSyncConsecutiveErrors))
                    break;
            }

            Thread.Sleep(RustControlSyncIntervalMs);
        }
    }

    /// <summary>
    /// Raises PlaybackEnded once every attached source has run out. Sources sit on the native
    /// EOS latch, so this is just the tick noticing that none of them is playing any more.
    /// A source still Playing (or a fresh one showing up) re-arms the latch.
    /// </summary>
    internal void PollRustPlaybackEndedOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        if (_sources.Length == 0)
        {
            _rustPlaybackEndedRaised = false;
            return;
        }

        bool _allDone = true;
        foreach (IAudioSource source in _sources)
        {
            if (source.State != AudioState.EndOfStream) { _allDone = false; break; }
        }

        if (!_allDone)
        {
            _rustPlaybackEndedRaised = false;
            return;
        }

        if (_rustPlaybackEndedRaised) return;

        _rustPlaybackEndedRaised = true;
        RaisePlaybackEnded();
    }

    /// <summary>
    /// Drives the master clock from the furthest-along playing track in local playback —
    /// without the MixThread the clock would just sit frozen. Network-controlled clocks are
    /// left to the synchroniser, DriveRustNativeSyncOnce pulls the tracks to them instead.
    /// </summary>
    private void _advanceMasterClockFromTracks()
    {
        if (_masterClock.IsNetworkControlled)
            return;

        double _projectPos = -1.0;
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            FileSource? _fs = _resolve<FileSource>(source);
            if (_fs is not null && _fs.State == AudioState.Playing)
            {
                //A track still sitting in its start-offset silence would drag the clock to its offset
                if (_fs.StartOffset > 0.0 && (_fs.RustTrack?.RenderedFrames ?? 0UL) == 0UL)
                    continue;

                //Project position, not content Position: a stretched track must not run the shared
                //clock at its own content rate and desync everyone else
                double _p = _fs.StartOffset + _fs.RustNativeRealPosition;
                if (_p > _projectPos) _projectPos = _p;
            }
        }

        if (_projectPos >= 0.0) _masterClock.SeekTo(_projectPos);
    }

    /// <summary>
    /// Polls both directions for a fresh fault (device lost, backend error): the session
    /// output, and the shared capture every input source feeds off.
    /// </summary>
    internal void PollRustStreamFaultOnce()
    {
        AudioStreamErrorKind _outputKind = AudioStreamErrorKind.None;
        AudioStreamErrorKind _captureKind = AudioStreamErrorKind.None;
        ulong _outputCount = _rustLastStreamErrorCount;
        ulong _captureCount = _rustLastCaptureErrorCount;

        lock (_rustSessionLock)
        {
            if (_rustOutputStream is { } _stream)
                _outputKind = _stream.PollErrorState(out _outputCount);

            if (_rustCapture is { } _capture)
                _captureKind = _capture.PollErrorState(out _captureCount);
        }

        _raiseRustFault(AudioStreamDirection.Output, _outputKind, _outputCount, ref _rustLastStreamErrorCount);
        _raiseRustFault(AudioStreamDirection.Input, _captureKind, _captureCount, ref _rustLastCaptureErrorCount);
    }

    /// <summary>
    /// Fires StreamFaulted once per fresh fault on one direction. The count is monotonic,
    /// so comparing it catches a repeat of the same kind too. Off the session lock, so a
    /// handler may call back in.
    /// </summary>
    /// <param name="direction"></param>
    /// <param name="kind"></param>
    /// <param name="count">error total the stream reported now</param>
    /// <param name="lastSeen">that direction's last reported total</param>
    private void _raiseRustFault(AudioStreamDirection direction, AudioStreamErrorKind kind, ulong count,
        ref ulong lastSeen)
    {
        if (count == lastSeen)
            return;

        lastSeen = count;

        if (kind == AudioStreamErrorKind.None)
            return;

        AudioStreamFaultKind _fault = kind == AudioStreamErrorKind.DeviceNotAvailable
            ? AudioStreamFaultKind.DeviceNotAvailable
            : AudioStreamFaultKind.BackendSpecific;

        string _what = direction == AudioStreamDirection.Output
            ? "output stream"
            : "shared capture stream, every input source is silent";

        Log.FatalError($"[Mixer] Native {_what} faulted: {_fault} (fault #{count})");

        StreamFaulted?.Invoke(this, new AudioStreamFaultEventArgs(_fault, count, direction));
    }

    /// <summary>
    /// Seek across the shared session: moves the clock and repositions every native decoder,
    /// since nothing in the managed pipeline carries the seek down for us.
    /// </summary>
    /// <param name="projectSeconds"></param>
    internal void SeekRustNative(double projectSeconds)
    {
        if (projectSeconds < 0.0) projectSeconds = 0.0;

        _masterClock.SeekTo(projectSeconds);

        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources.Values)
            {
                FileSource? _fs = _resolve<FileSource>(source);
                if (_fs is null)
                    continue;

                try { _applyRustStartOffset(_fs, projectSeconds); }
                catch (Exception ex) { Log.Error($"[Mixer] Seek of native track '{_fs.Id}' to {projectSeconds:F3}s failed", ex); }
            }
        }
    }

    /// <summary>
    /// Opens the session's native output on the engine device once and suspends the engine's
    /// own push output so the two don't fight. No-op without a session or a rust engine.
    /// </summary>
    private void _openRustOutput()
    {
        if (_rustOutputStream is not null || _rustSession is null)
            return;

        RustAudioEngine? _rustEngine = _engine as RustAudioEngine;
        AudioEngine? _nativeEngine = _rustEngine?.NativeEngine;
        if (_rustEngine is null || _nativeEngine is null)
        {
            Log.Error("[Mixer] Rust-native output cannot open: the mixer is not sitting on a live RustAudioEngine");
            return;
        }

        //The device buffer is this path's whole latency knob: the mixer renders in the callback,
        //so there is no render ring stacked on it the way a push stream has one
        _rustOutputStream = _rustSession.OpenOutput(
            _nativeEngine, _rustEngine.SelectedOutputDevice, _config.BufferSize);
        _rustLastStreamErrorCount = 0;
        _rustEngine.ReleaseOutput();
        _rustReleasedEngine = _rustEngine;

        //Ours now drives the device, so the engine's width and buffer diagnostics read it
        _rustEngine.TrackSessionOutput(_rustOutputStream);
        _rustDiagnosticEngine = _rustEngine;

        Log.Info($"[Mixer] Session output opened on '{_rustEngine.SelectedOutputDevice?.Name ?? "(default)"}': "
            + $"{_describeSessionWidth(_rustEngine)}, buffer {_config.BufferSize} frames requested, "
            + "no render ring (the mixer renders in the device callback)");
    }

    /// <summary>
    /// The bus width against what the device opened with. They part company on a card that only
    /// offers one width — the mix stays at the bus width and the engine spreads it over the rest
    /// — and that is exactly when someone asks why a route landed on the wrong socket.
    /// </summary>
    /// <param name="engine"></param>
    private string _describeSessionWidth(RustAudioEngine engine)
    {
        int _opened = engine.ActualOutputChannels;
        return _opened <= 0 || _opened == _config.Channels
            ? $"{_config.Channels}ch bus"
            : $"{_config.Channels}ch bus -> {_opened}ch device";
    }

    /// <summary>
    /// Transport start: opens the device output and fires every track against the shared clock.
    /// </summary>
    private void _startRustOutput()
    {
        lock (_rustSessionLock)
        {
            _openRustOutput();

            if (_rustOutputStream is null)
                return;

            //Offsets must be in place before PlayAll, otherwise the first block is already wrong
            double _project = _masterClock.CurrentTimestamp;
            foreach (IAudioSource source in _sources.Values)
            {
                FileSource? _fs = _resolve<FileSource>(source);
                if (_fs?.RustTrack is null)
                    continue;

                try
                {
                    if (_fs.StartOffset != 0.0)
                    {
                        _applyRustStartOffset(_fs, _project);
                    }
                    else
                    {
                        _fs.RustTrack.SetStartDelayFrames(0);
                        _rustAppliedStartOffsets[_fs.Id] = 0.0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[Mixer] Track '{_fs.Id}' starts misaligned, its offset could not be applied", ex);
                }
            }

            _rustSession?.PlayAll();
        }
    }

    /// <summary>
    /// Opens the output when a source shows up on an already running mixer that had none yet —
    /// the session is built lazily, so a Start() before the first source finds nothing to open.
    /// AddSource plays the sources itself, so only the device stream is needed here.
    /// </summary>
    private void _ensureRustOutputAfterAttach()
    {
        if (!_rustNative || !_isRunning)
            return;

        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null)
                return;

            _openRustOutput();
        }
    }

    /// <summary>
    /// Transport stop: stops all tracks while the output is live.
    /// </summary>
    private void _stopRustOutput()
    {
        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null) _rustSession?.StopAll();
        }
    }

    /// <summary>
    /// Transport pause: pauses all tracks while the output is live.
    /// </summary>
    private void _pauseRustOutput()
    {
        lock (_rustSessionLock)
        {
            if (_rustOutputStream is not null) _rustSession?.PauseAll();
        }
    }

    /// <summary>
    /// Tears down the session (tracks, feeders, output stream) and the tick, then hands the
    /// device back to the engine.
    /// </summary>
    private void _disposeRustSession()
    {
        _stopRustSyncTick();

        lock (_rustSessionLock)
        {
            //Native effects live on the session mixer, disposing it frees them — just drop the pairings
            _rustMasterEffects.Clear();

            foreach (var routing in _rustEffectSources)
                routing.Source.NativeChainReconciler = SourceWithEffects.NoNativeChain;

            _rustEffectSources.Clear();
            _rustAttachedTracks.Clear();

            try { _rustSession?.Dispose(); }
            catch (Exception ex) { Log.Error("[Mixer] Native session dispose failed, native tracks may leak", ex); }

            _rustSession = null;
            _rustCapture = null;
            _rustLastCaptureErrorCount = 0;
            _rustAppliedCaptureMaps.Clear();
            _rustAppliedRoutes.Clear();
            _rustOutputStream = null;

            //What we lent the engine for its diagnostics died with the session
            _rustDiagnosticEngine?.TrackSessionOutput(null);
            _rustDiagnosticEngine?.TrackSessionCapture(0);
            _rustDiagnosticEngine = null;

            _rustReleasedEngine?.RestoreOutput();
            _rustReleasedEngine = null;

            Log.Info("[Mixer] Native session disposed, device handed back to the engine");
        }
    }
}
