using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using System.Reflection;

namespace OwnaudioNET.Test;

/// <summary>
/// Demonstration program for OwnaudioNET library.
/// Shows how to use AudioMixer to play an audio file with 80% volume.
/// </summary>
public class TestProgram
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== OwnaudioNET AudioMixer Demonstration ===\n");
        Console.WriteLine("This program demonstrates audio playback using the AudioMixer");
        Console.WriteLine("with a FileSource at 80% volume.\n");

        AudioMixer? mixer = null;
        FileSource? fileSource0 = null;
        FileSource? fileSource1 = null;
        FileSource? fileSource2 = null;
        FileSource? fileSource3 = null;

        try
        {
            // ==========================================
            // Step 1: Initialize Audio Engine
            // ==========================================
            Console.WriteLine("[1/6] Initializing audio engine...");

            // Use the standard OwnaudioNet API - it will automatically use NativeAudioEngine
            AudioConfig config = new AudioConfig()
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                HostType = EngineHostType.None
            };

            // Initialize via OwnaudioNet (uses AudioEngineFactory internally)
            // This will try NativeAudioEngine first, then fallback to platform-specific engines
            OwnaudioNet.Initialize(config);

            Console.WriteLine($"  ✓ Initialized: {OwnaudioNet.IsInitialized}");
            Console.WriteLine($"  ✓ Version: {OwnaudioNet.Version}");
            Console.WriteLine($"  ✓ Engine Wrapper: {OwnaudioNet.Engine?.GetType().Name}");
            Console.WriteLine($"  ✓ Underlying Engine: {OwnaudioNet.Engine?.UnderlyingEngine.GetType().Name}");
            Console.WriteLine($"  ✓ Sample Rate: {OwnaudioNet.Engine?.Config.SampleRate} Hz");
            Console.WriteLine($"  ✓ Channels: {OwnaudioNet.Engine?.Config.Channels}");
            Console.WriteLine($"  ✓ Buffer Size: {OwnaudioNet.Engine?.FramesPerBuffer} frames");
            Console.WriteLine($"  ✓ Expected Latency: {(OwnaudioNet.Engine?.FramesPerBuffer / (double)OwnaudioNet.Engine?.Config.SampleRate! * 1000):F2} ms");


            // Get current audio device information
            var outputDevices = OwnaudioNet.Engine?.UnderlyingEngine.GetOutputDevices();
            if (outputDevices != null && outputDevices.Count > 0)
            {
                // Find the current device (either by OutputDeviceId or the default device)
                AudioDeviceInfo? currentDevice = null;

                if (!string.IsNullOrEmpty(config.OutputDeviceId))
                {
                    currentDevice = outputDevices.FirstOrDefault(d => d.DeviceId == config.OutputDeviceId);
                }
                else
                {
                    currentDevice = outputDevices.FirstOrDefault(d => d.IsDefault);
                }

                if (currentDevice != null)
                {
                    Console.WriteLine($"  ✓ Audio Engine: {currentDevice.EngineName}");
                    Console.WriteLine($"  ✓ Output Device: {currentDevice.Name}");
                    Console.WriteLine($"  ✓ Max Output channels: {currentDevice.MaxOutputChannels}");
                    Console.WriteLine($"  ✓ Max Input channels: {currentDevice.MaxInputChannels}");
                }
            }

            // ==========================================
            // Step 2: Start Audio Engine
            // ==========================================
            Console.WriteLine("\n[2/6] Starting audio engine...");
            OwnaudioNet.Start();
            Console.WriteLine($"  ✓ Engine running: {OwnaudioNet.IsRunning}");

            // ==========================================
            // Step 3: Create Audio Mixer
            // ==========================================
            Console.WriteLine("\n[3/6] Creating audio mixer...");
            // IMPORTANT: Pass IAudioEngine directly, not AudioEngineWrapper
            // This removes the extra CircularBuffer and PumpThread layer
            var Engine = OwnaudioNet.Engine!.UnderlyingEngine;

            mixer = new AudioMixer(Engine, bufferSizeInFrames: 512);
            Console.WriteLine($"  ✓ Mixer created: {mixer.Config.ToString()}");
            Console.WriteLine($"  ✓ Buffer size: {mixer.Config.BufferSize} frames");

            // Set master volume to 80% (0.8)
            mixer.MasterVolume = 0.8f;
            Console.WriteLine($"  ✓ Master volume set to: {mixer.MasterVolume:P0}");

            mixer.SourceError += (sender, e) =>
            {
                Console.WriteLine($"  ! Source error: {e.Message}");
            };

            // ==========================================
            // Create mastering effects to the mixer
            // ==========================================
            Console.WriteLine("\n Adding mastering effects to the mixer...");

            var _equalizer = new Equalizer30BandEffect();
            // Sub-bass region (mély és telt alap, Low-Shelf szerű emeléssel)
            _equalizer.SetBandGain(band: 0, frequency: 20, q: 0.5f, gainDB: 0.2f);     // 20 Hz Deep sub-bass
            _equalizer.SetBandGain(band: 1, frequency: 25, q: 0.5f, gainDB: 0.4f);     // 25 Hz Sub-bass foundation
            _equalizer.SetBandGain(band: 2, frequency: 31, q: 0.6f, gainDB: 0.6f);     // 31 Hz Sub-bass body
            _equalizer.SetBandGain(band: 3, frequency: 40, q: 0.7f, gainDB: 0.8f);     // 40 Hz Sub-bass warmth
            _equalizer.SetBandGain(band: 4, frequency: 50, q: 0.7f, gainDB: 0.8f);     // 50 Hz Sub-bass emphasis (a popzene basszus alappillére)
            _equalizer.SetBandGain(band: 5, frequency: 63, q: 0.7f, gainDB: -0.3f);   // 63 Hz Low bass cleanup (finom vágás a túlzott boom elkerülésére)

            // Bass region (punch és teltség)
            _equalizer.SetBandGain(band: 6, frequency: 80, q: 0.8f, gainDB: 0.3f);     // 80 Hz Bass foundation (Kick punch)
            _equalizer.SetBandGain(band: 7, frequency: 100, q: 0.8f, gainDB: 0.5f);    // 100 Hz Bass body
            _equalizer.SetBandGain(band: 8, frequency: 125, q: 0.9f, gainDB: 0.3f);    // 125 Hz Upper bass punch
            _equalizer.SetBandGain(band: 9, frequency: 160, q: 0.9f, gainDB: 0.1f);    // 160 Hz Bass definition

            // Low-mid region (erősebb vágások a tisztaságért, a pop vokálok érdekében)
            _equalizer.SetBandGain(band: 10, frequency: 200, q: 1.0f, gainDB: -0.4f); // 200 Hz Low-mid mud cut
            _equalizer.SetBandGain(band: 11, frequency: 250, q: 1.0f, gainDB: -0.8f); // 250 Hz Mud removal (erősebb vágás)
            _equalizer.SetBandGain(band: 12, frequency: 315, q: 1.1f, gainDB: -0.7f); // 315 Hz Boxiness cut
            _equalizer.SetBandGain(band: 13, frequency: 400, q: 1.1f, gainDB: -0.5f); // 400 Hz Honkiness reduction
            _equalizer.SetBandGain(band: 14, frequency: 500, q: 1.0f, gainDB: -0.2f); // 500 Hz Nasal frequency cut

            // Mid region (neutrális/enyhe vágás a vokál tisztaságáért)
            _equalizer.SetBandGain(band: 15, frequency: 630, q: 1.0f, gainDB: -0.1f); // 630 Hz Mid clarity
            _equalizer.SetBandGain(band: 16, frequency: 800, q: 1.0f, gainDB: 0.0f); // 800 Hz Mid balance
            _equalizer.SetBandGain(band: 17, frequency: 1000, q: 1.0f, gainDB: 0.0f); // 1 kHz Vocal balance
            _equalizer.SetBandGain(band: 18, frequency: 1250, q: 1.0f, gainDB: 0.0f); // 1.25 kHz Upper mid balance
            _equalizer.SetBandGain(band: 19, frequency: 1600, q: 1.0f, gainDB: 0.1f); // 1.6 kHz Neutral reference

            // Upper-mid region (kiemelés a vokál jelenlétéért és a mix átvágásáért)
            _equalizer.SetBandGain(band: 20, frequency: 2000, q: 1.0f, gainDB: 0.3f); // 2 kHz Vocal presence
            _equalizer.SetBandGain(band: 21, frequency: 2500, q: 0.9f, gainDB: 0.5f);  // 2.5 kHz Clarity boost
            _equalizer.SetBandGain(band: 22, frequency: 3150, q: 0.9f, gainDB: 0.7f);  // 3.15 kHz Definition (ez a popzene egyik kulcsfrekvenciája)
            _equalizer.SetBandGain(band: 23, frequency: 4000, q: 0.8f, gainDB: 0.5f);  // 4 kHz Presence boost
            _equalizer.SetBandGain(band: 24, frequency: 5000, q: 0.7f, gainDB: 0.3f);  // 5 kHz Detail enhancement

            // High region (erős High-Shelf szerű emelés a "csillogásért" és "levegősségért")
            _equalizer.SetBandGain(band: 25, frequency: 6300, q: 0.7f, gainDB: 0.3f);  // 6.3 kHz Airiness
            _equalizer.SetBandGain(band: 26, frequency: 8000, q: 0.6f, gainDB: 0.6f);  // 8 kHz Sparkle
            _equalizer.SetBandGain(band: 27, frequency: 10000, q: 0.6f, gainDB: 0.8f); // 10 kHz Shimmer
            _equalizer.SetBandGain(band: 28, frequency: 12500, q: 0.5f, gainDB: 1.0f); // 12.5 kHz Brilliance (erősebb emelés)
            _equalizer.SetBandGain(band: 29, frequency: 16000, q: 0.5f, gainDB: 1.0f); // 16 kHz Air band (erősebb emelés)

            var _compressor = new CompressorEffect(CompressorPreset.Vintage);

            // ==========================================
            // Add master effects
            // ==========================================
            mixer.AddMasterEffect(_equalizer);
            mixer.AddMasterEffect(_compressor);
            mixer.AddMasterEffect(new DynamicAmpEffect(DynamicAmpPreset.Music));

            _equalizer.Enabled = false;
            _compressor.Enabled = false;


            // ==========================================
            // Step 4: Create Audio Source
            // ==========================================
            Console.WriteLine("\n[4/6] Creating audio source...");

            string? exePath = Assembly.GetExecutingAssembly().Location;
            string? exeDirectory = Path.GetDirectoryName(exePath);

            // Use REAL WAV decoder from Ownaudio.Core
            string audioFilePath0 = Path.Combine(exeDirectory!, "media", "drums.wav");
            string audioFilePath1 = Path.Combine(exeDirectory!, "media", "bass.wav");
            string audioFilePath2 = Path.Combine(exeDirectory!, "media", "other.wav");
            string audioFilePath3 = Path.Combine(exeDirectory!, "media", "vocals.wav");

            Console.WriteLine($"  Loading files: 1 - {audioFilePath0}, 2 - {audioFilePath1}, 3 - {audioFilePath2}, 4 - {audioFilePath3}");

            // Get engine format for resampling
            int targetSampleRate = OwnaudioNet.Engine!.Config.SampleRate;
            int targetChannels = OwnaudioNet.Engine!.Config.Channels;

            fileSource0 = new FileSource(audioFilePath0, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);
            fileSource1 = new FileSource(audioFilePath1, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);
            fileSource2 = new FileSource(audioFilePath2, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);
            fileSource3 = new FileSource(audioFilePath3, 8192, targetSampleRate: targetSampleRate, targetChannels: targetChannels);

            // Set source volume to 100% (mixer already set to 80%)
            fileSource0.PitchShift = 0.0f;
            fileSource1.PitchShift = 0.0f;
            fileSource2.PitchShift = 0.0f;
            fileSource3.PitchShift = 0.0f;

            fileSource0.Volume = 1.0f;
            fileSource1.Volume = 1.0f;
            fileSource2.Volume = 1.0f;
            fileSource3.Volume = 1.0f;

            Console.WriteLine($"  ✓ File source created");
            Console.WriteLine($"  ✓ Format: {fileSource0.Config.ToString()}");
            Console.WriteLine($"  ✓ Duration: {fileSource0.Duration:F2} seconds");
            Console.WriteLine($"  ✓ Source volume: {fileSource0.Volume:P0}");
            Console.WriteLine($"  ✓ Source sample rate: {fileSource0.Config.SampleRate} Hz (expected: {targetSampleRate} Hz)");
            Console.WriteLine($"  ✓ Source channels: {fileSource0.Config.Channels} (expected: {targetChannels})");

            // Verify format match
            if (fileSource0.Config.SampleRate != targetSampleRate || fileSource0.Config.Channels != targetChannels)
            {
                Console.WriteLine($"  ! WARNING: Source format mismatch! This may cause playback issues.");
            }

            // ==========================================
            // Step 5: Start Mixer and Add Source
            // ==========================================
            Console.WriteLine("\n[5/6] Starting mixer and adding source...");

            // ==========================================
            // Add effect vocal track
            // ==========================================

            // 1. Compressor - dynamic control
            CompressorEffect compressor = new CompressorEffect(
                threshold: 0.4f,      // 40% threshold
                ratio: 3.0f,          // 3:1 compression
                attackTime: 5f,       // Fast attack
                releaseTime: 150f,    // Smooth release
                makeupGain: 1.5f      // +50% makeup gain
            );

            // 2. Delay - depth and space
            DelayEffect delay = new DelayEffect(
                time: 375,            // 375ms (eighth note at 120 BPM)
                repeat: 0.25f,        // Subtle feedback
                mix: 0.15f,           // Low in mix
                damping: 0.4f         // Warm repeats
            );

            // 3. Reverb - ambience
            ReverbEffect reverb = new ReverbEffect(
                size: 0.5f,           // Medium room
                damp: 0.6f,           // Natural damping
                wet: 0.25f,           // Reverb level
                dry: 0.75f,           // Dry signal
                stereoWidth: 0.8f,    // Wide stereo
                gainLevel: 0.015f,    // Standard gain
                mix: 0.25f            // 25% wet mix
            );

            var fileSource3Effect = new SourceWithEffects(fileSource3);
            fileSource3Effect.AddEffect(compressor);
            fileSource3Effect.AddEffect(delay);
            fileSource3Effect.AddEffect(reverb);

            // Add source to mixer (will automatically start because mixer is running)
            mixer.AddSource(fileSource0);
            mixer.AddSource(fileSource1);
            mixer.AddSource(fileSource2);
            mixer.AddSource(fileSource3Effect);

            // ==========================================
            // NEW MASTER CLOCK ARCHITECTURE (v2.1.0+)
            // ==========================================
            // Attach sources to Master Clock for sample-accurate synchronization
            fileSource0.AttachToClock(mixer.MasterClock);
            fileSource1.AttachToClock(mixer.MasterClock);
            fileSource2.AttachToClock(mixer.MasterClock);
            fileSource3.AttachToClock(mixer.MasterClock);

            // Optional: Set timeline positions (all start at 0.0 by default)
            fileSource0.StartOffset = 0.0;  // Drums start immediately
            fileSource1.StartOffset = 0.0;  // Bass start immediately
            fileSource2.StartOffset = 0.0;  // Other start immediately
            fileSource3.StartOffset = 0.0;  // Vocals start immediately

            // Master Clock Features:
            // - Timeline-based synchronization (timestamp in seconds)
            // - Sample-accurate precision (not just frame-accurate)
            // - Automatic drift correction (<10ms tolerance, ~480 samples @ 48kHz)
            // - Start offsets for DAW-style track positioning
            // - Realtime/Offline rendering modes
            // - Tempo-independent master timeline

            Console.WriteLine($"  ✓ Sources added to mixer");
            Console.WriteLine($"  ✓ Sources attached to Master Clock");
            Console.WriteLine($"  ✓ Active sources: {mixer.SourceCount}");
            Console.WriteLine($"  ✓ Master Clock mode: {mixer.MasterClock.Mode}");
            Console.WriteLine($"  ✓ File source state: {fileSource0.State}");

            // Subscribe to dropout events for monitoring
            mixer.TrackDropout += (sender, e) =>
            {
                Console.WriteLine($"\n  ! Track dropout: {e.TrackName}");
                Console.WriteLine($"    At time: {e.MasterTimestamp:F3}s");
                Console.WriteLine($"    Missed frames: {e.MissedFrames}");
                Console.WriteLine($"    Reason: {e.Reason}");
            };

            mixer.Start();

            // Start all sources for playback
            // With Master Clock, sources must be explicitly started
            fileSource0.Play();
            fileSource1.Play();
            fileSource2.Play();
            fileSource3.Play();

            // IMPORTANT: Start the mixer to begin playback
            // All attached sources will play in perfect sync with the Master Clock
            Console.WriteLine($"  ✓ Mixer started: {mixer.IsRunning}");
            Console.WriteLine($"  ✓ All sources playing");

            // ==========================================
            // Step 6: Playback Progress Display
            // ==========================================
            Console.WriteLine("\n[6/6] Playing audio...");
            Console.WriteLine("VOCAL effects: compressor -> delay -> reverb");
            Console.WriteLine("MASTER effects (from 30 seconds): equalizer -> compressor -> dynamicamp\n");
            Console.WriteLine("Press any key to stop playback early.\n");

            // Display playback progress
            DateTime startTime = DateTime.Now;
            bool userCancelled = false;
            int statusLine = -1;

            // Try to get cursor position (may fail in timeout/redirect scenarios)
            try
            {
                statusLine = Console.CursorTop;
            }
            catch (IOException)
            {
                // Console not available - will use line-by-line output instead
            }

            while (fileSource0.State == AudioState.Playing && !userCancelled)
            {
                // Update progress every 100ms
                Thread.Sleep(100);

                // Get position from Master Clock (timeline timestamp)
                double masterTimestamp = mixer.MasterClock.CurrentTimestamp;
                long masterSamplePosition = mixer.MasterClock.CurrentSamplePosition;

                double position = fileSource0.Position;
                double duration = fileSource0.Duration;
                int progressPercent = (int)((position / duration) * 100);
                int barWidth = 40;
                int filledWidth = (int)((position / duration) * barWidth);

                // Create progress bar
                string progressBar = new string('█', filledWidth) +
                                   new string('░', barWidth - filledWidth);

                if (statusLine != -1)
                {
                    try
                    {
                        Console.SetCursorPosition(0, statusLine);

                        int width = Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80;
                        Console.Write(new string(' ', width));
                        Console.SetCursorPosition(0, statusLine);
                    }
                    catch (InvalidOperationException)
                    {
                        statusLine = -1;
                    }
                }

                string infoLine = $"  Position: {new TimeSpan(0, 0, (int)position).ToString()} / {new TimeSpan(0, 0, (int)duration).ToString()}s  [{progressBar}] {progressPercent}%  ";
                string peakLine = $"| Peaks: L={mixer.LeftPeak:F2} R={mixer.RightPeak:F2}  ";
                string clockLine = $"| MClock: {masterTimestamp:F2}s  ";

                Console.Write(infoLine + peakLine + clockLine);

                if (statusLine == -1)
                {
                    Console.WriteLine();
                }

                // ================================================
                // At the 30th second we turn on the master effects
                // ================================================
                if (position > 30 && position < 35)
                {
                    _equalizer.Enabled = true;
                    _compressor.Enabled = true;
                }

                // Check for key press (safe check for console availability)
                try
                {
                    if (Console.KeyAvailable)
                    {
                        userCancelled = true;
                        Console.ReadKey(true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Console not available (redirected or in CI environment) - ignore
                }
            }

            Console.WriteLine("\n\n  ✓ Playback completed!");
            TimeSpan elapsed = DateTime.Now - startTime;
            double finalPosition = fileSource0.Position;
            Console.WriteLine($"  ✓ Real-time elapsed: {elapsed.TotalSeconds:F2} seconds");
            Console.WriteLine($"  ✓ Audio position reached: {finalPosition:F2} seconds");

            // Calculate tempo accuracy
            double tempoRatio = finalPosition / elapsed.TotalSeconds;
            double tempoError = (tempoRatio - 1.0) * 100.0;
            Console.WriteLine($"  ✓ Tempo ratio: {tempoRatio:F4} (1.0000 = perfect)");
            if (Math.Abs(tempoError) < 0.5)
            {
                Console.WriteLine($"  ✓ Tempo accuracy: EXCELLENT ({tempoError:+0.00;-0.00}%)");
            }
            else if (Math.Abs(tempoError) < 2.0)
            {
                Console.WriteLine($"  ⚠ Tempo accuracy: Good ({tempoError:+0.00;-0.00}%)");
            }
            else
            {
                Console.WriteLine($"  ✗ Tempo accuracy: POOR ({tempoError:+0.00;-0.00}%)");
            }

            // Print PulseAudio timing diagnostics if available
            if (Engine is Ownaudio.Linux.PulseAudioEngine pulseEngine)
            {
                pulseEngine.PrintTimingStatistics();
            }

            // ==========================================
            // Display Final Statistics
            // ==========================================
            Console.WriteLine("\n=== FINAL STATISTICS ===");
            Console.WriteLine($"  Total mixed frames: {mixer.TotalMixedFrames}");
            Console.WriteLine($"  Total underruns: {mixer.TotalUnderruns}");
            Console.WriteLine($"  Master volume: {mixer.MasterVolume:P0}");
            Console.WriteLine($"  Source state: {fileSource0.State}");
            Console.WriteLine($"  Final position: {fileSource0.Position:F2}s / {fileSource0.Duration:F2}s");
            Console.WriteLine($"  Master Clock timestamp: {mixer.MasterClock.CurrentTimestamp:F2}s");
            Console.WriteLine($"  Master Clock sample position: {mixer.MasterClock.CurrentSamplePosition}");

            // ==========================================
            // Cleanup
            // ==========================================
            Console.WriteLine("\n=== CLEANUP ===");

            Console.WriteLine("  Stopping mixer...");
            mixer.Stop();
            // Note: No need to stop sync group - Master Clock handles cleanup automatically

            Console.WriteLine("  Disposing mixer...");
            mixer.Dispose();

            Console.WriteLine("  Disposing source...");
            fileSource0.Dispose();
            fileSource1.Dispose();
            fileSource2.Dispose();
            fileSource3.Dispose();

            Console.WriteLine("  Stopping engine...");
            OwnaudioNet.Stop();

            Console.WriteLine("  Shutting down...");
            OwnaudioNet.Shutdown();

            Console.WriteLine("\n=== DEMONSTRATION COMPLETED SUCCESSFULLY ===");

            try
            {
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("Exiting...");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n\n✗ ERROR: {ex.GetType().Name}");
            Console.WriteLine($"  Message: {ex.Message}");
            Console.WriteLine($"  StackTrace:\n{ex.StackTrace}");

            // Cleanup on error
            try
            {
                fileSource0?.Dispose();
                fileSource1?.Dispose();
                fileSource2?.Dispose();
                fileSource3?.Dispose();
                mixer?.Dispose();
                OwnaudioNet.Shutdown();
            }
            catch
            {
                // Ignore cleanup errors
            }

            Environment.Exit(1);
        }
    }
}
