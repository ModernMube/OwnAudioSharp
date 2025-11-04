using Ownaudio.Core;
using Ownaudio.Decoders;
using Ownaudio.Exceptions;
using Ownaudio.Processors;
using Ownaudio.Sources.Extensions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using Ownaudio.Utilities.OwnChordDetect.Analysis;
using Ownaudio.Utilities.OwnChordDetect.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Sources
{
    /// <summary>
    /// A singleton class that provides functions for mixing and playing multiple audio sources.
    /// Manages output sources, input sources, real-time sources, and handles audio mixing operations.
    /// </summary>
    [Obsolete("This is legacy code, available only for compatibility!")]
    public unsafe partial class SourceManager
    {
        /// <summary>
        /// Shared ArrayPool for all sources to reduce GC pressure.
        /// OPTIMIZATION: Pre-configured with optimal settings for audio processing.
        /// - Max array length: 1MB (1024 * 1024 floats)
        /// - Max arrays per bucket: 50 (supports ~50 concurrent sources)
        /// </summary>
        private static readonly System.Buffers.ArrayPool<float> SharedAudioPool =
            System.Buffers.ArrayPool<float>.Create(maxArrayLength: 1024 * 1024, maxArraysPerBucket: 50);

        /// <summary>
        /// Buffer for temporarily storing audio data that needs to be written to file.
        /// Used when file writing operations are in progress to prevent data loss.
        /// </summary>
        private List<float> writedDataBuffer = new List<float>();

        /// <summary>
        /// Indicates whether this instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Static lock object for thread-safe singleton initialization.
        /// </summary>
        private static readonly object _lock = new object();
        
        /// <summary>
        /// The singleton instance of the SourceManager.
        /// </summary>
        private static SourceManager? _instance;

        /// <summary>
        /// Lock object for synchronizing file write operations.
        /// </summary>
        private object writeLock = new object();
        
        /// <summary>
        /// Flag indicating whether a file write operation is currently in progress.
        /// </summary>
        private bool isWriting = false;
        
        /// <summary>
        /// File path for the temporary raw audio data file during recording operations.
        /// </summary>
        private string writefilePath = "";

        /// <summary>
        /// Initializes a new instance of the <see cref="SourceManager"/> class.
        /// Sets up the volume processor with default volume settings.
        /// </summary>
        /// <remarks>
        /// This constructor is private to enforce the singleton pattern.
        /// The volume processor is initialized to 100% volume (1.0f).
        /// </remarks>
        private SourceManager()
        {
            VolumeProcessor = new VolumeProcessor { Volume = 1 };
        }

        /// <summary>
        /// Provides access to the shared ArrayPool for audio buffer management.
        /// OPTIMIZATION: All sources should use this pool to minimize GC allocations.
        /// </summary>
        /// <returns>Shared ArrayPool instance configured for audio processing.</returns>
        public static System.Buffers.ArrayPool<float> GetSharedAudioPool() => SharedAudioPool;

        /// <summary>
        /// Gets the singleton instance of the <see cref="SourceManager"/>.
        /// </summary>
        /// <value>
        /// The single instance of SourceManager, created on first access.
        /// </value>
        /// <remarks>
        /// This property implements thread-safe lazy initialization using double-checked locking pattern.
        /// The instance is created only when first accessed and the same instance is returned for all subsequent calls.
        /// </remarks>
        public static SourceManager Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new SourceManager();
                    }
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Ensures that source dependencies are maintained according to the defined rules.
        /// </summary>
        private void EnsureSourceDependencies()
        {
            bool hasOutputSources = Sources.Any(s => s is not SourceWithoutData);
            bool hasInputSources = SourcesInput.Count > 0;
            bool hasRealTimeSources = Sources.Any(s => s is SourceSound);
            bool hasSparkSources = SourcesSpark.Count > 0;
            bool hasEmptySource = Sources.Any(s => s is SourceWithoutData);

            // If there are InputSource, RealTimeSource, or SparkSource but no Output source, then EmptySource is needed
            if ((hasInputSources || hasRealTimeSources || hasSparkSources) && !hasOutputSources && !hasEmptySource)
            {
                var emptySource = new SourceWithoutData { Name = "WithoutData" };
                Sources.Add(emptySource);
                Logger?.LogInfo("EmptySource automatically added due to dependency rules.");
            }

            // If there are no InputSource, RealTimeSource, or SparkSource, but there is EmptySource, it should be removed
            if (!hasInputSources && !hasRealTimeSources && !hasSparkSources && hasEmptySource && hasOutputSources)
            {
                var emptySource = Sources.FirstOrDefault(s => s is SourceWithoutData);
                if (emptySource != null)
                {
                    Sources.Remove(emptySource);
                    Logger?.LogInfo("EmptySource automatically removed as it's no longer needed.");
                }
            }
        }

        /// <summary>
        /// Checks if a new source of the specified type can be added.
        /// </summary>
        /// <param name="sourceType">The type of source to be added</param>
        /// <param name="errorMessage">Error message if addition is not possible</param>
        /// <returns>True if the source can be added</returns>
        private bool CanAddSource(Type sourceType, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (sourceType == typeof(Source))
            {
                int currentSourceCount = Sources.Count(s => s is Source);
                if (currentSourceCount >= 10)
                {
                    errorMessage = "Maximum 10 Source instances can be added simultaneously.";
                    return false;
                }
            }
            else if (sourceType == typeof(SourceWithoutData))
            {
                if (Sources.Any(s => s is SourceWithoutData))
                {
                    errorMessage = "Only one EmptySource can exist at a time.";
                    return false;
                }
            }
            else if (sourceType == typeof(SourceInput))
            {
                if (SourcesInput.Count >= 1)
                {
                    errorMessage = "Only one InputSource can exist at a time.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks if a source can be removed without violating dependency rules.
        /// </summary>
        /// <param name="sourceToRemove">The source to be removed</param>
        /// <param name="errorMessage">Error message if removal would be problematic</param>
        /// <returns>True if the source can be safely removed</returns>
        private bool CanRemoveSource(ISource sourceToRemove, out string errorMessage)
        {
            errorMessage = string.Empty;

            // EmptySource cannot be removed manually - it's managed automatically
            if (sourceToRemove is SourceWithoutData)
            {
                errorMessage = "EmptySource cannot be removed manually - it's managed automatically.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if a SparkSource can be removed and handles EmptySource dependency.
        /// </summary>
        /// <param name="sparkToRemove">The SparkSource to be removed</param>
        /// <returns>True if removal is successful</returns>
        private bool HandleSparkSourceRemoval(SourceSpark sparkToRemove)
        {
            bool isLastSparkSource = SourcesSpark.Count == 1 && SourcesSpark.Contains(sparkToRemove);

            if (isLastSparkSource)
            {
                // Check if EmptySource needs to be removed after this SparkSource removal
                bool hasInputSources = SourcesInput.Count > 0;
                bool hasRealTimeSources = Sources.Any(s => s is SourceSound);
                bool hasEmptySource = Sources.Any(s => s is SourceWithoutData);
                bool hasOutputSources = Sources.Any(s => s is not SourceWithoutData);

                // If this is the last SparkSource and there are no other dependencies for EmptySource
                if (!hasInputSources && !hasRealTimeSources && hasEmptySource && hasOutputSources)
                {
                    var emptySource = Sources.FirstOrDefault(s => s is SourceWithoutData);
                    if (emptySource != null)
                    {
                        Sources.Remove(emptySource);
                        Logger?.LogInfo("EmptySource automatically removed after last SparkSource removal.");
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Adds a new output source from the specified URL or file path.
        /// </summary>
        /// <param name="url">The URL or file path of the audio source to add.</param>
        /// <param name="name">Optional name for the source (default is "Output").</param>
        /// <returns>A task that represents the asynchronous operation. The task result indicates whether the source was successfully loaded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the URL parameter is null.</exception>
        /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running or source limit is reached.</exception>
        public Task<bool> AddOutputSource(string url, string? name = "Output")
        {
            Ensure.NotNull(url, nameof(url));
            Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

            // Check if source can be added before creating it
            if (!CanAddSource(typeof(Source), out string errorMessage))
            {
                throw new OwnaudioException(errorMessage);
            }

            Source _source = new Source();
            _source.LoadAsync(url).Wait();
            _source.Name = name ?? "Output";

            if (!_source.IsLoaded)
            {
                return Task.FromResult(false);
            }

            Sources.Add(_source);

            if (_source.Duration.TotalMilliseconds > Duration.TotalMilliseconds)
                Duration = _source.Duration;

            IsLoaded = _source.IsLoaded;
            UrlList.Add(url);

            // Check and ensure source dependencies
            EnsureSourceDependencies();

            SetAndRaisePositionChanged(TimeSpan.Zero);
            Logger?.LogInfo($"Source added: {url}");

            return Task.FromResult(IsLoaded);
        }

        /// <summary>
        /// Adds an input source for recording or real-time audio input.
        /// </summary>
        /// <param name="inputVolume">The initial volume level for the input source (default: 0.0f).</param>
        /// <param name="name">Optional name for the input source (default: "Input").</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="OwnaudioException">Thrown when input limit is reached or input device is not available.</exception>
        public Task<bool> AddInputSource(float inputVolume = 0f, string? name = "Input")
        {
            if (!CanAddSource(typeof(SourceInput), out string errorMessage))
            {
                throw new OwnaudioException(errorMessage);
            }

            if (!IsRecorded)
            {
                if (OwnAudioEngine.DefaultInputDevice.State == Core.AudioDeviceState.Active)
                {
                    SourceInput _inputSource = new SourceInput(InputEngineOptions);
                    _inputSource.Name = name ?? "Input";
                    SourcesInput.Add(_inputSource);

                    if (_inputSource is not null)
                    {
                        IsRecorded = true;
                    }
                }
                else
                {
                    throw new OwnaudioException("No input device available or device has no input channels.");
                }
            }

            if (IsRecorded)
            {
                SourcesInput[SourcesInput.Count - 1].Volume = inputVolume;
            }

            // Check and ensure source dependencies
            EnsureSourceDependencies();

            Logger?.LogInfo("InputSource added.");
            return Task.FromResult(IsRecorded);
        }

        /// <summary>
        /// Adds a new real-time sample-based source to the audio mix.
        /// </summary>
        /// <param name="initialVolume">The initial volume level for the source (default: 1.0f).</param>
        /// <param name="dataChannels">The number of audio channels for the input data (default: 2 for stereo).</param>
        /// <param name="name">Optional name for the source (default: "Realtime").</param>
        /// <returns>The created <see cref="SourceSound"/> instance.</returns>
        public SourceSound AddRealTimeSource(float initialVolume = 1.0f, int dataChannels = 2, string? name = "Realtime")
        {
            var source = new SourceSound(dataChannels)
            {
                Volume = initialVolume,
                Logger = Logger,
                Name = name ?? "Realtime"
            };

            Sources.Add(source);

            // Optionally, set the maximum Duration value to 10 seconds
            if (Duration.TotalMilliseconds < 10000)
                Duration = TimeSpan.FromMilliseconds(10000);

            // Check and ensure source dependencies
            EnsureSourceDependencies();

            SetAndRaisePositionChanged(TimeSpan.Zero);
            Logger?.LogInfo("Real-time source added.");

            return source;
        }

        /// <summary>
        /// Adds a new simple source for sound effects
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="looping">Whether the source should loop</param>
        /// <param name="volume">Initial volume level</param>
        /// <returns>The created SimpleSource instance</returns>
        public SourceSpark AddSparkSource(string filePath, bool looping = false, float volume = 1.0f)
        {
            var sparkSource = new SourceSpark(filePath, looping)
            {
                Volume = volume,
                Logger = Logger
            };

            SourcesSpark.Add(sparkSource);

            // Check and ensure source dependencies after adding SparkSource
            EnsureSourceDependencies();

            Logger?.LogInfo($"Spark source added: {filePath}");

            return sparkSource;
        }

        /// <summary>
        /// Removes an output source by its index in the sources collection.
        /// </summary>
        /// <param name="SourceID">The zero-based index of the source to remove.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the Sources collection is null.</exception>
        /// <exception cref="OwnaudioException">Thrown when the specified source ID does not exist or cannot be removed.</exception>
        public Task<bool> RemoveOutputSource(int SourceID)
        {
            Ensure.NotNull(Sources, nameof(Sources));
            Ensure.That<OwnaudioException>(SourceID < Sources.Count, "Output source id not exist.");

            var sourceToRemove = Sources[SourceID];

            if (!CanRemoveSource(sourceToRemove, out string errorMessage))
            {
                throw new OwnaudioException(errorMessage);
            }

            try
            {
                Sources.RemoveAt(SourceID);

                // Update URL list - only for Source type sources
                if (sourceToRemove is Source && SourceID < UrlList.Count)
                {
                    UrlList.RemoveAt(SourceID);
                }

                // Check and ensure source dependencies after removal
                EnsureSourceDependencies();

                SetAndRaisePositionChanged(TimeSpan.Zero);
                Logger?.LogInfo($"Output source removed at index {SourceID}.");

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error removing output source: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Removes all input sources and resets the recording state.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public Task<bool> RemoveInputSource()
        {
            try
            {
                if (IsRecorded)
                {
                    SourcesInput.Clear();
                    IsRecorded = false;
                }

                // Check and ensure source dependencies after InputSource removal
                EnsureSourceDependencies();

                Logger?.LogInfo("Input source removed.");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error removing input source: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Removes a specific real-time source from the audio mix.
        /// </summary>
        /// <param name="source">The <see cref="SourceSound"/> instance to remove.</param>
        /// <returns>True if the source was found and removed successfully; otherwise, false.</returns>
        public bool RemoveRealtimeSource(SourceSound source)
        {
            if (!Sources.Contains(source))
            {
                return false;
            }

            try
            {
                source.Dispose();
                Sources.Remove(source);

                // Check and ensure source dependencies after RealTimeSource removal
                EnsureSourceDependencies();

                Logger?.LogInfo("Real-time source removed.");
                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error removing real-time source: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes a spark source
        /// </summary>
        /// <param name="sparkSource">The spark source to remove</param>
        /// <returns>True if successfully removed</returns>
        public bool RemoveSparkSource(SourceSpark sparkSource)
        {
            if (!SourcesSpark.Contains(sparkSource))
            {
                return false;
            }

            try
            {
                sparkSource.Stop();
                sparkSource.Dispose();
                SourcesSpark.Remove(sparkSource);

                // Handle SparkSource removal dependencies
                HandleSparkSourceRemoval(sparkSource);

                // Check and ensure source dependencies after SparkSource removal
                EnsureSourceDependencies();

                Logger?.LogInfo("Spark source removed.");
                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error removing spark source: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes all real-time sources from the audio mix.
        /// </summary>
        /// <returns>The number of sources that were removed.</returns>
        public int RemoveAllRealtimeSources()
        {
            var realTimeSources = Sources.OfType<SourceSound>().ToList();
            int removedCount = 0;

            foreach (var source in realTimeSources)
            {
                if (RemoveRealtimeSource(source))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        // New helper method: remove all Spark sources
        /// <summary>
        /// Removes all spark sources from the audio mix.
        /// </summary>
        /// <returns>The number of sources that were removed.</returns>
        public int RemoveAllSparkSources()
        {
            var sparkSources = SourcesSpark.ToList();
            int removedCount = 0;

            foreach (var source in sparkSources)
            {
                if (RemoveSparkSource(source))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        /// <summary>
        /// Gets a summary of current source configuration for debugging purposes.
        /// </summary>
        /// <returns>A string containing information about all current sources.</returns>
        public string GetSourcesSummary()
        {
            var summary = new System.Text.StringBuilder();
            summary.AppendLine("=== Source Manager Status ===");
            summary.AppendLine($"Total Sources: {Sources.Count}");
            summary.AppendLine($"Input Sources: {SourcesInput.Count}");
            summary.AppendLine($"Spark Sources: {SourcesSpark.Count}");
            summary.AppendLine($"Is Loaded: {IsLoaded}");
            summary.AppendLine($"Is Recorded: {IsRecorded}");
            summary.AppendLine($"State: {State}");
            summary.AppendLine();

            summary.AppendLine("Output Sources:");
            for (int i = 0; i < Sources.Count; i++)
            {
                var source = Sources[i];
                string sourceType = source.GetType().Name;
                summary.AppendLine($"  [{i}] {sourceType}: {source.Name}");
            }

            if (SourcesInput.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Input Sources:");
                for (int i = 0; i < SourcesInput.Count; i++)
                {
                    var source = SourcesInput[i];
                    summary.AppendLine($"  [{i}] {source.GetType().Name}: {source.Name}");
                }
            }

            if (SourcesSpark.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Spark Sources:");
                for (int i = 0; i < SourcesSpark.Count; i++)
                {
                    var source = SourcesSpark[i];
                    summary.AppendLine($"  [{i}] {source.GetType().Name}: {source.Name}");
                }
            }

            return summary.ToString();
        }

        /// <summary>
        /// Validates the current source configuration against the defined rules.
        /// </summary>
        /// <returns>A list of validation errors, empty if configuration is valid.</returns>
        public List<string> ValidateSourceConfiguration()
        {
            var errors = new List<string>();

            // Check Source count limit
            int sourceCount = Sources.Count(s => s is Source);
            if (sourceCount > 10)
            {
                errors.Add($"Too many Source instances: {sourceCount}/10 maximum allowed.");
            }

            // Check InputSource count limit
            if (SourcesInput.Count > 1)
            {
                errors.Add($"Too many InputSource instances: {SourcesInput.Count}/1 maximum allowed.");
            }

            // Check EmptySource count limit
            int emptySourceCount = Sources.Count(s => s is SourceWithoutData);
            if (emptySourceCount > 1)
            {
                errors.Add($"Too many EmptySource instances: {emptySourceCount}/1 maximum allowed.");
            }

            // Check EmptySource dependency rules
            bool hasInputSources = SourcesInput.Count > 0;
            bool hasRealTimeSources = Sources.Any(s => s is SourceSound);
            bool hasSparkSources = SourcesSpark.Count > 0;
            bool hasOutputSources = Sources.Any(s => s is not SourceWithoutData);
            bool hasEmptySource = Sources.Any(s => s is SourceWithoutData);

            // EmptySource should exist if there are dependent sources but no output sources
            if ((hasInputSources || hasRealTimeSources || hasSparkSources) && !hasOutputSources && !hasEmptySource)
            {
                errors.Add("EmptySource is required when InputSource, RealTimeSource, or SparkSource exists without output sources.");
            }

            // EmptySource should not exist if there are no dependent sources and there are output sources
            if (!hasInputSources && !hasRealTimeSources && !hasSparkSources && hasEmptySource && hasOutputSources)
            {
                errors.Add("EmptySource should not exist when there are output sources but no dependent sources.");
            }

            return errors;
        }

        /// <summary>
        /// Retrieves the <see cref="ISource"/> instance with the given name by name.
        /// </summary>
        /// <param name="name">The name of the source to retrieve. Cannot be null or empty.</param>
        /// <returns></returns>
        public ISource this[string name]
        {
            get
            {
                // Search in Sources collection
                var source = Sources.FirstOrDefault(s => s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
                if (source != null)
                    return source;

                // Search in SourcesInput collection
                source = SourcesInput.FirstOrDefault(s => s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
                if (source != null)
                    return source;

                // Search in SourcesSpark collection
                var sparkSource = SourcesSpark.FirstOrDefault(s => s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
                if (sparkSource != null)
                    return sparkSource;

                throw new ArgumentException($"Source with name '{name}' not found.", nameof(name));

            }
        }

        /// <summary>
        /// Retrieves the index of the first source in the collection whose name matches the specified value.
        /// </summary>
        /// <param name="name">The name of the source to search for. The comparison is case-insensitive.</param>
        /// <returns>The zero-based index of the first matching source, or -1 if no source with the specified name is found.</returns>
        public int GetSourceIndex(string name)
        {
            return Sources.FindIndex(s => s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        }

        /// <summary>
        /// Detects musical chords based on the specified audio source. 
        /// It also records the timing associated with the chord.
        /// </summary>
        /// <param name="sourceName"></param>
        /// <param name="intervalSecond"> The time interval in seconds for chord detection (default is 1.0 second)</param>
        /// <returns></returns>
        public (List<TimedChord>, MusicalKey, int) DetectChords(
            string sourceName,
            float intervalSecond = 1.0f)
        {

            TimeSpan _pos = this[sourceName].Position;

            if (!File.Exists(this[sourceName].CurrentUrl))
                throw new OwnaudioException("Source is not loaded.");

            if (this[sourceName].State != SourceState.Idle)
                throw new OwnaudioException("Source is not in idle state. Cannot detect chords while playing or buffering.");


            #nullable disable
            IAudioDecoder _decoder = AudioDecoderFactory.Create(this[sourceName].CurrentUrl, 22050, 1);
            var _result = _decoder.DecodeAllFrames(new TimeSpan(0));
            var _waveBuffer = new WaveBuffer(MemoryMarshal.Cast<byte, float>(_result.Frame.Data));
            #nullable restore

            using var model = new Model();
            var modelOutput = model.Predict(_waveBuffer, progress =>
            {
                /* Handle progress updates if needed */
                Console.Write($"\rRecognizing musical notes: {progress:P1}");
            });
            Console.WriteLine(" ");

            //Fine-tuning musical note recognition
            var convertOptions = new NotesConvertOptions
            {
                OnsetThreshold = 0.5f,      // Sound onset sensitivity
                FrameThreshold = 0.2f,      // Sound detection threshold
                MinNoteLength = 15,         // Minimum sound length (ms)
                MinFreq = 90f,              // Min frequency (Hz)
                MaxFreq = 2800f,            // Max frequency (Hz)
                IncludePitchBends = false,   // Pitch bend detection
                MelodiaTrick = true      // Harmonic detection
            };

            var converter = new NotesConverter(modelOutput);
            List<Utilities.Extensions.Note> rawNotes = converter.Convert(convertOptions);

            int detectTempo = MidiWriter.DetectTempo(rawNotes);

            //Fine - tuning musical chord recognition
            var analyzer = new SongChordAnalyzer(
                    windowSize: intervalSecond,        // 1 second windows
                    hopSize: 0.5f,           // 0.25 steps per second
                    minimumChordDuration: 1.0f, // Min 1.0 second chord
                    confidence: 0.90f       // Minimum 90% reliability
                );

            var chords = analyzer.AnalyzeSong(rawNotes);
            MusicalKey? detectedKey = analyzer.DetectedKey;

            this[sourceName].Seek(_pos);
            _decoder.Dispose();
            #nullable disable
            return (chords, detectedKey, detectTempo);
            #nullable restore
        }

        /// <summary>
        /// Starts playback of mixed sources with audio recording to the specified file.
        /// </summary>
        /// <param name="fileName">The file path where the mixed audio will be saved.</param>
        /// <param name="bitPerSamples">The bit depth for the output audio file (e.g., 16, 24, 32).</param>
        /// <remarks>
        /// This method configures the system for recording the mixed audio output:
        /// - Enables data writing mode
        /// - Sets up the output file path and temporary raw data file
        /// - Deletes any existing temporary files
        /// - Configures the bit depth for the output
        /// - Starts normal playback which will now include recording
        /// 
        /// The audio data is temporarily written to a raw file during playback and then
        /// converted to the final wave format when playback stops.
        /// </remarks>
        public void Play(string fileName, short bitPerSamples)
        {
            IsWriteData = true;
            
            SaveWaveFileName = fileName;
            
            string directoryPath = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
            writefilePath = Path.Combine(directoryPath, "writeaudiodata.raw");
            
            if(File.Exists(writefilePath))
                { File.Delete(writefilePath); }
            
            BitPerSamples = bitPerSamples;

            this.Play();             
        }

        /// <summary>
        /// Starts playback of all mixed audio sources.
        /// </summary>
        public void Play()
        {
            if (State is SourceState.Playing or SourceState.Buffering)
            {
                return;
            }

            if (State == SourceState.Paused)
            {
                SetAndRaiseStateChanged(SourceState.Playing);
                return;
            }

            if (Position.TotalMilliseconds >= Duration.TotalMilliseconds)
            {
                SetAndRaisePositionChanged(TimeSpan.Zero);
            }

            // Automatically add EmptySource if needed
            EnsureSourceDependencies();

            if (InitializeEngine())
            {
                EnsureThreadsDone();

                SetAndRaiseStateChanged(SourceState.Playing);

                Seek(Position);

                //Thread.Sleep(100);

                MixEngineThread = new Thread(MixEngine)
                {
                    Name = "Mix Engine Thread",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };

                if (IsRecorded && SourcesInput.Count > 0)
                    SourcesInput.Any(i => i.State == SourceState.Recording);

                if (Engine?.OwnAudioEngineStopped() != 0)
                    Engine?.Start();

                MixEngineThread.Start();
            }
            else
            {
                Debug.WriteLine("Engine Initialization Error!");
                return;
            }
        }

        /// <summary>
        /// Plays a simple source immediately
        /// </summary>
        /// <param name="sparkSource">The spark source to play</param>
        public void PlaySparkSource(SourceSpark sparkSource)
        {
            if (SourcesSpark.Contains(sparkSource))
            {
                sparkSource.Play();
            }
        }

        /// <summary>
        /// Pauses the playback of all audio sources.
        /// </summary>
        /// <remarks>
        /// This method pauses playback only if the current state is Playing or Buffering.
        /// The pause operation affects all sources in the mix and can be resumed later by calling Play().
        /// The audio engine continues running but no new audio data is processed while paused.
        /// </remarks>
        public void Pause()
        {
            if (State is SourceState.Playing or SourceState.Buffering)
                SetAndRaiseStateChanged(SourceState.Paused);
        }

        /// <summary>
        /// Stops playback completely and resets all audio processing.
        /// </summary>
        /// <remarks>
        /// This method performs a complete shutdown of audio operations:
        /// - Checks if already idle and returns early if so
        /// - Resets playback state for all sources
        /// - Terminates all background processing threads
        /// - Raises the StateChanged event
        /// - Stops the audio engine if it's running (but keeps it alive for restart)
        ///
        /// After calling this method, the system returns to its initial idle state
        /// and Play() can be called to resume audio operations.
        /// </remarks>
        public void Stop()
        {
            if(State == SourceState.Idle)
                return;

            ResetPlayback();

            EnsureThreadsDone();

            StateChanged?.Invoke(this, EventArgs.Empty);

            if (Engine?.OwnAudioEngineStopped() == 0)
            {
                Engine.Stop();
            }
        }

        /// <summary>
        /// Synchronizes all sources to the exact same position for perfect alignment.
        /// This is called internally during Play() to ensure sample-accurate synchronization.
        /// </summary>
        /// <param name="targetPosition">The exact position all sources should seek to.</param>
        /// <remarks>
        /// This method ensures frame-accurate synchronization by:
        /// - Sequentially seeking each source to the exact same position
        /// - Clearing all buffered data from each source's queue
        /// - Verifying each source reaches the target position before proceeding
        /// - Using a strict tolerance (10ms) for position verification
        /// - Retrying up to 3 times if a source doesn't reach the exact position
        /// </remarks>
        private void SynchronizeAllSources(TimeSpan targetPosition)
        {
            const int maxRetries = 3;
            const double toleranceMs = 10.0;

            Logger?.LogInfo($"Synchronizing all {Sources.Count} sources to position: {targetPosition}");

            // Sequential synchronization for maximum accuracy
            foreach (ISource src in Sources)
            {
                int retryCount = 0;
                bool synchronized = false;

                while (!synchronized && retryCount < maxRetries)
                {
                    // Set seeking flag to prevent race conditions
                    src.IsSeeking = true;

                    // Seek to target position (this will clear buffers internally)
                    src.Seek(targetPosition);

                    Thread.Sleep(30); // Allow seek to complete and buffers to stabilize

                    src.IsSeeking = false;

                    // Verify position accuracy
                    double positionDiff = Math.Abs((src.Position - targetPosition).TotalMilliseconds);
                    if (positionDiff <= toleranceMs)
                    {
                        synchronized = true;
                        Logger?.LogInfo($"Source '{src.Name}' synchronized to {src.Position} (diff: {positionDiff:F2}ms)");
                    }
                    else
                    {
                        retryCount++;
                        Logger?.LogWarning($"Source '{src.Name}' sync retry {retryCount}/{maxRetries} (diff: {positionDiff:F2}ms)");
                        Thread.Sleep(10); // Brief pause before retry
                    }
                }

                if (!synchronized)
                {
                    Logger?.LogError($"Source '{src.Name}' failed to synchronize after {maxRetries} attempts");
                }
            }

            // Final verification: check all sources are within tolerance
            if (Sources.Count > 1)
            {
                TimeSpan minPos = Sources.Min(s => s.Position);
                TimeSpan maxPos = Sources.Max(s => s.Position);
                double spread = (maxPos - minPos).TotalMilliseconds;

                Logger?.LogInfo($"Final sync verification - Position spread: {spread:F2}ms (min: {minPos}, max: {maxPos})");

                if (spread > toleranceMs * 2)
                {
                    Logger?.LogWarning($"Sources may not be perfectly synchronized. Spread: {spread:F2}ms");
                }
            }
        }

        /// <summary>
        /// Seeks all audio sources to the specified position.
        /// </summary>
        /// <param name="position">The target position to seek to.</param>
        /// <remarks>
        /// This method performs synchronized seeking across all sources:
        /// - Sets the seeking flag to prevent conflicts
        /// - Elevates thread priority for smooth seeking
        /// - Temporarily switches to buffering state during seek operation
        /// - Uses parallel processing to seek all sources simultaneously
        /// - Verifies seek accuracy and retries if necessary (within 50ms tolerance)
        /// - Restores the previous playback state after seeking
        /// - Includes comprehensive logging of the operation
        ///
        /// The method ensures all sources remain synchronized after the seek operation
        /// and handles any sources that may not seek to the exact requested position.
        /// </remarks>
        public void Seek(TimeSpan position)
        {
            if (!IsLoaded || IsSeeking)
                return;

            // Clamp position to valid range [0, Duration]
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (position > Duration)
                position = Duration;

            IsSeeking = true;

            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            bool wasPlaying = State == SourceState.Playing;
            if (wasPlaying)
            {
                SetAndRaiseStateChanged(SourceState.Buffering);
            }

            try
            {
                // Set IsSeeking flag on all sources FIRST - prevents drift correction interference
                foreach (ISource src in Sources)
                {
                    src.IsSeeking = true;
                }

                // CRITICAL: Wait longer for ALL threads to see IsSeeking flag and pause
                // Decoder thread polls every 10ms, Engine thread every 1ms
                // 50ms ensures both threads definitely see the flag and stop processing
                Thread.Sleep(50);

                Parallel.ForEach(Sources, src =>
                {
                    src.Seek(position);
                });

                Logger?.LogInfo($"Seeking to: {position}.");

                SetAndRaisePositionChanged(position);

                // CRITICAL: Wait for new data to start being decoded after seek
                // Decoder needs time to decode first frames at new position
                // 100ms ensures minimum buffering before resuming playback
                Thread.Sleep(30 * Sources.Count);
            }
            finally
            {
                foreach (ISource src in Sources)
                {
                    if (Math.Abs((src.Position - position).TotalMilliseconds) > 50)
                    {
                        src.Seek(position);
                    }
                    src.IsSeeking = false;
                }

                if (wasPlaying)
                {
                    SetAndRaiseStateChanged(SourceState.Playing);
                }

                // CRITICAL: Record seek timestamp to prevent drift correction immediately after
                _lastSeekTicks = DateTime.UtcNow.Ticks;

                IsSeeking = false;
                Logger?.LogInfo($"Successfully seeks to {position}.");
            }
        }

        /// <summary>
        /// Completely resets the SourceManager and the entire OwnAudio API to its initial state.
        /// </summary>
        /// <param name="resetGlobalSettings">If true, also resets global engine options to defaults.</param>
        /// <returns>True if the reset operation was successful; otherwise, false.</returns>
        /// <remarks>
        /// This method performs a comprehensive system reset:
        /// - Stops all playback and terminates threads
        /// - Stops and disposes the audio engine
        /// - Disposes and clears all output, input, and spark sources
        /// - Clears all URL lists and buffers
        /// - Resets all state variables (duration, position, flags)
        /// - Writes any pending recorded data to file
        /// - Clears audio data buffers and buffer pools
        /// - Resets volume and custom processors to defaults
        /// - Optionally resets global engine configuration
        /// - Raises appropriate events
        /// 
        /// The audio engine is left in a state where it can be reinitialized for new operations.
        /// This method includes comprehensive error handling and logging.
        /// </remarks>
        public bool Reset(bool resetGlobalSettings = false)
        {
            try
            {
                Logger?.LogInfo("Resetting SourceManager and OwnAudio API to initial state...");

                // Stop all playback and ensure threads are done
                if (State != SourceState.Idle)
                {
                    SetAndRaiseStateChanged(SourceState.Idle);
                    EnsureThreadsDone();
                }

                // Stop and dispose audio engine
                if (Engine?.OwnAudioEngineStopped() == 0)
                {
                    Engine?.Stop();
                }

                // Dispose and clear all sources
                foreach (ISource src in Sources.ToList())
                {
                    try
                    {
                        src.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Error disposing source '{src.Name}': {ex.Message}");
                    }
                }

                foreach (ISource inputSrc in SourcesInput.ToList())
                {
                    try
                    {
                        inputSrc.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Error disposing input source '{inputSrc.Name}': {ex.Message}");
                    }
                }

                foreach (SourceSpark spark in SourcesSpark.ToList())
                {
                    try
                    {
                        spark.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Error disposing spark source '{spark.Name}': {ex.Message}");
                    }
                }

                // Clear all collections
                Sources.Clear();
                SourcesInput.Clear();
                SourcesSpark.Clear();
                UrlList.Clear();

                // Reset all state variables
                Duration = TimeSpan.Zero;
                Position = TimeSpan.Zero;
                IsLoaded = false;
                IsRecorded = false;
                IsSeeking = false;
                IsWriteData = false;

                // Write any pending recorded data to file and clear buffers
                try
                {
                    writeDataToFile();
                    writedDataBuffer.Clear();
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Error writing final recorded data: {ex.Message}");
                }

                // Clear audio buffer pool
                try
                {
                    SimpleAudioBufferPool.Clear();
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Error clearing buffer pool: {ex.Message}");
                }

                // Reset processors to default values
                Volume = 1.0f;
                CustomSampleProcessor = new DefaultProcessor();
                OutputLevels = (0f, 0f);
                InputLevels = (0f, 0f);

                // Reset engine-specific variables
                SaveWaveFileName = null;
                BitPerSamples = OutputEngineOptions.SampleRate;

                // Dispose and nullify engine
                Engine?.Dispose();
                Engine = null;

                // Reset global engine options if requested
                if (resetGlobalSettings)
                {
                    OutputEngineOptions = AudioConfig.Default;
                    InputEngineOptions = AudioConfig.Default;
                    EngineFramesPerBuffer = 512;

                    Logger?.LogInfo("Global engine options reset to defaults.");
                }

                // Reset internal buffers and variables
                _mixBuffer = null;
                _lastMixBufferSize = 0;
                _levelCalculationBuffer = null;

                // Ensure tasks are completed
                if (!_levelCalculationTask.IsCompleted)
                {
                    try
                    {
                        _levelCalculationTask.Wait(TimeSpan.FromSeconds(1));
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Error waiting for level calculation task: {ex.Message}");
                    }
                }

                if (!_fileSaveTask.IsCompleted)
                {
                    try
                    {
                        _fileSaveTask.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Error waiting for file save task: {ex.Message}");
                    }
                }

                // Reset tasks to completed state
                _levelCalculationTask = Task.CompletedTask;
                _fileSaveTask = Task.CompletedTask;

                // Clean up temporary files
                try
                {
                    if (!string.IsNullOrEmpty(writefilePath) && File.Exists(writefilePath))
                    {
                        File.Delete(writefilePath);
                        writefilePath = "";
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Error cleaning up temporary files: {ex.Message}");
                }

                // Reset synchronization objects
                lock (writeLock)
                {
                    isWriting = false;
                }

                // Raise final state and position change events
                SetAndRaiseStateChanged(SourceState.Idle);
                SetAndRaisePositionChanged(TimeSpan.Zero);

                // Force garbage collection to clean up disposed objects
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Logger?.LogInfo("SourceManager and OwnAudio API successfully reset to initial state.");
                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error resetting SourceManager and OwnAudio API: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Resets only the SourceManager without affecting global settings.
        /// This is a convenience method that calls Reset(false).
        /// </summary>
        /// <returns>True if the reset operation was successful; otherwise, false.</returns>
        public bool ResetSourceManager()
        {
            return Reset(false);
        }

        /// <summary>
        /// Performs a full system reset including global engine settings.
        /// This is a convenience method that calls Reset(true).
        /// </summary>
        /// <returns>True if the reset operation was successful; otherwise, false.</returns>
        public bool ResetAll()
        {
            return Reset(true);
        }

        /// <summary>
        /// Gets unified decoder options for consistent audio format across all sources.
        /// </summary>
        /// <returns>instance configured with the output engine's sample rate and channel settings.</returns>
        /// <remarks>
        /// This method ensures all audio sources are decoded to the same format:
        /// - Uses the output engine's channel configuration
        /// - Uses the output engine's sample rate
        /// - Provides consistent audio format for mixing operations
        /// 
        /// Having unified decoder options is essential for proper audio mixing, as all sources
        /// must have the same sample rate and channel configuration to be mixed together properly.
        /// </remarks>
        public static (int samplerate, int channels) GetUnifiedDecoderOptions()
        {
            int outputChannels = (int)OutputEngineOptions.Channels;
            int outputSampleRate = OutputEngineOptions.SampleRate;

            return new (outputSampleRate, outputChannels);
        }

        /// <summary>
        /// Ensures that the mixing engine thread is properly terminated and cleaned up.
        /// </summary>
        /// <remarks>
        /// This method safely terminates the background mixing thread:
        /// - Calls EnsureThreadDone() to wait for proper thread termination
        /// - Sets the thread reference to null to enable garbage collection
        /// 
        /// This method should be called before starting new threads or during disposal
        /// to prevent thread leaks and ensure clean shutdown.
        /// </remarks>
        private void EnsureThreadsDone()
        {
            MixEngineThread?.EnsureThreadDone();

            MixEngineThread = null;
        }
        
        /// <summary>
        /// Writes recorded audio data to the specified wave file.
        /// </summary>
        /// <remarks>
        /// This method handles the conversion of raw audio data to a wave file:
        /// - Checks if data writing mode is enabled and files exist
        /// - Uses a background task to avoid blocking the main thread
        /// - Calls WriteWaveFile.WriteFile with the configured parameters
        /// - Uses the default sample rate from the output device
        /// - Assumes stereo (2-channel) output
        /// - Cleans up temporary files after successful write
        /// - Resets the write data mode and filename
        /// 
        /// The method includes error handling for file deletion operations and ignores
        /// exceptions that may occur during temporary file cleanup.
        /// </remarks>
        private void writeDataToFile()
        {
            if (IsWriteData && File.Exists(writefilePath) && SaveWaveFileName is not null)
            {
                Task.Run(() =>
                {
                    WaveFile.WriteFile(
                        filePath: SaveWaveFileName,
                        rawFilePath: writefilePath,
                        sampleRate: OutputEngineOptions.SampleRate,
                        channels: 2,
                        bitPerSamples: BitPerSamples);
                }).Wait();

                IsWriteData = false;
                SaveWaveFileName = null;

                if (File.Exists(writefilePath))
                {
                    try { File.Delete(writefilePath); } catch { /* Ignore */ }
                }
            }
        }

        /// <summary>
        /// Saves audio sample data to a temporary file with thread-safe buffering.
        /// </summary>
        /// <param name="samplesArray">The array of audio samples to write to the file.</param>
        /// <param name="writeFile">The file path where the samples should be written.</param>
        /// <remarks>
        /// This method provides thread-safe file writing with buffering:
        /// - Uses locking to prevent concurrent write operations
        /// - Buffers data in memory if a write operation is already in progress
        /// - Writes both current data and any buffered data in a single operation
        /// - Uses binary writer for efficient float data serialization
        /// - Appends data to the existing file to maintain continuity
        /// - Includes comprehensive error handling with logging
        /// - Ensures the writing flag is always reset in the finally block
        /// 
        /// The buffering mechanism prevents data loss when multiple threads attempt
        /// to write simultaneously, which is common in real-time audio applications.
        /// </remarks>
        private void SaveSamplesToFile(float[] samplesArray, string writeFile)
        {
            lock (writeLock)
            {
                if (isWriting) //Temporarily stores data if writing is in progress
                {
                    writedDataBuffer.AddRange(samplesArray);
                    return;
                }

                isWriting = true;
            }
            
            try
            {
                lock (writeLock)
                {
                    using (var fileStream = new FileStream(writeFile, FileMode.Append, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(fileStream))
                    {
                        foreach (var sample in samplesArray)
                        {
                            writer.Write(sample);
                        }

                        if (writedDataBuffer.Count > 0)
                        {
                            foreach (var sample in writedDataBuffer)
                            {
                                writer.Write(sample);
                            }

                            writedDataBuffer.Clear();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error during writing: {ex.Message}");
            }
            finally
            {
                lock (writeLock)
                {
                    isWriting = false;
                }
            }
        }

        /// <summary>
        /// Sets the <see cref="State"/> value and raises the <see cref="StateChanged"/> event if the value has changed.
        /// Also propagates the state change to all managed sources.
        /// </summary>
        /// <param name="state">The new source state to set.</param>
        /// <remarks>
        /// This method provides centralized state management:
        /// - Updates the SourceManager's state
        /// - Propagates the state change to all sources in the Sources collection
        /// - Raises the StateChanged event only when the state actually changes
        /// - Ensures all sources maintain synchronized states
        /// 
        /// This centralized approach ensures that all audio sources respond consistently
        /// to state changes initiated at the SourceManager level.
        /// </remarks>
        protected virtual void SetAndRaiseStateChanged(SourceState state)
        {
            var raise = State != state;
            State = state;

            if (Sources is not null)
            {
                foreach (ISource src in Sources)
                    src.ChangeState(state);
            }

            if (raise && StateChanged != null)
            {
                StateChanged.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Sets the <see cref="Position"/> value and raises the <see cref="PositionChanged"/> event if the value has changed.
        /// </summary>
        /// <param name="position">The new playback position to set.</param>
        /// <remarks>
        /// This method provides thread-safe position management:
        /// - Updates the current playback position
        /// - Raises the PositionChanged event only when the position actually changes
        /// - Invokes the event synchronously on the calling thread
        /// 
        /// The position represents the current playback time across all mixed sources
        /// and is used for synchronization and user interface updates.
        /// </remarks>
        protected virtual void SetAndRaisePositionChanged(TimeSpan position)
        {
            var raise = position != Position;
            Position = position;

            if (raise && PositionChanged != null)
            {
                PositionChanged.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="SourceManager"/> instance.
        /// </summary>
        /// <remarks>
        /// This method performs complete cleanup:
        /// - Sets the state to Idle to stop all processing
        /// - Terminates all background threads
        /// - Disposes of the audio engine
        /// - Suppresses finalizer execution for better performance
        /// - Sets the disposed flag to prevent multiple disposal
        /// 
        /// This method is safe to call multiple times and follows the standard dispose pattern.
        /// After disposal, the SourceManager instance should not be used for any operations.
        /// </remarks>
        public virtual void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            State = SourceState.Idle;
            EnsureThreadsDone();

            Engine?.Dispose();

            GC.SuppressFinalize(this);

            _disposed = true;
        }
    }
}
