using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ownaudio.Exceptions;
using Ownaudio.Engines;
using Ownaudio.Processors;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using System.Diagnostics;
using Ownaudio.Decoders.FFmpeg;

namespace Ownaudio.Sources
{
    /// <summary>
    /// A class that provides functions for mixing and playing sources.
    /// </summary>
    public unsafe partial class SourceManager
    {
        /// <summary>
        /// Buffer that temporarily stores written audio data during file operations.
        /// </summary>
        private List<float> writedDataBuffer = new List<float>();

        /// <summary>
        /// Indicates whether this instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Thread-safe lock object for singleton pattern implementation.
        /// </summary>
        private static readonly object _lock = new object();

        /// <summary>
        /// The single instance of SourceManager (Singleton pattern).
        /// </summary>
        private static SourceManager? _instance;

        /// <summary>
        /// Lock object for synchronizing write operations to prevent concurrent file access.
        /// </summary>
        private object writeLock = new object();

        /// <summary>
        /// Flag indicating whether a write operation is currently in progress.
        /// </summary>
        private bool isWriting = false;

        /// <summary>
        /// The file path where temporary audio data is written during recording operations.
        /// </summary>
        private string writefilePath = "";

        /// <summary>
        /// Initializes <see cref="SourceManager"/> instance by providing <see cref="IAudioEngine"/> instance.
        /// Private constructor to enforce singleton pattern.
        /// </summary>
        private SourceManager()
        {
            VolumeProcessor = new VolumeProcessor { Volume = 1 };
        }

        /// <summary>
        /// Gets the single instance of the SourceManager class (Singleton pattern).
        /// Thread-safe implementation using double-checked locking.
        /// </summary>
        /// <value>The singleton instance of SourceManager.</value>
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
        /// Adds an output source from the specified URL/file path.
        /// </summary>
        /// <param name="url">The file path or URL of the audio source to add.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a boolean indicating success.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="url"/> is null.</exception>
        /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running.</exception>
        public Task<bool> AddOutputSource(string url)
        {
            Ensure.NotNull(url, nameof(url));
            Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

            Source _source = new Source();
            _source.LoadAsync(url).Wait();
            Sources.Add(_source);

            if (_source.Duration.TotalMilliseconds > Duration.TotalMilliseconds)
                Duration = _source.Duration;

            IsLoaded = _source.IsLoaded;

            if (IsLoaded)
            {
                UrlList.Add(url);
                AddInputSource();
            }

            SetAndRaisePositionChanged(TimeSpan.Zero);

            Logger?.LogInfo("Source add url.");

            return Task.FromResult(IsLoaded);
        }

        /// <summary>
        /// Adds an empty output source with default duration for scenarios where no actual audio file is needed.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result contains a boolean indicating success.</returns>
        /// <exception cref="OwnaudioException">Thrown when the playback thread is currently running.</exception>
        public Task<bool> AddEmptyOutputSource()
        {
            Ensure.That<OwnaudioException>(State == SourceState.Idle, "Playback thread is currently running.");

            SourceWithoutData _sourceWithoutData = new SourceWithoutData();
            Sources.Add(_sourceWithoutData);

            if (Duration.TotalMilliseconds < _sourceWithoutData.Duration.TotalMilliseconds)
                Duration = _sourceWithoutData.Duration;

            IsLoaded = true;

            SetAndRaisePositionChanged(TimeSpan.Zero);

            return Task.FromResult(IsLoaded);
        }

        /// <summary>
        /// Adds an input source for recording audio with the specified input volume.
        /// Creates a new input source if recording is not already active.
        /// </summary>
        /// <param name="inputVolume">The volume level for the input source. Default is 0.0f.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a boolean indicating if recording is active.</returns>
        public Task<bool> AddInputSource(float inputVolume = 0f)
        {
            if (!IsRecorded)
            {
                if (InputEngineOptions.Device.MaxInputChannels > 0)
                {
                    SourceInput _inputSource = new SourceInput(InputEngineOptions);
                    SourcesInput.Add(_inputSource);
                    if (_inputSource is not null)
                    {
                        IsRecorded = true;
                    }
                }
            }

            if (IsRecorded)
            {
                SourcesInput[SourcesInput.Count - 1].Volume = inputVolume;
            }

            return Task.FromResult(IsRecorded);
        }

        /// <summary>
        /// Adds a new real-time sample-based source to the mix for streaming audio data.
        /// This method is useful for scenarios where audio data is generated or received in real-time.
        /// </summary>
        /// <param name="initialVolume">The initial volume for the source. Default is 1.0f (full volume).</param>
        /// <param name="dataChannels">The number of audio channels for input data. Default is 2 (stereo).</param>
        /// <returns>The created SourceSound instance that can be used to feed real-time audio data.</returns>
        public SourceSound AddRealTimeSource(float initialVolume = 1.0f, int dataChannels = 2)
        {
            var source = new SourceSound(dataChannels)
            {
                Volume = initialVolume,
                Logger = Logger
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
        /// Removes an output source by its index from the sources collection.
        /// </summary>
        /// <param name="SourceID">The zero-based index of the source to remove.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a boolean indicating success.</returns>
        /// <exception cref="ArgumentNullException">Thrown when Sources collection is null.</exception>
        /// <exception cref="OwnaudioException">Thrown when the specified SourceID does not exist.</exception>
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
            catch { return Task.FromResult(false); }

        }

        /// <summary>
        /// Removes all input sources and stops recording.
        /// Clears the input sources collection and resets recording state.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result is always true.</returns>
        public Task<bool> RemoveInputSource()
        {
            if (IsRecorded)
                SourcesInput.Clear();

            IsRecorded = false;
            AddInputSource();
            return Task.FromResult(true);
        }

        /// <summary>
        /// Removes a specific real-time source from the sources collection.
        /// Properly disposes the source before removing it.
        /// </summary>
        /// <param name="source">The SourceSound instance to remove.</param>
        /// <returns>True if the source was found and removed successfully; otherwise, false.</returns>
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
        /// Starts playback of mixed sources and saves the output to a specified file with given bit depth.
        /// Enables data writing mode and configures file output parameters.
        /// </summary>
        /// <param name="fileName">The output file name where the mixed audio will be saved.</param>
        /// <param name="bitPerSamples">The bit depth for the output audio file (e.g., 16, 24, 32).</param>
        public void Play(string fileName, short bitPerSamples)
        {
            IsWriteData = true;

            SaveWaveFileName = fileName;

            string directoryPath = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
            writefilePath = Path.Combine(directoryPath, "writeaudiodata.raw");

            if (File.Exists(writefilePath))
            { File.Delete(writefilePath); }

            BitPerSamples = bitPerSamples;

            this.Play();
        }

        /// <summary>
        /// Starts playback of all mixed sources.
        /// Handles different playback states and initializes the audio engine and mixing thread.
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

                MixEngineThread = new Thread(MixEngine) { Name = "Mix Engine Thread", IsBackground = true, Priority = ThreadPriority.AboveNormal };

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
        /// Pauses the current playback.
        /// Only effective when playback is in Playing or Buffering state.
        /// </summary>
        public void Pause()
        {
            if (State is SourceState.Playing or SourceState.Buffering)
                SetAndRaiseStateChanged(SourceState.Paused);
        }

        /// <summary>
        /// Stops the current playback and resets the player state.
        /// Terminates all threads, stops the audio engine, and cleans up resources.
        /// </summary>
        public void Stop()
        {
            if (State == SourceState.Idle)
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
        /// Seeks to the specified position in all audio sources.
        /// Temporarily sets thread priority to highest for smooth seeking operation.
        /// </summary>
        /// <param name="position">The target position to seek to.</param>
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
        /// Completely resets the player to its initial state.
        /// Clears all sources, resets all values, disposes resources, and saves any pending data.
        /// The audio engine remains initialized after reset.
        /// </summary>
        /// <returns>True if the operation was successful; otherwise, false.</returns>
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
        /// Gets unified decoder options that ensure all sources use the same format settings.
        /// Returns FFmpeg decoder options configured with the output engine's channels and sample rate.
        /// </summary>
        /// <returns>A configured FFmpegDecoderOptions instance with unified format settings.</returns>
        public static FFmpegDecoderOptions GetUnifiedDecoderOptions()
        {
            int outputChannels = (int)OutputEngineOptions.Channels;
            int outputSampleRate = OutputEngineOptions.SampleRate;

            return new FFmpegDecoderOptions(outputChannels, outputSampleRate);
        }

        /// <summary>
        /// Ensures that the mixing engine thread is properly terminated and cleaned up.
        /// Waits for the thread to complete before setting it to null.
        /// </summary>
        private void EnsureThreadsDone()
        {
            MixEngineThread?.EnsureThreadDone();

            MixEngineThread = null;
        }

        /// <summary>
        /// Writes the recorded audio data from temporary storage to the final wave file.
        /// This method is called internally to finalize audio file creation after recording/playback ends.
        /// </summary>
        private void writeDataToFile()
        {
            if (IsWriteData && File.Exists(writefilePath) && SaveWaveFileName is not null)
            {
                Task.Run(() =>
                {
                    WriteWaveFile.WriteFile(
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
        /// Saves audio sample data to a temporary file in a thread-safe manner.
        /// Uses buffering to handle concurrent write operations and prevent data loss.
        /// </summary>
        /// <param name="samplesArray">The array of audio samples to write to the file.</param>
        /// <param name="writeFile">The file path where the samples should be written.</param>
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
                Console.WriteLine($"ERROR file write: {ex.Message}");
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
        /// Also propagates the state change to all sources in the collection.
        /// </summary>
        /// <param name="state">The new playback state to set.</param>
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
        /// This method is used to notify subscribers about playback position updates.
        /// </summary>
        /// <param name="position">The new playback position to set.</param>
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
        /// Releases all resources used by the SourceManager instance.
        /// This method should be called when the instance is no longer needed to prevent memory leaks.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _bufferReady?.Dispose();
            ClearBufferPools();

            State = SourceState.Idle;
            EnsureThreadsDone();

            Engine?.Dispose();

            GC.SuppressFinalize(this);

            _disposed = true;
        }
    }
}
