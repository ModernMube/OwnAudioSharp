using System;
using FluentAssertions;
using OwnaudioNET.NetworkSync;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.NetworkSync;

/// <summary>
/// The pieces the following rests on, without a socket: the clock offset, the line through a
/// stepping position, the controller that turns a gap into a trim, and the two wire formats.
/// </summary>
public sealed class SyncCoreTests
{
    [Fact]
    public void Offset_SymmetricTrips_AreExact()
    {
        var clock = new ClockOffsetEstimator();

        for (int i = 0; i < 10; i++)
        {
            double t0 = i * 0.5;
            clock.Add(t0, t0 + 0.002 + 100.0, t0 + 0.0021 + 100.0, t0 + 0.0041);
        }

        clock.Offset.Should().BeApproximately(100.0, 1e-9);
        clock.RoundTrip.Should().BeApproximately(0.004, 1e-9);
    }

    /// <summary>
    /// Wifi: most trips wait in a queue on the way back, which drags a plain average off by
    /// half the wait. The fastest trips don't carry that.
    /// </summary>
    [Fact]
    public void Offset_QueuedTrips_DoNotPullTheOffset()
    {
        var clock = new ClockOffsetEstimator();
        var rnd = new Random(7);

        for (int i = 0; i < ClockOffsetEstimator.Window; i++)
        {
            double t0 = i * 0.5;
            double back = i % 5 == 0 ? 0.001 : 0.001 + rnd.NextDouble() * 0.040;
            clock.Add(t0, t0 + 0.001 + 50.0, t0 + 0.001 + 50.0, t0 + 0.002 + back - 0.001);
        }

        clock.Offset.Should().BeApproximately(50.0, 0.0015);
        clock.Jitter.Should().BeGreaterThan(0.005);
    }

    [Fact]
    public void Offset_Add_DoesNotAllocate()
    {
        var clock = new ClockOffsetEstimator();
        double t = 0;
        _allocates(() => { clock.Add(t, t + 1, t + 1, t + 0.001); t += 0.5; }).Should().Be(0);
    }

    /// <summary>
    /// The position steps every 15 ms; read at 20 Hz a reading is up to a step late. The line
    /// through a second and a half of them lands within a couple of ms of the step-averaged truth.
    /// </summary>
    [Fact]
    public void Fit_SteppingClock_PredictsSmoothly()
    {
        var fit = new PositionFit();
        var rnd = new Random(11);
        const double step = 0.015;
        double worst = 0;

        for (int i = 0; i < 200; i++)
        {
            double t = 10.0 + i * 0.05 + rnd.NextDouble() * step;
            double stepped = Math.Floor(t / step) * step;
            fit.Add(t, stepped, 1.0);

            if (i < 30) continue;

            fit.TryPredict(t + 0.02, out double p).Should().BeTrue();
            double truth = t + 0.02 - step / 2;
            worst = Math.Max(worst, Math.Abs(p - truth));
        }

        worst.Should().BeLessThan(0.003);
    }

    [Fact]
    public void Fit_OneStrayReading_IsLeftOut()
    {
        var fit = new PositionFit();
        for (int i = 0; i < 25; i++) fit.Add(i * 0.05, i * 0.05, 1.0);
        fit.Add(25 * 0.05, 25 * 0.05 + 0.2, 1.0);

        fit.TryPredict(25 * 0.05, out double p).Should().BeTrue();
        p.Should().BeApproximately(25 * 0.05, 0.001);
    }

    [Fact]
    public void Fit_NewRate_StartsOver()
    {
        var fit = new PositionFit();
        for (int i = 0; i < 20; i++) fit.Add(i * 0.05, i * 0.05, 1.0);

        fit.Add(1.0, 1.0, 0.9);

        fit.Count.Should().Be(1);
        fit.TryPredict(2.0, out double p).Should().BeTrue();
        p.Should().BeApproximately(1.9, 1e-9);
    }

    [Fact]
    public void Fit_Stopped_HoldsStill()
    {
        var fit = new PositionFit();
        fit.Add(5.0, 42.0, 0.0);

        fit.TryPredict(60.0, out double p).Should().BeTrue();
        p.Should().Be(42.0);
    }

    [Fact]
    public void Fit_Add_DoesNotAllocate()
    {
        var fit = new PositionFit();
        double t = 0;
        _allocates(() => { fit.Add(t, t, 1.0); fit.TryPredict(t, out _); t += 0.05; }).Should().Be(0);
    }

    [Fact]
    public void Controller_InsideDeadband_DoesNothing()
    {
        var sync = new SyncController();

        sync.Evaluate(1.0, 0.002).Kind.Should().Be(SyncActionKind.None);
        sync.Trim.Should().Be(1.0);
    }

    [Fact]
    public void Controller_Lagging_SpeedsUp_Leading_SlowsDown()
    {
        var lag = new SyncController().Evaluate(1.0, 0.010);
        var lead = new SyncController().Evaluate(1.0, -0.010);

        lag.Kind.Should().Be(SyncActionKind.Trim);
        lag.Trim.Should().BeApproximately(1.0 + 0.010 / SyncController.CatchUpSeconds, 1e-12);
        lead.Trim.Should().BeApproximately(1.0 - 0.010 / SyncController.CatchUpSeconds, 1e-12);
    }

    [Fact]
    public void Controller_TrimHasACeiling()
    {
        new SyncController().Evaluate(1.0, 0.070).Trim.Should().BeApproximately(1.0 + SyncController.MaxTrim, 1e-12);
    }

    [Fact]
    public void Controller_Seek_NeedsTwoBigReadings_ThenSettles()
    {
        var sync = new SyncController();

        sync.Evaluate(1.0, 0.300).Kind.Should().Be(SyncActionKind.None);
        sync.Evaluate(1.05, 0.300).Kind.Should().Be(SyncActionKind.Seek);
        sync.Trim.Should().Be(1.0);

        sync.Evaluate(1.5, 0.300).Kind.Should().Be(SyncActionKind.None, "a fresh jump is still settling");
        sync.Evaluate(2.6, 0.300).Kind.Should().Be(SyncActionKind.None, "first big one after settling");
        sync.Evaluate(2.65, 0.300).Kind.Should().Be(SyncActionKind.Seek);
    }

    [Fact]
    public void Controller_OneStray_DoesNotSeek()
    {
        var sync = new SyncController();

        sync.Evaluate(1.0, 0.300);
        sync.Evaluate(1.6, 0.004).Kind.Should().NotBe(SyncActionKind.Seek);
    }

    [Fact]
    public void Controller_SmallChanges_Wait()
    {
        var sync = new SyncController();

        sync.Evaluate(1.0, 0.010).Kind.Should().Be(SyncActionKind.Trim);
        sync.Evaluate(1.2, 0.020).Kind.Should().Be(SyncActionKind.None, "too soon after the last one");
        sync.Evaluate(2.0, 0.0105).Kind.Should().Be(SyncActionKind.None, "too small a step");
        sync.Evaluate(2.5, 0.001).Kind.Should().Be(SyncActionKind.Trim, "back to 1.0 always goes");
        sync.Trim.Should().Be(1.0);
    }

    /// <summary>
    /// The whole loop: our crystal runs 100 ppm slow, we start 40 ms behind, every reading has
    /// a couple of ms of noise. It has to catch up without a jump and then stay put.
    /// </summary>
    [Fact]
    public void Controller_ClosedLoop_CatchesUpAndHolds()
    {
        var sync = new SyncController();
        var rnd = new Random(3);

        double server = 0, us = -0.040, trim = 1.0;
        const double dt = 0.05, ppm = -100e-6;
        int seeks = 0;
        double worstLate = 0;

        for (int i = 0; i < 20 * 120; i++)
        {
            double now = i * dt;
            server += dt;
            us += dt * (1 + ppm) * trim;

            double noise = (rnd.NextDouble() - 0.5) * 0.002;
            var action = sync.Evaluate(now, server - us + noise);

            if (action.Kind == SyncActionKind.Seek) seeks++;
            if (action.Kind == SyncActionKind.Trim) trim = action.Trim;

            if (now > 30) worstLate = Math.Max(worstLate, Math.Abs(server - us));
        }

        seeks.Should().Be(0);
        worstLate.Should().BeLessThan(0.005);
    }

    [Fact]
    public void Stats_AMinuteBecomesOneLine_AndStartsOver()
    {
        var stats = new SyncStats();
        stats.Due(100.0).Should().BeFalse();

        stats.Add(100.0, 0.002);
        stats.Add(130.0, -0.008);
        stats.Trimmed();
        stats.Jumped();
        stats.Due(159.0).Should().BeFalse();
        stats.Due(160.0).Should().BeTrue();

        string line = stats.Take(160.0, 0.0021, 0.0014);

        line.Should().Contain("60s").And.Contain("avg 5.0 ms").And.Contain("max 8.0 ms")
            .And.Contain("50% within 5 ms").And.Contain("trims 1, seeks 1");
        stats.Readings.Should().Be(0);
        stats.Due(500.0).Should().BeFalse();
    }

    [Fact]
    public void Packet_RoundTrips()
    {
        var packet = new SyncPacket
        {
            Kind = SyncPacketKind.Position, Session = 77, Sequence = 5, Epoch = 3, Playing = true,
            ClientSent = 1.5, ServerReceived = 2.5, ServerSent = 3.25, Position = 61.125, Rate = 0.95, Port = 9876,
        };

        var bytes = new byte[SyncPacket.Size];
        packet.Write(bytes);

        SyncPacket.TryRead(bytes, out var back).Should().BeTrue();
        back.Should().Be(packet);
    }

    [Fact]
    public void Packet_Foreign_IsRejected()
    {
        var bytes = new byte[SyncPacket.Size];
        new SyncPacket { Kind = SyncPacketKind.Ping }.Write(bytes);

        bytes[0] ^= 0xFF;
        SyncPacket.TryRead(bytes, out _).Should().BeFalse();
        SyncPacket.TryRead(new byte[10], out _).Should().BeFalse();
    }

    [Fact]
    public void Packet_WriteRead_DoesNotAllocate()
    {
        var bytes = new byte[SyncPacket.Size];
        var packet = new SyncPacket { Kind = SyncPacketKind.Pong, Position = 1 };
        _allocates(() => { packet.Write(bytes); SyncPacket.TryRead(bytes, out _); }).Should().Be(0);
    }

    /// <summary>
    /// The command goes out exactly as long as it is and has to read back from that — a reader
    /// wanting the whole 256-byte buffer threw every real packet away.
    /// </summary>
    [Fact]
    public void Command_ReadsBackFromItsOwnLength()
    {
        var cmd = NetworkSyncProtocol.CreateTempoCommand(123, 1.05f, true);
        cmd.SequenceNumber = 9;

        var buffer = new byte[NetworkSyncProtocol.MaxPacketSize];
        int n = NetworkSyncProtocol.SerializeCommand(ref cmd, buffer);

        n.Should().Be(NetworkSyncProtocol.CommandSize);

        NetworkSyncProtocol.Command back = default;
        NetworkSyncProtocol.DeserializeCommand(buffer.AsSpan(0, n), ref back).Should().BeTrue();
        back.Should().Be(cmd);
    }

    [Fact]
    public void Command_And_SyncPacket_DoNotMistakeEachOther()
    {
        var cmd = NetworkSyncProtocol.CreateStopCommand(1);
        var buffer = new byte[NetworkSyncProtocol.MaxPacketSize];
        int n = NetworkSyncProtocol.SerializeCommand(ref cmd, buffer);
        SyncPacket.TryRead(buffer.AsSpan(0, n), out _).Should().BeFalse();

        var bytes = new byte[SyncPacket.Size];
        new SyncPacket { Kind = SyncPacketKind.Position }.Write(bytes);
        NetworkSyncProtocol.Command other = default;
        NetworkSyncProtocol.DeserializeCommand(bytes, ref other).Should().BeFalse();
    }

    /// <summary>
    /// Bytes the action allocated over a thousand runs, after a warm-up.
    /// </summary>
    private static long _allocates(Action action)
    {
        for (int i = 0; i < 100; i++) action();

        long _before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - _before;
    }
}
