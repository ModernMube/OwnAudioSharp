using System.Net;
using System.Net.Sockets;
using Logger;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Client side of the sync. The network never writes our clock: the mixer runs on its own like
/// a standalone player and only gets a small tempo trim and, rarely, one seek to stay on the
/// server's heard song position. It also follows the server's transport — starts where the
/// server is, pauses where it stopped. Losing the server is just a trim of 1.0, nothing jumps.
/// </summary>
public sealed class NetworkSyncClient : IDisposable
{
    internal const double PingIntervalSeconds = 0.5;
    internal const double LostAfterSeconds = 1.5;
    internal const int LostAfterMissedPongs = 4;

    /// <summary>
    /// A server jump that lands us closer than this is left to the trim.
    /// </summary>
    private const double ReseekAboveSeconds = 0.030;

    /// <summary>
    /// A go-in that left us silent (nothing loaded, all past the end) is retried this slowly.
    /// </summary>
    private const double GoInRetrySeconds = 5.0;

    private const int ControlTickMs = 50;
    private const int ReceiveTimeoutMs = 250;
    private const int ErrorReportInterval = 100;

    private readonly MasterClock _masterClock;
    private readonly string? _serverAddress;
    private readonly int _port;
    private readonly bool _allowOfflinePlayback;
    private ISyncTimeline? _timeline;

    private Socket? _socket;
    private Socket? _discovery;
    private Thread? _networkThread;
    private Thread? _controlThread;
    private volatile bool _isRunning;
    private bool _disposed;
    private volatile NetworkSyncProtocol.ConnectionState _connectionState = NetworkSyncProtocol.ConnectionState.Disconnected;

    private SocketAddress? _server;
    private uint _session;
    private int _lastCommand;
    private bool _haveCommand;

    private readonly object _gate = new object();
    private readonly ClockOffsetEstimator _clock = new ClockOffsetEstimator();
    private readonly PositionFit _fit = new PositionFit();
    private uint _epoch;
    private bool _haveEpoch;
    private bool _serverPlaying;
    private bool _lost;

    private readonly SyncController _controller = new SyncController();
    private readonly PositionFit _ownFit = new PositionFit();
    private readonly SyncStats _stats = new SyncStats();
    private uint _followedEpoch;
    private double _startLead;
    private double _landingAt = double.NaN;
    private double _nextGoInAt;
    private bool _wentIn;
    private bool _pausedForLoss;
    private int _controlErrors;
    private double _lastError;

    /// <summary>
    /// Current spot in the connect/sync lifecycle.
    /// </summary>
    public NetworkSyncProtocol.ConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// One-way network delay in seconds, half the fastest recent round trip.
    /// </summary>
    public double AverageLatency
    {
        get { lock (_gate) return _clock.RoundTrip * 0.5; }
    }

    /// <summary>
    /// Server minus us at the last look, seconds. Positive = we lag.
    /// </summary>
    internal double LastError => _lastError;

    internal double Jitter
    {
        get { lock (_gate) return _clock.Jitter; }
    }

    /// <summary>
    /// True while the threads are up.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Local transport is ours only while no server leads.
    /// </summary>
    public bool IsLocalControlAllowed => _connectionState == NetworkSyncProtocol.ConnectionState.Disconnected;

    /// <summary>
    /// The connection state flipped. From a sync thread.
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// A command the server's app sent (EnqueueCommand), once per command. From the network
    /// thread. Play/Pause/Seek are followed on their own already, this is for the app's extras.
    /// </summary>
    public event EventHandler<CommandReceivedEventArgs>? CommandReceived;

    /// <summary>
    /// Client for the mixer that owns this clock (the registered one). Null server address
    /// finds the server by its announcements.
    /// </summary>
    /// <param name="masterClock"></param>
    /// <param name="serverAddress">host or IP, null for auto-discovery</param>
    /// <param name="port">the server's UDP port</param>
    /// <param name="allowOfflinePlayback">keep playing when the server is lost, otherwise pause</param>
    public NetworkSyncClient(
        MasterClock masterClock,
        string? serverAddress = null,
        int port = 9876,
        bool allowOfflinePlayback = true)
    {
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
        _serverAddress = serverAddress;
        _port = port;
        _allowOfflinePlayback = allowOfflinePlayback;
    }

    internal NetworkSyncClient(AudioMixer mixer, string? serverAddress, int port, bool allowOfflinePlayback)
        : this(mixer.MasterClock, serverAddress, port, allowOfflinePlayback)
    {
        _timeline = new MixerSyncTimeline(mixer);
    }

    internal NetworkSyncClient(ISyncTimeline timeline, MasterClock clock, string? serverAddress, int port, bool allowOfflinePlayback)
        : this(clock, serverAddress, port, allowOfflinePlayback)
    {
        _timeline = timeline;
    }

    /// <summary>
    /// Resolves the server (or starts looking for one) and spins up the threads.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
            return;

        _timeline ??= _timelineOf(_masterClock);

        try
        {
            if (!string.IsNullOrEmpty(_serverAddress))
            {
                IPAddress[] _addresses = await Dns.GetHostAddressesAsync(_serverAddress);
                IPAddress _ip = Array.Find(_addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? throw new InvalidOperationException($"'{_serverAddress}' has no IPv4 address");

                _server = new IPEndPoint(_ip, _port).Serialize();
            }

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            _socket.ReceiveTimeout = ReceiveTimeoutMs;
        }
        catch (Exception ex)
        {
            Log.Error($"[SyncClient] Cannot start against '{_serverAddress ?? "(discovery)"}' on UDP {_port}", ex);
            _socket?.Close();
            _socket = null;
            throw new InvalidOperationException($"Failed to start network sync client: {ex.Message}", ex);
        }

        _isRunning = true;
        _setState(NetworkSyncProtocol.ConnectionState.Connecting);

        _networkThread = new Thread(_networkLoop) { Name = "NetworkSyncClient.Network", IsBackground = true };
        _controlThread = new Thread(_controlLoop) { Name = "NetworkSyncClient.Follow", IsBackground = true };
        _networkThread.Start();
        _controlThread.Start();

        Log.Info($"[SyncClient] Started, server {(_server is null ? "by discovery" : _endpointOf(_server).ToString())}");
    }

    private static ISyncTimeline _timelineOf(MasterClock clock)
    {
        AudioMixer? _mixer = OwnaudioNet.GetRegisteredAudioMixer();
        if (_mixer is null || !ReferenceEquals(_mixer.MasterClock, clock))
            throw new InvalidOperationException("Network sync needs the AudioMixer that owns this MasterClock to be the registered one (OwnaudioNet.SetPrimaryAudioMixer).");

        return new MixerSyncTimeline(_mixer);
    }

    /// <summary>
    /// Stops the threads, takes the trim off and closes the sockets.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        _networkThread?.Join(1000);
        _controlThread?.Join(1000);
        _socket?.Close();
        _socket = null;
        _discovery?.Close();
        _discovery = null;

        if (_timeline is { } _t) _t.Trim = 1f;
        _setState(NetworkSyncProtocol.ConnectionState.Disconnected);
    }

    #region Network thread

    private void _networkLoop()
    {
        byte[] _in  = new byte[512];
        byte[] _out = new byte[SyncPacket.Size];
        var _from = new SocketAddress(AddressFamily.InterNetwork);
        Socket _sock = _socket!;

        double _lastHeard = NetworkClock.Now;
        double _nextPing = 0;
        int _unanswered = 0;
        uint _pingSequence = 0;

        while (_isRunning)
        {
            if (_server is null)
            {
                _lookForServer(_in, _from);
                _lastHeard = NetworkClock.Now;
                _unanswered = 0;
                continue;
            }

            double _now = NetworkClock.Now;

            if (_now >= _nextPing)
            {
                var _ping = new SyncPacket
                {
                    Kind       = SyncPacketKind.Ping,
                    Session    = _session,
                    Sequence   = ++_pingSequence,
                    ClientSent = NetworkClock.Now,
                };
                _ping.Write(_out);

                try { _sock.SendTo(_out, SocketFlags.None, _server); }
                catch (SocketException) { }

                _unanswered++;
                _nextPing = _now + PingIntervalSeconds;
            }

            if (_now - _lastHeard > LostAfterSeconds || _unanswered > LostAfterMissedPongs)
            {
                if (_loseServer()) continue;
            }

            int _n;
            try { _n = _sock.ReceiveFrom(_in, SocketFlags.None, _from); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset) { continue; }
            catch (SocketException ex) when (_isRunning)
            {
                Log.Warning($"[SyncClient] Receive failed ({ex.SocketErrorCode}), carrying on");
                Thread.Sleep(ReceiveTimeoutMs);
                continue;
            }
            catch (Exception) { return; }

            double _received = NetworkClock.Now;
            if (!_from.Equals(_server)) continue;

            if (!SyncPacket.TryRead(_in.AsSpan(0, _n), out var _packet))
            {
                _takeCommand(_in.AsSpan(0, _n));
                continue;
            }

            if (_packet.Kind == SyncPacketKind.Pong && _session == 0) _adopt(_packet.Session);
            if (_packet.Session != _session) continue;

            _lastHeard = _received;

            if (_packet.Kind == SyncPacketKind.Pong)
            {
                _unanswered = 0;
                lock (_gate)
                {
                    _clock.Add(_packet.ClientSent, _packet.ServerReceived, _packet.ServerSent, _received);
                    _lost = false;
                }
            }
            else if (_packet.Kind == SyncPacketKind.Position)
            {
                lock (_gate) _takePosition(in _packet);
            }

            _refreshState();
        }
    }

    /// <summary>
    /// Discovery: listens on the announcement port until a server says hello, then pings that one.
    /// </summary>
    private void _lookForServer(byte[] buffer, SocketAddress from)
    {
        if (_discovery is null)
        {
            try
            {
                _discovery = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _discovery.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _discovery.Bind(new IPEndPoint(IPAddress.Any, _port + NetworkSyncServer.DiscoveryPortOffset));
                _discovery.ReceiveTimeout = ReceiveTimeoutMs;
            }
            catch (SocketException ex)
            {
                Log.Error($"[SyncClient] Cannot listen for servers on UDP {_port + NetworkSyncServer.DiscoveryPortOffset}", ex);
                _discovery?.Close();
                _discovery = null;
                Thread.Sleep(1000);
                return;
            }
        }

        int _n;
        try { _n = _discovery.ReceiveFrom(buffer, SocketFlags.None, from); }
        catch (SocketException) { return; }
        catch (ObjectDisposedException) { return; }

        if (!SyncPacket.TryRead(buffer.AsSpan(0, _n), out var _hello) || _hello.Kind != SyncPacketKind.Announce) return;

        IPEndPoint _found = new IPEndPoint(_endpointOf(from).Address, _hello.Port);
        _server = _found.Serialize();
        _adopt(_hello.Session);

        _discovery.Close();
        _discovery = null;

        Log.Info($"[SyncClient] Found server {_found}");
    }

    /// <summary>
    /// The server went quiet. The next pong or announcement may come from a restarted one, so
    /// the session goes and so does everything measured against it. A discovered server is
    /// forgotten, we look again. True when that happened.
    /// </summary>
    private bool _loseServer()
    {
        bool _first;
        lock (_gate)
        {
            _first = !_lost;
            _lost = true;
            _clock.Reset();
            _fit.Reset();
            _haveEpoch = false;
        }

        _session = 0;

        if (_first)
        {
            Log.Warning($"[SyncClient] Server {_endpointOf(_server!)} went quiet, running on our own");
            _setState(NetworkSyncProtocol.ConnectionState.Disconnected);
        }

        if (_serverAddress is not null) return false;

        _server = null;
        return true;
    }

    private void _adopt(uint session)
    {
        _session = session;
        _haveCommand = false;
    }

    /// <summary>
    /// Takes a reading unless it belongs to an older jump. A new epoch or playing flag starts the line over.
    /// </summary>
    private void _takePosition(in SyncPacket packet)
    {
        if (_haveEpoch && (int)(packet.Epoch - _epoch) < 0) return;

        if (!_haveEpoch || packet.Epoch != _epoch || packet.Playing != _serverPlaying)
        {
            _fit.Reset();
            _epoch = packet.Epoch;
            _serverPlaying = packet.Playing;
            _haveEpoch = true;
        }

        _fit.Add(packet.ServerSent, packet.Position, packet.Rate);
    }

    /// <summary>
    /// A command from the server's app. Each one comes three times, only the first gets through.
    /// </summary>
    private void _takeCommand(ReadOnlySpan<byte> bytes)
    {
        NetworkSyncProtocol.Command cmd = default;
        if (!NetworkSyncProtocol.DeserializeCommand(bytes, ref cmd)) return;
        if (_haveCommand && cmd.SequenceNumber - _lastCommand <= 0) return;

        _lastCommand = cmd.SequenceNumber;
        _haveCommand = true;

        try { CommandReceived?.Invoke(this, new CommandReceivedEventArgs(cmd)); }
        catch (Exception ex) { Log.Error($"[SyncClient] A CommandReceived handler threw on {cmd.Type}", ex); }
    }

    private void _refreshState()
    {
        bool _valid, _synced;
        lock (_gate)
        {
            _valid = _clock.IsValid;
            _synced = _valid && _haveEpoch;
        }

        if (_synced) _setState(NetworkSyncProtocol.ConnectionState.Synced);
        else if (_valid) _setState(NetworkSyncProtocol.ConnectionState.Connected);
    }

    #endregion

    #region Control thread

    private void _controlLoop()
    {
        while (_isRunning)
        {
            try { _followOnce(NetworkClock.Now); }
            catch (Exception ex)
            {
                int _n = ++_controlErrors;
                if (_n == 1 || _n % ErrorReportInterval == 0)
                    Log.Error($"[SyncClient] Following the server failed (occurrence #{_n})", ex);
            }

            Thread.Sleep(ControlTickMs);
        }
    }

    /// <summary>
    /// One look: the server's heard position against ours, then whatever it takes — go in, stop,
    /// follow a jump, trim, or (rarely) seek.
    /// </summary>
    private void _followOnce(double now)
    {
        ISyncTimeline _player = _timeline!;

        bool _lostNow, _ready, _serverPlays;
        uint _serverEpoch;
        double _server = 0;
        lock (_gate)
        {
            _lostNow = _lost;
            _serverPlays = _serverPlaying;
            _serverEpoch = _epoch;
            _ready = !_lost && _haveEpoch && _clock.IsValid && _fit.TryPredict(_clock.ToServer(now), out _server);
        }

        bool _playing = _player.Read(out double _position, out bool _rendering);

        if (!_ready)
        {
            _letGo();
            if (_lostNow && !_allowOfflinePlayback && _playing && !_pausedForLoss)
            {
                _pausedForLoss = true;
                _player.Follow(_position, false);
                Log.Info("[SyncClient] Server lost, pausing (offline playback is off)");
            }
            return;
        }

        _pausedForLoss = false;
        double _latency = _player.OutputLatency;

        if (!_serverPlays)
        {
            if (_playing || (_serverEpoch != _followedEpoch && Math.Abs(_position - _server) > 0.001))
            {
                _letGo();
                _player.Follow(_server, false);
            }

            _followedEpoch = _serverEpoch;
            _wentIn = false;
            return;
        }

        if (!_playing)
        {
            if (now < _nextGoInAt) return;
            _goIn(now, _server, _latency, _wentIn);
            _followedEpoch = _serverEpoch;
            return;
        }

        _wentIn = false;
        if (!_rendering) return;

        double _heard = _position - _latency;

        if (_serverEpoch != _followedEpoch)
        {
            _followedEpoch = _serverEpoch;
            if (Math.Abs(_server - _heard) > ReseekAboveSeconds) _seekTo(now, _server, _latency);
            return;
        }

        float _trim = _player.Trim;
        _ownFit.Add(now, _heard, _trim);
        if (!_ownFit.TryPredict(now, out double _own)) return;

        double _error = _server - _own;
        _lastError = _error;
        _learnStartLead(now, _error);
        _stats.Add(now, _error);
        if (_stats.Due(now)) Log.Info(_stats.Take(now, Jitter, AverageLatency * 2.0));

        SyncAction _action = _controller.Evaluate(now, _error);
        if (_action.Kind == SyncActionKind.Trim)
        {
            _stats.Trimmed();
            _player.Trim = (float)_action.Trim;
        }
        else if (_action.Kind == SyncActionKind.Seek)
        {
            _stats.Jumped();
            Log.Info($"[SyncClient] {_error * 1000:F0} ms off the server, jumping to it");
            _seekTo(now, _server, _latency);
        }
    }

    /// <summary>
    /// The server plays and we don't: start where it will be by the time our sound comes out,
    /// plus the lead our start usually loses. retry means the last go-in didn't get us playing.
    /// </summary>
    private void _goIn(double now, double server, double latency, bool retry)
    {
        double _target = Math.Max(0.0, server + latency + _startLead);

        _timeline!.Trim = 1f;
        _controller.Reset();
        _controller.Settle(now);
        _ownFit.Reset();
        _timeline.Follow(_target, true);

        _wentIn = true;
        _landingAt = now + SyncController.SettleSeconds;
        _nextGoInAt = now + (retry ? GoInRetrySeconds : SyncController.SettleSeconds);

        if (!retry)
            Log.Info($"[SyncClient] Following the server's play from {_target:F3}s (lead {_startLead * 1000:F0} ms)");
    }

    private void _seekTo(double now, double server, double latency)
    {
        _timeline!.Trim = 1f;
        _controller.Reset();
        _controller.Settle(now);
        _ownFit.Reset();
        _timeline.Follow(Math.Max(0.0, server + latency), true);
    }

    /// <summary>
    /// How far off the first landing after a start was goes into the next start's lead.
    /// </summary>
    private void _learnStartLead(double now, double error)
    {
        if (double.IsNaN(_landingAt) || now < _landingAt) return;

        _landingAt = double.NaN;
        _startLead = Math.Clamp(_startLead + error, 0.0, 0.5);
        Log.Info($"[SyncClient] Landed {error * 1000:F1} ms off, next start leads by {_startLead * 1000:F0} ms");
    }

    /// <summary>
    /// Nothing to follow: plain tempo again, the controller starts fresh next time.
    /// </summary>
    private void _letGo()
    {
        if (_timeline!.Trim != 1f) _timeline.Trim = 1f;
        _controller.Reset();
        _ownFit.Reset();
        _wentIn = false;
    }

    #endregion

    private void _setState(NetworkSyncProtocol.ConnectionState newState)
    {
        var oldState = _connectionState;
        if (oldState == newState)
            return;

        _connectionState = newState;

        if (newState == NetworkSyncProtocol.ConnectionState.Disconnected)
            Log.Warning($"[SyncClient] {oldState} -> {newState}");
        else
            Log.Info($"[SyncClient] {oldState} -> {newState}");

        try { ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState)); }
        catch (Exception ex) { Log.Error("[SyncClient] A ConnectionStateChanged handler threw", ex); }
    }

    private static IPEndPoint _endpointOf(SocketAddress address)
        => (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(address);

    /// <summary>
    /// Stops the client.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}

/// <summary>
/// Old/new state pair for the connection change event.
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    public NetworkSyncProtocol.ConnectionState OldState { get; }
    public NetworkSyncProtocol.ConnectionState NewState { get; }

    public ConnectionStateChangedEventArgs(
        NetworkSyncProtocol.ConnectionState oldState,
        NetworkSyncProtocol.ConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

/// <summary>
/// Carries a decoded command up to the listeners.
/// </summary>
public class CommandReceivedEventArgs : EventArgs
{
    public NetworkSyncProtocol.Command Command { get; }

    public CommandReceivedEventArgs(NetworkSyncProtocol.Command command)
    {
        Command = command;
    }
}
