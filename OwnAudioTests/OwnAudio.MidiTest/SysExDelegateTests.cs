using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OwnAudio.Midi.IO;

namespace OwnAudio.MidiTest;

/// <summary>
/// Unit tests for the <see cref="SysExReceivedHandler"/> delegate.
/// Verifies invocation semantics, span lifetime, multicast delivery,
/// and null-safe invocation — all without hardware dependencies.
/// </summary>
[TestClass]
public sealed class SysExDelegateTests
{
    #region Invocation Tests

    /// <summary>
    /// Verifies that a handler created from a lambda is invoked and receives the correct span data.
    /// </summary>
    [TestMethod]
    public void Invocation_LambdaHandler_ReceivesCorrectBytes()
    {
        byte[]? received = null;
        SysExReceivedHandler handler = data => received = data.ToArray();
        byte[] source = { 0xF0, 0x41, 0x10, 0xF7 };

        handler(source);

        CollectionAssert.AreEqual(source, received);
    }

    /// <summary>
    /// Verifies that data copied inside the callback equals the source bytes,
    /// confirming the stack-allocated span pattern is safe when copied immediately.
    /// </summary>
    [TestMethod]
    public void SpanLifetime_CopiedInsideCallback_EqualsSource()
    {
        byte[] copy = Array.Empty<byte>();
        SysExReceivedHandler handler = data => copy = data.ToArray();
        ReadOnlySpan<byte> span = stackalloc byte[] { 0xF0, 0x7E, 0x7F, 0xF7 };

        handler(span);

        Assert.AreEqual(4, copy.Length);
        Assert.AreEqual(0xF0, copy[0]);
        Assert.AreEqual(0xF7, copy[3]);
    }

    #endregion

    #region MulticastDelegate Tests

    /// <summary>
    /// Verifies that two handlers subscribed to the same event both receive the same bytes
    /// when the event is raised.
    /// </summary>
    [TestMethod]
    public void MulticastDelegate_TwoHandlers_BothReceiveSameBytes()
    {
        byte[]? firstReceived = null;
        byte[]? secondReceived = null;

        SysExReceivedHandler? evt = null;
        evt += data => firstReceived = data.ToArray();
        evt += data => secondReceived = data.ToArray();

        byte[] source = { 0xF0, 0x00, 0x21, 0x09, 0xF7 };
        evt!.Invoke(source);

        CollectionAssert.AreEqual(source, firstReceived);
        CollectionAssert.AreEqual(source, secondReceived);
    }

    #endregion

    #region NullSafe Tests

    /// <summary>
    /// Verifies that a nullable delegate invoked with the null-conditional operator
    /// does not throw when the delegate is null.
    /// </summary>
    [TestMethod]
    public void NullSafe_NullDelegate_DoesNotThrow()
    {
        SysExReceivedHandler? handler = null;
        byte[] data = { 0xF0, 0xF7 };

        handler?.Invoke(data);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Helper that raises a fake SysEx event carrying the given bytes,
    /// using the nullable conditional invocation pattern.
    /// </summary>
    /// <param name="subscribers">
    /// The current event delegate chain (may be null).
    /// </param>
    /// <param name="payload">
    /// The SysEx bytes to deliver to each subscriber.
    /// </param>
    private static void RaiseSysEx(SysExReceivedHandler? subscribers, ReadOnlySpan<byte> payload)
    {
        subscribers?.Invoke(payload);
    }

    #endregion
}
