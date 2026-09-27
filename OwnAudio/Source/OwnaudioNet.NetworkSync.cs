using OwnaudioNET.NetworkSync;
using OwnaudioNET.Mixing;
using OwnaudioNET.Synchronization;

namespace OwnaudioNET;

/// <summary>
/// Network synchronization API for OwnaudioNet.
/// </summary>
public static partial class OwnaudioNet
{
    private static NetworkSyncServer? _networkSyncServer;
    private static NetworkSyncClient? _networkSyncClient;
    private static readonly object _networkSyncLock = new();

    /// <summary>
    /// Event raised when network sync connection state changes (client only).
    /// </summary>
    public static event EventHandler<ConnectionStateChangedEventArgs>? NetworkSyncConnectionChanged;

    /// <summary>
    /// A command the server sent with BroadcastCommand arrived (client only), raised on a sync thread.
    /// The transport itself (play, pause, seek) is followed without it — this is for the app's own extras,
    /// like a tempo change.
    /// </summary>
    public static event EventHandler<CommandReceivedEventArgs>? NetworkSyncCommandReceived;

    /// <summary>
    /// Starts network synchronization in server mode.
    /// The server sends its heard song position to every client that pings it, and announces itself
    /// on the local network so clients can find it.
    /// </summary>
    /// <param name="port">UDP port to use (default: 9876).</param>
    /// <param name="useLocalTimeOnly">Use local time synchronization only (no internet required).</param>
    /// <exception cref="InvalidOperationException">Thrown if not initialized or already running network sync.</exception>
    public static async Task StartNetworkSyncServerAsync(int port = 9876, bool useLocalTimeOnly = true)
    {
        lock (_networkSyncLock)
        {
            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized before starting network sync. Call Initialize() first.");

            if (_networkSyncServer != null || _networkSyncClient != null)
                throw new InvalidOperationException("Network sync is already running. Call StopNetworkSync() first.");

            // Get AudioMixer's MasterClock
            var mixer = GetAudioMixer();
            if (mixer == null)
                throw new InvalidOperationException("AudioMixer not available. Ensure the audio system is properly initialized.");

            mixer.MasterClock.Mode = ClockMode.NetworkServer;
            mixer.MasterClock.IsNetworkControlled = false;

            _networkSyncServer = new NetworkSyncServer(mixer, port);
        }

        try { await _networkSyncServer.StartAsync(); }
        catch
        {
            StopNetworkSync();
            throw;
        }
    }

    /// <summary>
    /// Starts network synchronization in client mode.
    /// The client follows the server's transport and stays on its song position with a small tempo
    /// trim; its own clock is never taken over, so losing the server changes nothing audible.
    /// </summary>
    /// <param name="serverAddress">Server IP address (null for auto-discovery).</param>
    /// <param name="port">The server's UDP port (default: 9876). Discovery listens on port + 1.</param>
    /// <param name="allowOfflinePlayback">Continue playback when disconnected from server.</param>
    /// <exception cref="InvalidOperationException">Thrown if not initialized or already running network sync.</exception>
    public static async Task StartNetworkSyncClientAsync(
        string? serverAddress = null,
        int port = 9876,
        bool allowOfflinePlayback = true)
    {
        lock (_networkSyncLock)
        {
            if (!_initialized || _engineWrapper == null)
                throw new InvalidOperationException("OwnaudioNet must be initialized before starting network sync. Call Initialize() first.");

            if (_networkSyncServer != null || _networkSyncClient != null)
                throw new InvalidOperationException("Network sync is already running. Call StopNetworkSync() first.");

            // Get AudioMixer's MasterClock
            var mixer = GetAudioMixer();
            if (mixer == null)
                throw new InvalidOperationException("AudioMixer not available. Ensure the audio system is properly initialized.");

            mixer.MasterClock.Mode = ClockMode.NetworkClient;
            mixer.MasterClock.IsNetworkControlled = false;

            _networkSyncClient = new NetworkSyncClient(mixer, serverAddress, port, allowOfflinePlayback);
            _networkSyncClient.ConnectionStateChanged += (sender, e) => NetworkSyncConnectionChanged?.Invoke(sender, e);
            _networkSyncClient.CommandReceived += (sender, e) => NetworkSyncCommandReceived?.Invoke(sender, e);
        }

        try { await _networkSyncClient.StartAsync(); }
        catch
        {
            StopNetworkSync();
            throw;
        }
    }

    /// <summary>
    /// Stops network synchronization (server or client).
    /// Returns the system to standalone mode.
    /// </summary>
    public static void StopNetworkSync()
    {
        lock (_networkSyncLock)
        {
            if (_networkSyncServer != null)
            {
                _networkSyncServer.Stop();
                _networkSyncServer.Dispose();
                _networkSyncServer = null;
            }

            if (_networkSyncClient != null)
            {
                _networkSyncClient.Stop();
                _networkSyncClient.Dispose();
                _networkSyncClient = null;
            }

            var mixer = GetRegisteredAudioMixer();
            if (mixer != null)
            {
                mixer.MasterClock.Mode = ClockMode.Realtime;
                mixer.MasterClock.IsNetworkControlled = false;
            }
        }
    }

    /// <summary>
    /// Gets the current network synchronization status.
    /// </summary>
    /// <returns>Network sync status information.</returns>
    public static NetworkSyncStatus GetNetworkSyncStatus()
    {
        lock (_networkSyncLock)
        {
            var status = new NetworkSyncStatus();

            if (_networkSyncServer != null)
            {
                status.IsEnabled = true;
                status.IsServer = true;
                status.ClientCount = _networkSyncServer.ClientCount;
            }
            else if (_networkSyncClient != null)
            {
                status.IsEnabled = true;
                status.IsClient = true;
                status.ConnectionState = _networkSyncClient.ConnectionState;
                status.AverageLatency = _networkSyncClient.AverageLatency * 1000.0;
                status.ServerLatency = _networkSyncClient.AverageLatency * 1000.0;
                status.SyncDrift = _networkSyncClient.LastError * 1000.0;
                status.IsLocalControlAllowed = _networkSyncClient.IsLocalControlAllowed;
                status.TimeSyncTier = _networkSyncClient.ConnectionState >= NetworkSyncProtocol.ConnectionState.Connected
                    ? LocalTimeProvider.TimeSyncTier.PeerToPeer
                    : LocalTimeProvider.TimeSyncTier.SystemTime;
            }

            return status;
        }
    }

    /// <summary>
    /// Gets whether local control is allowed (client disconnected or standalone).
    /// </summary>
    /// <returns>True if local control is allowed, false otherwise.</returns>
    public static bool IsNetworkSyncLocalControlAllowed()
    {
        lock (_networkSyncLock)
        {
            if (_networkSyncClient != null)
            {
                return _networkSyncClient.IsLocalControlAllowed;
            }

            // Server or standalone - always allowed
            return true;
        }
    }

    /// <summary>
    /// Broadcasts a command to all clients (server only).
    /// </summary>
    /// <param name="command">Command to broadcast.</param>
    /// <returns>True if enqueued successfully, false if queue is full.</returns>
    /// <exception cref="InvalidOperationException">Thrown if not running as server.</exception>
    public static bool BroadcastCommand(ref NetworkSyncProtocol.Command command)
    {
        lock (_networkSyncLock)
        {
            if (_networkSyncServer == null)
                throw new InvalidOperationException("Not running as network sync server.");

            return _networkSyncServer.EnqueueCommand(ref command);
        }
    }

    /// <summary>
    /// Gets the registered AudioMixer instance for NetworkSync operations.
    /// </summary>
    /// <returns>The registered AudioMixer instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no AudioMixer is registered.</exception>
    private static AudioMixer GetAudioMixer()
    {
        var mixer = OwnaudioNet.GetRegisteredAudioMixer();
        
        if (mixer == null)
        {
            throw new InvalidOperationException(
                "No AudioMixer is registered for NetworkSync. " +
                "Create an AudioMixer instance before starting NetworkSync, or use " +
                "OwnaudioNet.SetPrimaryAudioMixer() to explicitly set one.");
        }
        
        return mixer;
    }
}
