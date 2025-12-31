using OwnaudioNET.NetworkSync;
using OwnaudioNET.Mixing;
using Ownaudio.Synchronization;

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
    /// Starts network synchronization in server mode.
    /// The server broadcasts timing information to all clients on the local network.
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

            // Set clock mode
            mixer.MasterClock.Mode = ClockMode.NetworkServer;
            mixer.MasterClock.IsNetworkControlled = false;

            // Create and start server
            _networkSyncServer = new NetworkSyncServer(mixer.MasterClock, port);
        }

        await _networkSyncServer.StartAsync();
    }

    /// <summary>
    /// Starts network synchronization in client mode.
    /// The client synchronizes with a server on the local network.
    /// </summary>
    /// <param name="serverAddress">Server IP address (null for auto-discovery).</param>
    /// <param name="port">UDP port to use (default: 9876).</param>
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

            // Set clock mode
            mixer.MasterClock.Mode = ClockMode.NetworkClient;
            mixer.MasterClock.IsNetworkControlled = true;

            // Create and start client
            _networkSyncClient = new NetworkSyncClient(
                mixer.MasterClock,
                serverAddress,
                port,
                allowOfflinePlayback);

            // Forward connection state changes
            _networkSyncClient.ConnectionStateChanged += (sender, e) =>
            {
                NetworkSyncConnectionChanged?.Invoke(sender, e);
            };
        }

        await _networkSyncClient.StartAsync();
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

            // Reset clock mode
            var mixer = GetAudioMixer();
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
                status.AverageLatency = _networkSyncClient.AverageLatency * 1000.0; // Convert to ms
                status.ServerLatency = _networkSyncClient.AverageLatency * 1000.0;
                status.IsLocalControlAllowed = _networkSyncClient.IsLocalControlAllowed;
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
    /// Gets the AudioMixer instance (internal helper).
    /// </summary>
    private static AudioMixer? GetAudioMixer()
    {
        // This assumes AudioMixer is accessible through the engine wrapper
        // You may need to adjust this based on your actual architecture
        // For now, return null - this will be implemented when integrating with AudioMixer
        return null;  // TODO: Implement AudioMixer access
    }
}
