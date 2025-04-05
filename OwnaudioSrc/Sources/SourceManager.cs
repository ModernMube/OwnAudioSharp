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
        private List<float> writedDataBuffer = new List<float>();
        private bool _disposed;

        private static readonly object _lock = new object();
        private static SourceManager? _instance;

        private object writeLock = new object();
        private bool isWriting = false;
        private string writefilePath = "";

        /// <summary>
        /// Initializes <see cref="Source"/> instance by providing <see cref="IAudioEngine"/> instance.
        /// </summary>
        private SourceManager()
        {
            VolumeProcessor = new VolumeProcessor { Volume = 1 };
        }

        /// <summary>
        /// Public static property to access the single instance
        /// </summary>
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
        /// Add an output source.
        /// </summary>
        /// <param name="url">Access and name of the file you want to play</param>
        /// <returns></returns>
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
            
            if(IsLoaded)
                UrlList.Add(url);

            SetAndRaisePositionChanged(TimeSpan.Zero);            

            Logger?.LogInfo("Source add url.");

            return Task.FromResult(IsLoaded);
        }

        /// <summary>
        /// Add an input source
        /// </summary>
        /// <returns></returns>
        public Task<bool> AddInputSource()
        {
            if(IsLoaded && !IsRecorded)
            {
                if(InputEngineOptions.Device.MaxInputChannels > 0)
                {
                    SourceInput _inputSource = new SourceInput(InputEngineOptions);
                    SourcesInput.Add(_inputSource);
                    if (_inputSource is not null)
                        IsRecorded = true;
                }
            }
            return Task.FromResult(IsRecorded);            
        }

        /// <summary>
        /// Removes the output source
        /// </summary>
        /// <param name="SourceID">The identification number of the source</param>
        /// <returns></returns>
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
        /// Remove input
        /// </summary>
        /// <returns></returns>
        public Task<bool> RemoveInputSource()
        {
            if (IsRecorded)
                SourcesInput.Clear();

            IsRecorded = false;
            return Task.FromResult(true);
        }
        
        /// <summary>
        /// Play mixed sources
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="bitPerSamples"></param>
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
        /// Play mixed sources
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
        /// Pauses playback
        /// </summary>
        public void Pause()
        {
            if (State is SourceState.Playing or SourceState.Buffering)
                SetAndRaiseStateChanged(SourceState.Paused);
        }

        /// <summary>
        /// Stops playback
        /// </summary>
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
                Engine.ResetPosition();
            }                 

            TerminateEngine();
        }

        /// <summary>
        /// Find the specified position in the sources
        /// </summary>
        /// <param name="position"></param>
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
        /// Clears all sources and resets all values, but leaves the audio engine initialized.
        /// </summary>
        /// <returns>True if the operation was successful, False otherwise.</returns>
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
                    });

                    IsWriteData = false;
                    SaveWaveFileName = null;

                    if (File.Exists(writefilePath))
                    {
                        try { File.Delete(writefilePath); } catch { /* Ignore */ }
                    }
                }

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
        ///  Uniform format settings for all sources
        /// </summary>
        /// <returns></returns>
        public static FFmpegDecoderOptions GetUnifiedDecoderOptions()
        {
            int outputChannels = (int)OutputEngineOptions.Channels;
            int outputSampleRate = OutputEngineOptions.SampleRate;

            return new FFmpegDecoderOptions(outputChannels, outputSampleRate);
        }

        /// <summary>
        /// Ending the thread that is doing the mixing
        /// </summary>
        private void EnsureThreadsDone()
        {
            MixEngineThread?.EnsureThreadDone();

            MixEngineThread = null;
        }
        
        /// <summary>
        /// Run <see cref="VolumeProcessor"/> and <see cref="CustomSampleProcessor"/> to the specified samples.
        /// </summary>
        /// <param name="samples">Audio samples to process to.</param>
        protected virtual void ProcessSampleProcessors(Span<float> samples)
        {
            bool useCustomProcessor = CustomSampleProcessor is { IsEnabled: true };
            bool useVolumeProcessor = VolumeProcessor.Volume != 1.0f;

            if (useCustomProcessor || useVolumeProcessor)
            {
                if (useCustomProcessor && CustomSampleProcessor is not null)
                    CustomSampleProcessor.Process(samples);

                if (useVolumeProcessor)
                    VolumeProcessor.Process(samples);
            }

            lock (_lock)
            {
                float[] samplesArray = samples.ToArray();
                Task.Run(() => 
                {
                    if (OutputEngineOptions.Channels == OwnAudioEngine.EngineChannels.Stereo)
                        OutputLevels = CalculateAverageStereoLevels(samplesArray);
                    else
                        OutputLevels = CalculateAverageMonoLevel(samplesArray);
                });
                
            }

            if (IsWriteData) //Save data to file
            {
                var samplesArray = samples.ToArray();
                Task.Run(() => { SaveSamplesToFile(samplesArray, writefilePath); });
            }
        }

        /// <summary>
        /// Write the data received in the parameter to a temporary file.
        /// </summary>
        /// <param name="samplesArray"></param>
        /// <param name="writeFile"></param>
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
                Console.WriteLine($"Hiba az írás során: {ex.Message}");
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
        /// Sets <see cref="State"/> value and raise <see cref="StateChanged"/> if value is changed.
        /// </summary>
        /// <param name="state">Playback state.</param>
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
        /// Sets <see cref="Position"/> value and raise <see cref="PositionChanged"/> if value is changed.
        /// </summary>
        /// <param name="position">Playback position.</param>
        protected virtual void SetAndRaisePositionChanged(TimeSpan position)
        {
            var raise = position != Position;
            Position = position;

            if (raise && PositionChanged != null)
            {
                PositionChanged.Invoke(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
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
