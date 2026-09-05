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
/// Hooking sources onto the shared session and taking them back off again.
/// </summary>
public sealed partial class AudioMixer
{
    /// <summary>
    /// Hooks a source onto the shared session: file to a native file track, samples to a
    /// memory track, input to a capture track. Anything else is ignored.
    /// </summary>
    /// <param name="source"></param>
    private void _attachSourceToRustSession(IAudioSource source)
    {
        FileSource? _fs = _resolve<FileSource>(source);
        if (_fs?.FilePath is not null)
        {
            _attachFileSource(source, _fs);
            return;
        }

        SampleSource? _ss = _resolve<SampleSource>(source);
        if (_ss is not null)
        {
            _attachSampleSource(source, _ss);
            return;
        }

        InputSource? _ins = _resolve<InputSource>(source);
        if (_ins is not null)
        {
            _attachInputSource(source, _ins);
            return;
        }

        StreamingSource? _sts = _resolve<StreamingSource>(source);
        if (_sts is not null) _attachStreamingSource(source, _sts);
    }

    /// <summary>
    /// Attaches a streaming source to a bare native track: the track is created with a
    /// ring-buffer feed, which the source's own pump thread fills from its generator.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="sts"></param>
    private void _attachStreamingSource(IAudioSource source, StreamingSource sts)
    {
        lock (_rustSessionLock)
        {
            _ensureRustSession();

            AudioTrack _track = _rustSession.AddTrack();
            sts.AttachRustTrack(_track);

            _applyRoutingAtAttach(source, sts.Id, sts.RustTrack);
            _rememberRustTrack(sts.Id, sts.RustTrack);
            _routeTrackEffects(source, sts);
        }
    }

    /// <summary>
    /// Files the track under the source id and wires the wrapper's chain to the native one,
    /// so a chain edit reconciles on the caller thread instead of waiting for the tick.
    /// Call under _rustSessionLock.
    /// </summary>
    /// <param name="source">the outermost mixer source</param>
    /// <param name="backing">the source owning the native track</param>
    private void _routeTrackEffects(IAudioSource source, IRustNativeChainSource backing)
    {
        if (source is not SourceWithEffects swe) return;

        //The wrapper may already carry a chain from before it was handed to the mixer
        foreach (IEffectProcessor effect in swe.GetEffects())
            ThrowIfNoNativeTwin(effect, $"source '{swe.Id}'");

        _rustEffectSources.Add(new RustTrackEffectRouting(swe, backing));
        swe.NativeChainReconciler = ReconcileRustTrackEffectsOnce;
        swe.NativeChainValidator = _effect => ThrowIfNoNativeTwin(_effect, $"source '{swe.Id}'");
    }

    /// <summary>
    /// Everything the mixer plays runs as a native twin, so a managed type with no adapter would
    /// simply be inaudible. Better a hard error at the call site than a silent chain — and better
    /// than throwing out of the rebuild, which the control tick would then hit every 15 ms.
    /// </summary>
    /// <param name="effect"></param>
    /// <param name="where">for the message: which chain rejected it</param>
    internal static void ThrowIfNoNativeTwin(IEffectProcessor effect, string where)
    {
        if (effect is VST3EffectProcessor || RustEffectAdapters.TryGetEffectType(effect, out _))
            return;

        throw new InvalidOperationException(
            $"Effect '{effect.GetType().Name}' has no native twin and cannot run on {where}. "
            + "Only built-in OwnaudioNET.Effects types (and VST3) are hosted natively.");
    }

    /// <summary>
    /// Remembers the track we attached, keyed by source id. Call under _rustSessionLock.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="track"></param>
    private void _rememberRustTrack(Guid id, AudioTrack? track)
    {
        if (track is not null) _rustAttachedTracks[id] = track;
    }

    /// <summary>
    /// Lays a source's channel map and output route onto the freshly attached track. Routing set
    /// before the add still has to land on the very first rendered block, hence here and not on
    /// the next tick.
    /// </summary>
    /// <remarks>
    /// A route the engine won't take — past its 16 channel reach, or naming a source channel the
    /// track hasn't got — is a mistake in the host's configuration, not a reason to abandon a
    /// track that plays perfectly well unrouted. It gets reported and the track goes on the bus
    /// plain, which is what the control tick does with the same failure; letting it out of
    /// AddSource instead would leave the source registered on a half wired track.
    /// </remarks>
    /// <param name="source">the outermost mixer source</param>
    /// <param name="id">bookkeeping key of the backed source</param>
    /// <param name="track"></param>
    private void _applyRoutingAtAttach(IAudioSource source, Guid id, AudioTrack? track)
    {
        try { _applyChannelMap(source, id, track); }
        catch (Exception ex) { Log.Error($"[Mixer] Output channel map of source '{id}' rejected at attach, it plays unmapped", ex); }

        try { _applyOutputRoute(source, id, track); }
        catch (Exception ex) { Log.Error($"[Mixer] Output route of source '{id}' rejected at attach, it plays unrouted", ex); }
    }

    /// <summary>
    /// Attaches a natively decoded file source to the shared session.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="fs"></param>
    private void _attachFileSource(IAudioSource source, FileSource fs)
    {
        lock (_rustSessionLock)
        {
            _ensureRustSession();

            //The source decides its own decode width; unset means the session's, as always
            FileTrack _track = _rustSession.AddFileTrack(
                fs.FilePath!,
                (ushort)(fs.DecodeChannels ?? _config.Channels));
            fs.AttachRustTrack(_track.Track, _track);

            _applyRoutingAtAttach(source, fs.Id, fs.RustTrack);
            _rememberRustTrack(fs.Id, fs.RustTrack);
            _routeTrackEffects(source, fs);
        }
    }

    /// <summary>
    /// Attaches a sample source, backed by a native memory track serving its buffer.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="ss"></param>
    private void _attachSampleSource(IAudioSource source, SampleSource ss)
    {
        lock (_rustSessionLock)
        {
            _ensureRustSession();

            MemoryTrack _track = _rustSession.AddMemoryTrack(ss.GetRustSampleSnapshot(), ss.Loop);
            ss.AttachRustTrack(_track.Track, _track);

            _applyRoutingAtAttach(source, ss.Id, ss.RustTrack);
            _rememberRustTrack(ss.Id, ss.RustTrack);
            _routeTrackEffects(source, ss);
        }
    }

    /// <summary>
    /// Attaches an input source, backed by a native capture writing straight into the track ring.
    /// Quietly does nothing under a non-rust engine (mock engine in tests) instead of blowing up.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="ins"></param>
    private void _attachInputSource(IAudioSource source, InputSource ins)
    {
        lock (_rustSessionLock)
        {
            RustAudioEngine? _rustEngine = _engine as RustAudioEngine;
            AudioEngine? _nativeEngine = _rustEngine?.NativeEngine;
            if (_nativeEngine is null)
                return;

            _ensureRustSession();

            //One device stream for every input track, not one each. On ASIO that's the whole
            //ballgame - a driver takes one client - and everywhere else it's just cheaper.
            if (_rustCapture is null && !_openRustCapture(_rustEngine!, _nativeEngine))
                return;

            AudioTrack _track = _rustSession.AddTrack();
            ins.AttachRustCapture(_track, _rustCapture!);

            try { _applyCaptureChannels(ins, _track); }
            catch (Exception ex)
            {
                Log.Error($"[Mixer] Capture channels of source '{ins.Id}' rejected at attach "
                    + $"(the device opened {_rustCapture!.ChannelCount}ch), it stays silent", ex);
            }

            _applyRoutingAtAttach(source, ins.Id, ins.RustTrack);
            _rememberRustTrack(ins.Id, ins.RustTrack);
            _routeTrackEffects(source, ins);
        }
    }

    /// <summary>
    /// Opens the one capture stream every input track taps, taking the device off the engine
    /// first. Reports the width it landed on, because that is the range an InputSource's
    /// CaptureChannels may address and there is no other way to find out what the card offers.
    /// Call under _rustSessionLock.
    /// </summary>
    /// <param name="rustEngine"></param>
    /// <param name="nativeEngine"></param>
    /// <returns>False when the device would not open, the caller then attaches nothing.</returns>
    private bool _openRustCapture(RustAudioEngine rustEngine, AudioEngine nativeEngine)
    {
        rustEngine.ReleaseInput();

        try
        {
            _rustCapture = _rustSession!.OpenCapture(
                nativeEngine, rustEngine.SelectedInputDevice, bufferFrames: (uint)_config.BufferSize);
        }
        catch (Exception ex)
        {
            Log.Error($"[Mixer] Shared capture cannot open on "
                + $"'{rustEngine.SelectedInputDevice?.Name ?? "(default)"}', input sources stay silent", ex);
            return false;
        }

        _rustLastCaptureErrorCount = 0;

        //Every input track taps this one stream, so its width is the whole capture surface
        rustEngine.TrackSessionCapture(_rustCapture.ChannelCount);
        _rustDiagnosticEngine = rustEngine;

        Log.Info($"[Mixer] Shared capture opened on '{rustEngine.SelectedInputDevice?.Name ?? "(default)"}': "
            + $"{_rustCapture.ChannelCount}ch, buffer {_config.BufferSize} frames requested");

        return true;
    }

    /// <summary>
    /// Unhooks a source and disposes its track. No-op without a native backend.
    /// </summary>
    /// <param name="source"></param>
    private void _detachSourceFromRustSession(IAudioSource source)
    {
        BaseAudioSource? _owner = _resolve<BaseAudioSource>(source);
        if (_owner is not IRustNativeChainSource _backed) return;

        _detachBackedSource(source, _owner.Id, _backed.RustTrack, _backed.DetachRustTrack);
    }

    /// <summary>
    /// Shared detach path: drops the routing and bookkeeping, unbinds the track, removes it.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="id">bookkeeping key of the backed source</param>
    /// <param name="track">the native track before detaching, null if the source was disposed already</param>
    /// <param name="detach">the backed source's own detach action</param>
    private void _detachBackedSource(IAudioSource source, Guid id, AudioTrack? track, Action detach)
    {
        lock (_rustSessionLock)
        {
            //A source disposed before RemoveSource has nulled its track, ours is still good
            if (track is null) _rustAttachedTracks.TryGetValue(id, out track);

            if (source is SourceWithEffects swe) swe.NativeChainReconciler = SourceWithEffects.NoNativeChain;

            _rustEffectSources.RemoveAll(r => ReferenceEquals(r.Source, source));
            _rustAppliedStartOffsets.Remove(id);
            _rustAppliedChannelMaps.Remove(id);
            _rustAppliedRoutes.Remove(id);
            _rustAttachedTracks.Remove(id);

            if (_rustAppliedCaptureMaps.Remove(id) && track is not null)
                _rustCapture?.Detach(track);

            detach();

            if (track is not null && _rustSession is not null)
                _rustSession.RemoveTrack(track);
        }
    }
}
