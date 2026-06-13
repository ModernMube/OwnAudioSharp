using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Tests for the hot-plug monitoring surface of <see cref="MidiPortFactory"/>.
/// Validates that the monitoring API is safe to call in all lifecycle orders
/// and that the <see cref="MidiPortFactory.PortsChanged"/> event can be subscribed
/// and unsubscribed without side effects.
/// </summary>
[TestClass]
public sealed class HotPlugMonitoringTests
{
    #region Constructor / Setup

    /// <summary>
    /// Ensures monitoring is stopped before each test so tests start from a clean state.
    /// </summary>
    [TestInitialize]
    public void Setup()
    {
        MidiPortFactory.StopMonitoring();
    }

    /// <summary>
    /// Ensures monitoring is stopped after each test to avoid leaking file system watchers.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        MidiPortFactory.StopMonitoring();
    }

    #endregion

    #region StartMonitoring Tests

    /// <summary>
    /// Verifies that <see cref="MidiPortFactory.StartMonitoring"/> does not throw on any platform.
    /// </summary>
    [TestMethod]
    public void StartMonitoring_DoesNotThrow()
    {
        MidiPortFactory.StartMonitoring();
    }

    /// <summary>
    /// Verifies that calling <see cref="MidiPortFactory.StartMonitoring"/> twice in a row
    /// (without an intervening stop) does not throw.
    /// </summary>
    [TestMethod]
    public void DoubleStart_DoesNotThrow()
    {
        MidiPortFactory.StartMonitoring();
        MidiPortFactory.StartMonitoring();
    }

    #endregion

    #region StopMonitoring Tests

    /// <summary>
    /// Verifies that <see cref="MidiPortFactory.StopMonitoring"/> called before
    /// <see cref="MidiPortFactory.StartMonitoring"/> does not throw.
    /// </summary>
    [TestMethod]
    public void StopMonitoring_WithoutStart_DoesNotThrow()
    {
        MidiPortFactory.StopMonitoring();
    }

    /// <summary>
    /// Verifies that calling <see cref="MidiPortFactory.StopMonitoring"/> twice in a row
    /// does not throw.
    /// </summary>
    [TestMethod]
    public void DoubleStop_DoesNotThrow()
    {
        MidiPortFactory.StartMonitoring();
        MidiPortFactory.StopMonitoring();
        MidiPortFactory.StopMonitoring();
    }

    #endregion

    #region PortsChanged Event Tests

    /// <summary>
    /// Verifies that subscribing to and unsubscribing from <see cref="MidiPortFactory.PortsChanged"/>
    /// does not throw, confirming the event add/remove accessors are safe to call freely.
    /// </summary>
    [TestMethod]
    public void PortsChanged_CanSubscribeAndUnsubscribe()
    {
        void Handler() { }

        MidiPortFactory.PortsChanged += Handler;
        MidiPortFactory.PortsChanged -= Handler;
    }

    #endregion
}
