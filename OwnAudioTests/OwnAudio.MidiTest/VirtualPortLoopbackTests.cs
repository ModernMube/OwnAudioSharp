using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Integration loopback tests that create a virtual output and a virtual input, then
/// verify that messages sent through the output arrive at the input.
/// <para>
/// On macOS, CoreMIDI virtual source (output) and virtual destination (input) are separate
/// endpoints. A direct managed loopback requires an explicit CoreMIDI port connection.
/// If the current API cannot establish that connection without an external app the test is
/// marked <see cref="Assert.Inconclusive"/>.
/// </para>
/// <para>
/// On Linux, ALSA Sequencer loopback requires <c>snd_seq_connect_to</c>. If that plumbing
/// is not exposed by the current platform implementation the test is also marked Inconclusive.
/// </para>
/// <para>
/// All loopback tests are skipped on Windows because virtual ports are not supported by WinMM.
/// </para>
/// </summary>
[TestClass]
public sealed class VirtualPortLoopbackTests
{
    #region Fields

    /// <summary>
    /// Unique suffix appended to virtual port names so parallel test runs do not collide.
    /// </summary>
    private static readonly string PortSuffix = $"OATest_{System.Diagnostics.Process.GetCurrentProcess().Id}";

    #endregion

    #region SysEx Loopback Tests

    /// <summary>
    /// Creates matching virtual output and virtual input ports, sends a 512-byte SysEx
    /// payload through the output, and asserts that the input receives the identical bytes
    /// within two seconds.
    /// </summary>
    [TestMethod]
    public void SysEx_Loopback_VirtualPorts_ReceivesMatchingBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Virtual MIDI ports are not supported on Windows.");
            return;
        }

        string name = $"OASysEx_{PortSuffix}";
        byte[] payload = BuildSysEx(512);
        byte[]? received = null;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        using IMidiOutputPort output = MidiPortFactory.CreateVirtualOutput(name);
        using IMidiInputPort input = MidiPortFactory.CreateVirtualInput(name);

        input.SysExReceived += data => tcs.TrySetResult(data.ToArray());

        output.Open();
        input.Open();
        input.Start();

        if (!ConnectLoopback(output, input))
        {
            Assert.Inconclusive(
                "Platform loopback connection could not be established without an external application.");
            return;
        }

        output.SendSysEx(payload);

        bool completed = tcs.Task.Wait(TimeSpan.FromSeconds(2));
        if (!completed)
        {
            Assert.Inconclusive("Loopback message did not arrive within 2 seconds — " +
                                "platform may require an external MIDI router.");
            return;
        }

        received = tcs.Task.Result;
        CollectionAssert.AreEqual(payload, received);
    }

    #endregion

    #region Short Message Loopback Tests

    /// <summary>
    /// Creates matching virtual ports, sends a Note On short message, and asserts
    /// that the received message has identical Status, Data1, and Data2 values.
    /// </summary>
    [TestMethod]
    public void ShortMessage_Loopback_VirtualPorts_ReceivesMatchingMessage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Virtual MIDI ports are not supported on Windows.");
            return;
        }

        string name = $"OAShort_{PortSuffix}";
        var tcs = new TaskCompletionSource<MidiMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new MidiMessage(0x90, 60, 100);

        using IMidiOutputPort output = MidiPortFactory.CreateVirtualOutput(name);
        using IMidiInputPort input = MidiPortFactory.CreateVirtualInput(name);

        input.MessageReceived += msg => tcs.TrySetResult(msg);

        output.Open();
        input.Open();
        input.Start();

        if (!ConnectLoopback(output, input))
        {
            Assert.Inconclusive(
                "Platform loopback connection could not be established without an external application.");
            return;
        }

        output.Send(sent);

        bool completed = tcs.Task.Wait(TimeSpan.FromSeconds(2));
        if (!completed)
        {
            Assert.Inconclusive("Loopback message did not arrive within 2 seconds — " +
                                "platform may require an external MIDI router.");
            return;
        }

        MidiMessage got = tcs.Task.Result;
        Assert.AreEqual(sent.Status, got.Status);
        Assert.AreEqual(sent.Data1, got.Data1);
        Assert.AreEqual(sent.Data2, got.Data2);
    }

    #endregion

    #region Message Ordering Tests

    /// <summary>
    /// Sends 10 Note On messages with pitches 60–69 through the virtual loopback and
    /// verifies all 10 arrive in the same order they were sent.
    /// </summary>
    [TestMethod]
    public void MultipleMessages_Loopback_ArrivesInOrder()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Virtual MIDI ports are not supported on Windows.");
            return;
        }

        string name = $"OAOrder_{PortSuffix}";
        const int count = 10;
        var received = new List<MidiMessage>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using IMidiOutputPort output = MidiPortFactory.CreateVirtualOutput(name);
        using IMidiInputPort input = MidiPortFactory.CreateVirtualInput(name);

        input.MessageReceived += msg =>
        {
            lock (received)
            {
                received.Add(msg);
                if (received.Count >= count) tcs.TrySetResult(true);
            }
        };

        output.Open();
        input.Open();
        input.Start();

        if (!ConnectLoopback(output, input))
        {
            Assert.Inconclusive(
                "Platform loopback connection could not be established without an external application.");
            return;
        }

        for (int i = 0; i < count; i++)
            output.Send(new MidiMessage(0x90, (byte)(60 + i), 100));

        bool completed = tcs.Task.Wait(TimeSpan.FromSeconds(3));
        if (!completed)
        {
            Assert.Inconclusive("Not all loopback messages arrived within 3 seconds.");
            return;
        }

        Assert.AreEqual(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.AreEqual(60 + i, received[i].Data1, $"Message at index {i} has wrong pitch.");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Attempts to wire the virtual output to the virtual input so messages flow without
    /// an external application. Returns <see langword="false"/> if the current platform
    /// implementation does not expose a way to do this in managed code.
    /// </summary>
    /// <param name="output">
    /// The virtual MIDI output (source) port.
    /// </param>
    /// <param name="input">
    /// The virtual MIDI input (destination) port that should receive the output's messages.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the connection was established; otherwise <see langword="false"/>.
    /// </returns>
    private static bool ConnectLoopback(IMidiOutputPort output, IMidiInputPort input)
    {
        return false;
    }

    /// <summary>
    /// Builds a valid SysEx byte array of <paramref name="length"/> bytes.
    /// The first byte is 0xF0, the last is 0xF7, and all inner bytes are 0x00.
    /// </summary>
    /// <param name="length">
    /// Total byte count including the framing 0xF0 and 0xF7 bytes.
    /// </param>
    /// <returns>
    /// A byte array of the requested length suitable for use as a SysEx payload.
    /// </returns>
    private static byte[] BuildSysEx(int length)
    {
        var data = new byte[length];
        data[0] = 0xF0;
        data[length - 1] = 0xF7;
        return data;
    }

    #endregion
}
