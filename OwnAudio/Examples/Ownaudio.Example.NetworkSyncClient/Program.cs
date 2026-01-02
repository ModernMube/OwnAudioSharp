using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.NetworkSync;
using OwnaudioNET.Sources;
using System.Reflection;
using static OwnaudioNET.NetworkSync.NetworkSyncProtocol;

namespace Ownaudio.Example.NetworkSyncClient;

/// <summary>
/// NetworkSync Client demonstration program.
/// Shows how to connect to a NetworkSync server and play synchronized audio.
/// </summary>
public class ClientProgram
{
    private static AudioMixer? _mixer;
    private static FileSource? _drums, _bass, _other, _vocals;
    private static ConnectionState _connectionState = ConnectionState.Disconnected;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== OwnaudioNET NetworkSync Client ===\n");
        Console.WriteLine("This client synchronizes audio playback with a NetworkSync server.\n");

        try
        {
            // Initialize audio engine
            Console.WriteLine("[1/5] Initializing audio engine...");
            AudioConfig config = new AudioConfig
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                HostType = EngineHostType.None
            };

            OwnaudioNet.Initialize(config);
            OwnaudioNet.Start();
            Console.WriteLine($"  ✓ Engine initialized and started");

            // Create AudioMixer (automatically registers for NetworkSync)
            Console.WriteLine("\n[2/5] Creating AudioMixer...");
            var engine = OwnaudioNet.Engine!.UnderlyingEngine;
            _mixer = new AudioMixer(engine, bufferSizeInFrames: 512);
            _mixer.MasterVolume = 0.8f;
            Console.WriteLine($"  ✓ AudioMixer created (ID: {_mixer.MixerId})");

            // Load audio files
            Console.WriteLine("\n[3/5] Loading audio files...");
            string? exePath = Assembly.GetExecutingAssembly().Location;
            string? exeDirectory = Path.GetDirectoryName(exePath);

            string drumsPath = Path.Combine(exeDirectory!, "media", "drums.wav");
            string bassPath = Path.Combine(exeDirectory!, "media", "bass.wav");
            string otherPath = Path.Combine(exeDirectory!, "media", "other.wav");
            string vocalsPath = Path.Combine(exeDirectory!, "media", "vocals.wav");

            int targetSampleRate = OwnaudioNet.Engine!.Config.SampleRate;
            int targetChannels = OwnaudioNet.Engine!.Config.Channels;

            _drums = new FileSource(drumsPath, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);
            _bass = new FileSource(bassPath, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);
            _other = new FileSource(otherPath, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);
            _vocals = new FileSource(vocalsPath, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);

            Console.WriteLine($"  ✓ Loaded 4 tracks ({_drums.Duration:F1}s duration)");

            // Add sources to mixer and attach to MasterClock
            _mixer.AddSource(_drums);
            _mixer.AddSource(_bass);
            _mixer.AddSource(_other);
            _mixer.AddSource(_vocals);

            _drums.AttachToClock(_mixer.MasterClock);
            _bass.AttachToClock(_mixer.MasterClock);
            _other.AttachToClock(_mixer.MasterClock);
            _vocals.AttachToClock(_mixer.MasterClock);

            _mixer.Start();
            Console.WriteLine($"  ✓ Sources synchronized with MasterClock");

            // Subscribe to connection state changes
            OwnaudioNet.NetworkSyncConnectionChanged += OnConnectionStateChanged;

            // Start NetworkSync client (auto-discovery)
            Console.WriteLine("\n[4/5] Connecting to NetworkSync server...");
            await OwnaudioNet.StartNetworkSyncClientAsync(
                serverAddress: null,  // Auto-discovery
                port: 9876,
                allowOfflinePlayback: true);
            Console.WriteLine($"  ✓ NetworkSync client started (auto-discovery mode)");
            Console.WriteLine($"  ✓ Searching for server on local network...");

            // Start playback (will sync with server when connected)
            _drums.Play();
            _bass.Play();
            _other.Play();
            _vocals.Play();

            // Interactive status display
            Console.WriteLine("\n[5/5] Client ready!\n");
            Console.WriteLine("Available commands:");
            Console.WriteLine("  status       - Show connection status");
            Console.WriteLine("  quit         - Exit client\n");

            // Status update loop
            bool running = true;
            DateTime lastUpdate = DateTime.Now;

            while (running)
            {
                // Update status every second
                if ((DateTime.Now - lastUpdate).TotalSeconds >= 1.0)
                {
                    ShowQuickStatus();
                    lastUpdate = DateTime.Now;
                }

                // Check for user input (non-blocking)
                if (Console.KeyAvailable)
                {
                    string? input = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        string command = input.Trim().ToLower();

                        switch (command)
                        {
                            case "status":
                                ShowDetailedStatus();
                                break;

                            case "quit":
                            case "exit":
                                running = false;
                                break;

                            default:
                                Console.WriteLine($"  ! Unknown command: {command}");
                                break;
                        }
                    }
                }

                Thread.Sleep(100);
            }

            // Cleanup
            Console.WriteLine("\n=== SHUTDOWN ===");
            Console.WriteLine("  Stopping NetworkSync...");
            OwnaudioNet.StopNetworkSync();

            Console.WriteLine("  Stopping mixer...");
            _mixer.Stop();

            Console.WriteLine("  Disposing resources...");
            _drums?.Dispose();
            _bass?.Dispose();
            _other?.Dispose();
            _vocals?.Dispose();
            _mixer?.Dispose();

            Console.WriteLine("  Stopping engine...");
            OwnaudioNet.Stop();
            OwnaudioNet.Shutdown();

            Console.WriteLine("\n=== CLIENT STOPPED ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ ERROR: {ex.GetType().Name}");
            Console.WriteLine($"  Message: {ex.Message}");
            Console.WriteLine($"  StackTrace:\n{ex.StackTrace}");

            // Cleanup on error
            try
            {
                OwnaudioNet.StopNetworkSync();
                _drums?.Dispose();
                _bass?.Dispose();
                _other?.Dispose();
                _vocals?.Dispose();
                _mixer?.Dispose();
                OwnaudioNet.Shutdown();
            }
            catch { }

            Environment.Exit(1);
        }
    }

    private static void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _connectionState = e.NewState;
        
        string stateText = e.NewState switch
        {
            ConnectionState.Connected => "CONNECTED",
            ConnectionState.Disconnected => "DISCONNECTED",
            ConnectionState.Connecting => "CONNECTING",
            _ => "UNKNOWN"
        };

        Console.WriteLine($"\n  >>> Connection state: {stateText}");
        
        if (e.NewState == ConnectionState.Connected)
        {
            Console.WriteLine($"  >>> Synchronized with server");
        }
        else if (e.NewState == ConnectionState.Disconnected)
        {
            Console.WriteLine($"  >>> Offline playback mode");
        }
    }

    private static void ShowQuickStatus()
    {
        var syncStatus = OwnaudioNet.GetNetworkSyncStatus();
        string stateIcon = _connectionState == ConnectionState.Connected ? "●" : "○";
        string stateText = _connectionState.ToString();

        Console.Write($"\r  {stateIcon} {stateText,-15} | ");
        Console.Write($"Position: {_drums?.Position:F1}s / {_drums?.Duration:F1}s | ");
        Console.Write($"MasterClock: {_mixer?.MasterClock.CurrentTimestamp:F2}s | ");
        
        if (_connectionState == ConnectionState.Connected)
        {
            Console.Write($"Latency: {syncStatus.AverageLatency:F1}ms");
        }
        else
        {
            Console.Write($"Offline mode    ");
        }
    }

    private static void ShowDetailedStatus()
    {
        var syncStatus = OwnaudioNet.GetNetworkSyncStatus();
        
        Console.WriteLine("\n  === CLIENT STATUS ===");
        Console.WriteLine($"  Connection: {_connectionState}");
        Console.WriteLine($"  NetworkSync: {(syncStatus.IsEnabled ? "Enabled" : "Disabled")}");
        
        if (_connectionState == ConnectionState.Connected)
        {
            Console.WriteLine($"  Server latency: {syncStatus.AverageLatency:F1}ms");
            Console.WriteLine($"  Local control: {(syncStatus.IsLocalControlAllowed ? "Allowed" : "Server controlled")}");
        }
        
        Console.WriteLine($"  Master Clock: {_mixer?.MasterClock.CurrentTimestamp:F2}s");
        Console.WriteLine($"  Track position: {_drums?.Position:F2}s / {_drums?.Duration:F2}s");
        Console.WriteLine($"  Master volume: {_mixer?.MasterVolume:P0}");
        Console.WriteLine($"  Peaks: L={_mixer?.LeftPeak:F2} R={_mixer?.RightPeak:F2}\n");
    }
}
