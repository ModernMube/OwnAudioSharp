using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Cross-platform factory tests for <see cref="MidiPortFactory"/>.
/// Tests that are unavailable on the current OS are marked
/// <see cref="Assert.Inconclusive"/> rather than failing, so the suite
/// remains green on all supported platforms.
/// </summary>
[TestClass]
public sealed class MidiPortFactoryTests
{
    #region Port Enumeration Tests

    /// <summary>
    /// Verifies that <see cref="MidiPortFactory.GetInputPortNames"/> returns a non-null list
    /// on all supported platforms.
    /// </summary>
    [TestMethod]
    public void GetInputPortNames_ReturnsNonNullList()
    {
        var names = MidiPortFactory.GetInputPortNames();

        Assert.IsNotNull(names);
    }

    /// <summary>
    /// Verifies that <see cref="MidiPortFactory.GetOutputPortNames"/> returns a non-null list
    /// on all supported platforms.
    /// </summary>
    [TestMethod]
    public void GetOutputPortNames_ReturnsNonNullList()
    {
        var names = MidiPortFactory.GetOutputPortNames();

        Assert.IsNotNull(names);
    }

    #endregion

    #region OpenInput Tests

    /// <summary>
    /// Verifies that attempting to open a MIDI input port with an unknown name
    /// throws <see cref="ArgumentException"/>.
    /// </summary>
    [TestMethod]
    public void OpenInput_InvalidName_ThrowsArgumentException()
    {
        bool threw = false;
        try
        {
            MidiPortFactory.OpenInput("__nonexistent_port_xyz_12345__");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Assert.IsTrue(threw, "Expected ArgumentException was not thrown.");
    }

    #endregion

    #region OpenOutput Tests

    /// <summary>
    /// Verifies that attempting to open a MIDI output port with an unknown name
    /// throws <see cref="ArgumentException"/>.
    /// </summary>
    [TestMethod]
    public void OpenOutput_InvalidName_ThrowsArgumentException()
    {
        bool threw = false;
        try
        {
            MidiPortFactory.OpenOutput("__nonexistent_port_xyz_12345__");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Assert.IsTrue(threw, "Expected ArgumentException was not thrown.");
    }

    #endregion

    #region Virtual Input Tests

    /// <summary>
    /// On macOS or Linux, creates a virtual MIDI input port, verifies it reports IsOpen == true
    /// after opening, and disposes it without throwing.
    /// Skipped on Windows because WinMM does not support virtual ports.
    /// </summary>
    [TestMethod]
    public void CreateVirtualInput_OnMacOrLinux_CreatesAndDisposes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Virtual MIDI input ports are not supported on Windows.");
            return;
        }

        using IMidiInputPort port = MidiPortFactory.CreateVirtualInput("OwnAudioTest_VirtualIn");
        port.Open();

        Assert.IsTrue(port.IsOpen);
    }

    #endregion

    #region Virtual Output Tests

    /// <summary>
    /// On macOS or Linux, creates a virtual MIDI output port, verifies it reports IsOpen == true
    /// after opening, and disposes it without throwing.
    /// Skipped on Windows because WinMM does not support virtual ports.
    /// </summary>
    [TestMethod]
    public void CreateVirtualOutput_OnMacOrLinux_CreatesAndDisposes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Virtual MIDI output ports are not supported on Windows.");
            return;
        }

        using IMidiOutputPort port = MidiPortFactory.CreateVirtualOutput("OwnAudioTest_VirtualOut");
        port.Open();

        Assert.IsTrue(port.IsOpen);
    }

    #endregion

    #region Monitoring Tests

    /// <summary>
    /// Verifies that calling <see cref="MidiPortFactory.StartMonitoring"/> followed by
    /// <see cref="MidiPortFactory.StopMonitoring"/> does not throw on any supported platform.
    /// Windows monitoring is a documented no-op, so the test still passes there.
    /// </summary>
    [TestMethod]
    public void StartMonitoring_AndStop_DoesNotThrow()
    {
        MidiPortFactory.StartMonitoring();
        MidiPortFactory.StopMonitoring();
    }

    #endregion
}
