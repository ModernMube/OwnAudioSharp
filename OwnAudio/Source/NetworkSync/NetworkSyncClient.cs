using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Ownaudio.Synchronization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Network synchronization client with automatic fallback and latency compensation.
/// Receives commands from server and synchronizes local MasterClock.
/// </summary>
public sealed class NetworkSyncClient : IDisposable
{
    private readonly MasterClock _masterClock;
    private readonly LocalTimeProvider _timeProvider;
    private readonly string? _serverAddress;
    private readonly int _port;
    private readonly bool _allowOfflinePlayback;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

    // Network
    private UdpClient? _udpClient;
    private Thread? _receiveThread;
    private Thread? _pingThread;
    private volatile bool _isRunning;
    private bool _disposed;

    // Connection state
    private volatile NetworkSyncProtocol.ConnectionState _connectionState = NetworkSyncProtocol.ConnectionState.Disconnected;
    private DateTime _lastServerMessageTime = DateTime.MinValue;
    private IPEndPoint? _serverEndpoint;

    // Latency measurement
    private double _averageLatency = 0.0;
    private readonly Queue<double> _latencyHistory = new(100);
    private readonly object _latencyLock = new();
    private int _pingSequence = 0;

    // Reconnection
    private int _reconnectAttempts = 0;
    private const int MaxReconnectAttempts = 10;
    private const int ReconnectBaseDelayMs = 1000;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public NetworkSyncProtocol.ConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// Gets the average network latency in seconds.
    /// </summary>
    public double AverageLatency => _averageLatency;

    /// <summary>
    /// Gets whether the client is running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets whether local control is allowed (disconnected state).
    /// </summary>
    public bool IsLocalControlAllowed => _connectionState == NetworkSyncProtocol.ConnectionState.Disconnected;

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when a command is received.
    /// </summary>
    public event EventHandler<CommandReceivedEventArgs>? CommandReceived;

    /// <summary>
    /// Initializes a new instance of the NetworkSyncClient class.
    /// </summary>
    /// <param name="masterClock">Master clock to synchronize.</param>
    /// <param name="serverAddress">Server address (null for auto-discovery).</param>
    /// <param name="port">UDP port.</param>
    /// <param name="allowOfflinePlayback">Allow playback to continue when disconnected.</param>
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
    /// Starts the network sync client.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
            return;

        try
        {
            // Create UDP client
            _udpClient = new UdpClient(_port);

            // Resolve server endpoint
            if (!string.IsNullOrEmpty(_serverAddress))
            {
                var addresses = await Dns.GetHostAddressesAsync(_serverAddress);
                if (addresses.Length > 0)
                {
                    _serverEndpoint = new IPEndPoint(addresses[0], _port);
                }
            }

            // Try to sync time
            if (_serverEndpoint != null)
            {
                await _timeProvider.TrySyncAsync(_serverEndpoint);
            }

            // Start threads
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
    /// Stops the network sync client.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        // Wait for threads to exit
        _receiveThread?.Join(TimeSpan.FromSeconds(2));
        _pingThread?.Join(TimeSpan.FromSeconds(2));

        // Close UDP client
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        SetConnectionState(NetworkSyncProtocol.ConnectionState.Disconnected);
    }

    /// <summary>
    /// Receive thread loop - processes incoming commands.
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
                    // Receive packet (blocking with timeout)
                    _udpClient!.Client.ReceiveTimeout = 1000;
                    EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                    int bytesReceived = _udpClient.Client.ReceiveFrom(buffer, ref remoteEndpoint);

                    if (bytesReceived > 0)
                    {
                        _lastServerMessageTime = DateTime.UtcNow;

                        // Deserialize command
                        NetworkSyncProtocol.Command cmd = default;
                        if (NetworkSyncProtocol.DeserializeCommand(buffer.AsSpan(0, bytesReceived), ref cmd))
                        {
                            ProcessCommand(ref cmd, (IPEndPoint)remoteEndpoint);
                        }
                    }
                }
                catch (SocketException)
                {
                    // Timeout or network error - check connection state
                    CheckConnectionTimeout();
                }
                catch
                {
                    // Other error - continue
                }
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    /// <summary>
    /// Ping thread loop - measures latency periodically.
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

                Thread.Sleep(5000);  // Ping every 5 seconds
            }
            catch
            {
                // Ignore errors
            }
        }
    }

    /// <summary>
    /// Sends a ping to the server for latency measurement.
    /// </summary>
    private void SendPing()
    {
        if (_udpClient == null || _serverEndpoint == null)
            return;

        var pingCmd = NetworkSyncProtocol.CreatePingCommand(
            DateTime.UtcNow.Ticks,
            _pingSequence++);

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
    /// Processes a received command (zero-allocation).
    /// </summary>
    private void ProcessCommand(ref NetworkSyncProtocol.Command cmd, IPEndPoint remoteEndpoint)
    {
        // Update server endpoint if not set
        if (_serverEndpoint == null)
        {
            _serverEndpoint = remoteEndpoint;
        }

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
                // Raise event for application to handle
                CommandReceived?.Invoke(this, new CommandReceivedEventArgs(cmd));
                break;
        }
    }

    /// <summary>
    /// Processes clock synchronization command.
    /// </summary>
    private void ProcessClockSync(ref NetworkSyncProtocol.Command cmd)
    {
        // Update connection state
        if (_connectionState == NetworkSyncProtocol.ConnectionState.Connecting ||
            _connectionState == NetworkSyncProtocol.ConnectionState.Connected)
        {
            SetConnectionState(NetworkSyncProtocol.ConnectionState.Synced);
        }

        // Synchronize master clock (lock-free write)
        _masterClock.SeekTo(cmd.MasterClockTimestamp);
    }

    /// <summary>
    /// Processes pong response for latency measurement.
    /// </summary>
    private void ProcessPong(ref NetworkSyncProtocol.Command cmd)
    {
        var now = DateTime.UtcNow.Ticks;
        var roundTripTime = (now - cmd.ClientSendTime) / (double)TimeSpan.TicksPerSecond;
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
    /// Checks for connection timeout and handles reconnection.
    /// </summary>
    private void CheckConnectionTimeout()
    {
        if (_connectionState == NetworkSyncProtocol.ConnectionState.Disconnected)
            return;

        var timeSinceLastMessage = (DateTime.UtcNow - _lastServerMessageTime).TotalSeconds;

        if (timeSinceLastMessage > 30)
        {
            // Connection lost - switch to standalone mode
            SetConnectionState(NetworkSyncProtocol.ConnectionState.Disconnected);

            // Attempt reconnection if allowed
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
    /// Sets the connection state and raises event.
    /// </summary>
    private void SetConnectionState(NetworkSyncProtocol.ConnectionState newState)
    {
        var oldState = _connectionState;
        if (oldState != newState)
        {
            _connectionState = newState;
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState));

            // Reset reconnect attempts on successful connection
            if (newState == NetworkSyncProtocol.ConnectionState.Synced)
            {
                _reconnectAttempts = 0;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _timeProvider?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Event args for connection state changed event.
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
/// Event args for command received event.
/// </summary>
public class CommandReceivedEventArgs : EventArgs
{
    public NetworkSyncProtocol.Command Command { get; }

    public CommandReceivedEventArgs(NetworkSyncProtocol.Command command)
    {
        Command = command;
    }
}
