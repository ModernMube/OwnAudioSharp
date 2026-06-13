using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.Clock;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Unit tests for <see cref="AudioEngineMidiClock"/>.
/// All tests use <see cref="FakeMidiOutput"/> so no hardware is required.
/// </summary>
[TestClass]
public sealed class AudioEngineMidiClockTests
{
    #region Fields

    /// <summary>
    /// System under test, recreated for each test method.
    /// </summary>
    private AudioEngineMidiClock _clock = default!;

    /// <summary>
    /// Fake output port that records every <see cref="MidiMessage"/> sent by the clock.
    /// </summary>
    private FakeMidiOutput _output = default!;

    #endregion

    #region Constructor / Setup

    /// <summary>
    /// Initializes a fresh <see cref="AudioEngineMidiClock"/> and <see cref="FakeMidiOutput"/>
    /// before each test so tests remain independent.
    /// </summary>
    [TestInitialize]
    public void Setup()
    {
        _clock = new AudioEngineMidiClock();
        _output = new FakeMidiOutput();
    }

    #endregion

    #region ProcessAudioBlock Tests

    /// <summary>
    /// Verifies that calling <see cref="AudioEngineMidiClock.ProcessAudioBlock"/> before
    /// <see cref="AudioEngineMidiClock.UpdateTempo"/> never calls Send on the output port,
    /// guarding against a divide-by-zero or infinite-loop condition.
    /// </summary>
    [TestMethod]
    public void ProcessAudioBlock_BeforeUpdateTempo_DoesNotSend()
    {
        _clock.ProcessAudioBlock(1024, _output);

        Assert.AreEqual(0, _output.SentMessages.Count);
    }

    /// <summary>
    /// Verifies that at 120 BPM and 48000 Hz exactly one pulse is emitted when
    /// exactly one pulse-worth of samples (1000) is processed.
    /// At 120 BPM: samplesPerPulse = 48000 / (120 * 24 / 60) = 48000 / 48 = 1000.
    /// </summary>
    [TestMethod]
    public void UpdateTempo_ThenProcessBlock_SendsCorrectPulseCount()
    {
        _clock.UpdateTempo(bpm: 120.0, sampleRate: 48000);

        _clock.ProcessAudioBlock(1000, _output);

        Assert.AreEqual(1, _output.SentMessages.Count);
    }

    /// <summary>
    /// Verifies fractional sample accumulation: 499 samples produces no pulse,
    /// then 501 more samples crosses the 1000-sample boundary producing exactly one pulse.
    /// </summary>
    [TestMethod]
    public void ProcessAudioBlock_AccumulatesFractionalSamples_EmitsPulseOnBoundary()
    {
        _clock.UpdateTempo(bpm: 120.0, sampleRate: 48000);

        _clock.ProcessAudioBlock(499, _output);

        Assert.AreEqual(0, _output.SentMessages.Count);

        _clock.ProcessAudioBlock(501, _output);

        Assert.AreEqual(1, _output.SentMessages.Count);
    }

    /// <summary>
    /// Verifies that a block of 4800 samples at 120 BPM / 48000 Hz emits exactly 4 pulses
    /// (4800 / 1000 = 4).
    /// </summary>
    [TestMethod]
    public void MultiplePulsesPerBlock_LargeBlock_EmitsCorrectCount()
    {
        _clock.UpdateTempo(bpm: 120.0, sampleRate: 48000);

        _clock.ProcessAudioBlock(4800, _output);

        Assert.AreEqual(4, _output.SentMessages.Count);
    }

    /// <summary>
    /// Verifies that every message sent by the clock carries the 0xF8 Timing Clock status byte.
    /// </summary>
    [TestMethod]
    public void PulseMessage_IsTimingClock_StatusIs0xF8()
    {
        _clock.UpdateTempo(bpm: 120.0, sampleRate: 48000);
        _clock.ProcessAudioBlock(3000, _output);

        foreach (MidiMessage msg in _output.SentMessages)
            Assert.AreEqual(0xF8, msg.Status);
    }

    /// <summary>
    /// Verifies that changing tempo mid-stream adjusts timing.
    /// At 120 BPM / 48000 Hz a full pulse is 1000 samples. After accumulating 499 samples
    /// the tempo doubles to 240 BPM (pulse = 500 samples). Because 499 samples already
    /// accumulated exceeds the new threshold of 500 just one sample later, at least one
    /// pulse must fire within the first block after the tempo change.
    /// </summary>
    [TestMethod]
    public void UpdateTempo_ChangesBpmLive_AdjustsTiming()
    {
        _clock.UpdateTempo(bpm: 120.0, sampleRate: 48000);
        _clock.ProcessAudioBlock(499, _output);

        Assert.AreEqual(0, _output.SentMessages.Count, "No pulse expected before tempo change.");

        _clock.UpdateTempo(bpm: 240.0, sampleRate: 48000);
        _clock.ProcessAudioBlock(500, _output);

        Assert.IsTrue(_output.SentMessages.Count >= 1, "At least one pulse expected after tempo change.");
    }

    /// <summary>
    /// Verifies that passing zero BPM to <see cref="AudioEngineMidiClock.UpdateTempo"/>
    /// does not cause a crash when <see cref="AudioEngineMidiClock.ProcessAudioBlock"/> is
    /// subsequently called. The guard inside ProcessAudioBlock (<c>if (_samplesPerPulse &lt;= 0) return</c>)
    /// should prevent any division by zero.
    /// </summary>
    [TestMethod]
    public void UpdateTempo_ZeroBpm_DoesNotThrow()
    {
        _clock.UpdateTempo(bpm: 0.0, sampleRate: 48000);

        _clock.ProcessAudioBlock(1024, _output);

        Assert.AreEqual(0, _output.SentMessages.Count);
    }

    #endregion
}
