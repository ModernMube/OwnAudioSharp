using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.AppCompat.App;
using OwnaudioNET;
using OwnaudioNET.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;
using AudioEngine = Ownaudio.Core.IAudioEngine;
using AudioEngineFactory = Ownaudio.Core.AudioEngineFactory;
using AudioConfig = Ownaudio.Core.AudioConfig;
using System.Diagnostics;
using Ownaudio.Example.Android;

namespace OwnaudioAndroidExample
{
    [Activity(Label = "@string/app_name", MainLauncher = true, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class SimpleMainActivity : AppCompatActivity
    {
        // UI Controls
        private Button? _btnInitialize;
        private Button? _btnPlay;
        private Button? _btnStop;
        private TextView? _tvStatus;
        private TextView? _tvProgress;
        private TextView? _tvPeaks;
        private TextView? _tvStats;
        private SeekBar? _seekVolume;
        private TextView? _tvVolume;

        // Audio components
        private AudioMixer? _mixer;
        private FileSource? _fileSource0; // drums
        private FileSource? _fileSource1; // bass
        private FileSource? _fileSource2; // other
        private FileSource? _fileSource3; // vocals
        private SourceWithEffects? _fileSource3Effect;
        private float _volume = 0.8f;

        // Effects
        private Equalizer30BandEffect? _equalizer;
        private CompressorEffect? _compressor;

        // Progress update
        private System.Threading.Timer? _progressTimer;
        private DateTime _startTime;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);            

            // Set layout
            SetContentView(Resource.Layout.activity_simple);

            // Initialize UI controls
            _btnInitialize = FindViewById<Button>(Resource.Id.btnInitialize);
            _btnPlay = FindViewById<Button>(Resource.Id.btnPlay);
            _btnStop = FindViewById<Button>(Resource.Id.btnStop);
            _tvStatus = FindViewById<TextView>(Resource.Id.tvStatus);
            _tvProgress = FindViewById<TextView>(Resource.Id.tvProgress);
            _tvPeaks = FindViewById<TextView>(Resource.Id.tvPeaks);
            _tvStats = FindViewById<TextView>(Resource.Id.tvStats);
            _seekVolume = FindViewById<SeekBar>(Resource.Id.seekVolume);
            _tvVolume = FindViewById<TextView>(Resource.Id.tvVolume);

            // Set event handlers
            if (_btnInitialize != null)
                _btnInitialize.Click += BtnInitialize_Click;

            if (_btnPlay != null)
            {
                _btnPlay.Click += BtnPlay_Click;
                _btnPlay.Enabled = false;
            }

            if (_btnStop != null)
            {
                _btnStop.Click += BtnStop_Click;
                _btnStop.Enabled = false;
            }

            if (_seekVolume != null)
            {
                _seekVolume.Max = 100;
                _seekVolume.Progress = 80;
                _seekVolume.ProgressChanged += SeekVolume_ProgressChanged;
            }

            UpdateStatus("Ready. Press Initialize to begin.");
            //UpdateStatus($"📝 Log file: {Ownaudio.Android.Common.FileLogger.LogFilePath}");
        }

        private async void BtnInitialize_Click(object? sender, EventArgs e)
        {
            try
            {
                UpdateStatus("[1/6] Initializing audio engine...");
                if (_btnInitialize != null)
                    _btnInitialize.Enabled = false;

                // Initialize audio engine using standard OwnaudioNet API
                // Android platform will be automatically detected and AAudioEngine will be used
                var config = new AudioConfig
                {
                    SampleRate = 48000,
                    Channels = 2,
                    BufferSize = 512,
                    EnableOutput = true,
                    EnableInput = false
                };

                // Initialize with standard API - Android detection is automatic
                await OwnaudioNet.InitializeAsync(config);

                UpdateStatus($"✓ Engine: {OwnaudioNet.Engine?.GetType().Name}");
                UpdateStatus($"✓ Sample Rate: {OwnaudioNet.Engine?.Config.SampleRate} Hz");
                UpdateStatus($"✓ Buffer Size: {OwnaudioNet.Engine?.FramesPerBuffer} frames");

                // ==========================================
                // Step 2: Start Audio Engine
                // ==========================================
                UpdateStatus("\n[2/6] Starting audio engine...");

                // Create audio mixer using the underlying engine directly
                var engine = OwnaudioNet.Engine!.UnderlyingEngine;

                // ✅ FIX: Start the underlying engine asynchronously
                // While Start() is usually fast (~5ms), running it async ensures UI responsiveness
                int startResult = await Task.Run(() => engine.Start());
                if (startResult < 0)
                {
                    throw new Exception($"Failed to start audio engine. Error code: {startResult}");
                }

                UpdateStatus($"✓ Engine running");

                // ==========================================
                // Step 3: Create Audio Mixer
                // ==========================================
                UpdateStatus("\n[3/6] Creating audio mixer...");

                _mixer = new AudioMixer(engine, bufferSizeInFrames: 512);
                _mixer.MasterVolume = _volume;

                UpdateStatus($"✓ Mixer created, Master volume: {_volume:P0}");

                _mixer.SourceError += (sender, e) =>
                {
                    UpdateStatus($"! Source error: {e.Message}");
                };

                // ==========================================
                // Create mastering effects
                // ==========================================
                UpdateStatus("Adding mastering effects...");

                _equalizer = new Equalizer30BandEffect();
                // Configure equalizer with pop music preset (same as desktop)
                ConfigureEqualizer(_equalizer);

                _compressor = new CompressorEffect(CompressorPreset.Vintage);

                // Add master effects
                _mixer.AddMasterEffect(_equalizer);
                _mixer.AddMasterEffect(_compressor);
                _mixer.AddMasterEffect(new DynamicAmpEffect(DynamicAmpPreset.Music));

                // Start disabled, will enable at 30s like desktop version
                _equalizer.Enabled = false;
                _compressor.Enabled = false;

                UpdateStatus("✓ Master effects added");

                // ==========================================
                // Step 4: Create Audio Sources (4 tracks)
                // ==========================================
                UpdateStatus("\n[4/6] Loading audio files...");

                try
                {
                    var requestedConfig = OwnaudioNet.Engine!.Config;
                    int targetSampleRate = requestedConfig.SampleRate;
                    int targetChannels = requestedConfig.Channels;

                    // Load all 4 audio files
                    string audioPath0 = await ExtractAssetAsync("drums.wav");
                    string audioPath1 = await ExtractAssetAsync("bass.wav");
                    string audioPath2 = await ExtractAssetAsync("other.wav");
                    string audioPath3 = await ExtractAssetAsync("vocals.wav");

                    _fileSource0 = new FileSource(audioPath0, 8192, targetSampleRate, targetChannels);
                    _fileSource1 = new FileSource(audioPath1, 8192, targetSampleRate, targetChannels);
                    _fileSource2 = new FileSource(audioPath2, 8192, targetSampleRate, targetChannels);
                    _fileSource3 = new FileSource(audioPath3, 8192, targetSampleRate, targetChannels);
                }
                catch(Exception)
                {
                    //System.Diagnostics.Debug.WriteLine("Failed to load audio files.", ex);
                    throw;
                }


                // Set volumes (same as desktop)
                _fileSource0.Volume = 0.7f; // drums
                _fileSource1.Volume = 0.7f; // bass
                _fileSource2.Volume = 0.7f; // other
                _fileSource3.Volume = 1.0f; // vocals

                UpdateStatus($"✓ 4 files loaded, Duration: {_fileSource0.Duration:F1}s");

                // ==========================================
                // Add effects to vocal track
                // ==========================================
                UpdateStatus("Adding vocal effects...");

                // Create vocal effects chain (same as desktop)
                var compressor = new CompressorEffect(
                    threshold: 0.4f,
                    ratio: 3.0f,
                    attackTime: 5f,
                    releaseTime: 150f,
                    makeupGain: 1.5f
                );

                var delay = new DelayEffect(
                    time: 375,
                    repeat: 0.25f,
                    mix: 0.15f,
                    damping: 0.4f
                );

                var reverb = new ReverbEffect(
                    size: 0.5f,
                    damp: 0.6f,
                    wet: 0.25f,
                    dry: 0.75f,
                    stereoWidth: 0.8f,
                    gainLevel: 0.015f,
                    mix: 0.25f
                );

                _fileSource3Effect = new SourceWithEffects(_fileSource3);
                _fileSource3Effect.AddEffect(compressor);
                _fileSource3Effect.AddEffect(delay);
                _fileSource3Effect.AddEffect(reverb);

                UpdateStatus("✓ Vocal effects added");

                // ==========================================
                // Step 5: Add sources to mixer and setup Master Clock sync
                // ==========================================
                UpdateStatus("\n[5/6] Setting up Master Clock synchronization...");

                _mixer.AddSource(_fileSource0);
                _mixer.AddSource(_fileSource1);
                _mixer.AddSource(_fileSource2);
                _mixer.AddSource(_fileSource3Effect);

                // ==========================================
                // NEW MASTER CLOCK ARCHITECTURE (v2.1.0+)
                // ==========================================
                // Attach sources to Master Clock for sample-accurate synchronization
                _fileSource0.AttachToClock(_mixer.MasterClock);
                _fileSource1.AttachToClock(_mixer.MasterClock);
                _fileSource2.AttachToClock(_mixer.MasterClock);
                _fileSource3.AttachToClock(_mixer.MasterClock);

                // Optional: Set timeline positions (all start at 0.0 by default)
                _fileSource0.StartOffset = 0.0;  // Drums start immediately
                _fileSource1.StartOffset = 0.0;  // Bass start immediately
                _fileSource2.StartOffset = 0.0;  // Other start immediately
                _fileSource3.StartOffset = 0.0;  // Vocals start immediately

                // Subscribe to dropout events for monitoring
                _mixer.TrackDropout += (sender, e) =>
                {
                    UpdateStatus($"! Track dropout: {e.TrackName} at {e.MasterTimestamp:F3}s");
                    Android.Util.Log.Warn("OwnaudioAndroidTest",
                        $"Track dropout: {e.TrackName}, Reason: {e.Reason}, Missed frames: {e.MissedFrames}");
                };

                UpdateStatus($"✓ Master Clock sync configured");
                UpdateStatus($"✓ Active sources: {_mixer.SourceCount}");
                UpdateStatus($"✓ Clock mode: {_mixer.MasterClock.Mode}");

                UpdateStatus("\n[6/6] Ready to play!");

                if (_btnPlay != null)
                    _btnPlay.Enabled = true;
            }
            catch (Exception ex)
            {
                // Log detailed error to logcat for debugging
                Android.Util.Log.Error("OwnaudioAndroidTest",
                    $"Initialization failed: {ex.GetType().Name}");
                Android.Util.Log.Error("OwnaudioAndroidTest",
                    $"Message: {ex.Message}");
                Android.Util.Log.Error("OwnaudioAndroidTest",
                    $"StackTrace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    Android.Util.Log.Error("OwnaudioAndroidTest",
                        $"Inner Exception: {ex.InnerException.GetType().Name}");
                    Android.Util.Log.Error("OwnaudioAndroidTest",
                        $"Inner Message: {ex.InnerException.Message}");
                }

                // Show user-friendly error dialog
                ShowError("Initialization Error",
                    $"{ex.GetType().Name}\n\n{ex.Message}\n\nCheck logcat for details.");

                UpdateStatus($"Error: {ex.Message}");

                if (_btnInitialize != null)
                    _btnInitialize.Enabled = true;
            }
        }

        private void ConfigureEqualizer(Equalizer30BandEffect eq)
        {
            // Pop music EQ preset (same as desktop version)
            // Sub-bass region
            eq.SetBandGain(0, 20, 0.5f, 0.2f);
            eq.SetBandGain(1, 25, 0.5f, 0.4f);
            eq.SetBandGain(2, 31, 0.6f, 0.6f);
            eq.SetBandGain(3, 40, 0.7f, 0.8f);
            eq.SetBandGain(4, 50, 0.7f, 0.8f);
            eq.SetBandGain(5, 63, 0.7f, -0.3f);

            // Bass region
            eq.SetBandGain(6, 80, 0.8f, 0.3f);
            eq.SetBandGain(7, 100, 0.8f, 0.5f);
            eq.SetBandGain(8, 125, 0.9f, 0.3f);
            eq.SetBandGain(9, 160, 0.9f, 0.1f);

            // Low-mid region (mud removal)
            eq.SetBandGain(10, 200, 1.0f, -0.4f);
            eq.SetBandGain(11, 250, 1.0f, -0.8f);
            eq.SetBandGain(12, 315, 1.1f, -0.7f);
            eq.SetBandGain(13, 400, 1.1f, -0.5f);
            eq.SetBandGain(14, 500, 1.0f, -0.2f);

            // Mid region
            eq.SetBandGain(15, 630, 1.0f, -0.1f);
            eq.SetBandGain(16, 800, 1.0f, 0.0f);
            eq.SetBandGain(17, 1000, 1.0f, 0.0f);
            eq.SetBandGain(18, 1250, 1.0f, 0.0f);
            eq.SetBandGain(19, 1600, 1.0f, 0.1f);

            // Upper-mid region (presence)
            eq.SetBandGain(20, 2000, 1.0f, 0.3f);
            eq.SetBandGain(21, 2500, 0.9f, 0.5f);
            eq.SetBandGain(22, 3150, 0.9f, 0.7f);
            eq.SetBandGain(23, 4000, 0.8f, 0.5f);
            eq.SetBandGain(24, 5000, 0.7f, 0.3f);

            // High region (air and sparkle)
            eq.SetBandGain(25, 6300, 0.7f, 0.3f);
            eq.SetBandGain(26, 8000, 0.6f, 0.6f);
            eq.SetBandGain(27, 10000, 0.6f, 0.8f);
            eq.SetBandGain(28, 12500, 0.5f, 1.0f);
            eq.SetBandGain(29, 16000, 0.5f, 1.0f);
        }

        private async void BtnPlay_Click(object? sender, EventArgs e)
        {
            try
            {
                UpdateStatus("Starting playback...");

                if (_btnPlay != null)
                    _btnPlay.Enabled = false;
                if (_btnStop != null)
                    _btnStop.Enabled = true;

                // If mixer was disposed (after Stop), reinitialize everything
                if (_mixer == null)
                {
                    // Reinitialize by calling Initialize again
                    BtnInitialize_Click(sender, e);

                    // Wait a bit for initialization to complete
                    await Task.Delay(1000);

                    if (_mixer == null)
                    {
                        throw new Exception("Mixer reinitialization failed");
                    }
                }

                // Start mixer
                _mixer.Start();

                // Start all sources for playback (IMPORTANT with Master Clock!)
                _fileSource0.Play();
                _fileSource1.Play();
                _fileSource2.Play();
                _fileSource3.Play();

                // All attached sources now play in perfect sync with Master Clock

                // Record start time for tempo accuracy calculation
                _startTime = DateTime.Now;

                // Start progress update timer
                _progressTimer = new System.Threading.Timer(UpdateProgressCallback, null, 0, 100);

                UpdateStatus("Playing... (effects will enable at 30s)");
            }
            catch (Exception ex)
            {
                // Log detailed error to logcat for debugging
                Android.Util.Log.Error("OwnaudioAndroidTest",
                    $"Playback failed: {ex.GetType().Name}");
                Android.Util.Log.Error("OwnaudioAndroidTest",
                    $"Message: {ex.Message}");
                Android.Util.Log.Error("OwnaudioAndroidTest",
                    $"StackTrace: {ex.StackTrace}");

                ShowError("Playback Error", ex.Message);
                UpdateStatus($"Error: {ex.Message}");

                // Re-enable play button on error
                if (_btnPlay != null)
                    _btnPlay.Enabled = true;
                if (_btnStop != null)
                    _btnStop.Enabled = false;
            }
        }

        private async void BtnStop_Click(object? sender, EventArgs e)
        {
            try
            {
                UpdateStatus("Stopping playback...");

                _progressTimer?.Dispose();
                _progressTimer = null;

                // Calculate and display final statistics BEFORE stopping
                if (_fileSource0 != null && _mixer != null)
                {
                    double finalPosition = _fileSource0.Position;
                    TimeSpan elapsed = DateTime.Now - _startTime;

                    UpdateStatus($"\n=== FINAL STATISTICS ===");
                    UpdateStatus($"Total mixed frames: {_mixer.TotalMixedFrames}");
                    UpdateStatus($"Total underruns: {_mixer.TotalUnderruns}");
                    UpdateStatus($"Real-time elapsed: {elapsed.TotalSeconds:F2}s");
                    UpdateStatus($"Audio position: {finalPosition:F2}s");
                    UpdateStatus($"Master Clock timestamp: {_mixer.MasterClock.CurrentTimestamp:F2}s");
                    UpdateStatus($"Master Clock samples: {_mixer.MasterClock.CurrentSamplePosition}");

                    // Calculate tempo accuracy
                    if (elapsed.TotalSeconds > 0)
                    {
                        double tempoRatio = finalPosition / elapsed.TotalSeconds;
                        double tempoError = (tempoRatio - 1.0) * 100.0;
                        UpdateStatus($"Tempo ratio: {tempoRatio:F4} (1.0000 = perfect)");

                        if (Math.Abs(tempoError) < 0.5)
                            UpdateStatus($"Tempo accuracy: EXCELLENT ({tempoError:+0.00;-0.00}%)");
                        else if (Math.Abs(tempoError) < 2.0)
                            UpdateStatus($"Tempo accuracy: Good ({tempoError:+0.00;-0.00}%)");
                        else
                            UpdateStatus($"Tempo accuracy: POOR ({tempoError:+0.00;-0.00}%)");
                    }
                }

                // ✅ CRITICAL FIX: Run all blocking operations asynchronously to prevent UI freeze
                await Task.Run(() =>
                {
                    // Note: No need to stop sync group - Master Clock handles cleanup automatically

                    // Stop and dispose all sources
                    if (_mixer != null)
                    {
                        if (_fileSource0 != null) _mixer.RemoveSource(_fileSource0);
                        if (_fileSource1 != null) _mixer.RemoveSource(_fileSource1);
                        if (_fileSource2 != null) _mixer.RemoveSource(_fileSource2);
                        if (_fileSource3Effect != null) _mixer.RemoveSource(_fileSource3Effect);
                    }

                    _fileSource0?.Dispose();
                    _fileSource1?.Dispose();
                    _fileSource2?.Dispose();
                    _fileSource3?.Dispose();
                    _fileSource3Effect?.Dispose();

                    // Stop the mixer (may block up to 2000ms)
                    _mixer?.Stop();
                    _mixer?.Dispose();
                });

                // Clear references
                _fileSource0 = null;
                _fileSource1 = null;
                _fileSource2 = null;
                _fileSource3 = null;
                _fileSource3Effect = null;
                _mixer = null;

                // ✅ CRITICAL FIX: Use async API for engine stop to prevent UI freeze
                // This prevents ANR (Application Not Responding) dialog on Android
                await OwnaudioNet.StopAsync();

                if (_btnPlay != null)
                    _btnPlay.Enabled = true;
                if (_btnStop != null)
                    _btnStop.Enabled = false;

                UpdateStatus("Stopped. Press Initialize to play again.");

                // Re-enable initialize button
                if (_btnInitialize != null)
                    _btnInitialize.Enabled = true;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("OwnaudioAndroidTest", $"Stop error: {ex.Message}");
                ShowError("Stop Error", ex.Message);
            }
        }

        private void SeekVolume_ProgressChanged(object? sender, SeekBar.ProgressChangedEventArgs e)
        {
            _volume = e.Progress / 100f;

            if (_mixer != null)
                _mixer.MasterVolume = _volume;

            if (_tvVolume != null)
                _tvVolume.Text = $"Volume: {e.Progress}%";
        }


        private void UpdateProgressCallback(object? state)
        {
            if (_fileSource0 == null || _fileSource0.State != AudioState.Playing)
            {
                // Auto-stop when playback completes
                if (_fileSource0?.State == AudioState.Stopped)
                {
                    RunOnUiThread(() => BtnStop_Click(null, EventArgs.Empty));
                }
                return;
            }

            RunOnUiThread(() =>
            {
                try
                {
                    double position = _fileSource0.Position;
                    double duration = _fileSource0.Duration;
                    int progressPercent = duration > 0 ? (int)((position / duration) * 100) : 0;

                    // Update progress display
                    if (_tvProgress != null)
                    {
                        _tvProgress.Text = $"Position: {TimeSpan.FromSeconds(position):mm\\:ss} / " +
                                          $"{TimeSpan.FromSeconds(duration):mm\\:ss} ({progressPercent}%)";
                    }

                    // Update peak meters
                    if (_tvPeaks != null && _mixer != null)
                    {
                        _tvPeaks.Text = $"Peaks: L={_mixer.LeftPeak:F2} R={_mixer.RightPeak:F2}";
                    }

                    // Update statistics
                    if (_tvStats != null && _mixer != null)
                    {
                        _tvStats.Text = $"Mixed: {_mixer.TotalMixedFrames} | Underruns: {_mixer.TotalUnderruns}";
                    }

                    // Enable master effects at 30 seconds (same as desktop)
                    if (position > 30 && position < 35)
                    {
                        if (_equalizer != null && !_equalizer.Enabled)
                        {
                            _equalizer.Enabled = true;
                            _compressor!.Enabled = true;
                            UpdateStatus("Master effects ENABLED at 30s");
                        }
                    }
                }
                catch
                {
                    // Ignore UI update errors
                }
            });
        }

        private async Task<string> ExtractAssetAsync(string assetFileName)
        {
            return await Task.Run(() =>
            {
                string cacheDir = CacheDir?.AbsolutePath ?? "";
                string targetPath = Path.Combine(cacheDir, assetFileName);

                // If already extracted, return path
                if (File.Exists(targetPath))
                    return targetPath;

                // Extract from assets
                using var assetStream = Assets?.Open(assetFileName);
                if (assetStream == null)
                    throw new FileNotFoundException($"Asset not found: {assetFileName}");

                using var fileStream = File.Create(targetPath);
                assetStream.CopyTo(fileStream);

                return targetPath;
            });
        }

        private void UpdateStatus(string message)
        {
            RunOnUiThread(() =>
            {
                if (_tvStatus != null)
                    _tvStatus.Text = message;
            });
        }

        private void ShowError(string title, string message)
        {
            RunOnUiThread(() =>
            {
                new AlertDialog.Builder(this)
                    .SetTitle(title)!
                    .SetMessage(message)!
                    .SetPositiveButton("OK", (s, e) => { })!
                    .Show();
            });
        }

        protected override void OnPause()
        {
            base.OnPause();

            Task.Run(() =>
            {
                try
                {
                    // Pause audio when app goes to background
                    _mixer?.Stop();
                    _fileSource0?.Stop();
                    _fileSource1?.Stop();
                    _fileSource2?.Stop();
                    _fileSource3?.Stop();
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("OwnaudioAndroidTest", $"OnPause error: {ex.Message}");
                }
            });
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            // Dispose timer (fast, no blocking)
            _progressTimer?.Dispose();
            
            Task.Run(async () =>
            {
                try
                {
                    // Note: No need to stop sync group - Master Clock handles cleanup automatically
                    _mixer?.Stop();
                    _mixer?.Dispose();

                    _fileSource0?.Dispose();
                    _fileSource1?.Dispose();
                    _fileSource2?.Dispose();
                    _fileSource3?.Dispose();
                    _fileSource3Effect?.Dispose();

                    // Use async API for engine shutdown
                    // This prevents up to 2000ms UI freeze and potential ANR
                    await OwnaudioNet.StopAsync();
                    OwnaudioNet.Shutdown();
                }
                catch (Exception ex)
                {
                    // Log but don't crash - we're already destroying
                    Android.Util.Log.Error("OwnaudioAndroidTest", $"OnDestroy cleanup error: {ex.Message}");
                }
            });
        }
    }
}
