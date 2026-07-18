using System.Buffers;
using System.Net;
using System.Net.Sockets;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Client side of the sync. Listens for the server's commands, tracks latency and
/// drives the local MasterClock. Falls back to local control when the link drops.
/// </summary>
public sealed class NetworkSyncClient : IDisposable
{
    private readonly MasterClock _masterClock;
    private readonly LocalTimeProvider _timeProvider;
    private readonly string? _serverAddress;
    private readonly int _port;
    private readonly bool _allowOfflinePlayback;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

    private UdpClient? _udpClient;
    private Thread? _receiveThread;
    private Thread? _pingThread;
    private volatile bool _isRunning;
    private bool _disposed;

    private volatile NetworkSyncProtocol.ConnectionState _connectionState = NetworkSyncProtocol.ConnectionState.Disconnected;
    private DateTime _lastServerMessageTime = DateTime.MinValue;
    private IPEndPoint? _serverEndpoint;

    private double _averageLatency = 0.0;
    private readonly Queue<double> _latencyHistory = new Queue<double>(100);
    private readonly object _latencyLock = new object();
    private int _pingSequence = 0;

    private int _reconnectAttempts = 0;
    private const int MaxReconnectAttempts = 10;
    private const int ReconnectBaseDelayMs = 1000;

    /// <summary>
    /// Current spot in the connect/sync lifecycle.
    /// </summary>
    public NetworkSyncProtocol.ConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// Rolling average round-trip latency in seconds.
    /// </summary>
    public double AverageLatency => _averageLatency;

    /// <summary>
    /// True while the receive/ping threads are up.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Local transport controls are only free while we're disconnected.
    /// </summary>
    public bool IsLocalControlAllowed => _connectionState == NetworkSyncProtocol.ConnectionState.Disconnected;

    /// <summary>
    /// Fires whenever the connection state flips.
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Fires on each transport command coming off the wire.
    /// </summary>
    public event EventHandler<CommandReceivedEventArgs>? CommandReceived;

    /// <summary>
    /// New client bound to a master clock. Null server address means auto-discovery.
    /// </summary>
    /// <param name="masterClock"></param>
    /// <param name="serverAddress"></param>
    /// <param name="port"></param>
    /// <param name="allowOfflinePlayback"></param>
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
        _timeProvider = new LocalTimeProvider();
    }

    /// <summary>
    /// Opens the socket, resolves the server and spins up the worker threads.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
            return;

        try
        {
            _udpClient = new UdpClient(_port);

            if (!string.IsNullOrEmpty(_serverAddress))
            {
                var addresses = await Dns.GetHostAddressesAsync(_serverAddress);
                if (addresses.Length > 0)
                    _serverEndpoint = new IPEndPoint(addresses[0], _port);
            }

            if (_serverEndpoint != null)
                await _timeProvider.TrySyncAsync(_serverEndpoint);

            _isRunning = true;

            _receiveThread = new Thread(ReceiveThreadLoop)
            {
                Name = "NetworkSyncClient.Receive",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _receiveThread.Start();

            _pingThread = new Thread(PingThreadLoop)
            {
                Name = "NetworkSyncClient.Ping",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _pingThread.Start();

            SetConnectionState(NetworkSyncProtocol.ConnectionState.Connecting);
        }
        catch (Exception ex)
        {
            _isRunning = false;
            SetConnectionState(NetworkSyncProtocol.ConnectionState.Disconnected);
            throw new InvalidOperationException($"Failed to start network sync client: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Flags the threads to quit, waits for them, tears down the socket.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        _receiveThread?.Join(TimeSpan.FromSeconds(2));
        _pingThread?.Join(TimeSpan.FromSeconds(2));

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        SetConnectionState(NetworkSyncProtocol.ConnectionState.Disconnected);
    }

    /// <summary>
    /// Receive loop - blocks on the socket and pushes decoded commands through.
    /// </summary>
    private void ReceiveThreadLoop()
    {
        byte[] buffer = _bufferPool.Rent(NetworkSyncProtocol.MaxPacketSize);

        try
        {
            while (_isRunning)
            {
                try
                {
                    _udpClient!.Client.ReceiveTimeout = 1000;
                    EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                    int bytesReceived = _udpClient.Client.ReceiveFrom(buffer, ref remoteEndpoint);

                    if (bytesReceived > 0)
                    {
                        _lastServerMessageTime = DateTime.UtcNow;

                        NetworkSyncProtocol.Command cmd = default;
                        if (NetworkSyncProtocol.DeserializeCommand(buffer.AsSpan(0, bytesReceived), ref cmd))
                            ProcessCommand(ref cmd, (IPEndPoint)remoteEndpoint);
                    }
                }
                catch (SocketException) { CheckConnectionTimeout(); }
                catch {}
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    /// <summary>
    /// Ping loop - pokes the server every 5s so we keep a latency estimate.
    /// </summary>
    private void PingThreadLoop()
    {
        while (_isRunning)
        {
            try
            {
                if (_serverEndpoint != null &&
                    _connectionState != NetworkSyncProtocol.ConnectionState.Disconnected)
                {
                    SendPing();
                }

                Thread.Sleep(5000);
            }
            catch {}
        }
    }

    /// <summary>
    /// Sends one ping stamped with the current tick.
    /// </summary>
    private void SendPing()
    {
        if (_udpClient == null || _serverEndpoint == null)
            return;

        var pingCmd = NetworkSyncProtocol.CreatePingCommand(DateTime.UtcNow.Ticks, _pingSequence++);

        byte[] buffer = _bufferPool.Rent(NetworkSyncProtocol.MaxPacketSize);
        try
        {
            Span<byte> bufferSpan = buffer.AsSpan(0, NetworkSyncProtocol.MaxPacketSize);
            int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref pingCmd, bufferSpan);
            _udpClient.Send(buffer, bytesWritten, _serverEndpoint);
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    /// <summary>
    /// Routes a decoded command to the right handler.
    /// </summary>
    /// <param name="cmd"></param>
    /// <param name="remoteEndpoint"></param>
    private void ProcessCommand(ref NetworkSyncProtocol.Command cmd, IPEndPoint remoteEndpoint)
    {
        if (_serverEndpoint == null)
            _serverEndpoint = remoteEndpoint;

        switch (cmd.Type)
        {
            case NetworkSyncProtocol.CommandType.ClockSync:
                ProcessClockSync(ref cmd);
                break;

            case NetworkSyncProtocol.CommandType.Pong:
                ProcessPong(ref cmd);
                break;

            case NetworkSyncProtocol.CommandType.Play:
            case NetworkSyncProtocol.CommandType.Pause:
            case NetworkSyncProtocol.CommandType.Stop:
            case NetworkSyncProtocol.CommandType.Seek:
            case NetworkSyncProtocol.CommandType.Tempo:
                CommandReceived?.Invoke(this, new CommandReceivedEventArgs(cmd));
                break;
        }
    }

    /// <summary>
    /// Clock tick from the server - snap the master clock and mark us synced.
    /// </summary>
    /// <param name="cmd"></param>
    private void ProcessClockSync(ref NetworkSyncProtocol.Command cmd)
    {
        if (_connectionState == NetworkSyncProtocol.ConnectionState.Connecting ||
            _connectionState == NetworkSyncProtocol.ConnectionState.Connected)
        {
            SetConnectionState(NetworkSyncProtocol.ConnectionState.Synced);
        }

        _masterClock.SeekTo(cmd.MasterClockTimestamp);
    }

    /// <summary>
    /// Pong came back - half the round trip, feed it into the average.
    /// </summary>
    /// <param name="cmd"></param>
    private void ProcessPong(ref NetworkSyncProtocol.Command cmd)
    {
        var roundTripTime = (DateTime.UtcNow.Ticks - cmd.ClientSendTime) / (double)TimeSpan.TicksPerSecond;
        var latency = roundTripTime / 2.0;

        lock (_latencyLock)
        {
            _latencyHistory.Enqueue(latency);
            if (_latencyHistory.Count > 100)
                _latencyHistory.Dequeue();

            _averageLatency = _latencyHistory.Average();
        }
    }

    /// <summary>
    /// No traffic for 30s - drop to disconnected and back off before retrying.
    /// </summary>
    private void CheckConnectionTimeout()
    {
        if (_connectionState == NetworkSyncProtocol.ConnectionState.Disconnected)
            return;

        var timeSinceLastMessage = (DateTime.UtcNow - _lastServerMessageTime).TotalSeconds;

        if (timeSinceLastMessage > 30)
        {
            SetConnectionState(NetworkSyncProtocol.ConnectionState.Disconnected);

            if (_reconnectAttempts < MaxReconnectAttempts)
            {
                _reconnectAttempts++;
                int delay = ReconnectBaseDelayMs * (int)Math.Pow(2, Math.Min(_reconnectAttempts, 5));
                Thread.Sleep(delay);

                SetConnectionState(NetworkSyncProtocol.ConnectionState.Connecting);
            }
        }
    }

    /// <summary>
    /// Swaps the state and raises the change event; a fresh sync resets the retry counter.
    /// </summary>
    /// <param name="newState"></param>
    private void SetConnectionState(NetworkSyncProtocol.ConnectionState newState)
    {
        var oldState = _connectionState;
        if (oldState == newState)
            return;

        _connectionState = newState;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState));

        if (newState == NetworkSyncProtocol.ConnectionState.Synced)
            _reconnectAttempts = 0;
    }

    /// <summary>
    /// Stops the threads and disposes the time provider.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _timeProvider?.Dispose();
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
