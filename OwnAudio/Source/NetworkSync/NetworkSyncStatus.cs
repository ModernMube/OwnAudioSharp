namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Status information for network synchronization.
/// </summary>
public class NetworkSyncStatus
{
    /// <summary>
    /// Gets whether network sync is enabled.
    /// </summary>
    public bool IsEnabled { get; internal set; }

    /// <summary>
    /// Gets whether this instance is a server.
    /// </summary>
    public bool IsServer { get; internal set; }

    /// <summary>
    /// Gets whether this instance is a client.
    /// </summary>
    public bool IsClient { get; internal set; }

    /// <summary>
    /// Gets the connection state (for clients).
    /// </summary>
    public NetworkSyncProtocol.ConnectionState ConnectionState { get; internal set; }

    /// <summary>
    /// Gets the number of connected clients (for servers).
    /// </summary>
    public int ClientCount { get; internal set; }

    /// <summary>
    /// Gets the average network latency in milliseconds (for clients).
    /// </summary>
    public double AverageLatency { get; internal set; }

    /// <summary>
    /// Gets the server latency in milliseconds (for clients).
    /// </summary>
    public double ServerLatency { get; internal set; }

    /// <summary>
    /// Gets the sync drift in milliseconds (for clients).
    /// </summary>
    public double SyncDrift { get; internal set; }

    /// <summary>
    /// Gets the current time synchronization tier.
    /// </summary>
    public LocalTimeProvider.TimeSyncTier TimeSyncTier { get; internal set; }

    /// <summary>
    /// Gets whether local control is allowed (client disconnected).
    /// </summary>
    public bool IsLocalControlAllowed { get; internal set; }
}
