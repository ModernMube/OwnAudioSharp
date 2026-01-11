using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Ownaudio;
using Ownaudio.Decoders;
using Ownaudio.Core;
using Ownaudio.Core.Common;
using Logger;
using System.Numerics;
using System.Reflection;

namespace OwnaudioNET.Features.Vocalremover
{
    #region Configuration and Options

    /// <summary>
    /// Configuration parameters for HTDemucs audio separation
    /// </summary>
    public class HTDemucsSeparationOptions
    {
        /// <summary>
        /// ONNX model file path (optional if using InternalModel)
        /// </summary>
        public string? ModelPath { get; set; }

        /// <summary>
        /// Internal embedded model to use (if ModelPath is not specified)
        /// </summary>
        public InternalModel Model { get; set; } = InternalModel.None;

        /// <summary>
        /// Output directory path
        /// </summary>
        public string OutputDirectory { get; set; } = "separated_htdemucs";

        /// <summary>
        /// Chunk size in seconds (10-30 seconds recommended for HTDemucs)
        /// </summary>
        public int ChunkSizeSeconds { get; set; } = 10;

        /// <summary>
        /// Overlap factor for chunk processing (0.0-0.5, recommended 0.25)
        /// </summary>
        public float OverlapFactor { get; set; } = 0.25f;

        /// <summary>
        /// Enable GPU acceleration (CUDA/DirectML)
        /// </summary>
        public bool EnableGPU { get; set; } = true;

        /// <summary>
        /// Target stems to extract
        /// </summary>
        public HTDemucsStem TargetStems { get; set; } = HTDemucsStem.All;

        /// <summary>
        /// Target sample rate (default: 44100 Hz)
        /// </summary>
        public int TargetSampleRate { get; set; } = 44100;

        /// <summary>
        /// Model-specific segment length override (0 = auto-detect from model)
        /// </summary>
        public int SegmentLength { get; set; } = 0;

        /// <summary>
        /// Margin to trim from chunk edges in seconds (to remove model warm-up artifacts)
        /// Default: 0.5 seconds
        /// </summary>
        public float MarginSeconds { get; set; } = 0.5f;

        /// <summary>
        /// Crossfade duration for valid regions in seconds
        /// Default: 0.05 seconds (50ms)
        /// </summary>
        public float CrossfadeSeconds { get; set; } = 0.05f;
    }

    /// <summary>
    /// HTDemucs stem types
    /// </summary>
    [Flags]
    public enum HTDemucsStem
    {
        /// <summary>Vocal stem</summary>
        Vocals = 1,

        /// <summary>Drums stem</summary>
        Drums = 2,

        /// <summary>Bass stem</summary>
        Bass = 4,

        /// <summary>Other instruments stem</summary>
        Other = 8,

        /// <summary>All stems</summary>
        All = Vocals | Drums | Bass | Other
    }

    #endregion

    #region Progress and Result Classes

    /// <summary>
    /// Progress information for HTDemucs separation process
    /// </summary>
    public class HTDemucsSeparationProgress
    {
        /// <summary>Current file being processed</summary>
        public string CurrentFile { get; set; } = string.Empty;

        /// <summary>Overall progress percentage (0-100)</summary>
        public double OverallProgress { get; set; }

        /// <summary>Current processing step description</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Number of chunks processed</summary>
        public int ProcessedChunks { get; set; }

        /// <summary>Total number of chunks</summary>
        public int TotalChunks { get; set; }

        /// <summary>Current stem being processed</summary>
        public HTDemucsStem? CurrentStem { get; set; }
    }

    /// <summary>
    /// Result of HTDemucs audio separation
    /// </summary>
    public class HTDemucsSeparationResult
    {
        /// <summary>Paths to separated stem files</summary>
        public Dictionary<HTDemucsStem, string> StemPaths { get; set; } = new();

        /// <summary>Processing duration</summary>
        public TimeSpan ProcessingTime { get; set; }

        /// <summary>Number of stems extracted</summary>
        public int StemCount => StemPaths.Count;

        /// <summary>Source audio duration</summary>
        public TimeSpan AudioDuration { get; set; }
    }

    #endregion

    #region Main Separator Service

    /// <summary>
    /// HTDemucs-based audio separation service for stem extraction
    /// Optimized for streaming processing with minimal memory footprint
    /// Uses OwnAudioEngine converters for high-performance audio processing
    /// </summary>
    /// <remarks>
    /// HTDemucs (Hybrid Transformer Demucs) separates audio into 4 stems:
    /// - Vocals: singing and speech
    /// - Drums: percussion instruments
    /// - Bass: bass guitar and low-frequency instruments
    /// - Other: all other instruments
    ///
    /// Performance characteristics:
    /// - CPU processing: ~10-15x realtime (16 cores)
    /// - GPU processing: ~50-100x realtime (CUDA)
    /// - Memory footprint: ~500-800 MB for 10s chunks
    /// </remarks>
    public class HTDemucsAudioSeparator : IDisposable
    {
        #region Events

        /// <summary>Progress update event</summary>
        public event EventHandler<HTDemucsSeparationProgress>? ProgressChanged;

        /// <summary>Processing completed event</summary>
        public event EventHandler<HTDemucsSeparationResult>? ProcessingCompleted;

        #endregion

        #region Private Fields

        private readonly HTDemucsSeparationOptions _options;
        private InferenceSession? _onnxSession;
        private bool _disposed = false;

        // STFT processor for spectrogram generation
        private STFTProcessor? _stftProcessor;

        // Model parameters (auto-detected or configured)
        private int _modelSegmentLength = 0;
        private int _modelSampleRate = 44100;
        private int _modelChannels = 2;
        private int _modelStemCount = 4;

        // Stem name mapping (order from model output)
        private readonly string[] _stemNames = { "vocals", "drums", "bass", "other" };

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize HTDemucs audio separation service
        /// </summary>
        /// <param name="options">Separation configuration options</param>
        public HTDemucsAudioSeparator(HTDemucsSeparationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize the ONNX model session
        /// </summary>
        public void Initialize()
        {
            // Check if using file path or embedded resource
            bool useEmbeddedModel = string.IsNullOrEmpty(_options.ModelPath) || !File.Exists(_options.ModelPath);

            if (useEmbeddedModel && _options.Model == InternalModel.None)
            {
                throw new InvalidOperationException("Either ModelPath must be a valid file or Model must be set to a valid InternalModel value.");
            }

            var sessionOptions = new SessionOptions
            {
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            // Configure execution providers
            if (_options.EnableGPU)
            {
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA();
                    Log.Info("CUDA execution provider enabled for HTDemucs.");
                }
                catch
                {
                    try
                    {
                        sessionOptions.AppendExecutionProvider_DML();
                        Log.Info("DirectML execution provider enabled for HTDemucs.");
                    }
                    catch
                    {
                        sessionOptions.AppendExecutionProvider_CPU();
                        Log.Info("Using CPU execution provider for HTDemucs.");
                    }
                }
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU();
                Log.Info("Using CPU execution provider for HTDemucs.");
            }

            // Load model from file or embedded resource
            if (useEmbeddedModel)
            {
                Log.Info($"Loading embedded HTDemucs model: {_options.Model}");
                var modelBytes = AudioSeparationExtensions.LoadModelBytes(_options.Model);
                _onnxSession = new InferenceSession(modelBytes, sessionOptions);
            }
            else
            {
                Log.Info($"Loading HTDemucs model from file: {_options.ModelPath}");
                _onnxSession = new InferenceSession(_options.ModelPath, sessionOptions);
            }

            // Auto-detect model parameters
            AutoDetectModelParameters();

            // Initialize STFT processor for HTDemucs (n_fft=4096, hop_length=1024)
            _stftProcessor = new STFTProcessor(nFft: 4096, hopLength: 1024);

            Log.Info($"HTDemucs model initialized: SegmentLength={_modelSegmentLength}, " +
                     $"SampleRate={_modelSampleRate}, Stems={_modelStemCount}");
        }

        /// <summary>
        /// Separate audio file into stems
        /// </summary>
        /// <param name="inputFilePath">Input audio file path</param>
        /// <returns>Separation result with stem file paths</returns>
        public HTDemucsSeparationResult Separate(string inputFilePath)
        {
            if (_onnxSession == null)
                throw new InvalidOperationException("Service not initialized. Call Initialize() first.");

            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException($"Input file not found: {inputFilePath}");

            var startTime = DateTime.Now;
            var filename = Path.GetFileNameWithoutExtension(inputFilePath);

            ReportProgress(new HTDemucsSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Loading audio file...",
                OverallProgress = 0
            });

            // Process audio using streaming approach
            var (stems, audioDuration) = ProcessAudioStreaming(inputFilePath);

            ReportProgress(new HTDemucsSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Saving stems...",
                OverallProgress = 90
            });

            Directory.CreateDirectory(_options.OutputDirectory);

            // Save results
            var stemPaths = SaveResults(filename, stems, _options.TargetSampleRate);

            var result = new HTDemucsSeparationResult
            {
                StemPaths = stemPaths,
                ProcessingTime = DateTime.Now - startTime,
                AudioDuration = audioDuration
            };

            ReportProgress(new HTDemucsSeparationProgress
            {
                CurrentFile = Path.GetFileName(inputFilePath),
                Status = "Completed",
                OverallProgress = 100
            });

            ProcessingCompleted?.Invoke(this, result);
            return result;
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _onnxSession?.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private Methods - Initialization

        /// <summary>
        /// Auto-detect model parameters from ONNX metadata
        /// </summary>
        private void AutoDetectModelParameters()
        {
            if (_onnxSession == null) return;

            try
            {
                var inputMetadata = _onnxSession.InputMetadata.FirstOrDefault();
                var outputMetadata = _onnxSession.OutputMetadata.FirstOrDefault();

                if (inputMetadata.Value != null)
                {
                    var inputShape = inputMetadata.Value.Dimensions;
                    Log.Info($"Model input shape: [{string.Join(", ", inputShape)}]");

                    // Expected input: [batch, channels, samples]
                    if (inputShape.Length >= 3)
                    {
                        _modelChannels = (int)inputShape[1];
                        if (inputShape[2] > 0)
                        {
                            _modelSegmentLength = (int)inputShape[2];
                        }
                    }
                }

                if (outputMetadata.Value != null)
                {
                    var outputShape = outputMetadata.Value.Dimensions;
                    Log.Info($"Model output shape: [{string.Join(", ", outputShape)}]");

                    // Expected output: [batch, stems, channels, samples]
                    if (outputShape.Length >= 4)
                    {
                        _modelStemCount = (int)outputShape[1];
                    }
                }

                // Override with user settings if provided
                if (_options.SegmentLength > 0)
                {
                    _modelSegmentLength = _options.SegmentLength;
                }

                // Default segment length if not detected
                if (_modelSegmentLength == 0)
                {
                    _modelSegmentLength = _options.ChunkSizeSeconds * _options.TargetSampleRate;
                    Log.Info($"Using default segment length: {_modelSegmentLength} samples");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not auto-detect model parameters: {ex.Message}");
                _modelSegmentLength = _options.ChunkSizeSeconds * _options.TargetSampleRate;
            }
        }

        #endregion

        #region Private Methods - Audio Processing

        /// <summary>
        /// Report progress to subscribers
        /// </summary>
        private void ReportProgress(HTDemucsSeparationProgress progress)
        {
            ProgressChanged?.Invoke(this, progress);
        }

        /// <summary>
        /// Process audio file using margin-trimming approach to eliminate edge artifacts
        /// </summary>
        private (Dictionary<HTDemucsStem, float[,]> stems, TimeSpan duration) ProcessAudioStreaming(string inputFilePath)
        {
            // Create decoder with AudioFormatConverter
            using var decoder = AudioDecoderFactory.Create(
                inputFilePath,
                targetSampleRate: _options.TargetSampleRate,
                targetChannels: 2
            );

            AudioStreamInfo info = decoder.StreamInfo;
            int totalFrames = (int)(info.Duration.TotalSeconds * _options.TargetSampleRate);
            TimeSpan audioDuration = info.Duration;

            Log.Info($"Audio loaded: {totalFrames} frames, {audioDuration.TotalSeconds:F2}s");

            // Initialize stem buffers
            var stems = InitializeStemBuffers(totalFrames);

            // Calculate margin-trimming parameters
            int marginSamples = (int)(_options.MarginSeconds * _options.TargetSampleRate);
            int crossfadeSamples = (int)(_options.CrossfadeSeconds * _options.TargetSampleRate);
            
            int chunkSize = _modelSegmentLength > 0 ? _modelSegmentLength : (_options.ChunkSizeSeconds * _options.TargetSampleRate);
            
            // ValidSize is the useful output from each chunk (after trimming margins)
            int validSize = chunkSize - 2 * marginSamples;
            
            // Stride is how much we advance per iteration (with small crossfade overlap)
            int stride = validSize - crossfadeSamples;

            if (validSize <= 0)
            {
                throw new ArgumentException($"Margin ({marginSamples}) is too large for chunk size ({chunkSize}). Valid size would be {validSize}.");
            }

            int totalChunks = (int)Math.Ceiling((double)totalFrames / stride);

            Log.Info($"Margin-trimming: chunk={chunkSize}, margin={marginSamples}, valid={validSize}, crossfade={crossfadeSamples}, stride={stride}, chunks={totalChunks}");

            using var context = new HTDemucsProcessingContext(chunkSize, crossfadeSamples);

            // Read entire audio into memory for easier context window access
            var audioData = ReadEntireAudio(decoder, totalFrames);

            int chunkIndex = 0;
            int targetPos = 0;

            while (targetPos < totalFrames)
            {
                // Calculate input window: [targetPos - margin, targetPos + validSize + margin]
                int windowStart = targetPos - marginSamples;
                int windowEnd = targetPos + validSize + marginSamples;
                int windowSize = windowEnd - windowStart;

                // Extract window with reflection padding at boundaries
                var windowChunk = ExtractWindowWithPadding(audioData, windowStart, windowSize, totalFrames);

                // Process chunk through model
                var separatedStems = ProcessChunk(windowChunk, context);

                // Trim margins from output: extract [margin ... margin + validSize]
                var trimmedStems = TrimMargins(separatedStems, marginSamples, validSize);

                // Calculate how much of the valid region we can actually use
                int validLength = Math.Min(validSize, totalFrames - targetPos);

                // Apply to output with crossfade
                ApplyTrimmedOverlapAdd(stems, trimmedStems, targetPos, crossfadeSamples, validLength, totalFrames);

                targetPos += stride;
                chunkIndex++;

                // Report progress
                ReportProgress(new HTDemucsSeparationProgress
                {
                    CurrentFile = Path.GetFileName(inputFilePath),
                    Status = $"Processing chunk {chunkIndex}/{totalChunks}",
                    ProcessedChunks = chunkIndex,
                    TotalChunks = totalChunks,
                    OverallProgress = 10 + ((double)chunkIndex / totalChunks * 80)
                });
            }

            return (stems, audioDuration);
        }

        /// <summary>
        /// Initialize stem buffers for all target stems
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> InitializeStemBuffers(int totalFrames)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            foreach (HTDemucsStem stem in Enum.GetValues(typeof(HTDemucsStem)))
            {
                if (stem == HTDemucsStem.All) continue;
                if (_options.TargetStems.HasFlag(stem))
                {
                    stems[stem] = new float[2, totalFrames];
                }
            }

            return stems;
        }

        /// <summary>
        /// Pad chunk to required size
        /// </summary>
        private float[,] PadChunk(float[,] chunk, int targetSize)
        {
            int currentSize = chunk.GetLength(1);
            if (currentSize >= targetSize) return chunk;

            var padded = new float[2, targetSize];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < currentSize; i++)
                {
                    padded[ch, i] = chunk[ch, i];
                }
                // Zero-pad the rest
            }

            return padded;
        }

        /// <summary>
        /// Read entire audio into memory for easier context window access
        /// </summary>
        private float[,] ReadEntireAudio(IAudioDecoder decoder, int totalFrames)
        {
            var audioData = new float[2, totalFrames];
            int framesRead = 0;

            int bufferSize = 8192;
            byte[] readBuffer = new byte[bufferSize * 2 * sizeof(float)];

            while (framesRead < totalFrames)
            {
                var result = decoder.ReadFrames(readBuffer);
                
                if (!result.IsSucceeded || result.FramesRead == 0)
                    break;

                int bytesRead = result.FramesRead * 2 * sizeof(float);
                var floatSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                    readBuffer.AsSpan(0, bytesRead)
                );

                for (int i = 0; i < result.FramesRead && framesRead + i < totalFrames; i++)
                {
                    audioData[0, framesRead + i] = floatSpan[i * 2];
                    audioData[1, framesRead + i] = floatSpan[i * 2 + 1];
                }

                framesRead += result.FramesRead;
            }

            return audioData;
        }

        /// <summary>
        /// Extract window with reflection padding at boundaries
        /// </summary>
        private float[,] ExtractWindowWithPadding(float[,] audioData, int windowStart, int windowSize, int totalFrames)
        {
            var window = new float[2, windowSize];

            for (int i = 0; i < windowSize; i++)
            {
                int sourceIdx = windowStart + i;

                // Reflection padding at start
                if (sourceIdx < 0)
                {
                    sourceIdx = -sourceIdx;
                }
                // Reflection padding at end
                else if (sourceIdx >= totalFrames)
                {
                    sourceIdx = 2 * totalFrames - sourceIdx - 2;
                }

                // Clamp to valid range
                sourceIdx = Math.Max(0, Math.Min(totalFrames - 1, sourceIdx));

                window[0, i] = audioData[0, sourceIdx];
                window[1, i] = audioData[1, sourceIdx];
            }

            return window;
        }

        /// <summary>
        /// Trim margins from separated stems output
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> TrimMargins(
            Dictionary<HTDemucsStem, float[,]> stems,
            int marginSamples,
            int validSize)
        {
            var trimmed = new Dictionary<HTDemucsStem, float[,]>();

            foreach (var kvp in stems)
            {
                var stem = kvp.Key;
                var source = kvp.Value;
                int sourceLength = source.GetLength(1);

                // Extract [margin ... margin + validSize]
                int extractLength = Math.Min(validSize, sourceLength - marginSamples);
                var trimmedStem = new float[2, extractLength];

                for (int ch = 0; ch < 2; ch++)
                {
                    for (int i = 0; i < extractLength; i++)
                    {
                        trimmedStem[ch, i] = source[ch, marginSamples + i];
                    }
                }

                trimmed[stem] = trimmedStem;
            }

            return trimmed;
        }

        /// <summary>
        /// Apply trimmed chunks with crossfade overlap
        /// </summary>
        private void ApplyTrimmedOverlapAdd(
            Dictionary<HTDemucsStem, float[,]> targetBuffers,
            Dictionary<HTDemucsStem, float[,]> sourceChunk,
            int position,
            int crossfadeSamples,
            int validLength,
            int totalLength)
        {
            foreach (var kvp in sourceChunk)
            {
                var stem = kvp.Key;
                var source = kvp.Value;

                if (!targetBuffers.ContainsKey(stem))
                    continue;

                var target = targetBuffers[stem];

                // First chunk: direct copy
                if (position == 0)
                {
                    CopyAudioRegion(source, target, 0, 0, Math.Min(validLength, totalLength));
                }
                else
                {
                    // Crossfade region
                    int crossfadeLength = Math.Min(crossfadeSamples, Math.Min(validLength, totalLength - position));
                    if (crossfadeLength > 0)
                    {
                        BlendOverlap(source, target, 0, position, crossfadeLength);
                    }

                    // Non-overlapping part
                    int nonOverlapStart = crossfadeLength;
                    int copyLength = Math.Min(validLength - nonOverlapStart, totalLength - position - nonOverlapStart);
                    if (copyLength > 0)
                    {
                        CopyAudioRegion(source, target, nonOverlapStart, position + nonOverlapStart, copyLength);
                    }
                }
            }
        }

        #endregion

        #region Private Methods - Model Inference

        /// <summary>
        /// Process a single audio chunk through HTDemucs model
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> ProcessChunk(
            float[,] audioChunk,
            HTDemucsProcessingContext context)
        {
            int chunkLength = audioChunk.GetLength(1);

            // Prepare waveform input tensor: [batch=1, channels=2, samples]
            // NO normalization - ONNX model handles this internally
            var inputTensorWave = new DenseTensor<float>(new[] { 1, 2, chunkLength });

            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < chunkLength; i++)
                {
                    inputTensorWave[0, ch, i] = audioChunk[ch, i];
                }
            }

            // Compute spectrogram input tensor using shared STFT processor
            // [batch=1, channels=2, freq_bins=2048, time_frames, complex=2]
            var spectrogramData = _stftProcessor!.ComputeSpectrogram(audioChunk);
            int n_frames = spectrogramData.GetLength(3);
            
            var flattenedSpec = STFTProcessor.Flatten5D(spectrogramData);
            var inputTensorSpec = new DenseTensor<float>(flattenedSpec, new[] { 1, 2, 2048, n_frames, 2 });

            // Get input names from model metadata
            var inputNames = _onnxSession!.InputMetadata.Keys.ToArray();
            
            Log.Info($"Model expects {inputNames.Length} inputs: {string.Join(", ", inputNames)}");
            Log.Info($"Waveform tensor shape: [1, 2, {chunkLength}]");
            Log.Info($"Spectrogram tensor shape: [1, 2, 2048, {n_frames}, 2]");

            // Run ONNX inference with both inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputNames[0], inputTensorWave),
                NamedOnnxValue.CreateFromTensor(inputNames[1], inputTensorSpec)
            };

            using var outputs = _onnxSession.Run(inputs);
            
            // HTDemucs HYBRID model outputs TWO tensors that must be MERGED:
            // outputs[0] = "add_76": [1, 4, 4, 2048, 431] - FREQUENCY BRANCH (masked spectrograms)
            // outputs[1] = "add_77": [1, 4, 2, 441000] - TIME BRANCH (waveform)
            // 
            // Python reference (htdemucs.py line 661): x = xt + x
            // Final output = time_branch + ISTFT(frequency_branch)
            
            var outputList = outputs.ToList();
            Log.Info($"Model returned {outputList.Count} outputs");
            
            if (outputList.Count < 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 outputs from HTDemucs model, got {outputList.Count}. " +
                    "The model should output both frequency branch (add_76) and time branch (add_77).");
            }
            
            // Get both outputs
            var freqBranchSpectrogram = outputList[0].AsTensor<float>();  // add_76: spectrogram [1,4,4,2048,431]
            var timeBranchWaveform = outputList[1].AsTensor<float>();     // add_77: waveform [1,4,2,441000]
            
            Log.Info($"Frequency branch (add_76): shape = [{string.Join(", ", freqBranchSpectrogram.Dimensions.ToArray())}]");
            Log.Info($"Time branch (add_77): shape = [{string.Join(", ", timeBranchWaveform.Dimensions.ToArray())}]");

            // Extract stems by merging both branches (ISTFT(freq) + time)
            // ONNX model already handles normalization/denormalization internally
            return ExtractStemsFromDualBranch(freqBranchSpectrogram, timeBranchWaveform, chunkLength);
        }

        /// <summary>
        /// Extract individual stems from model output tensor and convert from spectrogram to waveform
        /// HTDemucs outputs spectrograms, not waveforms, so ISTFT is required
        /// </summary>
        private Dictionary<HTDemucsStem, float[,]> ExtractStems(Tensor<float> outputTensor, int targetLength)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            // HTDemucs output shape: [batch, stems, channels, freq_bins, time_frames]
            // where channels = 4 (L_Real, L_Imag, R_Real, R_Imag)
            int stemCount = outputTensor.Dimensions[1];
            int outputChannels = outputTensor.Dimensions[2];
            int freqBins = outputTensor.Dimensions[3];
            int timeFrames = outputTensor.Dimensions[4];

            Log.Info($"Extracting stems from output: stems={stemCount}, channels={outputChannels}, freq={freqBins}, time={timeFrames}");

            // Map stem indices to enum values
            // NOTE: Default HTDemucs order is typically: Drums, Bass, Other, Vocals
            var stemMapping = new[]
            {
                HTDemucsStem.Drums,
                HTDemucsStem.Bass,
                HTDemucsStem.Other,
                HTDemucsStem.Vocals
            };

            for (int s = 0; s < Math.Min(stemCount, stemMapping.Length); s++)
            {
                var stem = stemMapping[s];

                if (!_options.TargetStems.HasFlag(stem))
                    continue;

                // Extract spectrogram for this stem
                // Output format: [batch=1, stems, channels=4, freq=2048, time=431]
                // Channels: 0=L_Real, 1=L_Imag, 2=R_Real, 3=R_Imag
                var spectrogram = new float[1, 2, freqBins, timeFrames, 2];

                // Reorganize from [L_Real, L_Imag, R_Real, R_Imag] to [L, R] with [Real, Imag]
                for (int t = 0; t < timeFrames; t++)
                {
                    for (int f = 0; f < freqBins; f++)
                    {
                        // Left channel
                        spectrogram[0, 0, f, t, 0] = outputTensor[0, s, 0, f, t]; // L_Real
                        spectrogram[0, 0, f, t, 1] = outputTensor[0, s, 1, f, t]; // L_Imag
                        
                        // Right channel
                        spectrogram[0, 1, f, t, 0] = outputTensor[0, s, 2, f, t]; // R_Real
                        spectrogram[0, 1, f, t, 1] = outputTensor[0, s, 3, f, t]; // R_Imag
                    }
                }

                // Convert spectrogram to waveform using ISTFT
                Log.Info($"Converting {stem} spectrogram to waveform using ISTFT (target length: {targetLength})");
                var waveform = _stftProcessor!.ComputeISTFT(spectrogram, targetLength);

                stems[stem] = waveform;
            }

            return stems;
        }

        /// <summary>
        /// Extract individual stems by merging BOTH frequency and time branches
        /// This is the CORRECT approach for HTDemucs hybrid model
        /// Python reference (htdemucs.py line 661): final = time_branch + freq_branch_after_istft
        /// NOTE: ONNX model handles normalization/denormalization internally
        /// </summary>
        /// <param name="freqSpectrograms">Frequency branch spectrograms [batch, stems, 4, freq_bins, time_frames]</param>
        /// <param name="timeWaveforms">Time branch waveforms [batch, stems, channels, samples]</param>
        /// <param name="targetLength">Target length to trim/pad to</param>
        /// <returns>Dictionary of stems with merged audio data</returns>
        private Dictionary<HTDemucsStem, float[,]> ExtractStemsFromDualBranch(
            Tensor<float> freqSpectrograms, 
            Tensor<float> timeWaveforms,
            int targetLength)
        {
            var stems = new Dictionary<HTDemucsStem, float[,]>();

            // Frequency branch: [batch, stems, 4, freq_bins, time_frames]
            // where channels = 4 (L_Real, L_Imag, R_Real, R_Imag)
            int stemCount = freqSpectrograms.Dimensions[1];
            int freqChannels = freqSpectrograms.Dimensions[2];
            int freqBins = freqSpectrograms.Dimensions[3];
            int timeFrames = freqSpectrograms.Dimensions[4];
            
            // Time branch: [batch, stems, channels, samples]
            int timeChannels = timeWaveforms.Dimensions[2];
            int timeSamples = timeWaveforms.Dimensions[3];

            Log.Info($"Merging dual branches: freq=[stems={stemCount}, ch={freqChannels}, freq={freqBins}, time={timeFrames}], " +
                     $"time=[stems={stemCount}, ch={timeChannels}, samples={timeSamples}]");

            // Map stem indices to enum values
            // HTDemucs default order: Drums, Bass, Other, Vocals
            var stemMapping = new[]
            {
                HTDemucsStem.Drums,
                HTDemucsStem.Bass,
                HTDemucsStem.Other,
                HTDemucsStem.Vocals
            };

            for (int s = 0; s < Math.Min(stemCount, stemMapping.Length); s++)
            {
                var stem = stemMapping[s];

                if (!_options.TargetStems.HasFlag(stem))
                    continue;

                // STEP 1: Extract and convert frequency branch spectrogram to waveform
                // Convert from [L_Real, L_Imag, R_Real, R_Imag] to [L, R] with [Real, Imag]
                var spectrogram = new float[1, 2, freqBins, timeFrames, 2];

                for (int t = 0; t < timeFrames; t++)
                {
                    for (int f = 0; f < freqBins; f++)
                    {
                        // Left channel
                        spectrogram[0, 0, f, t, 0] = freqSpectrograms[0, s, 0, f, t]; // L_Real
                        spectrogram[0, 0, f, t, 1] = freqSpectrograms[0, s, 1, f, t]; // L_Imag
                        
                        // Right channel
                        spectrogram[0, 1, f, t, 0] = freqSpectrograms[0, s, 2, f, t]; // R_Real
                        spectrogram[0, 1, f, t, 1] = freqSpectrograms[0, s, 3, f, t]; // R_Imag
                    }
                }

                // Convert spectrogram to waveform using ISTFT
                Log.Info($"Converting {stem} frequency branch spectrogram to waveform using ISTFT");
                var freqBranchWaveform = _stftProcessor!.ComputeISTFT(spectrogram, targetLength);

                // STEP 2: Extract time branch waveform
                var timeBranchWaveform = new float[timeChannels, Math.Min(timeSamples, targetLength)];
                int copyLength = Math.Min(timeSamples, targetLength);

                for (int ch = 0; ch < timeChannels; ch++)
                {
                    for (int i = 0; i < copyLength; i++)
                    {
                        timeBranchWaveform[ch, i] = timeWaveforms[0, s, ch, i];
                    }
                }

                // STEP 3: MERGE both branches (Python line 661: x = xt + x)
                // final = time_branch + freq_branch_istft
                // ONNX model already outputs denormalized values
                var mergedWaveform = new float[timeChannels, targetLength];
                for (int ch = 0; ch < timeChannels; ch++)
                {
                    for (int i = 0; i < targetLength; i++)
                    {
                        float freqSample = i < freqBranchWaveform.GetLength(1) ? freqBranchWaveform[ch, i] : 0f;
                        float timeSample = i < timeBranchWaveform.GetLength(1) ? timeBranchWaveform[ch, i] : 0f;
                        mergedWaveform[ch, i] = freqSample + timeSample;
                    }
                }

                Log.Info($"Merged {stem}: freq_branch + time_branch = final waveform [{timeChannels}, {targetLength}]");
                stems[stem] = mergedWaveform;
            }

            return stems;
        }

        #endregion

        #region Private Methods - Overlap-Add

        /// <summary>
        /// Apply overlap-add reconstruction with linear crossfade
        /// </summary>
        private void ApplyOverlapAdd(
            Dictionary<HTDemucsStem, float[,]> targetBuffers,
            Dictionary<HTDemucsStem, float[,]> sourceChunk,
            int position,
            int overlap,
            int chunkLength,
            int totalLength)
        {
            foreach (var kvp in sourceChunk)
            {
                var stem = kvp.Key;
                var source = kvp.Value;

                if (!targetBuffers.ContainsKey(stem))
                    continue;

                var target = targetBuffers[stem];

                // Determine regions
                int nonOverlapStart = (position == 0) ? 0 : overlap;
                int copyLength = Math.Min(chunkLength - nonOverlapStart, totalLength - position - nonOverlapStart);

                // Copy non-overlapping part
                if (copyLength > 0)
                {
                    CopyAudioRegion(source, target, nonOverlapStart, position + nonOverlapStart, copyLength);
                }

                // Blend overlapping region with linear crossfade
                if (position > 0 && overlap > 0)
                {
                    int blendLength = Math.Min(overlap, totalLength - position);
                    BlendOverlap(source, target, 0, position, blendLength);
                }
            }
        }

        /// <summary>
        /// Copy audio data from source to target
        /// </summary>
        private void CopyAudioRegion(float[,] source, float[,] target, int srcStart, int dstStart, int length)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < length; i++)
                {
                    if (dstStart + i < target.GetLength(1))
                    {
                        target[ch, dstStart + i] = source[ch, srcStart + i];
                    }
                }
            }
        }

        /// <summary>
        /// Blend overlapping region with constant-power crossfade
        /// Uses cosine-based fade to maintain constant energy across transition
        /// </summary>
        private void BlendOverlap(float[,] source, float[,] target, int srcStart, int dstStart, int length)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                for (int i = 0; i < length; i++)
                {
                    if (dstStart + i < target.GetLength(1))
                    {
                        // Constant-power crossfade using cosine/sine
                        // This maintains perceived loudness better than linear fade
                        float position = (float)i / (float)length;  // 0.0 to 1.0
                        float angle = position * (float)Math.PI / 2.0f;  // 0 to π/2
                        
                        float fadeOut = (float)Math.Cos(angle);  // 1.0 to 0.0 (previous chunk)
                        float fadeIn = (float)Math.Sin(angle);   // 0.0 to 1.0 (current chunk)

                        target[ch, dstStart + i] = target[ch, dstStart + i] * fadeOut +
                                                   source[ch, srcStart + i] * fadeIn;
                    }
                }
            }
        }

        #endregion

        #region Private Methods - File I/O

        /// <summary>
        /// Save separated stems to WAV files
        /// </summary>
        private Dictionary<HTDemucsStem, string> SaveResults(
            string filename,
            Dictionary<HTDemucsStem, float[,]> stems,
            int sampleRate)
        {
            var outputPaths = new Dictionary<HTDemucsStem, string>();

            foreach (var kvp in stems)
            {
                var stem = kvp.Key;
                var audio = kvp.Value;

                string stemName = stem.ToString().ToLower();
                string outputPath = Path.Combine(
                    _options.OutputDirectory,
                    $"{filename}_{stemName}.wav"
                );

                SaveAudio(outputPath, audio, sampleRate);
                outputPaths[stem] = outputPath;

                Log.Info($"Saved {stemName} stem: {outputPath}");
            }

            return outputPaths;
        }

        /// <summary>
        /// Save audio data to WAV file with normalization
        /// </summary>
        private void SaveAudio(string filePath, float[,] audio, int sampleRate)
        {
            int channels = audio.GetLength(0);
            int samples = audio.GetLength(1);

            // Find peak for normalization
            float maxVal = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                for (int i = 0; i < samples; i++)
                {
                    maxVal = Math.Max(maxVal, Math.Abs(audio[ch, i]));
                }
            }

            // Normalize to prevent clipping (0.95 = -0.44 dBFS headroom)
            float scale = (maxVal > 0.95f) ? (0.95f / maxVal) : 1.0f;

            // Interleave channels for WAV format
            var interleaved = new float[samples * channels];
            for (int i = 0; i < samples; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    interleaved[i * channels + ch] = audio[ch, i] * scale;
                }
            }

            // Use OwnAudioSharp WaveFile writer
            OwnaudioNET.Recording.WaveFile.Create(
                filePath,
                interleaved,
                sampleRate,
                channels,
                16  // bit depth
            );
        }

        #endregion

        #region Inner Classes

        /// <summary>
        /// Processing context for buffer reuse and GC pressure reduction
        /// </summary>
        private class HTDemucsProcessingContext : IDisposable
        {
            public float[] ReadBuffer { get; }
            public float[] IntermediateBuffer { get; }

            public HTDemucsProcessingContext(int chunkSize, int overlap)
            {
                int maxChunkSize = chunkSize + 2 * overlap;

                ReadBuffer = new float[maxChunkSize * 2];
                IntermediateBuffer = new float[maxChunkSize * 2];
            }

            public void Dispose()
            {
                // Managed arrays, nothing to dispose
            }
        }

        #endregion
    }

    #endregion

    #region Helper Extensions

    /// <summary>
    /// Helper extension methods for HTDemucs audio separation
    /// </summary>
    public static class HTDemucsExtensions
    {
        /// <summary>
        /// Creates a default HTDemucs separator instance using embedded model
        /// </summary>
        /// <param name="outputDirectory">Output directory for separated stems</param>
        /// <returns>Configured HTDemucsAudioSeparator</returns>
        public static HTDemucsAudioSeparator CreateDefaultSeparator(string outputDirectory = "separated_htdemucs")
        {
            var options = new HTDemucsSeparationOptions
            {
                Model = InternalModel.HTDemucs,
                OutputDirectory = outputDirectory,
                ChunkSizeSeconds = 10,
                OverlapFactor = 0.25f,
                EnableGPU = true,
                TargetStems = HTDemucsStem.All
            };

            return new HTDemucsAudioSeparator(options);
        }

        /// <summary>
        /// Creates a HTDemucs separator using external model file
        /// </summary>
        /// <param name="modelPath">Path to HTDemucs ONNX model file</param>
        /// <param name="outputDirectory">Output directory for separated stems</param>
        /// <returns>Configured HTDemucsAudioSeparator</returns>
        public static HTDemucsAudioSeparator CreateFromFile(string modelPath, string outputDirectory = "separated_htdemucs")
        {
            var options = new HTDemucsSeparationOptions
            {
                ModelPath = modelPath,
                OutputDirectory = outputDirectory,
                ChunkSizeSeconds = 10,
                OverlapFactor = 0.25f,
                EnableGPU = true,
                TargetStems = HTDemucsStem.All
            };

            return new HTDemucsAudioSeparator(options);
        }

        /// <summary>
        /// Creates a separator for specific stems only (using embedded model)
        /// </summary>
        /// <param name="targetStems">Stems to extract</param>
        /// <param name="outputDirectory">Output directory for separated stems</param>
        /// <returns>Configured HTDemucsAudioSeparator</returns>
        public static HTDemucsAudioSeparator CreateStemSelector(HTDemucsStem targetStems, string outputDirectory = "separated_htdemucs")
        {
            var options = new HTDemucsSeparationOptions
            {
                Model = InternalModel.HTDemucs,
                OutputDirectory = outputDirectory,
                ChunkSizeSeconds = 10,
                OverlapFactor = 0.25f,
                EnableGPU = true,
                TargetStems = targetStems
            };

            return new HTDemucsAudioSeparator(options);
        }

        /// <summary>
        /// Validate audio file format
        /// </summary>
        /// <param name="filePath">Audio file path</param>
        /// <returns>True if supported format</returns>
        public static bool IsValidAudioFile(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var supportedFormats = new[] { ".wav", ".mp3", ".flac" };

            return supportedFormats.Contains(extension);
        }

        /// <summary>
        /// Get estimated processing time based on file size
        /// </summary>
        /// <param name="filePath">Audio file path</param>
        /// <param name="useGPU">Whether GPU acceleration is available</param>
        /// <returns>Estimated processing time</returns>
        public static TimeSpan EstimateProcessingTime(string filePath, bool useGPU = false)
        {
            if (!File.Exists(filePath))
                return TimeSpan.Zero;

            var fileInfo = new FileInfo(filePath);
            var fileSizeMB = fileInfo.Length / (1024.0 * 1024.0);

            // Rough estimates based on typical performance
            double minutesPerMB = useGPU ? 0.1 : 1.0;  // GPU: 50-100x realtime, CPU: 10-15x realtime
            var estimatedMinutes = fileSizeMB * minutesPerMB;

            return TimeSpan.FromMinutes(Math.Max(0.1, estimatedMinutes));
        }

        /// <summary>
        /// Get stem name as localized string
        /// </summary>
        /// <param name="stem">Stem type</param>
        /// <returns>Human-readable stem name</returns>
        public static string GetStemName(this HTDemucsStem stem)
        {
            return stem switch
            {
                HTDemucsStem.Vocals => "Vocals",
                HTDemucsStem.Drums => "Drums",
                HTDemucsStem.Bass => "Bass",
                HTDemucsStem.Other => "Other",
                _ => "Unknown"
            };
        }
    }

    #endregion
}
