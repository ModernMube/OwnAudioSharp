using Ownaudio;
using Ownaudio.Fx;
using Ownaudio.Sources;
using System.Diagnostics;

namespace ChordDetect
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            if (OwnAudio.Initialize())
            {
                try
                {
                    SourceManager manager = SourceManager.Instance;

                    string audioFilePath = @"D:\Sogorock\Ocam\2025\Szepjulia\Szép Júlia - Beszkid József (cover)_audio.flac";

                    if (!File.Exists(audioFilePath))
                    {
                        Console.WriteLine($"Audio file not found: {audioFilePath}");
                        return;
                    }

                    bool loaded = await manager.AddOutputSource(audioFilePath);

                    if (!loaded)
                    {
                        Console.WriteLine("Failed to load audio file!");
                        return;
                    }

                    Console.WriteLine($"Audio loaded successfully!");
                    Console.WriteLine($"Duration: {manager.Duration}");
                    Console.WriteLine($"Sources: {manager.Sources.Count}");

                    var chordDetector = new RealtimeChordDetector(
                        sampleRate: SourceManager.OutputEngineOptions.SampleRate,
                        bufferDurationMs: 500,    // 2 másodperc elemzési puffer
                        detectionIntervalMs: 100,  // 500ms-ként új detektálás
                        minConfidence: 0.6f        // 60% minimum megbízhatóság
                    );

                    // Event feliratkozás
                    chordDetector.ChordDetected += (chord) =>
                    {
                        Debug.WriteLine($"Akkord: {chord.ChordName} ({chord.Confidence:P1})");
                        Debug.WriteLine($"Hangok: {string.Join(", ", chord.Notes)}");
                    };

                    manager.CustomSampleProcessor = chordDetector;
                    chordDetector.IsEnabled = true;

                    manager.Play();
                    Console.WriteLine("Play() called successfully");

                    Console.Clear();
                    Console.WriteLine("Hi! Ownaudio user");
                    Console.WriteLine($"Default output device: {OwnAudio.DefaultOutputDevice.Name}");
                    Console.WriteLine($"Audio duration: {manager.Duration}");
                    Console.WriteLine("Press any key to stop playback...");

                    Console.Read();

                    manager.Stop();
                    manager.Reset();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during playback: {ex.Message}");
                }
                finally
                {
                    OwnAudio.Free();
                }
            }
            else
            {
                Console.WriteLine("Library initialization failed!");

                if (!OwnAudio.IsFFmpegInitialized)
                {
                    Console.WriteLine("- FFmpeg initialization failed");
                }

                if (!OwnAudio.IsPortAudioInitialized)
                {
                    Console.WriteLine("- PortAudio initialization failed");
                }

                if (!OwnAudio.IsMiniAudioInitialized)
                {
                    Console.WriteLine("- MiniAudio initialization failed");
                }
            }

            Console.WriteLine("Press any key to exit...");
            Console.Read();
        }
    }
}
