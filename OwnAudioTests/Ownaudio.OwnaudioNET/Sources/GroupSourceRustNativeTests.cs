using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Sources;

/// <summary>
/// <see cref="GroupSource"/> on the Rust-native chain: clips keep their place across backends,
/// edits land on the native group live, and the mixer attaches the group like any other track.
/// </summary>
/// <remarks>
/// Mock engine and standalone sessions, no device — control plane only. What the group renders
/// is covered by the Rust tests.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class GroupSourceRustNativeTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MixerBufferFrames = 512;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly List<string> _wavs = new List<string>();

    public GroupSourceRustNativeTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        var _config = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = MixerBufferFrames };
        _engine = AudioEngineFactory.CreateMockEngine(_config);
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();

        foreach (string path in _wavs)
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void AddClip_KnowsItsLength_BeforeAnyBackend()
    {
        using var group = new GroupSource(SampleRate, Channels);

        SourceClip a = group.AddClip(_wav(1.0), 0.0);
        SourceClip b = group.AddClip(_wav(0.5), 4.0);

        a.Duration.Should().BeApproximately(1.0, 1e-6);
        a.IsInMemory.Should().BeTrue();
        b.EndSeconds.Should().BeApproximately(4.5, 1e-6);
        group.Duration.Should().BeApproximately(4.5, 1e-6);
        group.Clips.Should().HaveCount(2);
    }

    [Fact]
    public void LongFiles_Stream_ShortOnesLoad()
    {
        using var group = new GroupSource(SampleRate, Channels, memoryMaxSeconds: 1.0);

        group.AddClip(_wav(0.5), 0.0).IsInMemory.Should().BeTrue();
        group.AddClip(_wav(2.0), 1.0).IsInMemory.Should().BeFalse();
    }

    [Fact]
    public void NegativeStart_LandsAtZero()
    {
        using var group = new GroupSource(SampleRate, Channels);

        SourceClip clip = group.AddClip(_wav(1.0), -3.0);
        clip.StartSeconds.Should().Be(0.0);

        clip.StartSeconds = -1.0;
        clip.StartSeconds.Should().Be(0.0);
    }

    [Fact]
    public void StandaloneBackend_GetsEveryClip()
    {
        using var group = new GroupSource(SampleRate, Channels);
        group.AddClip(_wav(1.0), 0.0);
        group.AddClip(_wav(1.0), 2.0);

        group.EnsureStandaloneRustBackend();

        group.RustGroupTrack!.End.TotalSeconds.Should().BeApproximately(3.0, 1e-3);
    }

    [Fact]
    public void Move_And_Remove_LandOnTheNativeGroup()
    {
        using var group = new GroupSource(SampleRate, Channels);
        SourceClip a = group.AddClip(_wav(1.0), 0.0);
        SourceClip b = group.AddClip(_wav(1.0), 2.0);
        group.EnsureStandaloneRustBackend();

        b.StartSeconds = 5.0;
        group.RustGroupTrack!.End.TotalSeconds.Should().BeApproximately(6.0, 1e-3);
        group.Duration.Should().BeApproximately(6.0, 1e-6);

        group.RemoveClip(b).Should().BeTrue();
        group.RemoveClip(b).Should().BeFalse();
        b.IsOnGroup.Should().BeFalse();
        group.RustGroupTrack!.End.TotalSeconds.Should().BeApproximately(1.0, 1e-3);

        //An orphaned clip keeps its own value but no longer reaches the group
        b.StartSeconds = 9.0;
        group.Duration.Should().BeApproximately(1.0, 1e-6);
        a.IsOnGroup.Should().BeTrue();
    }

    [Fact]
    public void ClipAddedAfterBackend_IsPlacedStraightAway()
    {
        using var group = new GroupSource(SampleRate, Channels);
        group.EnsureStandaloneRustBackend();

        group.AddClip(_wav(1.0), 3.0);

        group.RustGroupTrack!.End.TotalSeconds.Should().BeApproximately(4.0, 1e-3);
    }

    [Fact]
    public void Mixer_AttachesTheGroup_AndReattachPlacesTheClipsAgain()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var group = new GroupSource(SampleRate, Channels);
        group.AddClip(_wav(1.0), 1.0);

        mixer.AddSource(group);
        group.RustGroupTrack.Should().NotBeNull();
        group.RustGroupTrack!.End.TotalSeconds.Should().BeApproximately(2.0, 1e-3);

        mixer.RemoveSource(group.Id);
        group.RustGroupTrack.Should().BeNull();

        group.Clips[0].StartSeconds = 4.0;
        mixer.AddSource(group);
        group.RustGroupTrack!.End.TotalSeconds.Should().BeApproximately(5.0, 1e-3);
    }

    [Fact]
    public void Mixer_TempoAndPitch_ReachTheTrack_ThroughAnEffectWrapper()
    {
        using var mixer = new AudioMixer(_engine, MixerBufferFrames);
        using var group = new GroupSource(SampleRate, Channels);
        group.AddClip(_wav(1.0), 0.0);
        using var wrapped = new SourceWithEffects(group);

        mixer.AddSource(wrapped);
        wrapped.Tempo = 1.1f;
        wrapped.PitchShift = -3.0f;

        group.RustTrack!.Tempo.Should().BeApproximately(1.1f, 1e-4f);
        group.RustTrack!.PitchSemitones.Should().BeApproximately(-3.0f, 1e-4f);
    }

    [Fact]
    public void Seek_StaysInsideTheTimeline()
    {
        using var group = new GroupSource(SampleRate, Channels);
        group.AddClip(_wav(1.0), 2.0);
        group.EnsureStandaloneRustBackend();

        group.Seek(2.5).Should().BeTrue();
        group.Position.Should().BeApproximately(2.5, 1e-6);
        group.Seek(3.5).Should().BeFalse();
        group.Seek(-1.0).Should().BeFalse();
    }

    [Fact]
    public void Dispose_ReleasesTheClips()
    {
        var group = new GroupSource(SampleRate, Channels);
        SourceClip clip = group.AddClip(_wav(1.0), 0.0);
        group.EnsureStandaloneRustBackend();

        group.Dispose();

        clip.IsOnGroup.Should().BeFalse();
        group.Clips.Should().BeEmpty();
    }

    /// <summary>
    /// A stereo 16-bit WAV of the given length at the test rate, removed on Dispose.
    /// </summary>
    private string _wav(double seconds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ownaudio_groupsource_{Guid.NewGuid():N}.wav");
        _wavs.Add(path);

        int frames = (int)Math.Round(seconds * SampleRate);
        int dataLen = frames * Channels * 2;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)Channels);
        w.Write(SampleRate);
        w.Write(SampleRate * Channels * 2);
        w.Write((ushort)(Channels * 2));
        w.Write((ushort)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);

        for (int i = 0; i < frames * Channels; i++)
            w.Write((short)((i % 1000) * 20));

        return path;
    }
}
