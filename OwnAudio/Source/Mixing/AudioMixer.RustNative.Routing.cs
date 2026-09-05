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
/// Channel maps, output routes and the capture taps.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Mirrors a source's OutputChannelMapping onto its track: source channel i sums into
    /// output mapping[i], unmapped outputs get silence. Only re-applied when it changed.
    /// Call under _rustSessionLock.
    /// </summary>
    /// <param name="source">the outermost mixer source carrying the map</param>
    /// <param name="key">bookkeeping key of the backed source</param>
    /// <param name="track"></param>
    private void _applyChannelMap(IAudioSource source, Guid key, AudioTrack? track)
    {
        if (track is null)
            return;

        int[]? _current = _resolve<BaseAudioSource>(source)?.OutputChannelMapping;

        if (_current is null && !_rustAppliedChannelMaps.ContainsKey(key))
            return;

        if (_rustAppliedChannelMaps.TryGetValue(key, out int[]? _applied) && _channelMapsEqual(_applied, _current))
            return;

        if (_current is null || _current.Length == 0)
            track.ClearOutputChannelMap();
        else
            track.SetOutputChannelMap(_current);

        //Own clone, otherwise an in-place edit of the same array slips through unnoticed
        _rustAppliedChannelMaps[key] = _current is null ? null : (int[])_current.Clone();
    }

    /// <summary>
    /// Value-compares two channel maps, either may be null.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    private static bool _channelMapsEqual(int[]? a, int[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>
    /// Re-applies every changed channel map so a live re-route takes effect on the next tick.
    /// </summary>
    internal void SyncRustChannelMapsOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources)
            {
                (Guid _id, AudioTrack? _track) = _resolveRustBacked(source);
                if (_track is null) continue;

                try { _applyChannelMap(source, _id, _track); }
                catch (Exception ex) { _logRustApplyError("Output channel map", _id, ex); }
            }
        }
    }

    /// <summary>
    /// OutputRoute last applied per source id. Own clone, same reason as the channel maps:
    /// an in-place edit of the caller's arrays has to be noticed. null means the route is off.
    /// </summary>
    private readonly Dictionary<Guid, OutputRoute?> _rustAppliedRoutes = new Dictionary<Guid, OutputRoute?>();

    /// <summary>
    /// Mirrors a source's OutputRoute onto its track: bus channel dst takes source channel
    /// map[dst] at gain[dst], unbound destinations get nothing from us. Only re-applied when it
    /// changed. Call under _rustSessionLock.
    /// </summary>
    /// <param name="source">the outermost mixer source carrying the route</param>
    /// <param name="key">bookkeeping key of the backed source</param>
    /// <param name="track"></param>
    private void _applyOutputRoute(IAudioSource source, Guid key, AudioTrack? track)
    {
        if (track is null)
            return;

        OutputRoute? _current = _resolve<BaseAudioSource>(source)?.OutputRoute;

        if (_current is null && !_rustAppliedRoutes.ContainsKey(key))
            return;

        if (_rustAppliedRoutes.TryGetValue(key, out OutputRoute? _applied) && OutputRoute.Equal(_applied, _current))
            return;

        if (_current is null || _current.SourceForChannel.Length == 0)
            track.ClearOutputRoute();
        else
            track.SetOutputRoute(_current.SourceForChannel, _current.Gains ?? ReadOnlySpan<float>.Empty);

        _rustAppliedRoutes[key] = _current is null ? null : new OutputRoute(_current.SourceForChannel, _current.Gains);
    }

    /// <summary>
    /// Re-applies every changed output route so a live re-cable lands on the next tick — a route
    /// write, not a stream reopen, which is what ASIO needs.
    /// </summary>
    internal void SyncRustOutputRoutesOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            foreach (IAudioSource source in _sources)
            {
                (Guid _id, AudioTrack? _track) = _resolveRustBacked(source);
                if (_track is null) continue;

                try { _applyOutputRoute(source, _id, _track); }
                catch (Exception ex) { _logRustApplyError("Output route", _id, ex); }
            }
        }
    }

    /// <summary>
    /// Which bus channels the master chain, gain and pan run over. Empty (the default) is the
    /// whole bus. Narrow it to [0,1] and a click on 3/4 reaches the driver as mixed, so the
    /// limiter on the main pair doesn't squash the direct out.
    /// </summary>
    public int[] MasterChannelScope
    {
        get => _masterChannelScope;
        set
        {
            _masterChannelScope = value ?? Array.Empty<int>();
            lock (_rustSessionLock)
            {
                if (_rustSession is not null)
                    _rustSession.MasterChannelScope = _masterChannelScope;
            }
        }
    }

    private int[] _masterChannelScope = Array.Empty<int>();

    /// <summary>
    /// The one capture stream every live input track taps. Opened with the first input source,
    /// owned by the session. Guarded by _rustSessionLock.
    /// </summary>
    private CaptureBridge? _rustCapture;

    /// <summary>
    /// Capture map last applied per source id, so a live re-cable is a tap swap and nothing else.
    /// </summary>
    private readonly Dictionary<Guid, int[]> _rustAppliedCaptureMaps = new Dictionary<Guid, int[]>();

    /// <summary>
    /// Points an input source at its physical capture channels. Null CaptureChannels means the
    /// default: the first N of the device, repeating the last one so a mono mic still fills a
    /// stereo track exactly the way the per-track capture used to. Call under _rustSessionLock.
    /// </summary>
    /// <param name="ins"></param>
    /// <param name="track"></param>
    private void _applyCaptureChannels(InputSource ins, AudioTrack? track)
    {
        if (track is null || _rustCapture is null) return;

        int _deviceChannels = _rustCapture.ChannelCount;
        int[] _wanted = ins.CaptureChannels ?? _defaultCaptureMap(_deviceChannels);

        if (_rustAppliedCaptureMaps.TryGetValue(ins.Id, out int[]? _applied) && _channelMapsEqual(_applied, _wanted))
            return;

        //Past the equality check on purpose: a tap only lands at attach and on a real re-cable,
        //so this can't turn into per-tick noise
        if (ins.CaptureChannels is null && _deviceChannels < _config.Channels)
            Log.Warning($"[Mixer] Source '{ins.Id}' wants {_config.Channels}ch but the device captures "
                + $"{_deviceChannels}ch, channel {_deviceChannels - 1} is duplicated to fill the rest — "
                + "set InputSource.CaptureChannels to pick the inputs yourself");

        _rustCapture.Attach(track, _wanted);
        _rustAppliedCaptureMaps[ins.Id] = (int[])_wanted.Clone();
    }

    /// <summary>
    /// First N device channels for a session-wide track, clamped so a narrower device duplicates
    /// its last channel instead of falling off the end.
    /// </summary>
    /// <param name="captureChannels"></param>
    private int[] _defaultCaptureMap(int captureChannels)
    {
        int[] _map = new int[Math.Max(1, _config.Channels)];
        for (int i = 0; i < _map.Length; i++)
            _map[i] = Math.Min(i, Math.Max(0, captureChannels - 1));

        return _map;
    }

    /// <summary>
    /// Re-taps any input source whose CaptureChannels changed, so a live re-cable lands on the
    /// next tick without touching a stream.
    /// </summary>
    internal void SyncRustCaptureChannelsOnce()
    {
        IAudioSource[] _sources = Volatile.Read(ref _rustSourceSnapshot);
        lock (_rustSessionLock)
        {
            if (_rustCapture is null) return;

            foreach (IAudioSource source in _sources)
            {
                InputSource? _ins = _resolve<InputSource>(source);
                if (_ins?.RustTrack is null || _ins.RustCapture is null) continue;

                try { _applyCaptureChannels(_ins, _ins.RustTrack); }
                catch (Exception ex) { _logRustApplyError("Capture channels", _ins.Id, ex); }
            }
        }
    }
}
