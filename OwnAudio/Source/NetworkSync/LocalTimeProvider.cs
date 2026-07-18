using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Grabs a network time reference without needing the internet.
/// Tries router NTP first, then a sync peer, then just falls back to system time.
/// </summary>
public sealed class LocalTimeProvider : IDisposable
{
    private readonly object _lock = new object();
    private double _timeOffset = 0.0;
    private DateTime _lastSyncTime = DateTime.MinValue;
    private TimeSyncTier _currentTier = TimeSyncTier.SystemTime;
    private bool _disposed;

    private static readonly string[] LocalNtpServers = new string[]
    {
        "192.168.1.1",
        "192.168.0.1",
        "10.0.0.1",
        "router.local"
    };

    /// <summary>
    /// Which source the current time is coming from.
    /// </summary>
    public enum TimeSyncTier
    {
        SystemTime = 0,
        PeerToPeer = 1,
        LocalNtp = 2
    }

    /// <summary>
    /// Source tier we last synced from.
    /// </summary>
    public TimeSyncTier CurrentTier => _currentTier;

    /// <summary>
    /// Offset in seconds between local and reference time.
    /// </summary>
    public double TimeOffset => _timeOffset;

    /// <summary>
    /// When we last pulled a good sync.
    /// </summary>
    public DateTime LastSyncTime => _lastSyncTime;

    /// <summary>
    /// UTC now, corrected by the measured offset.
    /// </summary>
    public DateTime GetSynchronizedTime()
    {
        lock (_lock) { return DateTime.UtcNow.AddSeconds(-_timeOffset); }
    }

    /// <summary>
    /// Same as GetSynchronizedTime but as ticks.
    /// </summary>
    public long GetSynchronizedTimeTicks() => GetSynchronizedTime().Ticks;

    /// <summary>
    /// Tier 1 - ask the local router's NTP server, first one that answers wins.
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public async Task<bool> TrySyncWithLocalNtpAsync(int timeout = 200)
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
            catch {}
        }

        return false;
    }

    /// <summary>
    /// Tier 2 - Cristian's algorithm against a sync peer, half the round trip is the latency.
    /// </summary>
    /// <param name="serverEndpoint"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public async Task<bool> TrySyncWithPeerAsync(IPEndPoint serverEndpoint, int timeout = 1000)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = timeout;

            var t0 = DateTime.UtcNow;
            var pingCmd = NetworkSyncProtocol.CreatePingCommand(t0.Ticks, 0);

            Span<byte> buffer = stackalloc byte[NetworkSyncProtocol.MaxPacketSize];
            int bytesWritten = NetworkSyncProtocol.SerializeCommand(ref pingCmd, buffer);

            await udpClient.SendAsync(buffer.Slice(0, bytesWritten).ToArray(), bytesWritten, serverEndpoint);

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
                    var serverTime = new DateTime(pongCmd.NtpTimestamp, DateTimeKind.Utc);
                    var estimatedLatency = (t1 - t0).TotalSeconds / 2.0;
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
        catch {}

        return false;
    }

    /// <summary>
    /// Walk the tiers top-down and return whichever one stuck.
    /// </summary>
    /// <param name="peerEndpoint"></param>
    /// <returns></returns>
    public async Task<TimeSyncTier> TrySyncAsync(IPEndPoint? peerEndpoint = null)
    {
        if (await TrySyncWithLocalNtpAsync())
            return TimeSyncTier.LocalNtp;

        if (peerEndpoint != null && await TrySyncWithPeerAsync(peerEndpoint))
            return TimeSyncTier.PeerToPeer;

        lock (_lock)
        {
            _timeOffset = 0.0;
            _currentTier = TimeSyncTier.SystemTime;
        }
        return TimeSyncTier.SystemTime;
    }

    /// <summary>
    /// Bare-bones SNTP request - build a mode 3 packet, read back the transmit timestamp.
    /// </summary>
    /// <param name="server"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    private async Task<DateTime?> GetNtpTimeAsync(string server, int timeout)
    {
        try
        {
            var dnsTask = Dns.GetHostAddressesAsync(server);
            if (await Task.WhenAny(dnsTask, Task.Delay(timeout)) != dnsTask)
                return null;

            var addresses = await dnsTask;
            if (addresses.Length == 0)
                return null;

            var endpoint = new IPEndPoint(addresses[0], 123);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveTimeout = timeout;
            socket.SendTimeout = timeout;

            byte[] ntpData = new byte[48];
            ntpData[0] = 0x1B;

            await socket.ConnectAsync(endpoint);
            await socket.SendAsync(ntpData, SocketFlags.None);

            int received = await socket.ReceiveAsync(ntpData, SocketFlags.None);
            if (received < 48)
                return null;

            ulong intPart = BinaryPrimitives.ReadUInt32BigEndian(ntpData.AsSpan(40));
            ulong fractPart = BinaryPrimitives.ReadUInt32BigEndian(ntpData.AsSpan(44));

            var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMilliseconds((long)milliseconds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Nothing native to free, just flip the flag.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
