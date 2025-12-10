using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ownaudio.Core;

namespace OwnaudioNET.Recording;

/// <summary>
/// Records audio data to memory and can export to WAV files.
/// Used by AudioMixer to implement the Play(fileName, bitDepth) recording functionality.
/// </summary>
public class AudioRecorder : IDisposable
{
    private readonly AudioConfig _config;
    private readonly List<float> _recordedSamples = new();
    private readonly object _lock = new();
    private bool _isRecording;
    private string? _outputFilePath;
    private int _bitDepth = 16;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the AudioRecorder class.
    /// </summary>
    /// <param name="config">Audio configuration for the recording.</param>
    public AudioRecorder(AudioConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Gets whether the recorder is currently recording.
    /// </summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// Gets the output file path if set.
    /// </summary>
    public string? OutputFilePath => _outputFilePath;

    /// <summary>
    /// Gets the number of recorded samples.
    /// </summary>
    public int RecordedSampleCount
    {
        get
        {
            lock (_lock)
            {
                return _recordedSamples.Count;
            }
        }
    }

    /// <summary>
    /// Starts recording audio data.
    /// </summary>
    /// <param name="outputFilePath">The file path where the recording will be saved.</param>
    /// <param name="bitDepth">Bit depth for the output file (16, 24, or 32).</param>
    public void StartRecording(string outputFilePath, int bitDepth = 16)
    {
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));

        if (bitDepth != 16 && bitDepth != 24 && bitDepth != 32)
            throw new ArgumentException("Bit depth must be 16, 24, or 32.", nameof(bitDepth));

        lock (_lock)
        {
            _isRecording = true;
            _outputFilePath = outputFilePath;
            _bitDepth = bitDepth;
            _recordedSamples.Clear();
        }
    }

    /// <summary>
    /// Stops recording and returns the number of recorded samples.
    /// </summary>
    /// <returns>The number of samples recorded.</returns>
    public int StopRecording()
    {
        lock (_lock)
        {
            _isRecording = false;

            return _recordedSamples.Count;
        }
    }

    /// <summary>
    /// Writes audio samples to the recording buffer.
    /// This method should be called during playback to capture audio.
    /// </summary>
    /// <param name="samples">The audio samples to record.</param>
    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        if (!_isRecording || samples.Length == 0)
            return;

        lock (_lock)
        {
            if (_isRecording)
            {
                // Add samples to recording buffer
                foreach (var sample in samples)
                {
                    _recordedSamples.Add(sample);
                }
            }
        }
    }

    /// <summary>
    /// Saves the recorded audio to a WAV file asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous save operation.</returns>
    public async Task SaveToFileAsync()
    {
        if (string.IsNullOrEmpty(_outputFilePath))
            throw new InvalidOperationException("Output file path is not set.");

        float[] samples;
        lock (_lock)
        {
            samples = _recordedSamples.ToArray();
        }

        if (samples.Length == 0)
            throw new InvalidOperationException("No audio data to save.");

        await Task.Run(() => SaveWavFile(_outputFilePath, samples, _config.SampleRate, _config.Channels, _bitDepth));
    }

    /// <summary>
    /// Clears all recorded audio data.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _recordedSamples.Clear();
        }
    }

    private void SaveWavFile(string filePath, float[] samples, int sampleRate, int channels, int bitDepth)
    {
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fileStream);

        int bytesPerSample = bitDepth / 8;
        int dataSize = samples.Length * bytesPerSample;

        // Write WAV header
        WriteWavHeader(writer, sampleRate, channels, bitDepth, dataSize);

        // Write audio data
        WriteAudioData(writer, samples, bitDepth);
    }

    private void WriteWavHeader(BinaryWriter writer, int sampleRate, int channels, int bitDepth, int dataSize)
    {
        int bytesPerSample = bitDepth / 8;

        // RIFF header
        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataSize); // File size - 8
        writer.Write(new[] { 'W', 'A', 'V', 'E' });

        // fmt chunk
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16); // Chunk size
        writer.Write((short)1); // Audio format (1 = PCM)
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample); // Byte rate
        writer.Write((short)(channels * bytesPerSample)); // Block align
        writer.Write((short)bitDepth);

        // data chunk
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataSize);
    }

    private void WriteAudioData(BinaryWriter writer, float[] samples, int bitDepth)
    {
        switch (bitDepth)
        {
            case 16:
                foreach (var sample in samples)
                {
                    short value = (short)(Math.Clamp(sample, -1f, 1f) * 32767f);
                    writer.Write(value);
                }
                break;

            case 24:
                foreach (var sample in samples)
                {
                    int value = (int)(Math.Clamp(sample, -1f, 1f) * 8388607f);
                    writer.Write((byte)(value & 0xFF));
                    writer.Write((byte)((value >> 8) & 0xFF));
                    writer.Write((byte)((value >> 16) & 0xFF));
                }
                break;

            case 32:
                foreach (var sample in samples)
                {
                    int value = (int)(Math.Clamp(sample, -1f, 1f) * 2147483647f);
                    writer.Write(value);
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _recordedSamples.Clear();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
