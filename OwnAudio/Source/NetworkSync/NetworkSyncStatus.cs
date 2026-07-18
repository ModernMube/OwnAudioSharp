namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Snapshot of where the network sync stands right now (server/client, latency, drift).
/// </summary>
public class NetworkSyncStatus
{
    public bool IsEnabled { get; internal set; }
    public bool IsServer { get; internal set; }
    public bool IsClient { get; internal set; }
    public NetworkSyncProtocol.ConnectionState ConnectionState { get; internal set; }
    public int ClientCount { get; internal set; }
    public double AverageLatency { get; internal set; }
    public double ServerLatency { get; internal set; }
    public double SyncDrift { get; internal set; }
    public LocalTimeProvider.TimeSyncTier TimeSyncTier { get; internal set; }
    public bool IsLocalControlAllowed { get; internal set; }
}
