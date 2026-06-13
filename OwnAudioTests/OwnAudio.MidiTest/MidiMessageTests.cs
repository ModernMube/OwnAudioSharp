using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Unit tests for the <see cref="MidiMessage"/> struct covering construction,
/// property derivation, and boolean predicate correctness.
/// All tests are pure in-memory with no hardware dependency.
/// </summary>
[TestClass]
public sealed class MidiMessageTests
{
    #region Constructor Tests

    /// <summary>
    /// Verifies that the Status byte passed to the constructor is stored without modification.
    /// </summary>
    [TestMethod]
    public void Constructor_WithAllBytes_StoresStatusCorrectly()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.AreEqual(0x90, msg.Status);
    }

    /// <summary>
    /// Verifies that Data1 is stored exactly as supplied.
    /// </summary>
    [TestMethod]
    public void Constructor_WithAllBytes_StoresData1Correctly()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.AreEqual(60, msg.Data1);
    }

    /// <summary>
    /// Verifies that Data2 is stored exactly as supplied.
    /// </summary>
    [TestMethod]
    public void Constructor_WithAllBytes_StoresData2Correctly()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.AreEqual(100, msg.Data2);
    }

    /// <summary>
    /// Verifies that when no timestamp argument is provided the field defaults to zero.
    /// </summary>
    [TestMethod]
    public void Constructor_WithoutTimestamp_DefaultsToZero()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.AreEqual(0L, msg.Timestamp);
    }

    /// <summary>
    /// Verifies that an explicit timestamp value is preserved.
    /// </summary>
    [TestMethod]
    public void Constructor_WithExplicitTimestamp_StoresTimestamp()
    {
        var msg = new MidiMessage(0x90, 60, 100, timestamp: 123456789L);

        Assert.AreEqual(123456789L, msg.Timestamp);
    }

    #endregion

    #region Type Property Tests

    /// <summary>
    /// Verifies that status 0x90 maps to <see cref="MidiMessageType.NoteOn"/>.
    /// </summary>
    [TestMethod]
    public void Type_NoteOnStatus_ReturnsNoteOn()
    {
        var msg = new MidiMessage(0x93, 60, 100);

        Assert.AreEqual(MidiMessageType.NoteOn, msg.Type);
    }

    /// <summary>
    /// Verifies that status 0x80 maps to <see cref="MidiMessageType.NoteOff"/>.
    /// </summary>
    [TestMethod]
    public void Type_NoteOffStatus_ReturnsNoteOff()
    {
        var msg = new MidiMessage(0x80, 60, 0);

        Assert.AreEqual(MidiMessageType.NoteOff, msg.Type);
    }

    /// <summary>
    /// Verifies that status 0xB0 maps to <see cref="MidiMessageType.ControlChange"/>.
    /// </summary>
    [TestMethod]
    public void Type_ControlChangeStatus_ReturnsControlChange()
    {
        var msg = new MidiMessage(0xB0, 7, 100);

        Assert.AreEqual(MidiMessageType.ControlChange, msg.Type);
    }

    /// <summary>
    /// Verifies that status 0xC0 maps to <see cref="MidiMessageType.ProgramChange"/>.
    /// </summary>
    [TestMethod]
    public void Type_ProgramChangeStatus_ReturnsProgramChange()
    {
        var msg = new MidiMessage(0xC0, 42, 0);

        Assert.AreEqual(MidiMessageType.ProgramChange, msg.Type);
    }

    /// <summary>
    /// Verifies that status 0xE0 maps to <see cref="MidiMessageType.PitchBend"/>.
    /// </summary>
    [TestMethod]
    public void Type_PitchBendStatus_ReturnsPitchBend()
    {
        var msg = new MidiMessage(0xE0, 0, 64);

        Assert.AreEqual(MidiMessageType.PitchBend, msg.Type);
    }

    /// <summary>
    /// Verifies that real-time status 0xF8 yields <see cref="MidiMessageType.SysEx"/>
    /// because the upper nibble 0xF0 maps to that enum value.
    /// </summary>
    [TestMethod]
    public void Type_TimingClockStatus_ReturnsSysExUpperNibble()
    {
        var msg = new MidiMessage(0xF8, 0, 0);

        Assert.AreEqual(MidiMessageType.SysEx, msg.Type);
    }

    #endregion

    #region Channel Property Tests

    /// <summary>
    /// Verifies that the channel is extracted as the lower nibble (channel 0).
    /// </summary>
    [TestMethod]
    public void Channel_StatusOnChannel0_ReturnsZero()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.AreEqual(0, msg.Channel);
    }

    /// <summary>
    /// Verifies that the channel is extracted correctly for channel 15.
    /// </summary>
    [TestMethod]
    public void Channel_StatusOnChannel15_ReturnsFifteen()
    {
        var msg = new MidiMessage(0x9F, 60, 100);

        Assert.AreEqual(15, msg.Channel);
    }

    /// <summary>
    /// Verifies an intermediate channel value is derived from the lower nibble.
    /// </summary>
    [TestMethod]
    public void Channel_StatusOnChannel3_ReturnsThree()
    {
        var msg = new MidiMessage(0x93, 60, 100);

        Assert.AreEqual(3, msg.Channel);
    }

    #endregion

    #region IsNoteOn Tests

    /// <summary>
    /// Verifies that a 0x9n message with velocity > 0 is considered a Note On.
    /// </summary>
    [TestMethod]
    public void IsNoteOn_NoteOnStatusWithVelocity_ReturnsTrue()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.IsTrue(msg.IsNoteOn);
    }

    /// <summary>
    /// Verifies that a 0x9n message with velocity == 0 is NOT a Note On (it is a Note Off).
    /// </summary>
    [TestMethod]
    public void IsNoteOn_NoteOnStatusWithZeroVelocity_ReturnsFalse()
    {
        var msg = new MidiMessage(0x90, 60, 0);

        Assert.IsFalse(msg.IsNoteOn);
    }

    /// <summary>
    /// Verifies that a 0x8n status byte is not treated as a Note On.
    /// </summary>
    [TestMethod]
    public void IsNoteOn_NoteOffStatus_ReturnsFalse()
    {
        var msg = new MidiMessage(0x80, 60, 100);

        Assert.IsFalse(msg.IsNoteOn);
    }

    #endregion

    #region IsNoteOff Tests

    /// <summary>
    /// Verifies that a 0x8n status byte is a Note Off.
    /// </summary>
    [TestMethod]
    public void IsNoteOff_NoteOffStatus_ReturnsTrue()
    {
        var msg = new MidiMessage(0x80, 60, 0);

        Assert.IsTrue(msg.IsNoteOff);
    }

    /// <summary>
    /// Verifies that a Note On with velocity zero is treated as a Note Off.
    /// </summary>
    [TestMethod]
    public void IsNoteOff_NoteOnWithZeroVelocity_ReturnsTrue()
    {
        var msg = new MidiMessage(0x90, 60, 0);

        Assert.IsTrue(msg.IsNoteOff);
    }

    /// <summary>
    /// Verifies that a Note On with non-zero velocity is not a Note Off.
    /// </summary>
    [TestMethod]
    public void IsNoteOff_NoteOnWithVelocity_ReturnsFalse()
    {
        var msg = new MidiMessage(0x90, 60, 127);

        Assert.IsFalse(msg.IsNoteOff);
    }

    #endregion

    #region IsControlChange Tests

    /// <summary>
    /// Verifies that a 0xBn message is identified as a Control Change.
    /// </summary>
    [TestMethod]
    public void IsControlChange_ControlChangeStatus_ReturnsTrue()
    {
        var msg = new MidiMessage(0xB0, 7, 100);

        Assert.IsTrue(msg.IsControlChange);
    }

    /// <summary>
    /// Verifies that a Note On is not a Control Change.
    /// </summary>
    [TestMethod]
    public void IsControlChange_NoteOnStatus_ReturnsFalse()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.IsFalse(msg.IsControlChange);
    }

    #endregion

    #region IsPitchBend Tests

    /// <summary>
    /// Verifies that a 0xEn message is identified as Pitch Bend.
    /// </summary>
    [TestMethod]
    public void IsPitchBend_PitchBendStatus_ReturnsTrue()
    {
        var msg = new MidiMessage(0xE0, 0, 64);

        Assert.IsTrue(msg.IsPitchBend);
    }

    /// <summary>
    /// Verifies that a Control Change is not a Pitch Bend.
    /// </summary>
    [TestMethod]
    public void IsPitchBend_ControlChangeStatus_ReturnsFalse()
    {
        var msg = new MidiMessage(0xB0, 1, 64);

        Assert.IsFalse(msg.IsPitchBend);
    }

    #endregion

    #region IsProgramChange Tests

    /// <summary>
    /// Verifies that a 0xCn message is identified as a Program Change.
    /// </summary>
    [TestMethod]
    public void IsProgramChange_ProgramChangeStatus_ReturnsTrue()
    {
        var msg = new MidiMessage(0xC0, 42, 0);

        Assert.IsTrue(msg.IsProgramChange);
    }

    /// <summary>
    /// Verifies that a Note On is not a Program Change.
    /// </summary>
    [TestMethod]
    public void IsProgramChange_NoteOnStatus_ReturnsFalse()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        Assert.IsFalse(msg.IsProgramChange);
    }

    #endregion

    #region ToString Tests

    /// <summary>
    /// Verifies that ToString returns a non-empty string.
    /// </summary>
    [TestMethod]
    public void ToString_AnyMessage_ReturnsNonEmptyString()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        string result = msg.ToString();

        Assert.IsFalse(string.IsNullOrEmpty(result));
    }

    /// <summary>
    /// Verifies that ToString includes the channel number.
    /// </summary>
    [TestMethod]
    public void ToString_Channel3_ContainsChannelValue()
    {
        var msg = new MidiMessage(0x93, 60, 100);

        string result = msg.ToString();

        StringAssert.Contains(result, "3");
    }

    /// <summary>
    /// Verifies that ToString includes the message type name.
    /// </summary>
    [TestMethod]
    public void ToString_NoteOnMessage_ContainsTypeInfo()
    {
        var msg = new MidiMessage(0x90, 60, 100);

        string result = msg.ToString();

        StringAssert.Contains(result, "NoteOn");
    }

    #endregion
}
