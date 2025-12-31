using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Local network time provider with fallback tiers (no internet required).
/// Tier 1: Local NTP server (router)
/// Tier 2: Peer-to-peer sync with network sync server
/// Tier 3: System time with drift compensation
/// </summary>
public sealed class LocalTimeProvider : IDisposable
{
    private readonly object _lock = new();
    private double _timeOffset = 0.0;  // Local time - Reference time
    private DateTime _lastSyncTime = DateTime.MinValue;
    private TimeSyncTier _currentTier = TimeSyncTier.SystemTime;
    private bool _disposed;

    // Local NTP servers to try (common router addresses)
    private static readonly string[] LocalNtpServers = new[]
    {
        "192.168.1.1",
        "192.168.0.1",
        "10.0.0.1",
        "router.local"
    };

    /// <summary>
    /// Time synchronization tier.
    /// </summary>
    public enum TimeSyncTier
    {
        /// <summary>
        /// System time only (no synchronization).
        /// </summary>
        SystemTime = 0,

        /// <summary>
        /// Peer-to-peer sync with network sync server.
        /// </summary>
        PeerToPeer = 1,

        /// <summary>
        /// Local NTP server (router).
        /// </summary>
        LocalNtp = 2
    }

    /// <summary>
    /// Gets the current time synchronization tier.
    /// </summary>
    public TimeSyncTier CurrentTier => _currentTier;

    /// <summary>
    /// Gets the time offset in seconds (local time - reference time).
    /// </summary>
    public double TimeOffset => _timeOffset;

    /// <summary>
    /// Gets the last successful sync time.
    /// </summary>
    public DateTime LastSyncTime => _lastSyncTime;

    /// <summary>
    /// Gets the synchronized time (UTC).
    /// </summary>
    public DateTime GetSynchronizedTime()
    {
        lock (_lock)
        {
            return DateTime.UtcNow.AddSeconds(-_timeOffset);
        }
    }

    /// <summary>
    /// Gets the synchronized time in ticks.
    /// </summary>
    public long GetSynchronizedTimeTicks()
    {
        return GetSynchronizedTime().Ticks;
    }

    /// <summary>
    /// Attempts to synchronize with local NTP server (Tier 1).
    /// </summary>
    /// <param name="timeout">Timeout in milliseconds.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> TrySyncWithLocalNtpAsync(int timeout = 1000)
    {
        foreach (var server in LocalNtpServers)
        {
            try
            {
                var ntpTime = await GetNtpTimeAsync(server, timeout);
                if (ntpTime.HasValue)
                {
                    lock (_lock)
                    {
                        _timeOffset = (DateTime.UtcNow - ntpTime.Value).TotalSeconds;
                        _lastSyncTime = DateTime.UtcNow;
                        _currentTier = TimeSyncTier.LocalNtp;
                    }
                    return true;
                }
            }
            catch
            {
                // Try next server
            }
        }

        return false;
    }

    /// <summary>
    /// Synchronizes with peer (network sync server) using Cristian's algorithm (Tier 2).
    /// </summary>
    /// <param name="serverEndpoint">Server endpoint.</param>
    /// <param name="timeout">Timeout in milliseconds.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public async Task<bool> TrySyncWithPeerAsync(IPEndPoint serverEndpoint, int timeout = 1000)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = timeout;

            // Send time request
            var t0 = DateTime.UtcNow;
            var pingCmd = NetworkSyncProtocol.CreatePingCommand(t0.Ticks, 0);
            
            Span<byte> buffer = stackalloc byte[NetworkSyncProtocol.MaxPacketSize];
            int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref pingCmd, buffer);
            
            await udpClient.SendAsync(buffer.Slice(0, bytesWritten).ToArray(), bytesWritten, serverEndpoint);

            // Receive response
            var receiveTask = udpClient.ReceiveAsync();
            var timeoutTask = Task.Delay(timeout);

            if (await Task.WhenAny(receiveTask, timeoutTask) == receiveTask)
            {
                var t1 = DateTime.UtcNow;
                var response = receiveTask.Result;

                NetworkSyncProtocol.Command pongCmd = default;
                if (NetworkSyncProtocol.DeserializeCommand(response.Buffer, ref pongCmd) &&
                    pongCmd.Type == NetworkSyncProtocol.CommandType.Pong)
                {
                    // Cristian's algorithm
                    var serverTime = new DateTime(pongCmd.NtpTimestamp, DateTimeKind.Utc);
                    var roundTripTime = (t1 - t0).TotalSeconds;
                    var estimatedLatency = roundTripTime / 2.0;
                    var synchronizedTime = serverTime.AddSeconds(estimatedLatency);

                    lock (_lock)
                    {
                        _timeOffset = (DateTime.UtcNow - synchronizedTime).TotalSeconds;
                        _lastSyncTime = DateTime.UtcNow;
                        _currentTier = TimeSyncTier.PeerToPeer;
                    }
                    return true;
                }
            }
        }
        catch
        {
            // Sync failed
        }

        return false;
    }

    /// <summary>
    /// Attempts to synchronize using all available tiers.
    /// </summary>
    /// <param name="peerEndpoint">Optional peer endpoint for Tier 2.</param>
    /// <returns>The tier that succeeded.</returns>
    public async Task<TimeSyncTier> TrySyncAsync(IPEndPoint? peerEndpoint = null)
    {
        // Tier 1: Local NTP
        if (await TrySyncWithLocalNtpAsync())
            return TimeSyncTier.LocalNtp;

        // Tier 2: Peer-to-peer
        if (peerEndpoint != null && await TrySyncWithPeerAsync(peerEndpoint))
            return TimeSyncTier.PeerToPeer;

        // Tier 3: System time (no sync)
        lock (_lock)
        {
            _timeOffset = 0.0;
            _currentTier = TimeSyncTier.SystemTime;
        }
        return TimeSyncTier.SystemTime;
    }

    /// <summary>
    /// Gets NTP time from a server.
    /// </summary>
    private async Task<DateTime?> GetNtpTimeAsync(string server, int timeout)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server);
            if (addresses.Length == 0)
                return null;

            var endpoint = new IPEndPoint(addresses[0], 123);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveTimeout = timeout;
            socket.SendTimeout = timeout;

            // NTP packet (48 bytes) - use byte array instead of Span for async
            byte[] ntpData = new byte[48];
            ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (Client)

            await socket.ConnectAsync(endpoint);
            await socket.SendAsync(ntpData, SocketFlags.None);
            
            int received = await socket.ReceiveAsync(ntpData, SocketFlags.None);
            if (received < 48)
                return null;

            // Parse NTP timestamp (seconds since 1900-01-01)
            ulong intPart = BinaryPrimitives.ReadUInt32BigEndian(ntpData.AsSpan(40));
            ulong fractPart = BinaryPrimitives.ReadUInt32BigEndian(ntpData.AsSpan(44));

            var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
            var ntpTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMilliseconds((long)milliseconds);

            return ntpTime;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
