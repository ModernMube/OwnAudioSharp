using OwnaudioNET;
using OwnaudioNET.Sources;
using OwnaudioNET.Effects;
using OwnaudioNET.Mixing;

namespace OwnaudioInput
{
    /// <summary>
    /// Example demonstrating InputSource usage with real-time audio effects.
    /// This example captures audio from the microphone/line-in and applies
    /// Delay and Chorus effects in real-time before playback.
    /// </summary>
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== OwnAudio InputSource Example ===");
            Console.WriteLine("This example demonstrates real-time audio input with effects.");
            Console.WriteLine();

            // Initialize the OwnAudio engine
            OwnaudioNet.Initialize();

            if (!OwnaudioNet.IsInitialized)
            {
                Console.WriteLine("Failed to initialize OwnAudio engine!");
                return;
            }

            try
            {
                Console.WriteLine("OwnAudio engine initialized successfully.");
                Console.WriteLine($"Sample Rate: {OwnaudioNet.Engine!.Config.SampleRate} Hz");
                Console.WriteLine($"Channels: {OwnaudioNet.Engine!.Config.Channels}");
                Console.WriteLine($"Buffer Size: {OwnaudioNet.Engine!.Config.BufferSize} frames");
                Console.WriteLine();

                // Start the audio engine
                OwnaudioNet.Start();

                if (!OwnaudioNet.IsRunning)
                {
                    Console.WriteLine("Failed to start audio engine!");
                    return;
                }

                Console.WriteLine("Audio engine started successfully.");
                Console.WriteLine();

                // Create InputSource for microphone/line-in capture
                Console.WriteLine("Creating InputSource for audio input...");
                var inputSource = new InputSource(OwnaudioNet.Engine, bufferSizeInFrames: 8192);

                // Wrap the input source with effects support
                var sourceWithEffects = new SourceWithEffects(inputSource);

                // Create and add Delay effect
                Console.WriteLine("Adding Delay effect (Classic Echo preset)...");
                var delayEffect = new DelayEffect(DelayPreset.ClassicEcho);
                delayEffect.Enabled = true;
                sourceWithEffects.AddEffect(delayEffect);

                // Create and add Chorus effect
                Console.WriteLine("Adding Chorus effect (Guitar Classic preset)...");
                var chorusEffect = new ChorusEffect(ChorusPreset.GuitarClassic);
                chorusEffect.Enabled = true;
                sourceWithEffects.AddEffect(chorusEffect);

                Console.WriteLine();
                Console.WriteLine("Effects configured:");
                Console.WriteLine($"  1. {delayEffect}");
                Console.WriteLine($"  2. {chorusEffect}");
                Console.WriteLine();

                // Create mixer and add the source
                var Engine = OwnaudioNet.Engine!.UnderlyingEngine;

                var mixer = new AudioMixer(Engine);
                mixer.AddSource(sourceWithEffects);

                // Start playback (begin capturing and processing)
                sourceWithEffects.Play();

                Console.WriteLine("========================================");
                Console.WriteLine("Audio input is now active with effects!");
                Console.WriteLine("Speak or play into your microphone...");
                Console.WriteLine("========================================");
                Console.WriteLine();
                Console.WriteLine("Commands:");
                Console.WriteLine("  1 - Toggle Delay effect");
                Console.WriteLine("  2 - Toggle Chorus effect");
                Console.WriteLine("  3 - Change Delay preset");
                Console.WriteLine("  4 - Change Chorus preset");
                Console.WriteLine("  V - Adjust volume");
                Console.WriteLine("  I - Show info");
                Console.WriteLine("  Q - Quit");
                Console.WriteLine();

                bool running = true;
                while (running)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);

                        switch (key.KeyChar)
                        {
                            case '1':
                                delayEffect.Enabled = !delayEffect.Enabled;
                                Console.WriteLine($"Delay effect: {(delayEffect.Enabled ? "ENABLED" : "DISABLED")}");
                                break;

                            case '2':
                                chorusEffect.Enabled = !chorusEffect.Enabled;
                                Console.WriteLine($"Chorus effect: {(chorusEffect.Enabled ? "ENABLED" : "DISABLED")}");
                                break;

                            case '3':
                                ChangeDelayPreset(delayEffect);
                                break;

                            case '4':
                                ChangeChorusPreset(chorusEffect);
                                break;

                            case 'v':
                            case 'V':
                                AdjustVolume(sourceWithEffects);
                                break;

                            case 'i':
                            case 'I':
                                ShowInfo(sourceWithEffects, delayEffect, chorusEffect);
                                break;

                            case 'q':
                            case 'Q':
                                running = false;
                                Console.WriteLine("Shutting down...");
                                break;
                        }
                    }

                    await Task.Delay(100);
                }

                // Cleanup
                sourceWithEffects.Stop();
                mixer.RemoveSource(sourceWithEffects);
                sourceWithEffects.Dispose();
                mixer.Dispose();

                Console.WriteLine("Resources released.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            finally
            {
                OwnaudioNet.Shutdown();
                Console.WriteLine("OwnAudio engine shut down.");
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        static void ChangeDelayPreset(DelayEffect delay)
        {
            Console.WriteLine();
            Console.WriteLine("Select Delay preset:");
            Console.WriteLine("  1 - Default");
            Console.WriteLine("  2 - Slap Back");
            Console.WriteLine("  3 - Classic Echo");
            Console.WriteLine("  4 - Ambient");
            Console.WriteLine("  5 - Rhythmic");
            Console.WriteLine("  6 - Ping Pong");
            Console.WriteLine("  7 - Tape Echo");
            Console.WriteLine("  8 - Dub");
            Console.WriteLine("  9 - Thickening");
            Console.Write("Choice: ");

            var key = Console.ReadKey();
            Console.WriteLine();

            DelayPreset preset = key.KeyChar switch
            {
                '1' => DelayPreset.Default,
                '2' => DelayPreset.SlapBack,
                '3' => DelayPreset.ClassicEcho,
                '4' => DelayPreset.Ambient,
                '5' => DelayPreset.Rhythmic,
                '6' => DelayPreset.PingPong,
                '7' => DelayPreset.TapeEcho,
                '8' => DelayPreset.Dub,
                '9' => DelayPreset.Thickening,
                _ => DelayPreset.Default
            };

            delay.SetPreset(preset);
            Console.WriteLine($"Delay preset changed to: {preset}");
            Console.WriteLine($"Settings: {delay}");
            Console.WriteLine();
        }

        static void ChangeChorusPreset(ChorusEffect chorus)
        {
            Console.WriteLine();
            Console.WriteLine("Select Chorus preset:");
            Console.WriteLine("  1 - Default");
            Console.WriteLine("  2 - Vocal Subtle");
            Console.WriteLine("  3 - Vocal Lush");
            Console.WriteLine("  4 - Guitar Classic");
            Console.WriteLine("  5 - Guitar Shimmer");
            Console.WriteLine("  6 - Synth Pad");
            Console.WriteLine("  7 - String Ensemble");
            Console.WriteLine("  8 - Vintage Analog");
            Console.WriteLine("  9 - Extreme");
            Console.Write("Choice: ");

            var key = Console.ReadKey();
            Console.WriteLine();

            ChorusPreset preset = key.KeyChar switch
            {
                '1' => ChorusPreset.Default,
                '2' => ChorusPreset.VocalSubtle,
                '3' => ChorusPreset.VocalLush,
                '4' => ChorusPreset.GuitarClassic,
                '5' => ChorusPreset.GuitarShimmer,
                '6' => ChorusPreset.SynthPad,
                '7' => ChorusPreset.StringEnsemble,
                '8' => ChorusPreset.VintageAnalog,
                '9' => ChorusPreset.Extreme,
                _ => ChorusPreset.Default
            };

            chorus.SetPreset(preset);
            Console.WriteLine($"Chorus preset changed to: {preset}");
            Console.WriteLine($"Settings: {chorus}");
            Console.WriteLine();
        }

        static void AdjustVolume(SourceWithEffects source)
        {
            Console.WriteLine();
            Console.Write($"Current volume: {source.Volume:F2} - Enter new volume (0.0 - 2.0): ");
            var input = Console.ReadLine();

            if (float.TryParse(input, out float volume))
            {
                source.Volume = Math.Clamp(volume, 0.0f, 2.0f);
                Console.WriteLine($"Volume set to: {source.Volume:F2}");
            }
            else
            {
                Console.WriteLine("Invalid input!");
            }
            Console.WriteLine();
        }

        static void ShowInfo(SourceWithEffects source, DelayEffect delay, ChorusEffect chorus)
        {
            Console.WriteLine();
            Console.WriteLine("=== Current Status ===");
            Console.WriteLine($"Source State: {source.State}");
            Console.WriteLine($"Volume: {source.Volume:F2}");
            Console.WriteLine($"Position: {source.Position:F2} seconds");
            Console.WriteLine();
            Console.WriteLine($"Delay: {delay}");
            Console.WriteLine($"Chorus: {chorus}");
            Console.WriteLine();
        }
    }
}
