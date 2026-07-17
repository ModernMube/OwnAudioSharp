using Ownaudio.Core;

namespace OwnaudioNET.Recording;

/// <summary>
/// Grabs the audio going through the mixer and dumps it into a WAV.
/// </summary>
public class AudioRecorder : IDisposable
{
    private readonly AudioConfig _config;
    private readonly List<float> _samples = new();
    private readonly object _lock = new();
    private bool _isRecording;
    private string? _outPath;
    private int _bitDepth = 16;
    private bool _disposed;

    /// <summary>
    /// New recorder wired to the given audio config.
    /// </summary>
    /// <param name="config"></param>
    public AudioRecorder(AudioConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// True while we are capturing samples.
    /// </summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// Where the WAV will land, once StartRecording was called.
    /// </summary>
    public string? OutputFilePath => _outPath;

    /// <summary>
    /// How many samples we grabbed so far.
    /// </summary>
    public int RecordedSampleCount
    {
        get { lock (_lock) { return _samples.Count; } }
    }

    /// <summary>
    /// Arm the recorder and clear whatever was buffered before.
    /// </summary>
    /// <param name="outputFilePath"></param>
    /// <param name="bitDepth"></param>
    public void StartRecording(string outputFilePath, int bitDepth = 16)
    {
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));

        if (bitDepth != 16 && bitDepth != 24 && bitDepth != 32)
            throw new ArgumentException("Bit depth must be 16, 24, or 32.", nameof(bitDepth));

        lock (_lock)
        {
            _isRecording = true;
            _outPath = outputFilePath;
            _bitDepth = bitDepth;
            _samples.Clear();
        }
    }

    /// <summary>
    /// Stop capturing, hand back the sample count.
    /// </summary>
    /// <returns></returns>
    public int StopRecording()
    {
        lock (_lock)
        {
            _isRecording = false;
            return _samples.Count;
        }
    }

    /// <summary>
    /// Feed the current playback block in. No-op when we are not recording.
    /// </summary>
    /// <param name="samples"></param>
    public void WriteSamples(ReadOnlySpan<float> samples)
    {
        if(!_isRecording || samples.Length == 0)
            return;

        lock (_lock)
        {
            foreach (var s in samples)
                _samples.Add(s);
        }
    }

    /// <summary>
    /// Flush everything we captured out to the WAV on a worker thread.
    /// </summary>
    /// <returns></returns>
    public async Task SaveToFileAsync()
    {
        if (string.IsNullOrEmpty(_outPath))
            throw new InvalidOperationException("Output file path is not set.");

        float[] data;
        lock (_lock) { data = _samples.ToArray(); }

        if (data.Length == 0)
            throw new InvalidOperationException("No audio data to save.");

        await Task.Run(() => WaveFile.Create(_outPath, data, _config.SampleRate, _config.Channels, _bitDepth));
    }

    /// <summary>
    /// Drop the captured samples.
    /// </summary>
    public void Clear()
    {
        lock (_lock) { _samples.Clear(); }
    }

    /// <summary>
    /// Free the buffer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock) { _samples.Clear(); }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
