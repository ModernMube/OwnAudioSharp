using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ownaudio.Exceptions;
using Ownaudio.Processors;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using System.Diagnostics;
using Ownaudio.Decoders.FFmpeg;

namespace Ownaudio.Sources
{
    /// <summary>
    /// A singleton class that provides functions for mixing and playing multiple audio sources.
    /// Manages output sources, input sources, real-time sources, and handles audio mixing operations.
    /// </summary>
    public unsafe partial class SourceManager
    {
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
        /// Adds a new output source from the specified URL or file path.
        /// </summary>
        /// <param name="url">The URL or file path of the audio source to add.</param>
        /// <param name="name">Optional name for the source (default is "Output").</param>
        /// <returns>A task that represents the asynchronous operation. The task result indicates whether the source was successfully loaded.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the URL parameter is null.</exception>
        /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running.</exception>
        /// <remarks>
        /// This method performs the following operations:
        /// - Creates a new Source instance and loads the audio from the specified URL
        /// - Updates the total duration to the longest source duration
        /// - Adds the URL to the internal URL list
        /// - Automatically adds an input source if the load is successful
        /// - Resets the position to zero and logs the operation
        /// </remarks>
        public Task<bool> AddOutputSource(string url, string? name = "Output")
        {
            Ensure.NotNull(url, nameof(url));
            Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

            Source _source = new Source();
            _source.LoadAsync(url).Wait();
            _source.Name = name ?? "Output";
            Sources.Add(_source);

            if (_source.Duration.TotalMilliseconds > Duration.TotalMilliseconds)
                Duration = _source.Duration;

            IsLoaded = _source.IsLoaded;
            
            if(IsLoaded)
            {
                UrlList.Add(url);
                AddInputSource();
            }                 

            SetAndRaisePositionChanged(TimeSpan.Zero);            

            Logger?.LogInfo("Source add url.");

            return Task.FromResult(IsLoaded);
        }

        /// <summary>
        /// Adds an empty output source without any audio content.
        /// </summary>
        /// <param name="name">Optional name for the empty source (default is "WithoutData").</param>
        /// <returns>A task that represents the asynchronous operation. The task result indicates whether the empty source was successfully added.</returns>
        /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running.</exception>
        /// <remarks>
        /// This method creates a placeholder source that can be used for mixing operations
        /// when no actual audio file is needed. It updates the duration and sets the loaded state to true.
        /// This is useful for scenarios where only input sources or real-time sources are being used.
        /// </remarks>
        public Task<bool> AddEmptyOutputSource(string? name = "WithoutData")
        {
            Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

            SourceWithoutData _sourceWithoutData = new SourceWithoutData();
            _sourceWithoutData.Name = name ?? "WithoutData";
            Sources.Add(_sourceWithoutData);

            if(Duration.TotalMilliseconds < _sourceWithoutData.Duration.TotalMilliseconds)
                Duration = _sourceWithoutData.Duration;

            IsLoaded = true;

            SetAndRaisePositionChanged(TimeSpan.Zero);

            return Task.FromResult(IsLoaded);
        }

        /// <summary>
        /// Adds an input source for recording or real-time audio input.
        /// </summary>
        /// <param name="inputVolume">The initial volume level for the input source (default: 0.0f).</param>
        /// <param name="name">Optional name for the input source (default: "Input").</param>
        /// <returns>A task that represents the asynchronous operation. The task result indicates whether the input source was successfully added.</returns>
        /// <remarks>
        /// This method creates an input source only if:
        /// - No input is currently being recorded
        /// - The input device has available input channels
        /// 
        /// If an input source already exists, this method updates the volume of the most recently added input source.
        /// The method sets the IsRecorded flag to true when a valid input source is created.
        /// </remarks>
        public Task<bool> AddInputSource(float inputVolume = 0f, string? name = "Input")
        {
            if(!IsRecorded)
            {
                if(InputEngineOptions.Device.MaxInputChannels > 0)
                {
                    SourceInput _inputSource = new SourceInput(InputEngineOptions);
                    _inputSource.Name = name ?? "Input";
                    SourcesInput.Add(_inputSource);
                    if (_inputSource is not null)
                    {
                        IsRecorded = true;
                    } 
                }
            }

            if(IsRecorded)
            {
                SourcesInput[SourcesInput.Count - 1].Volume = inputVolume;
            }

            return Task.FromResult(IsRecorded);
        }

        /// <summary>
        /// Adds a new real-time sample-based source to the audio mix.
        /// </summary>
        /// <param name="initialVolume">The initial volume level for the source (default: 1.0f).</param>
        /// <param name="dataChannels">The number of audio channels for the input data (default: 2 for stereo).</param>
        /// <param name="name">Optional name for the source (default: "Realtime").</param>
        /// <returns>The created <see cref="SourceSound"/> instance that can be used to feed real-time audio data.</returns>
        /// <remarks>
        /// This method creates a real-time audio source that can accept live audio samples.
        /// It performs the following operations:
        /// - Creates a new SourceSound with the specified channel configuration
        /// - Sets the initial volume and logger
        /// - Adds the source to the mixing engine
        /// - Updates the total duration (defaults to 10 seconds for real-time sources)
        /// - Automatically adds an empty output source if none exist
        /// 
        /// Real-time sources are useful for applications that need to inject live audio data
        /// into the mix, such as synthesizers, live audio effects, or streaming applications.
        /// </remarks>
        public SourceSound AddRealTimeSource(float initialVolume = 1.0f, int dataChannels = 2, string? name = "Realtime")
        {
            var source = new SourceSound(dataChannels)
            {
                Volume = initialVolume,
                Logger = Logger,
                Name = name ?? "Realtime"
            };

            Sources.Add(source);

            // Optionally, we can set the maximum Duration value to 10 seconds
            if (Duration.TotalMilliseconds < 10000)
                Duration = TimeSpan.FromMilliseconds(10000);

            SetAndRaisePositionChanged(TimeSpan.Zero);

            Logger?.LogInfo("Real-time source added.");

            if (!IsLoaded)
                AddEmptyOutputSource().Wait();

            return source;
        }

        /// <summary>
        /// Removes an output source by its index in the sources collection.
        /// </summary>
        /// <param name="SourceID">The zero-based index of the source to remove.</param>
        /// <returns>A task that represents the asynchronous operation. The task result indicates whether the removal was successful.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the Sources collection is null.</exception>
        /// <exception cref="OwnaudioException">Thrown when the specified source ID does not exist.</exception>
        /// <remarks>
        /// This method removes both the source from the Sources collection and its corresponding
        /// URL from the UrlList. It resets the position to zero after successful removal.
        /// The method includes error handling and will return false if the removal operation fails.
        /// </remarks>
        public Task<bool> RemoveOutputSource(int SourceID)
        {
            Ensure.NotNull(Sources, nameof(Sources));
            Ensure.That<OwnaudioException>(SourceID < Sources.Count, "Output source id not exist.");

            try 
            {
                Sources.RemoveAt(SourceID);
                UrlList?.RemoveAt(SourceID);

                SetAndRaisePositionChanged(TimeSpan.Zero);

                Logger?.LogInfo("Output source ID remove.");

                return Task.FromResult(true);
            }
            catch {  return  Task.FromResult(false); }
            
        }

        /// <summary>
        /// Removes all input sources and resets the recording state.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result is always true.</returns>
        /// <remarks>
        /// This method performs the following operations:
        /// - Clears all input sources from the SourcesInput collection
        /// - Sets IsRecorded to false to indicate no input is being recorded
        /// - Automatically adds a new input source to maintain system consistency
        /// 
        /// This is useful for resetting the input configuration or switching input devices.
        /// </remarks>
        public Task<bool> RemoveInputSource()
        {
            if (IsRecorded)
                SourcesInput.Clear();

            IsRecorded = false;
            AddInputSource();
            return Task.FromResult(true);
        }

        /// <summary>
        /// Removes a specific real-time source from the audio mix.
        /// </summary>
        /// <param name="source">The <see cref="SourceSound"/> instance to remove.</param>
        /// <returns>True if the source was found and removed successfully; otherwise, false.</returns>
        /// <remarks>
        /// This method safely removes a real-time source by:
        /// - Checking if the source exists in the Sources collection
        /// - Properly disposing of the source to free resources
        /// - Removing it from the collection
        /// - Logging the operation
        /// 
        /// If the source is not found in the collection, the method returns false without performing any action.
        /// </remarks>
        public bool RemoveRealtimeSource(SourceSound source)
        {
            if (Sources.Contains(source))
            {
                source.Dispose();
                Sources.Remove(source);
                Logger?.LogInfo("Real-time source removed.");
                return true;
            }

            return false;
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
                return Sources.FirstOrDefault(s => s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                    ?? throw new ArgumentException($"Source with name '{name}' not found.", nameof(name));

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
        /// <remarks>
        /// This method handles the complete playback initialization process:
        /// - Checks current state and handles paused/playing states appropriately
        /// - Resets position if at the end of the audio
        /// - Adds empty output source if only input sources exist
        /// - Initializes the audio engine
        /// - Creates and starts the mixing thread with above-normal priority
        /// - Starts input recording if configured
        /// - Starts the audio engine output
        /// 
        /// The method includes comprehensive error handling and will output debug information
        /// if engine initialization fails. All mixing operations run on a dedicated background thread.
        /// </remarks>
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

            if (SourcesInput.Count > 0 && Sources.Count < 1)
            {
                AddEmptyOutputSource(); 
            }

            if (InitializeEngine())
            {
                EnsureThreadsDone();

                SetAndRaiseStateChanged(SourceState.Playing);

                Seek(Position);

                Thread.Sleep(100);

                MixEngineThread = new Thread(MixEngine) { Name = "Mix Engine Thread", IsBackground = true, Priority = ThreadPriority.AboveNormal};

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
        /// - Stops the audio engine if it's running
        /// - Terminates the engine completely
        /// 
        /// After calling this method, the system returns to its initial idle state
        /// and Play() must be called to resume audio operations.
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

            TerminateEngine();
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

            IsSeeking = true;

            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            bool wasPlaying = State == SourceState.Playing;
            if (wasPlaying)
            {
                SetAndRaiseStateChanged(SourceState.Buffering);
            }

            try
            {
                Parallel.ForEach(Sources, src =>
                {
                    src.IsSeeking = true;
                    src.Seek(position);
                });

                Logger?.LogInfo($"Seeking to: {position}.");

                SetAndRaisePositionChanged(position);

                Thread.Sleep(10);
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

                IsSeeking = false;
                Logger?.LogInfo($"Successfully seeks to {position}.");
            }
        }

        /// <summary>
        /// Completely resets the SourceManager to its initial state.
        /// </summary>
        /// <returns>True if the reset operation was successful; otherwise, false.</returns>
        /// <remarks>
        /// This method performs a comprehensive system reset:
        /// - Stops all playback and terminates threads
        /// - Stops and disposes the audio engine
        /// - Disposes and clears all output and input sources
        /// - Clears the URL list
        /// - Resets all state variables (duration, position, flags)
        /// - Writes any pending recorded data to file
        /// - Clears audio data buffers
        /// - Resets volume and custom processors
        /// - Raises state and position change events
        /// 
        /// The audio engine is left in a state where it can be reinitialized for new operations.
        /// This method includes comprehensive error handling and logging.
        /// </remarks>
        public bool Reset()
        {
            try
            {
                Logger?.LogInfo("Resetting SourceManager to initial state...");

                if (State != SourceState.Idle)
                {
                    SetAndRaiseStateChanged(SourceState.Idle);
                    EnsureThreadsDone();
                }

                if (Engine?.OwnAudioEngineStopped() == 0)
                {
                    Engine?.Stop();
                }

                foreach (ISource src in Sources)
                {
                    src.Dispose();
                }

                foreach (ISource inputSrc in SourcesInput)
                {
                    inputSrc.Dispose();
                }

                Sources.Clear();
                SourcesInput.Clear();
                UrlList.Clear();

                Duration = TimeSpan.Zero;
                Position = TimeSpan.Zero;
                IsLoaded = false;
                IsRecorded = false;
                IsSeeking = false;

                writeDataToFile();
                writedDataBuffer.Clear();

                Volume = 1.0f;

                CustomSampleProcessor = null;

                SetAndRaiseStateChanged(SourceState.Idle);
                SetAndRaisePositionChanged(TimeSpan.Zero);

                Logger?.LogInfo("SourceManager successfully reset to initial state.");
                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error resetting SourceManager: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets unified decoder options for consistent audio format across all sources.
        /// </summary>
        /// <returns>A <see cref="FFmpegDecoderOptions"/> instance configured with the output engine's sample rate and channel settings.</returns>
        /// <remarks>
        /// This method ensures all audio sources are decoded to the same format:
        /// - Uses the output engine's channel configuration
        /// - Uses the output engine's sample rate
        /// - Provides consistent audio format for mixing operations
        /// 
        /// Having unified decoder options is essential for proper audio mixing, as all sources
        /// must have the same sample rate and channel configuration to be mixed together properly.
        /// </remarks>
        public static FFmpegDecoderOptions GetUnifiedDecoderOptions()
        {
            int outputChannels = (int)OutputEngineOptions.Channels;
            int outputSampleRate = OutputEngineOptions.SampleRate;

            return new FFmpegDecoderOptions(outputChannels, outputSampleRate);
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
                        sampleRate: OwnAudio.DefaultOutputDevice.DefaultSampleRate,
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
                Console.WriteLine($"Error during writing: {ex.Message}");
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
