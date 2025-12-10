using Ownaudio;
using Ownaudio.Core;
using Ownaudio.Decoders;
using OwnaudioNET.Core;
using OwnaudioNET.Exceptions;
using System;
using System.Buffers;
using System.IO;

namespace OwnaudioNET.Sources;

/// <summary>
/// Partial class for FileSource - implements audio data extraction functionality.
/// Provides GetByteAudioData and GetFloatAudioData methods for extracting raw audio samples.
/// </summary>
public partial class FileSource
{
    private string? _filePath;

    /// <summary>
    /// Extracts raw audio data as bytes at the specified position.
    /// This creates a temporary decoder instance to read the data without affecting playback.
    /// </summary>
    /// <param name="position">The position in the audio to extract data from.</param>
    /// <param name="duration">Optional duration of audio to extract. If null, extracts from position to end.</param>
    /// <returns>Byte array containing raw audio data (Float32 interleaved samples).</returns>
    /// <exception cref="AudioException">Thrown when data extraction fails.</exception>
    public byte[] GetByteAudioData(TimeSpan position, TimeSpan? duration = null)
    {
        ThrowIfDisposed();

        try
        {
            // Calculate how much data to read
            TimeSpan actualDuration = duration ?? TimeSpan.FromSeconds(Math.Max(0, Duration - position.TotalSeconds));

            if (actualDuration <= TimeSpan.Zero)
            {
                return Array.Empty<byte>();
            }

            // Check if we have a file path to create a decoder
            if (string.IsNullOrEmpty(_filePath))
            {
                return Array.Empty<byte>();
            }

            // Create a temporary decoder to extract data without affecting playback
            using var tempDecoder = AudioDecoderFactory.Create(_filePath, _streamInfo.SampleRate, _streamInfo.Channels);

            // Seek to target position
            if (!tempDecoder.TrySeek(position, out string seekError))
            {
                throw new AudioException($"Failed to seek to position {position}: {seekError}");
            }

            // Calculate total bytes needed
            int totalFrames = (int)(actualDuration.TotalSeconds * _streamInfo.SampleRate);
            int totalBytes = totalFrames * _streamInfo.Channels * sizeof(float);

            // Use a MemoryStream to accumulate data, which is more flexible than a fixed-size array.
            using var memoryStream = new MemoryStream(totalBytes > 0 ? totalBytes : 32768);

            // ZERO-ALLOC: Use a reusable byte buffer for the new ReadFrames method.
            var byteBuffer = ArrayPool<byte>.Shared.Rent(4096 * _streamInfo.Channels * sizeof(float));

            try
            {
                int bytesWritten = 0;
                // Decode frames until we have enough data
                while (bytesWritten < totalBytes)
                {
                    var result = tempDecoder.ReadFrames(byteBuffer);

                    if (result.IsEOF || !result.IsSucceeded || result.FramesRead == 0)
                    {
                        break;
                    }

                    // Copy frame data to result
                    int bytesRead = result.FramesRead * _streamInfo.Channels * sizeof(float);
                    int bytesToCopy = Math.Min(bytesRead, totalBytes - bytesWritten);
                    memoryStream.Write(byteBuffer, 0, bytesToCopy);
                    bytesWritten += bytesToCopy;
                }

                return memoryStream.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(byteBuffer);
            }
        }
        catch (Exception ex) when (ex is not AudioException)
        {
            throw new AudioException($"Failed to extract audio data: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts raw audio data as float samples at the specified position.
    /// This creates a temporary decoder instance to read the data without affecting playback.
    /// </summary>
    /// <param name="position">The position in the audio to extract data from.</param>
    /// <param name="duration">Optional duration of audio to extract. If null, extracts from position to end.</param>
    /// <returns>Float array containing audio samples (Float32 interleaved).</returns>
    /// <exception cref="AudioException">Thrown when data extraction fails.</exception>
    public float[] GetFloatAudioData(TimeSpan position, TimeSpan? duration = null)
    {
        byte[] byteData = GetByteAudioData(position, duration);

        if (byteData.Length == 0)
        {
            return Array.Empty<float>();
        }

        // Convert byte[] to float[]
        float[] floatData = new float[byteData.Length / sizeof(float)];
        Buffer.BlockCopy(byteData, 0, floatData, 0, byteData.Length);

        return floatData;
    }

    /// <summary>
    /// Gets the current output levels (peak levels) for this source.
    /// This monitors the audio buffer for peak sample values.
    /// </summary>
    /// <returns>Tuple containing left and right channel peak levels (0.0 to 1.0), or null if not available.</returns>
    public (float left, float right)? GetOutputLevels()
    {
        ThrowIfDisposed();

        if (State != AudioState.Playing || _buffer.IsEmpty)
        {
            return (0f, 0f);
        }

        try
        {
            // Create a temporary buffer to peek at current audio data
            int peekSamples = Math.Min(512 * _streamInfo.Channels, _buffer.Available);
            if (peekSamples == 0)
            {
                return (0f, 0f);
            }

            Span<float> peekBuffer = stackalloc float[peekSamples];
            int actualSamples = _buffer.Peek(peekBuffer);

            if (actualSamples == 0)
            {
                return (0f, 0f);
            }

            // Calculate peak levels for each channel
            float leftPeak = 0f;
            float rightPeak = 0f;
            int channels = _streamInfo.Channels;

            for (int i = 0; i < actualSamples; i += channels)
            {
                float leftSample = Math.Abs(peekBuffer[i]);
                leftPeak = Math.Max(leftPeak, leftSample);

                if (channels > 1)
                {
                    float rightSample = Math.Abs(peekBuffer[i + 1]);
                    rightPeak = Math.Max(rightPeak, rightSample);
                }
            }

            // If mono, use same value for both channels
            if (channels == 1)
            {
                rightPeak = leftPeak;
            }

            // Apply volume scaling
            leftPeak *= Volume;
            rightPeak *= Volume;

            return (leftPeak, rightPeak);
        }
        catch
        {
            return (0f, 0f);
        }
    }
}
