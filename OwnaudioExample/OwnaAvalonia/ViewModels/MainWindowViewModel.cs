using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Reactive;
using ReactiveUI;

using Avalonia.Platform.Storage;
using Avalonia.Threading;

using Ownaudio;
using Ownaudio.Sources;
using Ownaudio.Engines;
using Ownaudio.Common;

using OwnaAvalonia.Models;
using OwnaAvalonia.Views;
using OwnaAvalonia.Processor;
using Ownaudio.Fx;

namespace OwnaAvalonia.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, ILogger
    {
        private int _trackNumber = 0;
        private SourceManager? player;
        private bool _isStopRequested = true;
        private bool _isFFmpegInitialized;
        private int _sourceOutputId = -1;
        private FXProcessor _Fxprocessor;
        private FXProcessor _inputFxprocessor;

        #region Reactive commands
        public ReactiveCommand<Unit, Unit> AddFileCommand { get; }
        public ReactiveCommand<Unit, Unit> RemoveFileCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetCommand { get; }
        public ReactiveCommand<Unit, Unit> InputCommand { get; }
        public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
        public ReactiveCommand<Unit, Unit> StopCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveFilePathCommand { get; }
        #endregion

        public MainWindowViewModel()
        {
            AddFileCommand = ReactiveCommand.Create(addFileCommand);
            RemoveFileCommand = ReactiveCommand.Create(removeFileCommand);
            ResetCommand = ReactiveCommand.Create(resetCommand);
            InputCommand = ReactiveCommand.Create(inputCommand);
            PlayPauseCommand = ReactiveCommand.Create(playPauseCommand);
            StopCommand = ReactiveCommand.Create(stopCommand);
            SaveFilePathCommand = ReactiveCommand.Create(saveFilePathCommand);

            _Fxprocessor = new FXProcessor() { IsEnabled = IsFxEnabled };
            _inputFxprocessor = new FXProcessor() { IsEnabled = true };

            AudioEngineInitialize();
        }

        #region Binding propertyes
        private float _pitch = 0.0f;
        public float Pitch 
        { 
            get => _pitch; 
          
            set  
            {
                this.RaiseAndSetIfChanged(ref _pitch, value);
                for (int i = 0; i < player?.Sources.Count; i++)
                {
                    player.SetPitch(i, value);
                }
            } 
        }                                                

        private float _tempo = 0.0f;
        public float Tempo
        {
            get => _tempo;
            
            set
            {
                this.RaiseAndSetIfChanged(ref _tempo, value);
                for (int i = 0; i < player?.Sources.Count; i++)
                {
                    player.SetTempo(i, value);
                }
            }
        }

        private float _volume = 100.0f;
        public float Volume 
        { 
            get => _volume; 
            set
            {
                this.RaiseAndSetIfChanged(ref _volume, value);
                if (player is not null)
                    { player.Volume = value / 100; }
            }   
        }

        private TimeSpan _duration;
        public TimeSpan Duration { get => _duration; set => this.RaiseAndSetIfChanged(ref _duration, value); }

        private TimeSpan _position;
        public TimeSpan Position { get => _position; set => this.RaiseAndSetIfChanged(ref _position, value); }

        private bool _isSaveFile;
        public bool IsSaveFile { get => _isSaveFile; set { this.RaiseAndSetIfChanged(ref _isSaveFile, value); SaveFilePath = ""; } }

        private bool _isFxEnabled = false;
        public bool IsFxEnabled
        {
            get => _isFxEnabled;
            set
            {
                this.RaiseAndSetIfChanged(ref _isFxEnabled, value);
                _Fxprocessor.IsEnabled = value; 
            }
        }

        private string? _saveFilePath;
        public string? SaveFilePath { get => _saveFilePath; set => this.RaiseAndSetIfChanged(ref _saveFilePath, value); }

        private string? _playPauseText = "Play";
        public string? PlayPauseText { get => _playPauseText; set => this.RaiseAndSetIfChanged(ref _playPauseText, value); }

        public ObservableCollection<string> FileNames { get; } = new ObservableCollection<string>();

        public ObservableCollection<Log> Logs { get; } = new ObservableCollection<Log>();
        #endregion

        public void LogInfo(string message)
        {
            Dispatcher.UIThread.InvokeAsync(() => Logs.Add(new Log(message, Log.LogType.Info)));
        }

        public void LogWarning(string message)
        {
            Dispatcher.UIThread.InvokeAsync(() => Logs.Add(new Log(message, Log.LogType.Warning)));
        }

        public void LogError(string message)
        {
            Dispatcher.UIThread.InvokeAsync(() => Logs.Add(new Log(message, Log.LogType.Error)));
        }

        public void Seek(double ms)
        {
            if (player is not null && player.IsLoaded)
            {
                player.Seek(TimeSpan.FromMilliseconds(ms));
            }
               
        }

        private void AudioEngineInitialize()
        {
            AudioEngineOutputOptions _audioEngineOptions = new AudioEngineOutputOptions
            (
                device: OwnAudio.DefaultOutputDevice,
                channels: OwnAudioEngine.EngineChannels.Stereo,
                sampleRate: OwnAudio.DefaultOutputDevice.DefaultSampleRate,
                latency: OwnAudio.DefaultOutputDevice.DefaultHighOutputLatency
            );

            AudioEngineInputOptions _audioInputOptions = new AudioEngineInputOptions
            (
                device: OwnAudio.DefaultInputDevice,
                channels: OwnAudioEngine.EngineChannels.Mono,
                sampleRate: OwnAudio.DefaultInputDevice.DefaultSampleRate,
                latency: OwnAudio.DefaultInputDevice.DefaultLowInputLatency
            );

            SourceManager.OutputEngineOptions = _audioEngineOptions;
            SourceManager.InputEngineOptions = _audioInputOptions;
            SourceManager.EngineFramesPerBuffer = 512;
            
            player = SourceManager.Instance;
            player.CustomSampleProcessor = _Fxprocessor;             
            add_FXprocessor();

            player.Logger = this;             

            player.StateChanged += OnStateChanged;
            player.PositionChanged += OnPositionChanged;

            _isFFmpegInitialized = OwnAudio.IsFFmpegInitialized;

            if (!_isFFmpegInitialized)
            {
                LogError($"Decoder not initialize!");
                LogError($"Wrong file path: {OwnAudio.FFmpegPath}");
            }
        }

        private void add_FXprocessor()
        {
            /// <summary>
            /// Adjusting the following EQ parameters will emphasize the highs, 
            /// remove unnecessary lows and clean up the midrange
            /// </summary>
            Equalizer _equalizer = new Equalizer((float)SourceManager.OutputEngineOptions.SampleRate);
            // Sub-bass - Enyhe kiemelés a mély basszusban
            _equalizer.SetBandGain(band: 0, frequency: 30, q: 0.7f, gainDB: 1.2f);    // 30 Hz
            // Low bass - Enyhe vágás a tisztább hangzásért
            _equalizer.SetBandGain(band: 1, frequency: 60, q: 0.8f, gainDB: -1.0f);   // 60 Hz
            // Upper bass - Kis kiemelés a "punch" érdekében
            _equalizer.SetBandGain(band: 2, frequency: 120, q: 1.0f, gainDB: 0.8f);   // 120 Hz
            // Low mids - Enyhe vágás a "muddy" hangzás elkerülésére
            _equalizer.SetBandGain(band: 3, frequency: 250, q: 1.2f, gainDB: -2.0f);  // 250 Hz
            // Middle - Kis vágás a tisztább vokálokért
            _equalizer.SetBandGain(band: 4, frequency: 500, q: 1.4f, gainDB: -1.5f);  // 500 Hz
            // Upper mids - Enyhe kiemelés a vokál jelenlétért
            _equalizer.SetBandGain(band: 5, frequency: 2000, q: 1.0f, gainDB: -0.5f);  // 2 kHz
            // Presence - Kiemelés a részletekért
            _equalizer.SetBandGain(band: 6, frequency: 4000, q: 1.2f, gainDB: 0.6f);  // 4 kHz
            // High mids - Levegősség hozzáadása
            _equalizer.SetBandGain(band: 7, frequency: 6000, q: 1.0f, gainDB: 0.3f);  // 6 kHz
            // Highs - Csillogás
            _equalizer.SetBandGain(band: 8, frequency: 10000, q: 0.8f, gainDB: 0.8f); // 10 kHz
            // Air band - Extra fényesség
            _equalizer.SetBandGain(band: 9, frequency: 16000, q: 0.7f, gainDB: 0.8f); // 16 kHz

            // Mastering compressor
            Compressor _compressor = new Compressor
            (
                threshold: 0.5f,    // -6 dB
                ratio: 4.0f,        // 4:1 kompressziós arány
                attackTime: 100f,   // 100 ms
                releaseTime: 200f,  // 200 ms
                makeupGain: 1.0f,    // 0 dB
                sampleRate: SourceManager.OutputEngineOptions.SampleRate
            );

            // Mastering enhancer
            Enhancer _enhancer = new Enhancer
            (
                mix: 0.2f,          // 20% of the original signal is mixed back
                cutFreq: 4000.0f,   // High-pass cutoff 4000 Hz
                gain: 2.5f,         // Pre - saturation amplification  2.5x
                sampleRate: SourceManager.OutputEngineOptions.SampleRate
            );

            // Dinamikus erősítés
            DynamicAmp _dynamicAmp = new DynamicAmp
            (
                targetLevel: 0.22f,        // Alacsonyabb célszint a jobb dinamikáért
                attackTimeSeconds: 1.25f,   // Lassabb attack a természetesebb felfutásért
                releaseTimeSeconds: 2.25f,  // Hosszabb release az észrevétlen lecsengésért
                noiseThreshold: 0.035f    // Alacsony zajküszöb a halk részek megőrzéséért
            );

            _Fxprocessor.AddFx(_equalizer);
            _Fxprocessor.AddFx(_enhancer);
            _Fxprocessor.AddFx(_compressor);
            _Fxprocessor.AddFx(_dynamicAmp);
        }

        private void add_inputFxprocessor()
        {
            Reverb _reverb = new Reverb
                (
                    size: 0.45f,        // Közepes tér, hosszú reverb tail
                    damp: 0.45f,        // Mérsékelt magasfrekvenciás csillapítás
                    wet: 0.25f,         // 25% effekt - nem túl sok reverb
                    dry: 0.75f,         // 85% száraz jel - az ének érthetősége megmarad
                    stereoWidth: 0.8f,  // Jó sztereó tér, de nem túl széles
                    sampleRate: SourceManager.OutputEngineOptions.SampleRate
                );

            Delay _delay = new Delay
                (
                    time: 310,      // Delay time 370 ms
                    repeat: 0.4f,   // Rate of delayed signal feedback to the input 50%
                    mix: 0.15f,     // Delayed signal ratio in the mix 15%
                    sampleRate: SourceManager.OutputEngineOptions.SampleRate
                );

            Compressor _vocalCompressor = new Compressor
                (
                    threshold: 0.25f,   // -12 dB - az emberi hang átlagos dinamikatartományához igazítva
                    ratio: 3.0f,        // 3:1 - természetes, muzikális kompresszió
                    attackTime: 10f,    // 10 ms - elég gyors hogy elkapja a tranzenseket
                    releaseTime: 100f,  // 100 ms - követi az ének természetes lecsengését
                    makeupGain: 2.0f    // +6 dB - kompenzálja a kompressziót
                );
            
            _inputFxprocessor.AddFx(_reverb);
            _inputFxprocessor.AddFx(_delay);
            _inputFxprocessor.AddFx(_vocalCompressor);
        }

        /// <summary>
        /// Sets the type of audio files that can be opened.
        /// </summary>
        private FilePickerSaveOptions options = new FilePickerSaveOptions
        {
            Title = "Save Your Audio File",
            SuggestedFileName = "OwnAudioSaveFile.wav",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Wave File")
                {
                    Patterns = new[] { "*.wav" }
                }
            }
        };

        /// <summary>
        /// Sets the type of audio files that can be opened.
        /// </summary>
        private FilePickerFileType _audioFiles { get; } = new("Audio File")
        {
            Patterns = new[] { "*.wav", "*.flac", "*.mp3", "*.aac", "*.aiff", "*.mp4", "*.m4a", "*.ogg", "*.wma", "*.webm" },
            AppleUniformTypeIdentifiers = new[] { "public.audio" },
            MimeTypes = new[] { "audio/wav", "audio/flac", "audio/mpeg", "audio/aac", "audio/aiff", "audio/mp4", "audio/ogg", "audio/x-ms-wma", "audio/webm" }
        };
                                                                                                                    
        private async void addFileCommand()
        {
            if (MainWindow.Instance != null && _isStopRequested && _isFFmpegInitialized)
            {
                IReadOnlyList<IStorageFile> result = await MainWindow.Instance.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
                {
                    Title = "Select audio file",
                    AllowMultiple = true,
                    FileTypeFilter = new FilePickerFileType[] { _audioFiles }
                });

                string? referenceFile;
                if (result.Count > 0)
                {
                    foreach (var file in result)
                    {
                        referenceFile = file.TryGetLocalPath();
                        if(referenceFile is not null)
                        {                           
                            if(player is not null && !await player.AddOutputSource(referenceFile))
                            {
                                return;
                            }

                            FileNames.Add(String.Format("track{0}:  {1}", (_trackNumber++).ToString(), referenceFile));
                            _sourceOutputId++;
                        }
                    }

                    if (player is not null)
                    {
                        Duration = player.Duration;
                        Position = TimeSpan.Zero;
                    }

                    for (int i = 0; i < player?.Sources.Count; i++)
                    {
                        player.SetTempo(i, 0);
                        player.SetPitch(i, 0);
                    }
                }
            }
        }

        private void removeFileCommand()
        {
            if(FileNames.Count > 0 && _isStopRequested)
            {
                if (FileNames[FileNames.Count - 1].Contains("input source"))
                {
                    player?.RemoveInputSource();

                }
                else
                {
                    player?.RemoveOutputSource(_sourceOutputId);
                    _sourceOutputId--;
                }

                FileNames.RemoveAt(FileNames.Count - 1);
                _trackNumber--;
            }
                
        }

        private void inputCommand()
        {
            if (_isStopRequested && _isFFmpegInitialized)
            {
                player?.AddInputSource();

                FileNames.Add("Add new input source");
            }
        }

        private async void saveFilePathCommand()
        {
            if(MainWindow.Instance != null)
            {
                var result = await MainWindow.Instance.StorageProvider.SaveFilePickerAsync(options);
                if (result != null)
                {
                    SaveFilePath = result.TryGetLocalPath();
                }
            }
        }

        private void playPauseCommand()
        {
            if (player is not null && !player.IsLoaded) 
                return;

            _isStopRequested = false;
            if(player is not null)
            {
                player.Volume = Volume / 100;
                player.IsWriteData = IsSaveFile;
                if (!player.IsRecorded)
                {
                    player.AddInputSource();
                    SourceManager.Instance.SourcesInput[0].CustomSampleProcessor = _inputFxprocessor;
                    add_inputFxprocessor();
                }
                    
            }                                             

            if (player?.State is SourceState.Paused or SourceState.Idle) 
                if(IsSaveFile && SaveFilePath is not null)
                    player.Play(SaveFilePath, 16);
                else
                    player.Play();
            else
                player?.Pause();
        }

        private void stopCommand()
        {
            _isStopRequested = true;
            player?.Stop();
        }

        private void resetCommand()
        {
            if (!_isStopRequested)
                player?.Stop();
               
            player?.Sources.Clear();
            FileNames.Clear();
            Logs.Clear();

            Pitch = 0;
            Tempo = 0;

            _sourceOutputId = -1;

            AudioEngineInitialize();
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            if(player is not null)
                PlayPauseText = player.State is SourceState.Playing or SourceState.Buffering ? "Pause" : "Play";
        }

        private void OnPositionChanged(object? sender, EventArgs e)
        {
            if (player is not null && player.IsSeeking)
            {
                return;
            }


            if (player is not null && ((player.Position - Position).TotalSeconds > 1 || Position > player.Position))
            {
                Position = player.Position;
            }

            if (!_isStopRequested && Position == TimeSpan.Zero)
                _isStopRequested = true;
        }
    }
}
