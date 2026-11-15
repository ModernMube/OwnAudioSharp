using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// The engine-side fades, control plane: what a request does before and after the session
/// exists, and that nothing waits forever without an output. What the ramp sounds like is
/// the Rust side's (fade.rs, multitrack tests).
/// </summary>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeFadeTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly IAudioEngine _engine;
    private readonly string _wav;

    public AudioMixerRustNativeFadeTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;

        _engine = AudioEngineFactory.CreateMockEngine(new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 });
        _wav = _silentWav();
    }

    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        _engine.Dispose();
        try { File.Delete(_wav); } catch (IOException) { }
    }

    [Fact]
    public void AMasterFadeAskedBeforeTheSessionLandsOnIt()
    {
        using var mixer = new AudioMixer(_engine, 512);

        mixer.FadeMaster(0f, TimeSpan.Zero);
        mixer.IsMasterFadeDone.Should().BeTrue();
        mixer.MasterFadeLevel.Should().Be(0f);

        using var source = new FileSource(_wav);
        mixer.AddSourcePrepared(source);

        mixer.IsMasterFadeDone.Should().BeFalse("the jump to silence waits for the first rendered block");
        mixer.MasterVolume.Should().Be(1f, "the fade never moves the user's level");
    }

    [Fact]
    public void ASourceFadesOnlyOnTheMixer()
    {
        using var mixer = new AudioMixer(_engine, 512);
        using var source = new FileSource(_wav);

        mixer.FadeSource(source, 0f, TimeSpan.FromSeconds(1)).Should().BeFalse();
        mixer.IsSourceFadeDone(source).Should().BeTrue();

        mixer.AddSourcePrepared(source);

        mixer.FadeSource(source, 0f, TimeSpan.FromSeconds(1)).Should().BeTrue();
        mixer.IsSourceFadeDone(source).Should().BeFalse();
    }

    [Fact]
    public async Task WaitingOnAFadeWithNoOutputReturns()
    {
        using var mixer = new AudioMixer(_engine, 512);
        using var source = new FileSource(_wav);
        mixer.AddSourcePrepared(source);

        var wait = mixer.FadeMasterAsync(0f, TimeSpan.FromSeconds(5));

        (await Task.WhenAny(wait, Task.Delay(2000))).Should().BeSameAs(wait);
        await wait;
    }

    [Fact]
    public void TheManagedMixRefusesAFade()
    {
        RustNativeChain.Override = false;
        using var mixer = new AudioMixer(_engine, 512);

        mixer.Invoking(m => m.FadeMaster(0f, TimeSpan.FromSeconds(1))).Should().Throw<NotSupportedException>();
    }

    /// <summary>
    /// A tenth of a second of 16-bit stereo silence.
    /// </summary>
    private static string _silentWav()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ownaudio_fade_{Guid.NewGuid():N}.wav");
        const int frames = SampleRate / 10;
        int data = frames * Channels * 2;

        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + data);
            writer.Write("WAVEfmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * Channels * 2);
            writer.Write((short)(Channels * 2));
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(data);
            writer.Write(new byte[data]);
        }

        return path;
    }
}
