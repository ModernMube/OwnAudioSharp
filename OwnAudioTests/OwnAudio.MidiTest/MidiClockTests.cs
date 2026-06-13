using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.Clock;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Unit and behavioral tests for <see cref="MidiClock"/>.
/// Tests that require timing use short sleep intervals and wide tolerances to remain
/// reliable across CI runners with varying scheduling precision.
/// </summary>
[TestClass]
public sealed class MidiClockTests
{
    #region Fields

    /// <summary>
    /// Fake MIDI output port shared between the clock and the assertions.
    /// </summary>
    private FakeMidiOutput _output = default!;

    #endregion

    #region Constructor / Setup

    /// <summary>
    /// Creates a fresh <see cref="FakeMidiOutput"/> before each test.
    /// </summary>
    [TestInitialize]
    public void Setup()
    {
        _output = new FakeMidiOutput();
    }

    #endregion

    #region Bpm Property Tests

    /// <summary>
    /// Verifies that the default constructor produces a clock with BPM == 120.
    /// </summary>
    [TestMethod]
    public void Constructor_DefaultBpm_Returns120()
    {
        using var clock = new MidiClock();

        Assert.AreEqual(120.0, clock.Bpm);
    }

    /// <summary>
    /// Verifies that setting BPM below the minimum (20) clamps to 20.
    /// </summary>
    [TestMethod]
    public void Bpm_SetBelowMinimum_ClampsTo20()
    {
        using var clock = new MidiClock();

        clock.Bpm = 1.0;

        Assert.AreEqual(20.0, clock.Bpm);
    }

    /// <summary>
    /// Verifies that setting BPM above the maximum (300) clamps to 300.
    /// </summary>
    [TestMethod]
    public void Bpm_SetAboveMaximum_ClampsTo300()
    {
        using var clock = new MidiClock();

        clock.Bpm = 999.0;

        Assert.AreEqual(300.0, clock.Bpm);
    }

    #endregion

    #region Start Tests

    /// <summary>
    /// Verifies that <see cref="MidiClock.IsRunning"/> is true immediately after <see cref="MidiClock.Start"/>.
    /// </summary>
    [TestMethod]
    public void Start_SetsIsRunning()
    {
        using var clock = new MidiClock(120.0, _output);

        clock.Start();

        Assert.IsTrue(clock.IsRunning);
        clock.Stop();
    }

    /// <summary>
    /// Verifies that calling <see cref="MidiClock.Start"/> twice does not throw
    /// and that the clock remains running.
    /// </summary>
    [TestMethod]
    public void DoubleStart_IsIdempotent()
    {
        using var clock = new MidiClock(120.0, _output);

        clock.Start();
        clock.Start();

        Assert.IsTrue(clock.IsRunning);
        clock.Stop();
    }

    /// <summary>
    /// Verifies that starting the clock causes exactly one 0xFA Start message to be sent.
    /// </summary>
    [TestMethod]
    public void Start_SendsStartMessage()
    {
        using var clock = new MidiClock(120.0, _output);

        clock.Start();
        clock.Stop();

        int startCount = _output.SentMessages.Count(m => m.Status == 0xFA);
        Assert.AreEqual(1, startCount);
    }

    #endregion

    #region Stop Tests

    /// <summary>
    /// Verifies that <see cref="MidiClock.IsRunning"/> is false after <see cref="MidiClock.Stop"/>.
    /// </summary>
    [TestMethod]
    public void Stop_ClearsIsRunning()
    {
        using var clock = new MidiClock(120.0, _output);
        clock.Start();

        clock.Stop();

        Assert.IsFalse(clock.IsRunning);
    }

    /// <summary>
    /// Verifies that stopping the clock causes exactly one 0xFC Stop message to be sent.
    /// </summary>
    [TestMethod]
    public void Stop_SendsStopMessage()
    {
        using var clock = new MidiClock(120.0, _output);
        clock.Start();

        clock.Stop();

        int stopCount = _output.SentMessages.Count(m => m.Status == 0xFC);
        Assert.AreEqual(1, stopCount);
    }

    #endregion

    #region Continue Tests

    /// <summary>
    /// Verifies that calling <see cref="MidiClock.Continue"/> on a stopped clock
    /// sends exactly one 0xFB Continue message.
    /// </summary>
    [TestMethod]
    public void Continue_SendsContinueMessage()
    {
        using var clock = new MidiClock(120.0, _output);

        clock.Continue();

        int continueCount = _output.SentMessages.Count(m => m.Status == 0xFB);
        Assert.AreEqual(1, continueCount);
        clock.Stop();
    }

    #endregion

    #region Dispose Tests

    /// <summary>
    /// Verifies that <see cref="MidiClock.Dispose"/> can be called twice without throwing.
    /// </summary>
    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var clock = new MidiClock(120.0, _output);
        clock.Start();

        clock.Dispose();
        clock.Dispose();
    }

    /// <summary>
    /// Verifies that <see cref="MidiClock.Dispose"/> stops a running clock without throwing.
    /// </summary>
    [TestMethod]
    public void Dispose_StopsRunningClock_DoesNotThrow()
    {
        var clock = new MidiClock(120.0, _output);
        clock.Start();

        clock.Dispose();

        Assert.IsFalse(clock.IsRunning);
    }

    #endregion

    #region Pulse Timing Tests

    /// <summary>
    /// Starts the clock at 120 BPM, sleeps 100 ms, then stops it.
    /// Counts the 0xF8 Timing Clock pulses and asserts the count is within a wide but
    /// sensible range: 24 PPQN × 2 BPS × 0.1 s = 4.8 pulses → expected 3 to 8 on any machine.
    /// </summary>
    [TestMethod]
    public void PulsesReceived_Within100Ms_WithinTimingTolerance()
    {
        using var clock = new MidiClock(120.0, _output);

        clock.Start();
        Thread.Sleep(100);
        clock.Stop();

        int pulses = _output.SentMessages.Count(m => m.Status == 0xF8);
        Assert.IsTrue(pulses >= 3 && pulses <= 8,
            $"Expected between 3 and 8 0xF8 pulses in 100 ms at 120 BPM; received {pulses}.");
    }

    #endregion
}
