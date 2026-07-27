using AVFoundation;
using Foundation;
using OwnaudioNET;
using OwnaudioNET.Core;
using OwnaudioNET.Effects;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using UIKit;
using AudioConfig = Ownaudio.Core.AudioConfig;

namespace OwnaudioIosExample
{
    /// <summary>
    /// Four synchronized tracks through the mixer, with master effects and a vocal chain —
    /// the iOS twin of the Android demo.
    /// </summary>
    public class MainViewController : UIViewController
    {
        private UIButton _btnInitialize = null!;
        private UIButton _btnPlay = null!;
        private UIButton _btnStop = null!;
        private UITextView _txtStatus = null!;
        private UILabel _lblProgress = null!;
        private UILabel _lblPeaks = null!;
        private UILabel _lblStats = null!;
        private UILabel _lblVolume = null!;
        private UISlider _sldVolume = null!;

        private AudioMixer? _mixer;
        private FileSource? _drums;
        private FileSource? _bass;
        private FileSource? _other;
        private FileSource? _vocals;
        private SourceWithEffects? _vocalsWithFx;

        private Equalizer30BandEffect? _equalizer;
        private CompressorEffect? _compressor;

        private NSTimer? _progressTimer;
        private DateTime _startTime;
        private float _volume = 0.8f;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.SystemBackground;

            _buildUi();
            _configureAudioSession();

            _log("Ready. Press Initialize to begin.");
        }

        /// <summary>
        /// Lays the whole thing out in one stack view, no storyboard involved.
        /// </summary>
        private void _buildUi()
        {
            UILabel _title = new UILabel { Text = "OwnAudio iOS Demo", Font = UIFont.BoldSystemFontOfSize(24), TextAlignment = UITextAlignment.Center };

            _txtStatus = new UITextView { Editable = false, Font = UIFont.SystemFontOfSize(13), BackgroundColor = UIColor.SecondarySystemBackground };
            _txtStatus.HeightAnchor.ConstraintEqualTo(180).Active = true;

            _lblProgress = _infoLabel("Position: 00:00 / 00:00 (0%)");
            _lblPeaks = _infoLabel("Peaks: L=0.00 R=0.00");
            _lblStats = _infoLabel("Mixed: 0 | Underruns: 0");
            _lblVolume = _infoLabel($"Volume: {(int)(_volume * 100)}%");

            _sldVolume = new UISlider { MinValue = 0f, MaxValue = 1f, Value = _volume };
            _sldVolume.ValueChanged += (s, e) => _volumeChanged(_sldVolume.Value);

            _btnInitialize = _button("INITIALIZE", UIColor.SystemBlue);
            _btnInitialize.TouchUpInside += async (s, e) => await _initializeAsync();

            _btnPlay = _button("PLAY", UIColor.SystemGreen);
            _btnPlay.Enabled = false;
            _btnPlay.TouchUpInside += (s, e) => _play();

            _btnStop = _button("STOP", UIColor.SystemRed);
            _btnStop.Enabled = false;
            _btnStop.TouchUpInside += async (s, e) => await _stopAsync();

            UIStackView _stack = new UIStackView(new UIView[]
            {
                _title, _txtStatus, _lblProgress, _lblPeaks, _lblStats,
                _lblVolume, _sldVolume, _btnInitialize, _btnPlay, _btnStop
            });

            _stack.Axis = UILayoutConstraintAxis.Vertical;
            _stack.Spacing = 12;
            _stack.TranslatesAutoresizingMaskIntoConstraints = false;
            View!.AddSubview(_stack);

            UILayoutGuide _safe = View.SafeAreaLayoutGuide;
            _stack.TopAnchor.ConstraintEqualTo(_safe.TopAnchor, 16).Active = true;
            _stack.LeadingAnchor.ConstraintEqualTo(_safe.LeadingAnchor, 16).Active = true;
            _stack.TrailingAnchor.ConstraintEqualTo(_safe.TrailingAnchor, -16).Active = true;
        }

        private UILabel _infoLabel(string text)
        {
            return new UILabel { Text = text, Font = UIFont.SystemFontOfSize(15) };
        }

        private UIButton _button(string title, UIColor colour)
        {
            UIButton _b = new UIButton(UIButtonType.System);
            _b.SetTitle(title, UIControlState.Normal);
            _b.BackgroundColor = colour;
            _b.SetTitleColor(UIColor.White, UIControlState.Normal);
            _b.Layer.CornerRadius = 8;
            _b.HeightAnchor.ConstraintEqualTo(48).Active = true;
            return _b;
        }

        /// <summary>
        /// iOS stays silent unless somebody claims an audio session — the engine does not do this
        /// for you, and without it playback is routed nowhere and the ringer switch mutes you.
        /// </summary>
        private void _configureAudioSession()
        {
            AVAudioSession _session = AVAudioSession.SharedInstance();

            NSError? _error = _session.SetCategory(AVAudioSessionCategory.Playback);
            if (_error != null) { _log($"! Audio session category: {_error.LocalizedDescription}"); }

            _session.SetPreferredSampleRate(48000, out _);
            _session.SetPreferredIOBufferDuration(512.0 / 48000.0, out _);

            _session.SetActive(true, out _error);
            if (_error != null) { _log($"! Audio session activate: {_error.LocalizedDescription}"); }
        }

        private async Task _initializeAsync()
        {
            try
            {
                _btnInitialize.Enabled = false;
                _log("[1/6] Initializing audio engine...");

                AudioConfig _config = new AudioConfig
                {
                    SampleRate = 48000,
                    Channels = 2,
                    BufferSize = 512,
                    EnableOutput = true,
                    EnableInput = false
                };

                await OwnaudioNet.InitializeAsync(_config);

                _log($"+ Engine: {OwnaudioNet.Engine?.GetType().Name}");
                _log($"+ Sample rate: {OwnaudioNet.Engine?.Config.SampleRate} Hz");
                _log($"+ Buffer: {OwnaudioNet.Engine?.FramesPerBuffer} frames");

                _log("[2/6] Starting audio engine...");
                var _engine = OwnaudioNet.Engine!.UnderlyingEngine;

                int _started = await Task.Run(() => _engine.Start());
                if (_started < 0) throw new Exception($"Failed to start audio engine, code {_started}");

                _log("+ Engine running");

                _log("[3/6] Creating mixer...");
                _mixer = new AudioMixer(_engine, bufferSizeInFrames: 512);
                _mixer.MasterVolume = _volume;
                _mixer.SourceError += (s, e) => _log($"! Source error: {e.Message}");

                _equalizer = new Equalizer30BandEffect();
                _equalizer.SetPreset(Equalizer30Preset.Pop);
                _compressor = new CompressorEffect(CompressorPreset.Vintage);

                _mixer.AddMasterEffect(_equalizer);
                _mixer.AddMasterEffect(_compressor);
                _mixer.AddMasterEffect(new DynamicAmpEffect(DynamicAmpPreset.Music));

                _equalizer.Enabled = false;
                _compressor.Enabled = false;

                _log("+ Master effects added (enable at 30s)");

                _log("[4/6] Loading audio files...");
                int _rate = OwnaudioNet.Engine!.Config.SampleRate;
                int _channels = OwnaudioNet.Engine!.Config.Channels;

                _drums = new FileSource(_bundlePath("drums"), 8192, _rate, _channels);
                _bass = new FileSource(_bundlePath("bass"), 8192, _rate, _channels);
                _other = new FileSource(_bundlePath("other"), 8192, _rate, _channels);
                _vocals = new FileSource(_bundlePath("vocals"), 8192, _rate, _channels);

                _drums.Volume = 0.7f;
                _bass.Volume = 0.7f;
                _other.Volume = 0.7f;
                _vocals.Volume = 1.0f;

                _log($"+ 4 files loaded, duration {_drums.Duration:F1}s");

                var _comp = new CompressorEffect(threshold: 0.4f, ratio: 3.0f, attackTime: 5f, releaseTime: 150f, makeupGain: 1.5f);
                var _delay = new DelayEffect(time: 375, repeat: 0.25f, mix: 0.15f, damping: 0.4f);
                var _reverb = new ReverbEffect(size: 0.5f, damp: 0.6f, mix: 0.25f, stereoWidth: 0.8f);

                _vocalsWithFx = new SourceWithEffects(_vocals);
                _vocalsWithFx.AddEffect(_comp);
                _vocalsWithFx.AddEffect(_delay);
                _vocalsWithFx.AddEffect(_reverb);

                _log("+ Vocal chain added");

                _log("[5/6] Attaching to master clock...");
                _mixer.AddSource(_drums);
                _mixer.AddSource(_bass);
                _mixer.AddSource(_other);
                _mixer.AddSource(_vocalsWithFx);

                _drums.AttachToClock(_mixer.MasterClock);
                _bass.AttachToClock(_mixer.MasterClock);
                _other.AttachToClock(_mixer.MasterClock);
                _vocals.AttachToClock(_mixer.MasterClock);

                _mixer.TrackDropout += (s, e) => _log($"! Dropout: {e.TrackName} at {e.MasterTimestamp:F3}s");

                _log($"+ Sources: {_mixer.SourceCount}, clock mode {_mixer.MasterClock.Mode}");
                _log("[6/6] Ready to play!");

                _btnPlay.Enabled = true;
            }
            catch (Exception ex)
            {
                _log($"FAILED: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null) { _log($"  inner: {ex.InnerException.Message}"); }

                _showError("Initialization Error", $"{ex.GetType().Name}\n\n{ex.Message}");
                _btnInitialize.Enabled = true;
            }
        }

        private void _play()
        {
            try
            {
                _btnPlay.Enabled = false;
                _btnStop.Enabled = true;

                _mixer!.Start();
                _drums!.Play();
                _bass!.Play();
                _other!.Play();
                _vocals!.Play();

                _startTime = DateTime.Now;
                _progressTimer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromMilliseconds(100), _ => _tick());

                _log("Playing...");
            }
            catch (Exception ex)
            {
                _log($"Playback failed: {ex.Message}");
                _showError("Playback Error", ex.Message);
                _btnPlay.Enabled = true;
                _btnStop.Enabled = false;
            }
        }

        private async Task _stopAsync()
        {
            try
            {
                _progressTimer?.Invalidate();
                _progressTimer = null;

                if (_drums != null && _mixer != null)
                {
                    double _position = _drums.Position;
                    TimeSpan _elapsed = DateTime.Now - _startTime;

                    _log("=== FINAL STATISTICS ===");
                    _log($"Mixed frames: {_mixer.TotalMixedFrames}, underruns: {_mixer.TotalUnderruns}");
                    _log($"Real time: {_elapsed.TotalSeconds:F2}s, audio position: {_position:F2}s");

                    if (_elapsed.TotalSeconds > 0)
                    {
                        double _ratio = _position / _elapsed.TotalSeconds;
                        _log($"Tempo ratio: {_ratio:F4} ({(_ratio - 1.0) * 100.0:+0.00;-0.00}%)");
                    }
                }

                await Task.Run(() =>
                {
                    if (_mixer != null)
                    {
                        if (_drums != null) _mixer.RemoveSource(_drums);
                        if (_bass != null) _mixer.RemoveSource(_bass);
                        if (_other != null) _mixer.RemoveSource(_other);
                        if (_vocalsWithFx != null) _mixer.RemoveSource(_vocalsWithFx);
                    }

                    _drums?.Dispose();
                    _bass?.Dispose();
                    _other?.Dispose();
                    _vocals?.Dispose();
                    _vocalsWithFx?.Dispose();

                    _mixer?.Stop();
                    _mixer?.Dispose();
                });

                _drums = null;
                _bass = null;
                _other = null;
                _vocals = null;
                _vocalsWithFx = null;
                _mixer = null;

                await OwnaudioNet.StopAsync();

                _btnPlay.Enabled = false;
                _btnStop.Enabled = false;
                _btnInitialize.Enabled = true;

                _log("Stopped. Press Initialize to play again.");
            }
            catch (Exception ex)
            {
                _log($"Stop error: {ex.Message}");
                _showError("Stop Error", ex.Message);
            }
        }

        private void _volumeChanged(float value)
        {
            _volume = value;
            if (_mixer != null) { _mixer.MasterVolume = _volume; }
            _lblVolume.Text = $"Volume: {(int)(value * 100)}%";
        }

        /// <summary>
        /// Runs on the UI timer, so it also notices when the track has run out and stops for us.
        /// </summary>
        private void _tick()
        {
            if (_drums == null || _mixer == null) return;

            if (_drums.State == AudioState.Stopped)
            {
                _ = _stopAsync();
                return;
            }

            double _position = _drums.Position;
            double _duration = _drums.Duration;
            int _percent = _duration > 0 ? (int)(_position / _duration * 100) : 0;

            _lblProgress.Text = $"Position: {TimeSpan.FromSeconds(_position):mm\\:ss} / {TimeSpan.FromSeconds(_duration):mm\\:ss} ({_percent}%)";
            _lblPeaks.Text = $"Peaks: L={_mixer.LeftPeak:F2} R={_mixer.RightPeak:F2}";
            _lblStats.Text = $"Mixed: {_mixer.TotalMixedFrames} | Underruns: {_mixer.TotalUnderruns}";

            if (_position > 30 && _position < 35 && _equalizer != null && !_equalizer.Enabled)
            {
                _equalizer.Enabled = true;
                _compressor!.Enabled = true;
                _log("Master effects ENABLED at 30s");
            }
        }

        /// <summary>
        /// BundleResource files land flat next to the executable inside the .app.
        /// </summary>
        private string _bundlePath(string name)
        {
            string? _path = NSBundle.MainBundle.PathForResource(name, "wav");
            if (_path == null) throw new FileNotFoundException($"Bundle resource missing: {name}.wav");
            return _path;
        }

        private void _log(string message)
        {
            InvokeOnMainThread(() =>
            {
                _txtStatus.Text += message + "\n";

                NSRange _end = new NSRange(_txtStatus.Text.Length - 1, 1);
                _txtStatus.ScrollRangeToVisible(_end);
            });
        }

        private void _showError(string title, string message)
        {
            InvokeOnMainThread(() =>
            {
                UIAlertController _alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
                _alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
                PresentViewController(_alert, true, null);
            });
        }

        public override void ViewWillDisappear(bool animated)
        {
            base.ViewWillDisappear(animated);

            _progressTimer?.Invalidate();
            _mixer?.Stop();
        }
    }
}
