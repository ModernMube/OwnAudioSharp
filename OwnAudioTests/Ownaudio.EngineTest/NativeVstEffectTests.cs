using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Tests for hosting an external VST3 plugin natively in the Rust effect chain (plan E.6): the
/// master and track chains accept an opaque plugin handle plus a process function pointer through the
/// VST FFI, register the effect, and remove it — all without opening an audio device or invoking the
/// (fake) process callback.
/// </summary>
[TestClass]
public class NativeVstEffectTests
{
    private const int SampleRate = 48_000;
    private const ushort Channels = 2;
    private const uint BlockSize = 512;

    // The Rust bridge only checks that the plugin handle and process pointer are non-null when the
    // effect is added; neither is dereferenced until the mixer renders, which these tests never do.
    private static readonly IntPtr FakePluginHandle = new(0x1234);
    private static readonly IntPtr FakeProcessFn = new(0x5678);

    [TestMethod]
    public void AddVstMasterEffect_RegistersInChain()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        Assert.AreEqual(0, session.MasterEffects.Effects.Count);

        object token = session.MasterEffects.AddVst(FakePluginHandle, FakeProcessFn, Channels, BlockSize, 0);

        Assert.IsInstanceOfType(token, typeof(NativeVstEffect));
        Assert.AreEqual(1, session.MasterEffects.Effects.Count);
        Assert.AreSame(token, session.MasterEffects.Effects[0]);
    }

    [TestMethod]
    public void VstMasterEffect_EnabledAndMixParametersRoundTrip()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        object token = session.MasterEffects.AddVst(FakePluginHandle, FakeProcessFn, Channels, BlockSize, 0);

        // The bridge exposes only the shared enable (0) and mix (1) parameters; both round-trip
        // through the control-side shadow. Effect-specific plugin parameters are not exposed here.
        Assert.IsTrue(session.MasterEffects.SetParam(token, 1u, 0.25f));
        Assert.AreEqual(0.25f, session.MasterEffects.GetParam(token, 1u));

        Assert.IsTrue(session.MasterEffects.SetParam(token, 0u, 0.0f));
        Assert.AreEqual(0.0f, session.MasterEffects.GetParam(token, 0u));
    }

    [TestMethod]
    public void RemoveVstMasterEffect_ClearsChain()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        object token = session.MasterEffects.AddVst(FakePluginHandle, FakeProcessFn, Channels, BlockSize, 0);
        Assert.AreEqual(1, session.MasterEffects.Effects.Count);

        session.MasterEffects.Remove(token);
        Assert.AreEqual(0, session.MasterEffects.Effects.Count);
    }

    [TestMethod]
    public void AddVstTrackEffect_RegistersOnTrack()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);
        AudioTrack track = session.AddTrack();

        object token = track.Effects.AddVst(FakePluginHandle, FakeProcessFn, Channels, BlockSize, 0);

        Assert.IsInstanceOfType(token, typeof(NativeVstEffect));
        Assert.AreEqual(1, track.Effects.Effects.Count);

        track.Effects.Remove(token);
        Assert.AreEqual(0, track.Effects.Effects.Count);
    }
}
