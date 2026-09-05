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
/// The control-rate mirror: volume, pan, loop, the master bus and start offsets.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// One pass of mirroring volume/pan/loop onto every attached track, and pulling the
    /// track's peaks back for metering (the managed OnSamplesRead path that used to feed
    /// them doesn't run here). Goes through the resolvers so an effect-wrapped track isn't
    /// skipped. Public-ish for deterministic tests.
    /// </summary>
    internal void SyncRustControlStateOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        foreach (IAudioSource source in _sources)
        {
            BaseAudioSource? _owner = _resolve<BaseAudioSource>(source);
            if (_owner is not IRustNativeChainSource _backed) continue;

            AudioTrack? _track = _backed.RustTrack;
            if (_track is null) continue;

            _track.Gain = _owner.Volume;
            _track.Pan = _owner.Pan;

            //Only the file and memory tracks loop natively, the other two have nowhere to put it
            switch (_owner)
            {
                case FileSource _fs when _fs.RustFileTrack is not null:
                    _fs.RustFileTrack.Loop = _fs.Loop;
                    break;
                case SampleSource _ss when _ss.RustMemoryTrack is not null:
                    _ss.RustMemoryTrack.Loop = _ss.Loop;
                    break;
            }

            _owner.SetOutputLevels(_owner.State == AudioState.Playing ? _track.Peaks : (0f, 0f));
        }
    }

    /// <summary>
    /// Pushes master volume/pan onto the native master bus and reads its peaks back into
    /// LeftPeak/RightPeak, which the missing MixThread used to compute.
    /// </summary>
    internal void SyncRustMasterOnce()
    {
        MultiTrackSession? _session;
        lock (_rustSessionLock) { _session = _rustSession; }

        if (_session is null)
            return;

        _session.MasterGain = _masterVolume;
        _session.MasterPan = _masterPan;

        (float _left, float _right) = _session.GetMasterPeaks();
        _leftPeak = _left;
        _rightPeak = _right;
    }

    /// <summary>
    /// Applies a source's StartOffset against a project position: content = project - offset.
    /// Non-negative content seeks the decoder there, negative holds the track silent for the
    /// remaining frames. Call under _rustSessionLock.
    /// </summary>
    /// <param name="fs"></param>
    /// <param name="projectPosition">project timeline position in seconds</param>
    private void _applyRustStartOffset(FileSource fs, double projectPosition)
    {
        AudioTrack? _track = fs.RustTrack;
        if (_track is null)
            return;

        double _offset = fs.StartOffset;
        double _local = projectPosition - _offset;

        if (_local >= 0.0)
        {
            //Seek target is wall-clock but the decoder speaks content time, so scale by tempo
            float _tempo = fs.Tempo <= 0f ? 1f : fs.Tempo;
            fs.Seek(Math.Clamp(_local * _tempo, 0.0, fs.Duration));
            _track.SetStartDelayFrames(0);
        }
        else
        {
            fs.Seek(0.0);
            _track.SetStartDelayFrames((long)Math.Round(-_local * _config.SampleRate));
        }

        _rustAppliedStartOffsets[fs.Id] = _offset;
    }

    /// <summary>
    /// Drops a freshly attached track onto the clock before it is allowed to play. Without it
    /// the track runs from the head of the file until the next tick catches it — 15 ms of the
    /// wrong audio and an audible jump when it lands.
    /// </summary>
    /// <param name="source"></param>
    internal void AlignRustSourceToClock(IAudioSource source)
    {
        if (!_rustNative) return;

        FileSource? _fs = _resolve<FileSource>(source);
        if (_fs?.RustTrack is null) return;

        lock (_rustSessionLock)
        {
            try { _applyRustStartOffset(_fs, _masterClock.CurrentTimestamp); }
            catch (Exception ex) { Log.Error($"[Mixer] Hot-swapped source '{_fs.Id}' could not be put on the clock", ex); }
        }
    }

    /// <summary>
    /// Realigns any track whose StartOffset changed since it was last applied, so a live
    /// offset edit lands without an explicit seek. Untouched offsets are left alone.
    /// </summary>
    internal void SyncRustStartOffsetsOnce()
    {
        double _project = _masterClock.CurrentTimestamp;

        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources)
            {
                FileSource? _fs = _resolve<FileSource>(source);
                if (_fs?.RustTrack is null)
                    continue;

                bool _known = _rustAppliedStartOffsets.TryGetValue(_fs.Id, out double _applied);
                if (_known && _applied == _fs.StartOffset)
                    continue;

                try { _applyRustStartOffset(_fs, _project); }
                catch (Exception ex) { _logRustApplyError("Start offset", _fs.Id, ex); }
            }
        }
    }
}
