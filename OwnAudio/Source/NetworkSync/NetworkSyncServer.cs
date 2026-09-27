using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Logger;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Server side of the sync. Answers pings on the spot and sends its heard song position to every
/// client that pinged — unicast, 20 Hz while playing, 1 Hz stopped, at once on a jump. Once a
/// second it announces itself on each card's broadcast address for the clients looking for it.
/// Nothing on either thread allocates once the clients are in.
/// </summary>
public sealed class NetworkSyncServer : IDisposable
{
    internal const int TickMs = 50;
    internal const double ClientTimeoutSeconds = 5.0;

    /// <summary>
    /// Announcements go to the sync port + 1, so a client looking on the same machine never
    /// shares a socket with us.
    /// </summary>
    internal const int DiscoveryPortOffset = 1;

    /// <summary>
    /// More than the control tick and a device block together — a bigger step than this in
    /// the heard position is a jump, not jitter.
    /// </summary>
    private const double JumpSeconds = 0.1;

    private const double StoppedResendSeconds = 1.0;
    private const double AnnounceSeconds = 1.0;
    private const int RefreshCardsEvery = 10;
    private const int MaxClients = 64;
    private const int CommandQueueSize = 256;
    private const int CommandRepeats = 3;

    /// <summary>
    /// On macOS closing a socket waits for a blocked receive on another thread, so the
    /// receive comes up for air now and then to see it's time to go.
    /// </summary>
    private const int ReceiveTimeoutMs = 250;

    private readonly MasterClock _masterClock;
    private readonly int _port;
    private ISyncTimeline? _timeline;

    private Socket? _socket;
    private Thread? _receiver;
    private Thread? _sender;
    private volatile bool _isRunning;
    private bool _disposed;
    private readonly AutoResetEvent _wake = new AutoResetEvent(false);

    private readonly object _gate = new object();
    private readonly List<Client> _clients = new List<Client>();
    private uint _session;

    private readonly NetworkSyncProtocol.Command[] _commandQueue = new NetworkSyncProtocol.Command[CommandQueueSize];
    private int _commandQueueHead;
    private int _commandQueueTail;
    private int _commandSequence;
    private readonly object _commandQueueLock = new object();

    private class Client
    {
        public SocketAddress Address = null!;
        public IPEndPoint Endpoint = null!;
        public double LastPing;
    }

    /// <summary>
    /// Clients pinging us right now.
    /// </summary>
    public int ClientCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    /// <summary>
    /// True while the threads are up.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// A new client pinged. From the receive thread.
    /// </summary>
    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;

    /// <summary>
    /// A client stopped pinging for 5 s and got dropped. From the send thread.
    /// </summary>
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

    /// <summary>
    /// Sent to on top of the broadcast addresses. The tests put loopback here.
    /// </summary>
    internal IPEndPoint[] ExtraAnnounceTargets { get; set; } = [];

    /// <summary>
    /// The port actually bound, 0 asks the OS for one.
    /// </summary>
    internal int BoundPort { get; private set; }

    /// <summary>
    /// Server for the mixer that owns this clock (the registered one), listening on the given UDP port.
    /// </summary>
    /// <param name="masterClock"></param>
    /// <param name="port"></param>
    public NetworkSyncServer(MasterClock masterClock, int port = 9876)
    {
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
        _port = port;
    }

    internal NetworkSyncServer(AudioMixer mixer, int port) : this(mixer.MasterClock, port)
    {
        _timeline = new MixerSyncTimeline(mixer);
    }

    internal NetworkSyncServer(ISyncTimeline timeline, MasterClock clock, int port) : this(clock, port)
    {
        _timeline = timeline;
    }

    /// <summary>
    /// Binds the socket and starts the threads.
    /// </summary>
    public Task StartAsync()
    {
        if (_isRunning)
            return Task.CompletedTask;

        _timeline ??= _timelineOf(_masterClock);

        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            _socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _socket.ReceiveTimeout = ReceiveTimeoutMs;
            BoundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }
        catch (Exception ex)
        {
            Log.Error($"[SyncServer] Cannot bind UDP {_port}", ex);
            _socket?.Close();
            _socket = null;
            throw new InvalidOperationException($"Failed to start network sync server: {ex.Message}", ex);
        }

        _session = (uint)Random.Shared.Next(1, int.MaxValue);
        _isRunning = true;

        _receiver = new Thread(_receiveLoop) { Name = "NetworkSyncServer.Pong", IsBackground = true };
        _sender   = new Thread(_sendLoop)    { Name = "NetworkSyncServer.Position", IsBackground = true };
        _receiver.Start();
        _sender.Start();

        Log.Info($"[SyncServer] Serving on UDP {BoundPort}, announcing on {BoundPort + DiscoveryPortOffset}");
        return Task.CompletedTask;
    }

    private static ISyncTimeline _timelineOf(MasterClock clock)
    {
        AudioMixer? _mixer = OwnaudioNet.GetRegisteredAudioMixer();
        if (_mixer is null || !ReferenceEquals(_mixer.MasterClock, clock))
            throw new InvalidOperationException("Network sync needs the AudioMixer that owns this MasterClock to be the registered one (OwnaudioNet.SetPrimaryAudioMixer).");

        return new MixerSyncTimeline(_mixer);
    }

    /// <summary>
    /// Stops the threads and closes the socket.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _wake.Set();

        _receiver?.Join(1000);
        _sender?.Join(1000);
        _socket?.Close();
        _socket = null;

        int _dropped;
        lock (_gate)
        {
            _dropped = _clients.Count;
            _clients.Clear();
        }

        Log.Info($"[SyncServer] Stopped, {_dropped} clients dropped");
    }

    /// <summary>
    /// Queues a command for the clients; it goes out three times over the next ticks, the
    /// clients drop the repeats. False when the queue is full.
    /// </summary>
    /// <param name="command"></param>
    public bool EnqueueCommand(ref NetworkSyncProtocol.Command command)
    {
        lock (_commandQueueLock)
        {
            int nextTail = (_commandQueueTail + 1) % CommandQueueSize;
            if (nextTail == _commandQueueHead)
            {
                Log.Error($"[SyncServer] Command ring full ({CommandQueueSize}), a {command.Type} command was dropped");
                return false;
            }

            command.SequenceNumber = ++_commandSequence;
            _commandQueue[_commandQueueTail] = command;
            _commandQueueTail = nextTail;
        }

        _wake.Set();
        return true;
    }

    /// <summary>
    /// Answers an old-style command ping (LocalTimeProvider's peer sync) with a command pong.
    /// </summary>
    /// <param name="clientEndpoint"></param>
    /// <param name="pingCmd"></param>
    public void HandlePing(IPEndPoint clientEndpoint, ref NetworkSyncProtocol.Command pingCmd)
    {
        if (_socket is not { } _s) return;

        var pongCmd = NetworkSyncProtocol.CreatePongCommand(pingCmd.ClientSendTime, pingCmd.SequenceNumber);

        Span<byte> _out = stackalloc byte[NetworkSyncProtocol.CommandSize];
        int _n = NetworkSyncProtocol.SerializeCommand(ref pongCmd, _out);

        try { _s.SendTo(_out.Slice(0, _n), SocketFlags.None, clientEndpoint); }
        catch (SocketException) { }
    }

    private void _receiveLoop()
    {
        byte[] _in  = new byte[512];
        byte[] _out = new byte[SyncPacket.Size];
        var _from = new SocketAddress(AddressFamily.InterNetwork);
        Socket _sock = _socket!;

        while (_isRunning)
        {
            int _n;
            try { _n = _sock.ReceiveFrom(_in, SocketFlags.None, _from); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset) { continue; }
            catch (Exception) { return; }

            double _received = NetworkClock.Now;

            if (!SyncPacket.TryRead(_in.AsSpan(0, _n), out var _ping))
            {
                _legacyPing(_in.AsSpan(0, _n), _from);
                continue;
            }

            if (_ping.Kind != SyncPacketKind.Ping || (_ping.Session != 0 && _ping.Session != _session)) continue;
            if (!_touch(_from, _received)) continue;

            var _pong = new SyncPacket
            {
                Kind           = SyncPacketKind.Pong,
                Session        = _session,
                Sequence       = _ping.Sequence,
                ClientSent     = _ping.ClientSent,
                ServerReceived = _received,
                ServerSent     = NetworkClock.Now,
            };
            _pong.Write(_out);

            try { _sock.SendTo(_out, SocketFlags.None, _from); }
            catch (SocketException) { }
        }
    }

    private void _legacyPing(ReadOnlySpan<byte> bytes, SocketAddress from)
    {
        NetworkSyncProtocol.Command cmd = default;
        if (!NetworkSyncProtocol.DeserializeCommand(bytes, ref cmd) || cmd.Type != NetworkSyncProtocol.CommandType.Ping) return;

        HandlePing(_endpointOf(from), ref cmd);
    }

    /// <summary>
    /// A ping came in: refreshes a known client, or takes a new one. Two clients may share a
    /// machine, so a client back on a new port just leaves its old entry to age out. False when full.
    /// </summary>
    private bool _touch(SocketAddress from, double now)
    {
        IPEndPoint? _new = null;

        lock (_gate)
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                if (!_clients[i].Address.Equals(from)) continue;

                _clients[i].LastPing = now;
                return true;
            }

            if (_clients.Count >= MaxClients) return false;

            _new = _endpointOf(from);
            var _copy = new SocketAddress(from.Family, from.Size);
            from.Buffer.Span.Slice(0, from.Size).CopyTo(_copy.Buffer.Span);
            _clients.Add(new Client { Address = _copy, Endpoint = _new, LastPing = now });
        }

        Log.Info($"[SyncServer] Client {_new} joined");
        ClientConnected?.Invoke(this, new ClientConnectedEventArgs(_new));
        _wake.Set();
        return true;
    }

    private void _sendLoop()
    {
        byte[] _out = new byte[NetworkSyncProtocol.MaxPacketSize];
        var _repeats = new NetworkSyncProtocol.Command[16];
        var _repeatsLeft = new int[16];
        Socket _sock = _socket!;
        ISyncTimeline _player = _timeline!;

        uint _epoch = 1;
        uint _sequence = 0;
        bool _lastPlaying = false;
        double _lastHeard = double.NaN;
        double _lastAt = 0;
        double _nextStoppedSend = 0;
        double _nextAnnounce = 0;
        double _nextSweep = 0;
        int _announces = 0;
        SocketAddress[] _cards = [];

        while (_isRunning)
        {
            _wake.WaitOne(TickMs);
            if (!_isRunning) return;

            double _now = NetworkClock.Now;

            try
            {
                bool _playing = _player.Read(out double _position, out bool _rendering) && _rendering;
                double _heard = _playing ? _position - _player.OutputLatency : _position;

                bool _moved = _playing != _lastPlaying
                    || (_playing
                        ? Math.Abs(_heard - (_lastHeard + (_now - _lastAt))) > JumpSeconds
                        : Math.Abs(_heard - _lastHeard) > 0.001);

                if (_moved) _epoch++;
                _lastPlaying = _playing;
                _lastHeard = _heard;
                _lastAt = _now;

                if (_moved || _playing || _now >= _nextStoppedSend)
                {
                    _nextStoppedSend = _now + StoppedResendSeconds;

                    var _packet = new SyncPacket
                    {
                        Kind       = SyncPacketKind.Position,
                        Session    = _session,
                        Sequence   = ++_sequence,
                        Epoch      = _epoch,
                        Playing    = _playing,
                        ServerSent = _now,
                        Position   = _heard,
                        Rate       = _playing ? 1.0 : 0.0,
                    };
                    _packet.Write(_out);
                    _sendToClients(_sock, _out.AsSpan(0, SyncPacket.Size));
                }
            }
            catch (Exception ex)
            {
                Log.Error("[SyncServer] Reading the song position failed", ex);
            }

            _sendCommands(_sock, _out, _repeats, _repeatsLeft);

            if (_now >= _nextAnnounce)
            {
                _nextAnnounce = _now + AnnounceSeconds;
                if (_announces++ % RefreshCardsEvery == 0) _cards = _announceTargets();
                _announce(_sock, _out, _cards);
            }

            if (_now >= _nextSweep)
            {
                _nextSweep = _now + 1.0;
                _dropSilentClients(_now);
            }
        }
    }

    private void _sendToClients(Socket socket, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                try { socket.SendTo(bytes, SocketFlags.None, _clients[i].Address); }
                catch (SocketException) { }
            }
        }
    }

    /// <summary>
    /// Moves the queued commands into the repeat slots and sends every slot still owed a copy.
    /// A full set of slots overwrites the one with the fewest copies left.
    /// </summary>
    private void _sendCommands(Socket socket, byte[] buffer, NetworkSyncProtocol.Command[] repeats, int[] left)
    {
        while (true)
        {
            NetworkSyncProtocol.Command cmd;
            lock (_commandQueueLock)
            {
                if (_commandQueueHead == _commandQueueTail) break;

                cmd = _commandQueue[_commandQueueHead];
                _commandQueueHead = (_commandQueueHead + 1) % CommandQueueSize;
            }

            int _slot = 0;
            for (int i = 1; i < left.Length; i++)
                if (left[i] < left[_slot]) _slot = i;

            repeats[_slot] = cmd;
            left[_slot] = CommandRepeats;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] == 0) continue;

            int _n = NetworkSyncProtocol.SerializeCommand(ref repeats[i], buffer);
            _sendToClients(socket, buffer.AsSpan(0, _n));
            left[i]--;
        }
    }

    private void _announce(Socket socket, byte[] buffer, SocketAddress[] cards)
    {
        var _hello = new SyncPacket { Kind = SyncPacketKind.Announce, Session = _session, Port = BoundPort };
        _hello.Write(buffer);

        foreach (SocketAddress _to in cards)
        {
            try { socket.SendTo(buffer.AsSpan(0, SyncPacket.Size), SocketFlags.None, _to); }
            catch (SocketException) { }
        }
    }

    /// <summary>
    /// Each up, non-loopback IPv4 card's directed broadcast address — not 255.255.255.255, that
    /// one leaves on whichever card the OS likes.
    /// </summary>
    private SocketAddress[] _announceTargets()
    {
        var _list = new List<SocketAddress>();
        int _port = BoundPort + DiscoveryPortOffset;

        try
        {
            foreach (var _card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (_card.OperationalStatus != OperationalStatus.Up) continue;
                if (_card.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var _unicast in _card.GetIPProperties().UnicastAddresses)
                {
                    if (_unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    byte[] _ip   = _unicast.Address.GetAddressBytes();
                    byte[] _mask = _unicast.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) _ip[i] = (byte)(_ip[i] | ~_mask[i]);

                    _list.Add(new IPEndPoint(new IPAddress(_ip), _port).Serialize());
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warning($"[SyncServer] Cannot list the network cards, not announcing: {ex.Message}");
        }

        foreach (var _extra in ExtraAnnounceTargets) _list.Add(_extra.Serialize());
        return _list.ToArray();
    }

    private void _dropSilentClients(double now)
    {
        List<IPEndPoint>? _gone = null;

        lock (_gate)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (now - _clients[i].LastPing < ClientTimeoutSeconds) continue;

                (_gone ??= new List<IPEndPoint>()).Add(_clients[i].Endpoint);
                _clients.RemoveAt(i);
            }
        }

        if (_gone is null) return;

        foreach (var _endpoint in _gone)
        {
            Log.Warning($"[SyncServer] Client {_endpoint} went silent, dropped");
            ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(_endpoint));
        }
    }

    private static IPEndPoint _endpointOf(SocketAddress address)
        => (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(address);

    /// <summary>
    /// Stops the server.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _wake.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Endpoint of a client that just connected.
/// </summary>
public class ClientConnectedEventArgs : EventArgs
{
    public IPEndPoint ClientEndpoint { get; }

    public ClientConnectedEventArgs(IPEndPoint clientEndpoint)
    {
        ClientEndpoint = clientEndpoint;
    }
}

/// <summary>
/// Endpoint of a client that dropped off.
/// </summary>
public class ClientDisconnectedEventArgs : EventArgs
{
    public IPEndPoint ClientEndpoint { get; }

    public ClientDisconnectedEventArgs(IPEndPoint clientEndpoint)
    {
        ClientEndpoint = clientEndpoint;
    }
}
