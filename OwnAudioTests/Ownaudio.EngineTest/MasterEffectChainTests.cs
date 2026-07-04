using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ownaudio.Audio.Effects;
using Ownaudio.Audio.Tracks;

namespace Ownaudio.EngineTest;

/// <summary>
/// Tests for the native master effect chain (plan E.1): effects added to
/// <see cref="MultiTrackSession.MasterEffects"/> are created, parameter-controlled and removed
/// through the master-bus FFI, without opening an audio device.
/// </summary>
[TestClass]
public class MasterEffectChainTests
{
    private const int SampleRate = 48_000;
    private const ushort Channels = 2;

    [TestMethod]
    public void AddMasterEffect_RegistersInChain()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        Assert.AreEqual(0, session.MasterEffects.Effects.Count);

        var reverb = session.MasterEffects.Add<ReverbEffect>(SampleRate);

        Assert.IsNotNull(reverb);
        Assert.AreEqual(1, session.MasterEffects.Effects.Count);
        Assert.AreSame(reverb, session.MasterEffects.Effects[0]);
    }

    [TestMethod]
    public void MasterEffect_ParameterRoundTrips()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        var compressor = (CompressorEffect)session.MasterEffects.Add(EffectType.Compressor, SampleRate);

        // Setting a typed parameter forwards to the native effect via set_param and the control-side
        // shadow reflects it back.
        compressor.ThresholdDb = -18.0f;
        Assert.AreEqual(-18.0f, compressor.ThresholdDb, 0.0001f);
    }

    [TestMethod]
    public void RemoveMasterEffect_ClearsChain()
    {
        using var session = new MultiTrackSession(SampleRate, Channels);

        session.MasterEffects.Add<ReverbEffect>(SampleRate);
        session.MasterEffects.Add<CompressorEffect>(SampleRate);
        Assert.AreEqual(2, session.MasterEffects.Effects.Count);

        session.MasterEffects.RemoveAt(0);
        Assert.AreEqual(1, session.MasterEffects.Effects.Count);

        session.MasterEffects.Clear();
        Assert.AreEqual(0, session.MasterEffects.Effects.Count);
    }
}
