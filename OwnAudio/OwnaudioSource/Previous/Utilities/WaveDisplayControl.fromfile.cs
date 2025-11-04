using Ownaudio.Decoders;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ownaudio.Utilities
{
    public partial class WaveAvaloniaDisplay : Avalonia.Controls.Control
    {
        private const int _maxsample = 10000;

        /// <summary>
        /// Loads and displays audio data from the specified file with minimal memory usage.
        /// Supports both MiniAudio and FFmpeg decoders automatically.
        /// </summary>
        /// <param name="filePath">Path to the audio file to load.</param>
        /// <param name="maxSamples">Maximum number of samples to keep for visualization (default: 100000).</param>
        /// <param name="preferFFmpeg">If true, tries FFmpeg decoder first, otherwise tries MiniAudio first (default: false).</param>
        /// <param name="channels">Desired channel count for decoding (default: 1 for mono visualization).</param>
        /// <param name="sampleRate">Desired sample rate for decoding (default: 44100).</param>
        /// <returns>True if the file was loaded successfully, false otherwise.</returns>
        /// <exception cref="ArgumentNullException">Thrown when filePath is null or empty.</exception>
        /// <remarks>
        /// This method optimizes memory usage by:
        /// - Processing audio in small chunks instead of loading everything at once
        /// - Downsampling the data for visualization purposes
        /// - Using ArrayPool for temporary buffers
        /// - Disposing of the decoder immediately after processing
        /// - Automatically falling back between decoder types
        /// The resulting audio data will be suitable for waveform display while keeping memory usage minimal.
        /// </remarks>
        public bool LoadFromAudioFile(string filePath, int maxSamples = 100000, bool preferFFmpeg = false,
            int channels = 1, int sampleRate = 44100)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            if (!File.Exists(filePath))
                return false;

            // Reset current state
            _audioData = null!;
            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            InvalidateVisual();

            // Try decoders in preferred order
            return TryLoadFile(filePath, maxSamples, channels, sampleRate);
        }

        /// <summary>
        /// Tries to load audio file using MiniAudio decoder.
        /// </summary>
        /// <param name="filePath">Path to the audio file.</param>
        /// <param name="maxSamples">Maximum number of samples for visualization.</param>
        /// <param name="channels">Desired channel count.</param>
        /// <param name="sampleRate">Desired sample rate.</param>
        /// <returns>True if successful, false otherwise.</returns>
        private bool TryLoadFile(string filePath, int maxSamples, int channels, int sampleRate)
        {
            IAudioDecoder? decoder = null;
            var tempBuffer = ArrayPool<float>.Shared.Rent(4096);
            var sampleList = new List<float>(maxSamples);

            try
            {
                //options = new FFmpegDecoderOptions(channels, sampleRate);
                decoder = AudioDecoderFactory.Create(filePath, sampleRate, channels);

                var streamInfo = decoder.StreamInfo;
                if (streamInfo.Channels == 0 || streamInfo.SampleRate == 0)
                    return false;

                // Calculate downsampling ratio
                var estimatedTotalSamples = (long)(streamInfo.Duration.TotalSeconds * streamInfo.SampleRate * streamInfo.Channels);
                var downsampleRatio = Math.Max(1, (int)(estimatedTotalSamples / maxSamples));

                int sampleCounter = 0;
                int channelCount = streamInfo.Channels;

                // Process audio in chunks
                while (true)
                {
                    var result = decoder.DecodeNextFrame();

                    if (result.IsEOF || !result.IsSucceeded)
                        break;

                    if (result.Frame?.Data == null)
                        continue;

                    // Convert byte data back to float samples
                    var frameData = result.Frame.Data;
                    int floatCount = frameData.Length / sizeof(float);

                    if (tempBuffer.Length < floatCount)
                    {
                        ArrayPool<float>.Shared.Return(tempBuffer);
                        tempBuffer = ArrayPool<float>.Shared.Rent(floatCount);
                    }

                    Buffer.BlockCopy(frameData, 0, tempBuffer, 0, frameData.Length);

                    // Process samples with downsampling and channel mixing
                    for (int i = 0; i < floatCount; i += channelCount)
                    {
                        if (sampleCounter % downsampleRatio == 0)
                        {
                            // Mix channels to mono by averaging
                            float sample = 0f;
                            for (int ch = 0; ch < channelCount && i + ch < floatCount; ch++)
                            {
                                sample += tempBuffer[i + ch];
                            }
                            sample /= channelCount;

                            sampleList.Add(sample);

                            if (sampleList.Count >= maxSamples)
                                break;
                        }
                        sampleCounter++;
                    }

                    if (sampleList.Count >= maxSamples)
                        break;
                }

                // Set result if successful
                if (sampleList.Count > 0)
                {
                    _audioData = sampleList.ToArray();
                    InvalidateVisual();
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MiniAudio decoder failed: {ex.Message}");
                return false;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(tempBuffer);
                decoder?.Dispose();
            }
        }

        /// <summary>
        /// Asynchronously loads and displays audio data from the specified file with minimal memory usage.
        /// Supports both MiniAudio and FFmpeg decoders automatically.
        /// </summary>
        /// <param name="filePath">Path to the audio file to load.</param>
        /// <param name="maxSamples">Maximum number of samples to keep for visualization (default: 100000).</param>
        /// <param name="preferFFmpeg">If true, tries FFmpeg decoder first, otherwise tries MiniAudio first (default: false).</param>
        /// <param name="channels">Desired channel count for decoding (default: 1 for mono visualization).</param>
        /// <param name="sampleRate">Desired sample rate for decoding (default: 44100).</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>Task that returns true if the file was loaded successfully, false otherwise.</returns>
        public async Task<bool> LoadFromAudioFileAsync(string filePath, int maxSamples = 100000, bool preferFFmpeg = false,
            int channels = 1, int sampleRate = 44100, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => LoadFromAudioFile(filePath, maxSamples, preferFFmpeg, channels, sampleRate), cancellationToken);
        }

        /// <summary>
        /// Loads audio data from a stream with minimal memory usage.
        /// Supports both MiniAudio and FFmpeg decoders automatically.
        /// </summary>
        /// <param name="stream">Audio stream to load from.</param>
        /// <param name="audioFormat">Audio format (wav, mp3, flac) <see cref="AudioFormat"/></param>
        /// <param name="preferFFmpeg">If true, tries FFmpeg decoder first, otherwise tries MiniAudio first (default: false).</param>
        /// <param name="channels">Desired channel count for decoding (default: 1 for mono visualization).</param>
        /// <param name="sampleRate">Desired sample rate for decoding (default: 44100).</param>
        /// <returns>True if the stream was loaded successfully, false otherwise.</returns>
        public bool LoadFromAudioStream(Stream stream, AudioFormat audioFormat, bool preferFFmpeg = false,
            int channels = 1, int sampleRate = 44100)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            // Reset current state
            _audioData = null;
            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            InvalidateVisual();

            // Try decoders in preferred order
            return TryLoadStream(stream, AudioFormat.Wav, channels, sampleRate);
        }

        /// <summary>
        /// Tries to load audio stream using MiniAudio decoder.
        /// </summary>
        /// <param name="stream">Audio stream to load.</param>
        /// <param name="maxSamples">Maximum number of samples for visualization.</param>
        /// <param name="channels">Desired channel count.</param>
        /// <param name="sampleRate">Desired sample rate.</param>
        /// <returns>True if successful, false otherwise.</returns>
        private bool TryLoadStream(Stream stream, AudioFormat audioFormat, int channels, int sampleRate)
        {
            IAudioDecoder? decoder = null;
            var tempBuffer = ArrayPool<float>.Shared.Rent(4096);
            var sampleList = new List<float>(_maxsample);

            try
            {
                //var options = new FFmpegDecoderOptions(channels, sampleRate);
                decoder = AudioDecoderFactory.Create(stream, AudioFormat.Wav, sampleRate, channels);

                return ProcessDecoderData(decoder, tempBuffer, sampleList, _maxsample);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MiniAudio stream decoder failed: {ex.Message}");
                return false;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(tempBuffer);
                decoder?.Dispose();
            }
        }

        /// <summary>
        /// Common method to process decoder data regardless of decoder type.
        /// </summary>
        /// <param name="decoder">The audio decoder (MiniDecoder or FFmpegDecoder).</param>
        /// <param name="tempBuffer">Temporary buffer for processing.</param>
        /// <param name="sampleList">List to collect processed samples.</param>
        /// <param name="maxSamples">Maximum number of samples to collect.</param>
        /// <returns>True if processing was successful, false otherwise.</returns>
        private bool ProcessDecoderData(IAudioDecoder decoder, float[] tempBuffer, List<float> sampleList, int maxSamples)
        {
            var streamInfo = decoder.StreamInfo;
            if (streamInfo.Channels == 0 || streamInfo.SampleRate == 0)
                return false;

            var estimatedTotalSamples = (long)(streamInfo.Duration.TotalSeconds * streamInfo.SampleRate * streamInfo.Channels);
            var downsampleRatio = Math.Max(1, (int)(estimatedTotalSamples / maxSamples));

            int sampleCounter = 0;
            int channelCount = streamInfo.Channels;

            while (true)
            {
                var result = decoder.DecodeNextFrame();

                if (result.IsEOF || !result.IsSucceeded)
                    break;

                if (result.Frame?.Data == null)
                    continue;

                var frameData = result.Frame.Data;
                int floatCount = frameData.Length / sizeof(float);

                if (tempBuffer.Length < floatCount)
                {
                    ArrayPool<float>.Shared.Return(tempBuffer);
                    tempBuffer = ArrayPool<float>.Shared.Rent(floatCount);
                }

                Buffer.BlockCopy(frameData, 0, tempBuffer, 0, frameData.Length);

                for (int i = 0; i < floatCount; i += channelCount)
                {
                    if (sampleCounter % downsampleRatio == 0)
                    {
                        float sample = 0f;
                        for (int ch = 0; ch < channelCount && i + ch < floatCount; ch++)
                        {
                            sample += tempBuffer[i + ch];
                        }
                        sample /= channelCount;

                        sampleList.Add(sample);

                        if (sampleList.Count >= maxSamples)
                            break;
                    }
                    sampleCounter++;
                }

                if (sampleList.Count >= maxSamples)
                    break;
            }

            if (sampleList.Count > 0)
            {
                _audioData = sampleList.ToArray();
                InvalidateVisual();
                return true;
            }

            return false;
        }
    }
}
