using Logger;
using Ownaudio.Core;
using OwnaudioNET.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Interfaces;
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
        Log.Info("=== OwnaudioNET AudioMixer Demonstration ===\n");
        Log.Info("This program demonstrates audio playback using the AudioMixer");
        Log.Info("with a FileSource at 80% volume.\n");

        AudioMixer? mixer = null;
        FileSource? fileSource0 = null;
        FileSource? fileSource1 = null;
        FileSource? fileSource2 = null;
        FileSource? fileSource3 = null;

        try
        {
            // Step 1: Initialize Audio Engine
            Log.Info("[1/6] Initializing audio engine...");

            // Use the standard OwnaudioNet API - it uses the Rust-backed engine (cpal)
            AudioConfig config = new AudioConfig()
            {
                SampleRate = 48000,
                Channels = 2,
                BufferSize = 512,
                HostType = EngineHostType.None
            };

            // Initialize via OwnaudioNet (uses AudioEngineFactory internally)
            OwnaudioNet.Initialize(config);

            Log.Info($"  ✓ Initialized: {OwnaudioNet.IsInitialized}");
            Log.Info($"  ✓ Version: {OwnaudioNet.Version}");
            Log.Info($"  ✓ Engine Wrapper: {OwnaudioNet.Engine?.GetType().Name}");
            Log.Info($"  ✓ Underlying Engine: {OwnaudioNet.Engine?.UnderlyingEngine.GetType().Name}");
            Log.Info($"  ✓ Sample Rate: {OwnaudioNet.Engine?.Config.SampleRate} Hz");
            Console.WriteLine($"  ✓ Channels: {OwnaudioNet.Engine?.Config.Channels}");
            Console.WriteLine($"  ✓ Buffer Size: {OwnaudioNet.Engine?.FramesPerBuffer} frames");
            Console.WriteLine($"  ✓ Expected Latency: {(OwnaudioNet.Engine?.FramesPerBuffer / (double)OwnaudioNet.Engine?.Config.SampleRate! * 1000):F2} ms");


            // Get current audio device information
            var outputDevices = OwnaudioNet.Engine?.UnderlyingEngine.GetOutputDevices();
            if (outputDevices != null && outputDevices.Count > 0)
            {
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
            
            // Step 2: Start Audio Engine
            Console.WriteLine("\n[2/6] Starting audio engine...");
            OwnaudioNet.Start();
            Console.WriteLine($"  ✓ Engine running: {OwnaudioNet.IsRunning}");

            // Step 3: Create Audio Mixer
            Console.WriteLine("\n[3/6] Creating audio mixer...");
            
            var Engine = OwnaudioNet.Engine!.UnderlyingEngine;

            mixer = new AudioMixer(Engine, bufferSizeInFrames: 512);
            Console.WriteLine($"  ✓ Mixer created: {mixer.Config.ToString()}");
            Console.WriteLine($"  ✓ Buffer size: {mixer.Config.BufferSize} frames");

            // Output fader sits after the limiter, so this is the final trim
            mixer.MasterVolume = 0.9f;
            Console.WriteLine($"  ✓ Master volume set to: {mixer.MasterVolume:P0}");

            mixer.SourceError += (sender, e) =>
            {
                Console.WriteLine($"  ! Source error: {e.Message}");
            };

            // Create mastering effects to the mixer
            Console.WriteLine("\n Adding mastering effects to the mixer...");

            //Master curve in dB, one value per ISO band from 20 Hz up to 16 kHz.
            //Nothing gets boosted under 100 Hz, that is what was breaking up in the limiter.
            float[] _masterCurve =
            {
                -4.0f, -3.0f, -2.0f, -1.0f, -0.4f,  0.0f,  0.0f, -0.2f, -0.3f, -0.4f,
                -0.6f, -0.9f, -0.8f, -0.5f, -0.3f, -0.1f,  0.0f,  0.0f,  0.0f,  0.1f,
                 0.3f,  0.4f,  0.5f,  0.4f,  0.3f,  0.3f,  0.5f,  0.7f,  0.8f,  0.6f
            };

            var _masterEq = new Equalizer30BandEffect(config.SampleRate, _masterCurve);

            //Glue comp, under 2:1 and barely a dB of gain reduction. Long release, a fast one
            //rides the bass waveform and that is what turns into distortion.
            var _glue = new CompressorEffect(
                threshold: 0.70f,
                ratio: 1.8f,
                attackTime: 30f,
                releaseTime: 400f,
                makeupGain: 1.06f,
                sampleRate: config.SampleRate);

            // Just a hint of sheen on top
            var _exciter = new EnhancerEffect(mix: 0.07f, cutFreq: 5000f, gain: 1.6f, sampleRate: config.SampleRate);

            //Peak safety at -0.5 dBFS. Slow release and a long look-ahead so it doesn't chew on the low end
            var _limiter = new LimiterEffect(config.SampleRate, threshold: -2.0f, ceiling: -0.5f, release: 180f, lookAheadMs: 10f);

            mixer.AddMasterEffect(_masterEq);
            mixer.AddMasterEffect(_glue);
            mixer.AddMasterEffect(_exciter);
            mixer.AddMasterEffect(_limiter);

            _masterEq.Enabled = false;
            _glue.Enabled = false;
            _exciter.Enabled = false;


            // Step 4: Create Audio Source
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

            fileSource0.PitchShift = 0.0f;
            fileSource1.PitchShift = 0.0f;
            fileSource2.PitchShift = 0.0f;
            fileSource3.PitchShift = 0.0f;

            //Stem balance: drums are the reference, the instruments get tucked so the
            //vocal owns the middle. Nothing on the vocal bus changes its level any more.
            fileSource0.Volume = 0.55f;
            fileSource1.Volume = 0.52f;
            fileSource2.Volume = 0.65f;
            fileSource3.Volume = 0.98f;

            // Lead vocal belongs dead center, the stems keep their own stereo width
            fileSource0.Pan = 0.0f;
            fileSource1.Pan = 0.0f;
            fileSource2.Pan = 0.0f;
            fileSource3.Pan = 0.0f;

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

            // Step 5: Start Mixer and Add Source
            Console.WriteLine("\n[5/6] Starting mixer and adding source...");

            //DRUM BUS - 4:1 with an attack slow enough to let the kick and snare snap through,
            //then a little top end back on the cymbals.
            var _drumComp = new CompressorEffect(
                threshold: 0.60f,
                ratio: 3.5f,
                attackTime: 14f,
                releaseTime: 110f,
                makeupGain: 1.10f,
                sampleRate: targetSampleRate);

            var _drumAir = new EnhancerEffect(mix: 0.10f, cutFreq: 6000f, gain: 1.5f, sampleRate: targetSampleRate);

            var drumBus = new SourceWithEffects(fileSource0);
            drumBus.AddEffect(_drumComp);
            drumBus.AddEffect(_drumAir);

            //BASS BUS - gentle ratio and a long release. Nothing here adds level any more,
            //the previous +3.5 dB of makeup was driving the master into breakup.
            //EQ bands are 31.25 / 62.5 / 125 / 250 / 500 / 1k / 2k / 4k / 8k / 16k in dB.
            var _bassComp = new CompressorEffect(
                threshold: 0.58f,
                ratio: 3.0f,
                attackTime: 25f,
                releaseTime: 220f,
                makeupGain: 1.10f,
                sampleRate: targetSampleRate);

            //Subsonic gone, mud dipped, 1k lifted so the notes read on small speakers
            var _bassEq = new EqualizerEffect(targetSampleRate,
                -6.0f, -0.5f, 0.0f, -1.5f, -0.5f, 1.0f, 0.5f, 0.0f, -1.0f, -2.0f);

            var bassBus = new SourceWithEffects(fileSource1);
            bassBus.AddEffect(_bassComp);
            bassBus.AddEffect(_bassEq);

            //INSTRUMENT BUS - light glue only, the low-mid dip carves the pocket for the vocal
            var _instComp = new CompressorEffect(
                threshold: 0.60f,
                ratio: 2.5f,
                attackTime: 25f,
                releaseTime: 250f,
                makeupGain: 1.15f,
                sampleRate: targetSampleRate);

            var _instEq = new EqualizerEffect(targetSampleRate,
                0.0f, 0.0f, -0.5f, -1.5f, -1.0f, -0.5f, 0.0f, 0.5f, 1.0f, 0.5f);

            var instBus = new SourceWithEffects(fileSource2);
            instBus.AddEffect(_instComp);
            instBus.AddEffect(_instEq);
            
            //Female lead, so we start from the vocal plate and tune it for her range.
            var _vocalReverb = new OwnReverbEffect(OwnReverbPreset.VocalPlate);
            _vocalReverb.PreDelay = 55f;
            _vocalReverb.Decay = 1.8f;
            _vocalReverb.Size = 0.62f;
            _vocalReverb.Damping = 0.46f;
            _vocalReverb.LowDamping = 0.52f;
            _vocalReverb.Diffusion = 0.94f;
            _vocalReverb.EarlyLevel = 0f;
            _vocalReverb.ModRate = 1.1f;
            _vocalReverb.ModDepth = 0.32f;
            _vocalReverb.Width = 1.15f;
            _vocalReverb.DuckDepth = 0.32f;
            _vocalReverb.DuckAttack = 8f;
            _vocalReverb.DuckRelease = 260f;

            //Audible, but the dry voice stays out in front
            _vocalReverb.Mix = 0.32f;

            var vocalBus = new SourceWithEffects(fileSource3);
            vocalBus.AddEffect(_vocalReverb);

            //Staged demo: the first 15s is the raw mix, then one group joins every 15 seconds
            IEffectProcessor[] _instrumentFx = { _drumComp, _drumAir, _bassComp, _bassEq, _instComp, _instEq };
            IEffectProcessor[] _vocalFx = { _vocalReverb };
            IEffectProcessor[] _masterFx = { _masterEq, _glue, _exciter };

            foreach (var _fx in _instrumentFx) _fx.Enabled = false;
            foreach (var _fx in _vocalFx) _fx.Enabled = false;

            // Add source to mixer (will automatically start because mixer is running)
            mixer.AddSource(drumBus);
            mixer.AddSource(bassBus);
            mixer.AddSource(instBus);
            mixer.AddSource(vocalBus);

            // Optional: Set timeline positions (all start at 0.0 by default)
            drumBus.StartOffset = 0.0;   // Drums start immediately
            bassBus.StartOffset = 0.0;   // Bass start immediately
            instBus.StartOffset = 0.0;   // Other start immediately
            vocalBus.StartOffset = 0.0;  // Vocals start immediately

            Console.WriteLine($"  ✓ Sources added to mixer");
            Console.WriteLine($"  ✓ Sources attached to Master Clock");
            Console.WriteLine($"  ✓ Active sources: {mixer.SourceCount}");
            Console.WriteLine($"  ✓ Master Clock mode: {mixer.MasterClock.Mode}");
            Console.WriteLine($"  ✓ Drum bus attached to clock: {drumBus.IsAttachedToClock}");
            Console.WriteLine($"  ✓ File source state: {drumBus.State}");

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
            drumBus.Play();
            bassBus.Play();
            instBus.Play();
            vocalBus.Play();

            Console.WriteLine($"  ✓ Mixer started: {mixer.IsRunning}");
            Console.WriteLine($"  ✓ All sources playing");

            // Step 6: Playback Progress Display
            Console.WriteLine("\n[6/6] Playing audio...");
            Console.WriteLine("  0s - 15s : raw mix, no processing (the limiter stays on for peak safety)");
            Console.WriteLine("     15s   : instruments in - drums comp+enhancer, bass comp+eq, other comp+eq");
            Console.WriteLine("     30s   : vocals in - OwnReverb only, ducked vocal plate");
            Console.WriteLine("     45s   : master bus in - eq -> glue comp -> exciter\n");
            Console.WriteLine("Press any key to stop playback early.\n");

            // Display playback progress
            DateTime startTime = DateTime.Now;
            bool userCancelled = false;
            int statusLine = -1;
            bool _instIn = false;
            bool _vocalIn = false;
            bool _masterIn = false;

            // Try to get cursor position (may fail in timeout/redirect scenarios)
            try
            {
                statusLine = Console.CursorTop;
            }
            catch (IOException) { }

            while (drumBus.State == AudioState.Playing && !userCancelled)
            {
                // Update progress every 100ms
                Thread.Sleep(100);

                // Get position from Master Clock (timeline timestamp)
                double masterTimestamp = mixer.MasterClock.CurrentTimestamp;
                long masterSamplePosition = mixer.MasterClock.CurrentSamplePosition;

                double position = drumBus.Position;
                double duration = drumBus.Duration;
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

                string stage = _masterIn ? "+master" : _vocalIn ? "+vocals" : _instIn ? "instruments" : "dry";

                string infoLine = $"  Position: {new TimeSpan(0, 0, (int)position).ToString()} / {new TimeSpan(0, 0, (int)duration).ToString()}s  [{progressBar}] {progressPercent}%  ";
                string peakLine = $"| Peaks: L={mixer.LeftPeak:F2} R={mixer.RightPeak:F2}  ";
                string clockLine = $"| MClock: {masterTimestamp:F2}s  ";
                string stageLine = $"| Fx: {stage}  ";

                Console.Write(infoLine + peakLine + clockLine + stageLine);

                if (statusLine == -1)
                {
                    Console.WriteLine();
                }

                //One group of effects joins every 15 seconds, the limiter was never off
                if (position >= 15 && !_instIn)
                {
                    foreach (var _fx in _instrumentFx) _fx.Enabled = true;
                    _instIn = true;
                }

                if (position >= 30 && !_vocalIn)
                {
                    foreach (var _fx in _vocalFx) _fx.Enabled = true;
                    _vocalIn = true;
                }

                if (position >= 45 && !_masterIn)
                {
                    foreach (var _fx in _masterFx) _fx.Enabled = true;
                    _masterIn = true;
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
                catch (InvalidOperationException) { }
            }

            Console.WriteLine("\n\n  ✓ Playback completed!");
            TimeSpan elapsed = DateTime.Now - startTime;
            double finalPosition = drumBus.Position;
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

            // Display Final Statistics
            Console.WriteLine("\n=== FINAL STATISTICS ===");
            Console.WriteLine($"  Total mixed frames: {mixer.TotalMixedFrames}");
            Console.WriteLine($"  Total underruns: {mixer.TotalUnderruns}");
            Console.WriteLine($"  Master volume: {mixer.MasterVolume:P0}");
            Console.WriteLine($"  Source state: {drumBus.State}");
            Console.WriteLine($"  Final position: {drumBus.Position:F2}s / {drumBus.Duration:F2}s");
            Console.WriteLine($"  Master Clock timestamp: {mixer.MasterClock.CurrentTimestamp:F2}s");
            Console.WriteLine($"  Master Clock sample position: {mixer.MasterClock.CurrentSamplePosition}");

            // Cleanup
            Console.WriteLine("\n=== CLEANUP ===");

            Console.WriteLine("  Stopping mixer...");
            mixer.Stop();

            Console.WriteLine("  Disposing mixer...");
            mixer.Dispose();

            Console.WriteLine("  Disposing source...");
            drumBus.Dispose();
            bassBus.Dispose();
            instBus.Dispose();
            vocalBus.Dispose();

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
