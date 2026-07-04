using System;
using System.IO;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// D.2.d (plan 14 / WS2, control plane) tests for the <see cref="AudioMixer"/> Rust-native facade:
/// the mixer owns a shared session, attaches/detaches each file source's track on add/remove/clear,
/// mirrors control state onto the tracks, and manages the running state without the managed
/// MixThread. Legacy mode is unaffected.
/// </summary>
/// <remarks>
/// The tests use the mock engine and never open a device, so they are hardware-free. The shared
/// session is created against the native mixer/track via the loaded FFI library.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeFacadeTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly string _wavPath;
    private readonly string _wavPath2;

    /// <summary>
    /// Enables the Rust-native chain, builds a mock engine and writes two temp WAV sources.
    /// </summary>
    public AudioMixerRustNativeFacadeTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        var config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        _engine = AudioEngineFactory.CreateMockEngine(config);

        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate);
        _wavPath2 = WriteTempWav(Channels, SampleRate, frames: SampleRate);
    }

    /// <summary>
    /// Restores the opt-in override, disposes the engine and removes the temp WAVs.
    /// </summary>
    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
        DeleteQuietly(_wavPath);
        DeleteQuietly(_wavPath2);
    }

    /// <summary>
    /// Adding a file source creates the shared session and attaches the source to a track in it.
    /// </summary>
    [Fact]
    public void AddSource_AttachesTrackToSharedSession()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        mixer.IsRustNative.Should().BeTrue();
        mixer.RustSession.Should().BeNull();

        var source = new FileSource(_wavPath);
        mixer.AddSource(source).Should().BeTrue();

        mixer.RustSession.Should().NotBeNull();
        mixer.RustSession!.Tracks.Count.Should().Be(1);
        source.RustTrack.Should().NotBeNull();
        source.RustTrack.Should().BeSameAs(mixer.RustSession!.Tracks[0]);
    }

    /// <summary>
    /// Multiple sources share one session, each with its own track.
    /// </summary>
    [Fact]
    public void MultipleSources_ShareOneSession()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var a = new FileSource(_wavPath);
        var b = new FileSource(_wavPath2);

        mixer.AddSource(a);
        mixer.AddSource(b);

        mixer.RustSession!.Tracks.Count.Should().Be(2);
        a.RustTrack.Should().NotBeSameAs(b.RustTrack);
    }

    /// <summary>
    /// Removing a source detaches it and removes its track from the session.
    /// </summary>
    [Fact]
    public void RemoveSource_DetachesAndRemovesTrack()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var a = new FileSource(_wavPath);
        var b = new FileSource(_wavPath2);
        mixer.AddSource(a);
        mixer.AddSource(b);

        mixer.RemoveSource(a).Should().BeTrue();

        mixer.RustSession!.Tracks.Count.Should().Be(1);
        a.RustTrack.Should().BeNull();
        b.RustTrack.Should().NotBeNull();
    }

    /// <summary>
    /// The control-state sync mirrors each source's volume and loop onto its track and feeder.
    /// </summary>
    [Fact]
    public void SyncControlState_MirrorsVolumeAndLoop()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var source = new FileSource(_wavPath) { Volume = 0.5f, Loop = true };
        mixer.AddSource(source);

        mixer.SyncRustControlStateOnce();

        source.RustTrack!.Gain.Should().BeApproximately(0.5f, 0.0001f);
        source.RustFileTrack!.Loop.Should().BeTrue();
    }

    /// <summary>
    /// Seeking the Rust-native mixer moves the master clock and repositions each attached source's
    /// native decoder (the managed MixThread/soft-sync path that legacy relies on does not run here).
    /// </summary>
    [Fact]
    public void Seek_MovesMasterClock_AndRepositionsSources()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var source = new FileSource(_wavPath);
        mixer.AddSource(source);
        mixer.Start();

        mixer.Seek(0.5);

        // The master clock jumps to the target so the reported playback position updates immediately.
        mixer.MasterClock.CurrentTimestamp.Should().BeApproximately(0.5, 0.001);

        // The source reports the seeked content position (start offset 0): the rendered position
        // counts from zero after the seek, plus the recorded seek base.
        source.Position.Should().BeApproximately(0.5, 0.02);

        mixer.Stop();
    }

    /// <summary>
    /// Start moves the mixer to running and Stop back, without starting the managed MixThread.
    /// </summary>
    [Fact]
    public void StartStop_ManageRunningState()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        mixer.AddSource(new FileSource(_wavPath));

        mixer.IsRunning.Should().BeFalse();

        mixer.Start();
        mixer.IsRunning.Should().BeTrue();

        mixer.Stop();
        mixer.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Clearing sources detaches all of them and empties the session.
    /// </summary>
    [Fact]
    public void ClearSources_DetachesAll()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var a = new FileSource(_wavPath);
        var b = new FileSource(_wavPath2);
        mixer.AddSource(a);
        mixer.AddSource(b);

        mixer.ClearSources();

        mixer.RustSession!.Tracks.Count.Should().Be(0);
        a.RustTrack.Should().BeNull();
        b.RustTrack.Should().BeNull();
    }

    /// <summary>
    /// In legacy mode the mixer never creates a session and reports non-Rust-native.
    /// </summary>
    [Fact]
    public void LegacyMixer_HasNoSession()
    {
        RustNativeChain.Override = false;

        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        var legacyConfig = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        using var legacyEngine = AudioEngineFactory.CreateMockEngine(legacyConfig);

        mixer.IsRustNative.Should().BeFalse();

        mixer.AddSource(new FileSource(_wavPath)).Should().BeTrue();

        mixer.RustSession.Should().BeNull();
    }

    /// <summary>
    /// Deletes a file, ignoring any I/O error.
    /// </summary>
    /// <param name="path">The file path to remove.</param>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Writes a temporary 16-bit PCM WAV file and returns its path.
    /// </summary>
    /// <param name="channels">Channel count.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="frames">Number of audio frames to write.</param>
    /// <returns>The absolute path of the written WAV file.</returns>
    private static string WriteTempWav(int channels, int sampleRate, int frames)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ownaudio_mixer_rustnative_{Guid.NewGuid():N}.wav");

        int dataLen = frames * channels * 2;
        int byteRate = sampleRate * channels * 2;
        short blockAlign = (short)(channels * 2);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((ushort)blockAlign);
        w.Write((ushort)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);

        for (int i = 0; i < frames; i++)
        {
            short value = (short)((i % 1000) * 30);
            for (int c = 0; c < channels; c++)
            {
                w.Write(value);
            }
        }

        return path;
    }
}
