using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// D.2.d (plan 14 / WS2, output wiring) device smoke for the Rust-native <see cref="AudioMixer"/>:
/// with the shared-engine approach, starting the mixer opens the shared session's native output on
/// the underlying engine's device and suspends the engine's own push output. This exercises the
/// real <c>OpenOutput</c>/<c>SuspendOutput</c> path end-to-end.
/// </summary>
/// <remarks>
/// Requires a working native engine and output device; when none is available the test returns
/// early (treated as passing), matching the project's device-dependent test convention. It plays a
/// very short buffer, so it is intentionally kept out of the pure headless suite behavior.
/// </remarks>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeOutputSmokeTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;
    private readonly string _wavPath;

    /// <summary>
    /// Enables the Rust-native chain and writes a short temp WAV source.
    /// </summary>
    public AudioMixerRustNativeOutputSmokeTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;
        _wavPath = WriteTempWav(Channels, SampleRate, frames: SampleRate / 2);
    }

    /// <summary>
    /// Restores the opt-in override and removes the temp WAV.
    /// </summary>
    public void Dispose()
    {
        RustNativeChain.Override = _priorOverride;
        try
        {
            if (File.Exists(_wavPath))
                File.Delete(_wavPath);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Adding master effects to a Rust-native mixer that is already running on a real engine (as the
    /// player does) must not throw. Reproduction for the "add effects to master → crash" report.
    /// </summary>
    [Fact]
    public void AddMasterEffects_OnRunningRealEngine_DoesNotThrow()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate, Channels = Channels, BufferSize = 512,
            EnableOutput = true, EnableInput = false,
        };

        IAudioEngine engine;
        try { engine = AudioEngineFactory.CreateEngine(config); }
        catch { return; }

        try
        {
            using var mixer = new AudioMixer(engine, 512);
            using var source = new FileSource(_wavPath);
            mixer.AddSource(source);
            mixer.Start();
            Thread.Sleep(120);

            // Add every built-in effect type to the master while running — find any that throws.
            var factories = new (string Name, Func<global::OwnaudioNET.Interfaces.IEffectProcessor> Make)[]
            {
                ("Reverb", () => new global::OwnaudioNET.Effects.ReverbEffect()),
                ("Equalizer", () => new global::OwnaudioNET.Effects.EqualizerEffect(sampleRate: SampleRate)),
                ("Equalizer30", () => new global::OwnaudioNET.Effects.Equalizer30BandEffect(sampleRate: SampleRate)),
                ("Compressor", () => new global::OwnaudioNET.Effects.CompressorEffect()),
                ("Limiter", () => new global::OwnaudioNET.Effects.LimiterEffect(SampleRate)),
                ("Delay", () => new global::OwnaudioNET.Effects.DelayEffect(sampleRate: SampleRate)),
                ("Chorus", () => new global::OwnaudioNET.Effects.ChorusEffect(sampleRate: SampleRate)),
                ("Distortion", () => new global::OwnaudioNET.Effects.DistortionEffect()),
                ("Overdrive", () => new global::OwnaudioNET.Effects.OverdriveEffect()),
                ("Flanger", () => new global::OwnaudioNET.Effects.FlangerEffect(sampleRate: SampleRate)),
                ("Phaser", () => new global::OwnaudioNET.Effects.PhaserEffect(sampleRate: SampleRate)),
                ("Rotary", () => new global::OwnaudioNET.Effects.RotaryEffect()),
                ("AutoGain", () => new global::OwnaudioNET.Effects.AutoGainEffect()),
                ("Enhancer", () => new global::OwnaudioNET.Effects.EnhancerEffect(sampleRate: SampleRate)),
                ("DynamicAmp", () => new global::OwnaudioNET.Effects.DynamicAmpEffect()),
            };

            Action act = () =>
            {
                foreach (var (name, make) in factories)
                {
                    mixer.ClearMasterEffects();
                    try
                    {
                        mixer.AddMasterEffect(make());
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"AddMasterEffect threw for {name}: {ex.GetType().Name}: {ex.Message}", ex);
                    }
                    mixer.MirrorRustMasterEffectsOnce();
                }
            };

            act.Should().NotThrow();
            Thread.Sleep(150);
            mixer.Stop();
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// Starting a Rust-native mixer on a real engine opens the shared session output and runs
    /// without error; stopping and disposing tear it down cleanly.
    /// </summary>
    [Fact]
    public void Start_OnRealEngine_OpensSessionOutput_AndTearsDownCleanly()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        try
        {
            using var mixer = new AudioMixer(engine, 512);
            using var source = new FileSource(_wavPath);
            mixer.AddSource(source).Should().BeTrue();

            mixer.Start();
            mixer.IsRunning.Should().BeTrue();
            mixer.RustSession.Should().NotBeNull();

            Thread.Sleep(120);

            mixer.Stop();
            mixer.IsRunning.Should().BeFalse();
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// A <see cref="global::OwnaudioNET.Sources.SampleSource"/> played through a Rust-native mixer on
    /// a real engine actually renders natively: its native memory track advances
    /// <see cref="Ownaudio.Audio.Tracks.AudioTrack.RenderedFrames"/> as the device consumes the
    /// buffer — proving the in-memory audio reaches the output with no managed audio path.
    /// </summary>
    [Fact]
    public void SampleSource_OnRealEngine_RendersNatively()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        try
        {
            // One second of quiet stereo tone in memory.
            var samples = new float[SampleRate * Channels];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.1f * MathF.Sin(i * 0.01f);
            }
            var sourceConfig = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };

            using var mixer = new AudioMixer(engine, 512);
            using var source = new global::OwnaudioNET.Sources.SampleSource(samples, sourceConfig);
            mixer.AddSource(source).Should().BeTrue();
            source.RustTrack.Should().NotBeNull();

            mixer.Start();
            source.Play();

            Thread.Sleep(200);

            // The native memory source has been read and mixed to the device.
            source.RustTrack!.RenderedFrames.Should().BeGreaterThan(0UL,
                "the native memory track must render the in-memory buffer to the device");

            mixer.Stop();
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// During Rust-native playback the master clock is advanced from the native track position by the
    /// control-rate sync tick (the managed MixThread that legacy uses for this does not run), so the
    /// reported position tracks the audio instead of freezing.
    /// </summary>
    [Fact]
    public void Playback_AdvancesMasterClock_FromNativeTrack()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        try
        {
            using var mixer = new AudioMixer(engine, 512);
            using var source = new FileSource(_wavPath);
            mixer.AddSource(source);

            mixer.Start();
            source.Play();

            mixer.MasterClock.CurrentTimestamp.Should().Be(0.0, "clock starts at zero before any audio renders");

            Thread.Sleep(250);

            double advanced = mixer.MasterClock.CurrentTimestamp;
            mixer.Stop();

            advanced.Should().BeGreaterThan(0.05,
                "the master clock must advance from the native track's rendered position during playback");
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// Regression for the "start the mixer, then add sources" ordering (as the MultitrackPlayer does):
    /// the mixer is started with no sources — so at <see cref="AudioMixer.Start"/> time there is no
    /// session and the native output stream is not opened — and a source is added and played only
    /// afterwards. The first added source must lazily open the shared output so audio actually renders
    /// and the master clock advances, rather than staying silent with a frozen position.
    /// </summary>
    [Fact]
    public void AddSourceAfterStart_OpensOutput_AndAdvancesClock()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        try
        {
            using var mixer = new AudioMixer(engine, 512);

            // Start BEFORE any source exists — the session is still null here, so Start opens no output.
            mixer.Start();
            mixer.IsRunning.Should().BeTrue();

            using var source = new FileSource(_wavPath);
            mixer.AddSource(source).Should().BeTrue();
            source.Play();

            mixer.MasterClock.CurrentTimestamp.Should().Be(0.0, "clock starts at zero before any audio renders");

            Thread.Sleep(250);

            double advanced = mixer.MasterClock.CurrentTimestamp;
            ulong rendered = source.RustTrack?.RenderedFrames ?? 0UL;
            mixer.Stop();

            rendered.Should().BeGreaterThan(0UL,
                "adding the first source after Start must open the shared native output so the track renders");
            advanced.Should().BeGreaterThan(0.05,
                "the master clock must advance even when sources are added after the mixer was started");
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// Regression for the MultitrackPlayer's SmartMaster panel: enabling SmartMaster while a
    /// Rust-native mixer is already playing must insert it on the native master bus and keep audio
    /// flowing (the managed effect only processes while attached). Adds SmartMaster mid-playback,
    /// mirrors it, toggles its Enabled state, and reloads a speaker preset — none may throw and the
    /// track must keep rendering to the device.
    /// </summary>
    [Fact]
    public void AddSmartMasterWhileRunning_MirrorsAndKeepsRendering()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        try
        {
            using var mixer = new AudioMixer(engine, 512);
            using var source = new FileSource(_wavPath);
            mixer.AddSource(source);
            mixer.Start();
            source.Play();

            Thread.Sleep(120);

            var smartMaster = new global::OwnaudioNET.Effects.SmartMaster.SmartMasterEffect();
            var effectConfig = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };
            smartMaster.Initialize(effectConfig);

            Action act = () =>
            {
                // Insert on the running mixer (what the SmartMaster enable toggle now does).
                mixer.AddMasterEffect(smartMaster);
                mixer.MirrorRustMasterEffectsOnce();

                // Load a factory preset — the control tick mirrors the new config onto the native effect.
                smartMaster.LoadSpeakerPreset(global::OwnaudioNET.Effects.SmartMaster.SpeakerType.HiFi);
                mixer.MirrorRustMasterEffectsOnce();

                // Toggle enabled off/on — mirrored as the native enable parameter.
                smartMaster.Enabled = false;
                mixer.MirrorRustMasterEffectsOnce();
                smartMaster.Enabled = true;
                mixer.MirrorRustMasterEffectsOnce();
            };

            act.Should().NotThrow();

            Thread.Sleep(150);

            ulong rendered = source.RustTrack?.RenderedFrames ?? 0UL;
            mixer.Stop();

            rendered.Should().BeGreaterThan(0UL,
                "audio must keep rendering through the master bus after SmartMaster is attached live");
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// Regression for "recording is empty in the Rust-native chain": because the mix is rendered
    /// natively (the managed mix thread never runs), <c>WriteToRecorder</c> is never called, so the
    /// recorder must instead drain the mixer's native master-output capture. Plays a non-silent
    /// in-memory tone through a running Rust-native mixer, records it, and asserts the WAV contains
    /// real (non-zero) audio well beyond the header.
    /// </summary>
    [Fact]
    public void Recording_OnRealEngine_CapturesNativeMasterOutputToWav()
    {
        if (!AudioEngineFactory.IsNativeEngineAvailable())
        {
            return;
        }

        var config = new AudioConfig
        {
            SampleRate = SampleRate,
            Channels = Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = false,
        };

        IAudioEngine engine;
        try
        {
            engine = AudioEngineFactory.CreateEngine(config);
        }
        catch
        {
            return;
        }

        string recordPath = Path.Combine(
            Path.GetTempPath(), $"ownaudio_rustnative_record_{Guid.NewGuid():N}.wav");

        try
        {
            // One second of clearly non-silent stereo tone in memory.
            var samples = new float[SampleRate * Channels];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.3f * MathF.Sin(i * 0.05f);
            }
            var sourceConfig = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };

            using var mixer = new AudioMixer(engine, 512);
            using var source = new global::OwnaudioNET.Sources.SampleSource(samples, sourceConfig);
            mixer.AddSource(source).Should().BeTrue();

            mixer.Start();
            source.Play();

            Thread.Sleep(100);
            mixer.StartRecording(recordPath);
            mixer.IsRecording.Should().BeTrue();

            Thread.Sleep(400);

            mixer.StopRecording();
            mixer.IsRecording.Should().BeFalse();
            mixer.Stop();

            File.Exists(recordPath).Should().BeTrue();

            byte[] bytes = File.ReadAllBytes(recordPath);
            // Header is 44 bytes; a real capture of ~0.4 s must add many kilobytes of PCM data.
            bytes.Length.Should().BeGreaterThan(44 + 8000,
                "the native master-output capture must have written substantial audio to the WAV");

            bool hasNonZeroPcm = false;
            for (int i = 44; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    hasNonZeroPcm = true;
                    break;
                }
            }

            hasNonZeroPcm.Should().BeTrue(
                "the recorded WAV must contain the non-silent rendered mix, not silence");
        }
        finally
        {
            engine.Dispose();
            try
            {
                if (File.Exists(recordPath))
                    File.Delete(recordPath);
            }
            catch
            {
                // best-effort cleanup
            }
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
            $"ownaudio_mixer_rustnative_smoke_{Guid.NewGuid():N}.wav");

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
