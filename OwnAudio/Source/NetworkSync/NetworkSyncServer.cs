using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Ownaudio.Synchronization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Network synchronization server with zero-GC broadcast.
/// Manages clients, broadcasts commands, and provides clock synchronization.
/// </summary>
public sealed class NetworkSyncServer : IDisposable
{
    private readonly MasterClock _masterClock;
    private readonly LocalTimeProvider _timeProvider;
    private readonly int _port;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

    // Network
    private UdpClient? _udpClient;
    private Thread? _broadcastThread;
    private volatile bool _isRunning;
    private bool _disposed;

    // Client tracking
    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();
    private readonly object _clientsLock = new();

    // Command queue (ring buffer for zero-GC)
    private readonly NetworkSyncProtocol.Command[] _commandQueue;
    private int _commandQueueHead = 0;
    private int _commandQueueTail = 0;
    private readonly object _commandQueueLock = new();
    private const int CommandQueueSize = 256;

    // Broadcast interval (100Hz = 10ms)
    private const int BroadcastIntervalMs = 10;

    /// <summary>
    /// Client information.
    /// </summary>
    private class ClientInfo
    {
        public string EndpointKey { get; set; } = string.Empty;
        public IPEndPoint Endpoint { get; set; } = null!;
        public DateTime LastHeartbeat { get; set; }
        public double AverageLatency { get; set; }
        public NetworkSyncProtocol.ConnectionState State { get; set; }
    }

    /// <summary>
    /// Gets the number of connected clients.
    /// </summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Gets whether the server is running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Event raised when a client connects.
    /// </summary>
    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;

    /// <summary>
    /// Event raised when a client disconnects.
    /// </summary>
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

    /// <summary>
    /// Initializes a new instance of the NetworkSyncServer class.
    /// </summary>
    /// <param name="masterClock">Master clock to synchronize.</param>
    /// <param name="port">UDP port to listen on.</param>
    public NetworkSyncServer(MasterClock masterClock, int port = 9876)
    {
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
        _port = port;
        _timeProvider = new LocalTimeProvider();
        _commandQueue = new NetworkSyncProtocol.Command[CommandQueueSize];
    }

    /// <summary>
    /// Starts the network sync server.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
            return;

        try
        {
            // Try to sync time (local NTP or system time)
            await _timeProvider.TrySyncAsync();

            // Create UDP client
            _udpClient = new UdpClient(_port);
            _udpClient.EnableBroadcast = true;

            // Start broadcast thread
            _isRunning = true;
            _broadcastThread = new Thread(BroadcastThreadLoop)
            {
                Name = "NetworkSyncServer.Broadcast",
                IsBackground = true,
                Priority = ThreadPriority.Normal  // Normal priority (not highest)
            };
            _broadcastThread.Start();
        }
        catch (Exception ex)
        {
            _isRunning = false;
            throw new InvalidOperationException($"Failed to start network sync server: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stops the network sync server.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        // Wait for broadcast thread to exit
        if (_broadcastThread != null && _broadcastThread.IsAlive)
        {
            _broadcastThread.Join(TimeSpan.FromSeconds(2));
        }

        // Close UDP client
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        // Clear clients
        _clients.Clear();
    }

    /// <summary>
    /// Enqueues a command to be broadcast to all clients (zero-allocation).
    /// </summary>
    /// <param name="command">Command to enqueue (passed by reference).</param>
    /// <returns>True if enqueued successfully, false if queue is full.</returns>
    public bool EnqueueCommand(ref NetworkSyncProtocol.Command command)
    {
        lock (_commandQueueLock)
        {
            int nextTail = (_commandQueueTail + 1) % CommandQueueSize;
            if (nextTail == _commandQueueHead)
                return false;  // Queue full

            _commandQueue[_commandQueueTail] = command;
            _commandQueueTail = nextTail;
            return true;
        }
    }

    /// <summary>
    /// Broadcast thread loop (runs at 100Hz).
    /// </summary>
    private void BroadcastThreadLoop()
    {
        // Pre-allocate buffer (reused for all broadcasts)
        byte[] buffer = _bufferPool.Rent(NetworkSyncProtocol.MaxPacketSize);
        
        try
        {
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _port);

            while (_isRunning)
            {
                try
                {
                    // 1. Broadcast clock sync (every cycle)
                    BroadcastClockSync(buffer, broadcastEndpoint);

                    // 2. Broadcast queued commands
                    BroadcastQueuedCommands(buffer, broadcastEndpoint);

                    // 3. Clean up stale clients (every 100 cycles = 1 second)
                    if (Environment.TickCount % 1000 < BroadcastIntervalMs)
                    {
                        CleanupStaleClients();
                    }

                    // Sleep for broadcast interval
                    Thread.Sleep(BroadcastIntervalMs);
                }
                catch
                {
                    // Log error but continue
                    Thread.Sleep(BroadcastIntervalMs * 2);
                }
            }
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    /// <summary>
    /// Broadcasts clock synchronization to all clients (zero-allocation).
    /// </summary>
    private void BroadcastClockSync(byte[] buffer, IPEndPoint endpoint)
    {
        if (_udpClient == null)
            return;

        // Create clock sync command
        var cmd = NetworkSyncProtocol.CreateClockSyncCommand(
            _timeProvider.GetSynchronizedTimeTicks(),
            _masterClock.CurrentTimestamp,
            _masterClock.CurrentSamplePosition,
            _masterClock.SampleRate);

        // Serialize (zero-allocation using Span)
        Span<byte> bufferSpan = buffer.AsSpan(0, NetworkSyncProtocol.MaxPacketSize);
        int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref cmd, bufferSpan);

        // Broadcast
        try
        {
            _udpClient.Send(buffer, bytesWritten, endpoint);
        }
        catch
        {
            // Ignore send errors
        }
    }

    /// <summary>
    /// Broadcasts queued commands to all clients (zero-allocation).
    /// </summary>
    private void BroadcastQueuedCommands(byte[] buffer, IPEndPoint endpoint)
    {
        if (_udpClient == null)
            return;

        while (true)
        {
            NetworkSyncProtocol.Command cmd;
            
            lock (_commandQueueLock)
            {
                if (_commandQueueHead == _commandQueueTail)
                    break;  // Queue empty

                cmd = _commandQueue[_commandQueueHead];
                _commandQueueHead = (_commandQueueHead + 1) % CommandQueueSize;
            }

            // Serialize (zero-allocation using Span)
            Span<byte> bufferSpan = buffer.AsSpan(0, NetworkSyncProtocol.MaxPacketSize);
            int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref cmd, bufferSpan);

            // Broadcast
            try
            {
                _udpClient.Send(buffer, bytesWritten, endpoint);
            }
            catch
            {
                // Ignore send errors
            }
        }
    }

    /// <summary>
    /// Cleans up clients that haven't sent a heartbeat in 30 seconds.
    /// </summary>
    private void CleanupStaleClients()
    {
        var now = DateTime.UtcNow;
        var staleClients = new List<string>();

        foreach (var kvp in _clients)
        {
            if ((now - kvp.Value.LastHeartbeat).TotalSeconds > 30)
            {
                staleClients.Add(kvp.Key);
            }
        }

        foreach (var key in staleClients)
        {
            if (_clients.TryRemove(key, out var client))
            {
                ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(client.Endpoint));
            }
        }
    }

    /// <summary>
    /// Handles a ping from a client (for latency measurement).
    /// </summary>
    public void HandlePing(IPEndPoint clientEndpoint, ref NetworkSyncProtocol.Command pingCmd)
    {
        if (_udpClient == null)
            return;

        // Update client info
        string key = clientEndpoint.ToString();
        _clients.AddOrUpdate(key,
            _ => new ClientInfo
            {
                EndpointKey = key,
                Endpoint = clientEndpoint,
                LastHeartbeat = DateTime.UtcNow,
                State = NetworkSyncProtocol.ConnectionState.Connected
            },
            (_, existing) =>
            {
                existing.LastHeartbeat = DateTime.UtcNow;
                return existing;
            });

        // Send pong response
        var pongCmd = NetworkSyncProtocol.CreatePongCommand(
            pingCmd.ClientSendTime,
            pingCmd.SequenceNumber);

        byte[] buffer = _bufferPool.Rent(NetworkSyncProtocol.MaxPacketSize);
        try
        {
            Span<byte> bufferSpan = buffer.AsSpan(0, NetworkSyncProtocol.MaxPacketSize);
            int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref pongCmd, bufferSpan);
            _udpClient.Send(buffer, bytesWritten, clientEndpoint);
        }
        finally
        {
            _bufferPool.Return(buffer);
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
/// Event args for client connected event.
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
/// Event args for client disconnected event.
/// </summary>
public class ClientDisconnectedEventArgs : EventArgs
{
    public IPEndPoint ClientEndpoint { get; }

    public ClientDisconnectedEventArgs(IPEndPoint clientEndpoint)
    {
        ClientEndpoint = clientEndpoint;
    }
}
