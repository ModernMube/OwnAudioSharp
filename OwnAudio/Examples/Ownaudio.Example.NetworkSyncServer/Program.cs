using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Core;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using System.Reflection;

namespace Ownaudio.Example.NetworkSyncServer;

/// <summary>
/// NetworkSync Server demonstration program.
/// Shows how to use NetworkSync to broadcast synchronized audio playback to multiple clients.
/// </summary>
public class ServerProgram
{
    private static AudioMixer? _mixer;
    private static FileSource? _drums, _bass, _other, _vocals;
    private static bool _isPlaying = false;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== OwnaudioNET NetworkSync Server ===\n");
        Console.WriteLine("This server broadcasts synchronized audio playback to network clients.\n");

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
            Console.WriteLine($"  ✓ Sample Rate: {OwnaudioNet.Engine?.Config.SampleRate} Hz");

            // Create AudioMixer (automatically registers for NetworkSync)
            Console.WriteLine("\n[2/5] Creating AudioMixer...");
            var engine = OwnaudioNet.Engine!.UnderlyingEngine;
            _mixer = new AudioMixer(engine, bufferSizeInFrames: 512);
            _mixer.MasterVolume = 0.8f;
            Console.WriteLine($"  ✓ AudioMixer created (ID: {_mixer.MixerId})");
            Console.WriteLine($"  ✓ Automatically registered for NetworkSync");

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

            _drums.Volume = 1.0f;
            _bass.Volume = 1.0f;
            _other.Volume = 1.0f;
            _vocals.Volume = 1.0f;

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
            Console.WriteLine($"  ✓ Sources added and synchronized with MasterClock");

            // Start NetworkSync server
            Console.WriteLine("\n[4/5] Starting NetworkSync server...");
            await OwnaudioNet.StartNetworkSyncServerAsync(port: 9876, useLocalTimeOnly: true);
            Console.WriteLine($"  ✓ NetworkSync server started on port 9876");
            Console.WriteLine($"  ✓ Broadcasting to local network");

            // Interactive command loop
            Console.WriteLine("\n[5/5] Server ready!\n");
            Console.WriteLine("Available commands:");
            Console.WriteLine("  play         - Start playback");
            Console.WriteLine("  pause        - Pause playback");
            Console.WriteLine("  stop         - Stop playback");
            Console.WriteLine("  seek <sec>   - Seek to position (seconds)");
            Console.WriteLine("  status       - Show server status");
            Console.WriteLine("  quit         - Exit server\n");

            bool running = true;
            while (running)
            {
                Console.Write("> ");
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string command = parts[0].ToLower();

                switch (command)
                {
                    case "play":
                        PlayTracks();
                        break;

                    case "pause":
                        PauseTracks();
                        break;

                    case "stop":
                        StopTracks();
                        break;

                    case "seek":
                        if (parts.Length > 1 && double.TryParse(parts[1], out double position))
                        {
                            SeekTracks(position);
                        }
                        else
                        {
                            Console.WriteLine("  ! Usage: seek <seconds>");
                        }
                        break;

                    case "status":
                        ShowStatus();
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

            Console.WriteLine("\n=== SERVER STOPPED ===");
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

    private static void PlayTracks()
    {
        if (_isPlaying)
        {
            Console.WriteLine("  ! Already playing");
            return;
        }

        _drums?.Play();
        _bass?.Play();
        _other?.Play();
        _vocals?.Play();
        _isPlaying = true;

        Console.WriteLine($"  ✓ Playback started at {_mixer?.MasterClock.CurrentTimestamp:F2}s");
    }

    private static void PauseTracks()
    {
        if (!_isPlaying)
        {
            Console.WriteLine("  ! Not playing");
            return;
        }

        _drums?.Pause();
        _bass?.Pause();
        _other?.Pause();
        _vocals?.Pause();
        _isPlaying = false;

        Console.WriteLine($"  ✓ Playback paused at {_mixer?.MasterClock.CurrentTimestamp:F2}s");
    }

    private static void StopTracks()
    {
        _drums?.Stop();
        _bass?.Stop();
        _other?.Stop();
        _vocals?.Stop();
        _isPlaying = false;

        Console.WriteLine($"  ✓ Playback stopped");
    }

    private static void SeekTracks(double position)
    {
        _drums?.Seek(position);
        _bass?.Seek(position);
        _other?.Seek(position);
        _vocals?.Seek(position);

        Console.WriteLine($"  ✓ Seeked to {position:F2}s");
    }

    private static void ShowStatus()
    {
        var syncStatus = OwnaudioNet.GetNetworkSyncStatus();
        
        Console.WriteLine("\n  === SERVER STATUS ===");
        Console.WriteLine($"  NetworkSync: {(syncStatus.IsEnabled ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Connected clients: {syncStatus.ClientCount}");
        Console.WriteLine($"  Playback state: {(_isPlaying ? "Playing" : "Stopped/Paused")}");
        Console.WriteLine($"  Master Clock: {_mixer?.MasterClock.CurrentTimestamp:F2}s");
        Console.WriteLine($"  Track position: {_drums?.Position:F2}s / {_drums?.Duration:F2}s");
        Console.WriteLine($"  Master volume: {_mixer?.MasterVolume:P0}");
        Console.WriteLine($"  Peaks: L={_mixer?.LeftPeak:F2} R={_mixer?.RightPeak:F2}\n");
    }
}
