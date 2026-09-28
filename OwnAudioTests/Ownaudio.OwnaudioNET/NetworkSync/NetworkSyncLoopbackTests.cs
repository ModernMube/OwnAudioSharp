using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OwnaudioNET.NetworkSync;
using OwnaudioNET.Synchronization;
using Xunit;

namespace Ownaudio.OwnaudioNET.Tests.NetworkSync;

/// <summary>
/// Server and client over real UDP on loopback, each driving a simulated player instead of a
/// mixer: different output latencies, a slow crystal on the client, a position that steps in
/// device blocks. What gets checked is what a listener would hear — the two heard positions.
/// </summary>
[Collection("NetworkSync")]
public sealed class NetworkSyncLoopbackTests : IDisposable
{
    private readonly SimPlayer _serverPlayer = new SimPlayer(ppm: 0, latency: 0.012);
    private readonly SimPlayer _clientPlayer = new SimPlayer(ppm: -150e-6, latency: 0.035);
    private readonly NetworkSyncServer _server;
    private NetworkSyncClient? _client;

    public NetworkSyncLoopbackTests()
    {
        _server = new NetworkSyncServer(_serverPlayer, new MasterClock(48000, 2), 0);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server.Dispose();
    }

    private async Task _connect(bool discovery = false)
    {
        await _server.StartAsync();

        if (discovery)
            _server.ExtraAnnounceTargets = [new IPEndPoint(IPAddress.Loopback, _server.BoundPort + NetworkSyncServer.DiscoveryPortOffset)];

        _client = new NetworkSyncClient(_clientPlayer, new MasterClock(48000, 2),
            discovery ? null : "127.0.0.1", _server.BoundPort, allowOfflinePlayback: true);
        await _client.StartAsync();
    }

    [Fact]
    public async Task Client_JoinsAPlayingServer_AndHearsTheSamePlace()
    {
        _serverPlayer.Follow(10.0, true);
        await _connect();

        _waitFor(() => _clientPlayer.IsPlaying, 3.0).Should().BeTrue("the client goes in when the server plays");

        Thread.Sleep(3000);
        double worst = _worstGap(1.5);

        worst.Should().BeLessThan(0.005, "both heard positions land on the same spot");
        _client!.ConnectionState.Should().Be(NetworkSyncProtocol.ConnectionState.Synced);
        _server.ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task Client_FindsTheServer_ByItsAnnouncement()
    {
        _serverPlayer.Follow(3.0, true);
        await _connect(discovery: true);

        _waitFor(() => _clientPlayer.IsPlaying, 5.0).Should().BeTrue();
        _client!.ConnectionState.Should().Be(NetworkSyncProtocol.ConnectionState.Synced);
    }

    [Fact]
    public async Task ServerPause_PausesTheClient_WhereTheServerStands()
    {
        _serverPlayer.Follow(20.0, true);
        await _connect();
        _waitFor(() => _clientPlayer.IsPlaying, 3.0).Should().BeTrue();
        Thread.Sleep(1000);

        _serverPlayer.Follow(42.0, false);

        _waitFor(() => !_clientPlayer.IsPlaying, 2.0).Should().BeTrue();
        _waitFor(() => Math.Abs(_clientPlayer.Stored - 42.0) < 0.001, 2.0).Should().BeTrue("a stop lands where the server stands");
    }

    [Fact]
    public async Task ServerJump_IsFollowed()
    {
        _serverPlayer.Follow(5.0, true);
        await _connect();
        _waitFor(() => _clientPlayer.IsPlaying, 3.0).Should().BeTrue();
        Thread.Sleep(2000);

        _serverPlayer.Follow(60.0, true);

        _waitFor(() => Math.Abs(_gap()) < 0.015, 2.0).Should().BeTrue("the jump reaches the client in one step");
        Thread.Sleep(5000);
        _worstGap(1.0).Should().BeLessThan(0.005, "what the landing missed the trim runs off");
    }

    [Fact]
    public async Task TwoClientsOnOneMachine_BothFollow()
    {
        _serverPlayer.Follow(8.0, true);
        await _connect();

        var secondPlayer = new SimPlayer(ppm: 80e-6, latency: 0.020);
        using var second = new NetworkSyncClient(secondPlayer, new MasterClock(48000, 2), "127.0.0.1", _server.BoundPort, true);
        await second.StartAsync();

        _waitFor(() => _clientPlayer.IsPlaying && secondPlayer.IsPlaying, 3.0).Should().BeTrue();
        Thread.Sleep(2000);

        _server.ClientCount.Should().Be(2, "one machine, two ports, two clients");
        double now = NetworkClock.Now;
        Math.Abs(_serverPlayer.Heard(now) - secondPlayer.Heard(now)).Should().BeLessThan(0.015);
    }

    [Fact]
    public async Task Commands_ArriveOnce()
    {
        await _connect();
        _waitFor(() => _client!.ConnectionState == NetworkSyncProtocol.ConnectionState.Synced, 3.0).Should().BeTrue();

        int received = 0;
        float tempo = 0;
        _client!.CommandReceived += (_, e) => { Interlocked.Increment(ref received); tempo = e.Command.TempoValue; };

        var cmd = NetworkSyncProtocol.CreateTempoCommand(0, 1.1f, false);
        _server.EnqueueCommand(ref cmd).Should().BeTrue();

        _waitFor(() => received > 0, 2.0).Should().BeTrue();
        Thread.Sleep(300);
        received.Should().Be(1, "the repeats are dropped");
        tempo.Should().Be(1.1f);
    }

    [Fact]
    public async Task ThrowingHandler_DoesNotStopTheClient()
    {
        await _connect();
        _waitFor(() => _client!.ConnectionState == NetworkSyncProtocol.ConnectionState.Synced, 3.0).Should().BeTrue();

        int received = 0;
        _client!.CommandReceived += (_, _) =>
        {
            Interlocked.Increment(ref received);
            throw new InvalidOperationException("app bug");
        };

        var first = NetworkSyncProtocol.CreateTempoCommand(0, 1.1f, false);
        _server.EnqueueCommand(ref first).Should().BeTrue();
        _waitFor(() => received == 1, 2.0).Should().BeTrue();

        var second = NetworkSyncProtocol.CreateTempoCommand(0, 1.2f, false);
        _server.EnqueueCommand(ref second).Should().BeTrue();

        _waitFor(() => received == 2, 2.0).Should().BeTrue("the network thread survived the first throw");
        _client.ConnectionState.Should().Be(NetworkSyncProtocol.ConnectionState.Synced);
    }

    [Fact]
    public async Task ServerInStartSilence_CountsAsPlaying()
    {
        _serverPlayer.SilentFor = 3.0;
        _serverPlayer.Follow(0.0, true);
        await _connect();

        _waitFor(() => _clientPlayer.IsPlaying, 2.5).Should().BeTrue("the song runs on the server even before its first sound");

        Thread.Sleep(3000);
        _worstGap(1.0).Should().BeLessThan(0.015);
    }

    [Fact]
    public async Task GoIn_ThatLeavesUsSilent_IsRetriedSlowly()
    {
        _clientPlayer.Refuses = true;
        _serverPlayer.Follow(10.0, true);
        await _connect();

        Thread.Sleep(4000);

        _clientPlayer.Starts.Should().BeInRange(1, 2, "after a failed go-in the retry waits seconds, not a settle");
    }

    [Fact]
    public async Task ServerGone_TakesTheTrimOff_AndKeepsPlaying()
    {
        _serverPlayer.Follow(10.0, true);
        await _connect();
        _waitFor(() => _clientPlayer.IsPlaying, 3.0).Should().BeTrue();

        _server.Stop();

        _waitFor(() => _client!.ConnectionState == NetworkSyncProtocol.ConnectionState.Disconnected, 4.0).Should().BeTrue();
        _waitFor(() => _clientPlayer.Trim == 1f, 1.0).Should().BeTrue("the next control tick lets go of the trim");
        _clientPlayer.IsPlaying.Should().BeTrue("offline playback is allowed");
    }

    /// <summary>
    /// Server heard minus client heard, right now.
    /// </summary>
    private double _gap()
    {
        double now = NetworkClock.Now;
        return _serverPlayer.Heard(now) - _clientPlayer.Heard(now);
    }

    private double _worstGap(double seconds)
    {
        double worst = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            worst = Math.Max(worst, Math.Abs(_gap()));
            Thread.Sleep(20);
        }
        return worst;
    }

    private static bool _waitFor(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>
    /// A player on its own crystal: runs at (1 + ppm) × trim, its reported position steps in
    /// 512-frame blocks like a device callback, and the speaker trails it by the latency.
    /// SilentFor mimics a start-offset silence, Refuses a player with nothing to start.
    /// </summary>
    private sealed class SimPlayer : ISyncTimeline
    {
        private const double Block = 512.0 / 48000.0;

        private readonly object _lock = new object();
        private readonly double _ppm;
        private bool _playing;
        private double _anchorPosition;
        private double _anchorAt;
        private float _trim = 1f;
        private int _starts;

        public double SilentFor { get; set; }

        public bool Refuses { get; set; }

        public int Starts => Volatile.Read(ref _starts);

        public SimPlayer(double ppm, double latency)
        {
            _ppm = ppm;
            OutputLatency = latency;
        }

        public double OutputLatency { get; }

        public bool IsPlaying { get { lock (_lock) return _playing; } }

        /// <summary>
        /// Where it was parked or started from.
        /// </summary>
        public double Stored { get { lock (_lock) return _anchorPosition; } }

        public float Trim
        {
            get { lock (_lock) return _trim; }
            set
            {
                lock (_lock)
                {
                    double now = NetworkClock.Now;
                    _anchorPosition = _at(now);
                    _anchorAt = now;
                    _trim = value;
                }
            }
        }

        public bool Read(out double position, out bool rendering)
        {
            lock (_lock)
            {
                double exact = _at(NetworkClock.Now);
                rendering = _playing && exact - _anchorPosition >= SilentFor;

                if (_playing && !rendering) position = _anchorPosition;
                else position = _playing ? _anchorPosition + Math.Floor((exact - _anchorPosition) / Block) * Block : exact;

                return _playing;
            }
        }

        public void Follow(double position, bool play)
        {
            if (play) Interlocked.Increment(ref _starts);

            lock (_lock)
            {
                _anchorPosition = position;
                _anchorAt = NetworkClock.Now;
                _playing = play && !Refuses;
            }
        }

        public double Heard(double now)
        {
            lock (_lock) return _at(now) - (_playing ? OutputLatency : 0.0);
        }

        private double _at(double now)
            => _playing ? _anchorPosition + (now - _anchorAt) * (1.0 + _ppm) * _trim : _anchorPosition;
    }
}
