using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Server side of the sync. Blasts a clock tick at 100Hz, queues one-off transport
/// commands and hands out pongs. Broadcast is zero-GC on the hot path.
/// </summary>
public sealed class NetworkSyncServer : IDisposable
{
    private readonly MasterClock _masterClock;
    private readonly LocalTimeProvider _timeProvider;
    private readonly int _port;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

    private UdpClient? _udpClient;
    private Thread? _broadcastThread;
    private volatile bool _isRunning;
    private bool _disposed;

    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();

    private readonly NetworkSyncProtocol.Command[] _commandQueue;
    private int _commandQueueHead = 0;
    private int _commandQueueTail = 0;
    private readonly object _commandQueueLock = new object();
    private const int CommandQueueSize = 256;

    private const int BroadcastIntervalMs = 10;

    /// <summary>
    /// What we remember about a connected client.
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
    /// How many clients are currently on the books.
    /// </summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// True while the broadcast thread is up.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Fires when a new client shows up. Not wired yet, hence the pragma.
    /// </summary>
#pragma warning disable CS0067
    public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
#pragma warning restore CS0067

    /// <summary>
    /// Fires when a client goes stale and gets dropped.
    /// </summary>
    public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

    /// <summary>
    /// New server bound to a master clock, listening on the given UDP port.
    /// </summary>
    /// <param name="masterClock"></param>
    /// <param name="port"></param>
    public NetworkSyncServer(MasterClock masterClock, int port = 9876)
    {
        _masterClock = masterClock ?? throw new ArgumentNullException(nameof(masterClock));
        _port = port;
        _timeProvider = new LocalTimeProvider();
        _commandQueue = new NetworkSyncProtocol.Command[CommandQueueSize];
    }

    /// <summary>
    /// Opens the broadcast socket and starts the pump thread.
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
            return;

        try
        {
            _udpClient = new UdpClient(_port);
            _udpClient.EnableBroadcast = true;

            _isRunning = true;
            _broadcastThread = new Thread(BroadcastThreadLoop)
            {
                Name = "NetworkSyncServer.Broadcast",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _broadcastThread.Start();
        }
        catch (Exception ex)
        {
            _isRunning = false;
            throw new InvalidOperationException($"Failed to start network sync server: {ex.Message}", ex);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the pump thread and tears the socket down.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        if (_broadcastThread != null && _broadcastThread.IsAlive)
            _broadcastThread.Join(TimeSpan.FromSeconds(2));

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        _clients.Clear();
    }

    /// <summary>
    /// Drops a command into the ring buffer. False if the ring is full.
    /// </summary>
    /// <param name="command"></param>
    /// <returns></returns>
    public bool EnqueueCommand(ref NetworkSyncProtocol.Command command)
    {
        lock (_commandQueueLock)
        {
            int nextTail = (_commandQueueTail + 1) % CommandQueueSize;
            if (nextTail == _commandQueueHead)
                return false;

            _commandQueue[_commandQueueTail] = command;
            _commandQueueTail = nextTail;
            return true;
        }
    }

    /// <summary>
    /// Pump loop - clock tick + queued commands every 10ms, stale sweep once a second.
    /// </summary>
    private void BroadcastThreadLoop()
    {
        byte[] buffer = _bufferPool.Rent(NetworkSyncProtocol.MaxPacketSize);

        try
        {
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _port);

            while (_isRunning)
            {
                try
                {
                    BroadcastClockSync(buffer, broadcastEndpoint);
                    BroadcastQueuedCommands(buffer, broadcastEndpoint);

                    if (Environment.TickCount % 1000 < BroadcastIntervalMs)
                        CleanupStaleClients();

                    Thread.Sleep(BroadcastIntervalMs);
                }
                catch
                {
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
    /// Serializes the current clock and broadcasts it.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="endpoint"></param>
    private void BroadcastClockSync(byte[] buffer, IPEndPoint endpoint)
    {
        if (_udpClient == null)
            return;

        var cmd = NetworkSyncProtocol.CreateClockSyncCommand(
            _timeProvider.GetSynchronizedTimeTicks(),
            _masterClock.CurrentTimestamp,
            _masterClock.CurrentSamplePosition,
            _masterClock.SampleRate);

        Span<byte> bufferSpan = buffer.AsSpan(0, NetworkSyncProtocol.MaxPacketSize);
        int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref cmd, bufferSpan);

        try { _udpClient.Send(buffer, bytesWritten, endpoint); }
        catch {}
    }

    /// <summary>
    /// Drains the command ring and broadcasts each one out.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="endpoint"></param>
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
                    break;

                cmd = _commandQueue[_commandQueueHead];
                _commandQueueHead = (_commandQueueHead + 1) % CommandQueueSize;
            }

            Span<byte> bufferSpan = buffer.AsSpan(0, NetworkSyncProtocol.MaxPacketSize);
            int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref cmd, bufferSpan);

            try { _udpClient.Send(buffer, bytesWritten, endpoint); }
            catch {}
        }
    }

    /// <summary>
    /// Boots any client we haven't heard from in 30s, raising the disconnect event.
    /// </summary>
    private void CleanupStaleClients()
    {
        var now = DateTime.UtcNow;
        var staleClients = new List<string>();

        foreach (var kvp in _clients)
        {
            if ((now - kvp.Value.LastHeartbeat).TotalSeconds > 30)
                staleClients.Add(kvp.Key);
        }

        foreach (var key in staleClients)
        {
            if (_clients.TryRemove(key, out var client))
                ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(client.Endpoint));
        }
    }

    /// <summary>
    /// Client pinged us - refresh its heartbeat and bounce a pong back.
    /// </summary>
    /// <param name="clientEndpoint"></param>
    /// <param name="pingCmd"></param>
    public void HandlePing(IPEndPoint clientEndpoint, ref NetworkSyncProtocol.Command pingCmd)
    {
        if (_udpClient == null)
            return;

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

        var pongCmd = NetworkSyncProtocol.CreatePongCommand(pingCmd.ClientSendTime, pingCmd.SequenceNumber);

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

    /// <summary>
    /// Stops the server and disposes the time provider.
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
