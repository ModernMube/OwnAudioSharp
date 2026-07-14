using System;
using System.Diagnostics;
using System.Threading;
using FluentAssertions;
using Ownaudio.Audio.Tracks;
using Ownaudio.Core;
using OwnaudioNET.Engine;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using Xunit;
using AudioEngineFactory = OwnaudioNET.Engine.AudioEngineFactory;

namespace Ownaudio.OwnaudioNET.Tests.Mixing;

/// <summary>
/// E.5 verification — a built-in effect routed to the native master bus in the Rust-native chain is
/// <em>audible</em>: it measurably alters the rendered master output, not merely "does not throw".
/// </summary>
/// <remarks>
/// <para>
/// The earlier <see cref="AudioMixerRustNativeOutputSmokeTests"/> proves that adding master effects
/// while running does not crash and that audio keeps flowing; this test closes the remaining E.5 gap
/// by proving the effect is actually <em>in the signal path</em> and changes what reaches the output.
/// </para>
/// <para>
/// It plays a steady in-memory tone through a running Rust-native mixer and captures the native master
/// output (post master-effect, via <see cref="MultiTrackSession.StartCapture"/>) in two phases: first
/// with no master effect, then after inserting a strong all-band equalizer cut. The captured RMS must
/// drop clearly once the effect is active — the headless, objective equivalent of the live "the effect
/// is now audible" check. Requires a working native engine and output device; when none is available
/// the test returns early (treated as passing), matching the project's device-dependent convention.
/// </para>
/// </remarks>
[Collection("RustNativeChain")]
public sealed class AudioMixerRustNativeEffectAudibilityTests : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly bool? _priorOverride;

    /// <summary>
    /// Forces the Rust-native chain on for the duration of the test.
    /// </summary>
    public AudioMixerRustNativeEffectAudibilityTests()
    {
        _priorOverride = RustNativeChain.Override;
        RustNativeChain.Override = true;
    }

    /// <summary>
    /// Restores the opt-in override.
    /// </summary>
    public void Dispose() => RustNativeChain.Override = _priorOverride;

    /// <summary>
    /// A strong all-band equalizer cut inserted on the native master bus must audibly attenuate the
    /// rendered master output, proving the built-in effect is processed natively in the signal path.
    /// </summary>
    [Fact]
    public void MasterEqualizerCut_AudiblyAttenuatesNativeMasterOutput()
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
            // Four seconds of a steady ~380 Hz stereo tone in memory (well inside the EQ range), long
            // enough to outlast the whole capture with margin. Both channels share the frame phase.
            var samples = new float[SampleRate * Channels * 4];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.3f * MathF.Sin((i / Channels) * 0.05f);
            }
            var sourceConfig = new AudioConfig { SampleRate = SampleRate, Channels = Channels, BufferSize = 512 };

            using var mixer = new AudioMixer(engine, 512);
            using var source = new SampleSource(samples, sourceConfig);
            mixer.AddSource(source).Should().BeTrue();

            mixer.Start();
            source.Play();

            MultiTrackSession? session = mixer.RustSession;
            session.Should().NotBeNull("adding a source to a started Rust-native mixer opens the shared session");

            // A ~1 s ring so the periodic drain never falls behind the device.
            session!.StartCapture(SampleRate * Channels);

            var buffer = new float[8192];

            // Phase 1 — clean (no master effect).
            double cleanSumSq = 0;
            long cleanCount = 0;
            DrainFor(session, buffer, 400, ref cleanSumSq, ref cleanCount);

            // Insert a strong all-band EQ cut on the master bus and mirror it onto the native effect.
            var eq = new global::OwnaudioNET.Effects.EqualizerEffect(sampleRate: SampleRate)
            {
                Band0Gain = -12f, Band1Gain = -12f, Band2Gain = -12f, Band3Gain = -12f, Band4Gain = -12f,
                Band5Gain = -12f, Band6Gain = -12f, Band7Gain = -12f, Band8Gain = -12f, Band9Gain = -12f,
            };
            mixer.AddMasterEffect(eq);
            mixer.MirrorRustMasterEffectsOnce();

            // Let the change reach the device; discard the transition region.
            double discardSumSq = 0;
            long discardCount = 0;
            DrainFor(session, buffer, 200, ref discardSumSq, ref discardCount);

            // Phase 2 — with the effect active.
            double fxSumSq = 0;
            long fxCount = 0;
            DrainFor(session, buffer, 400, ref fxSumSq, ref fxCount);

            session.StopCapture();
            mixer.Stop();

            cleanCount.Should().BeGreaterThan(0, "the native master capture must yield samples in the clean phase");
            fxCount.Should().BeGreaterThan(0, "the native master capture must yield samples with the effect active");

            double cleanRms = Math.Sqrt(cleanSumSq / cleanCount);
            double fxRms = Math.Sqrt(fxSumSq / fxCount);

            cleanRms.Should().BeGreaterThan(0.05, "the tone must actually be rendering to the master output");
            fxRms.Should().BeLessThan(cleanRms * 0.6,
                "the native master EQ cut must audibly attenuate the rendered output, proving the built-in effect is in the native signal path");
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// Drains the session's master capture for <paramref name="milliseconds"/>, accumulating the sum of
    /// squares and sample count of everything read (for an incremental RMS).
    /// </summary>
    /// <param name="session">The capturing session.</param>
    /// <param name="buffer">Scratch buffer for <see cref="MultiTrackSession.ReadCapture"/>.</param>
    /// <param name="milliseconds">How long to keep draining.</param>
    /// <param name="sumSq">Accumulated sum of squared samples.</param>
    /// <param name="count">Accumulated sample count.</param>
    private static void DrainFor(
        MultiTrackSession session, float[] buffer, int milliseconds, ref double sumSq, ref long count)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            int n = session.ReadCapture(buffer);
            for (int i = 0; i < n; i++)
            {
                double s = buffer[i];
                sumSq += s * s;
            }
            count += n;
            Thread.Sleep(5);
        }
    }
}
