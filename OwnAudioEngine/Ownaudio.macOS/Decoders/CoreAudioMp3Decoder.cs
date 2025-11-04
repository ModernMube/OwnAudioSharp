using System;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Core.Common;
using Ownaudio.Decoders;
using Ownaudio.Decoders.Mp3;
using Ownaudio.macOS.Interop;

namespace Ownaudio.macOS.Decoders;

/// <summary>
/// macOS Core Audio (ExtAudioFile) MP3 decoder implementation.
/// Uses native macOS AudioToolbox framework for hardware-accelerated MP3 decoding with zero external dependencies.
/// </summary>
/// <remarks>
/// This decoder:
/// - Uses macOS AudioToolbox ExtAudioFile API (available on macOS 10.5+)
/// - Outputs Float32 PCM samples, interleaved
/// - Pre-allocates all buffers for zero-allocation decode path
/// - Thread-safe for construction, but not for concurrent decode calls
/// - Supports seeking by sample position
/// - Automatically handles format conversion (MP3 → Float32 PCM)
///
/// PTS (Presentation Timestamp) Handling - OPTIMIZED:
/// - Uses sample-accurate PTS calculation based on DECODED DATA SIZE
/// - Frame duration = (samplesPerChannel * 1000.0) / sampleRate
/// - PTS incremented by frame duration (_currentPts += duration)
/// - Consistent with WAV/FLAC/Windows MP3 decoders for multi-file sync
/// - Seek sets PTS to seek position (not 0) for correct multi-file playback
///
/// GC Optimization:
/// - Pre-allocated decode buffers (4096 samples default)
/// - Pinned memory for P/Invoke calls (GCHandle)
/// - Span&lt;T&gt; usage for zero-copy operations
/// - Stack-allocated structures where possible
/// - Immediate release of CoreFoundation objects (CFRelease)
/// </remarks>
public sealed class CoreAudioMp3Decoder : IPlatformMp3Decoder
{
    private const int DefaultSamplesPerFrame = 4096;

    // ExtAudioFile handle
    private AudioToolboxInterop.ExtAudioFileRef _audioFile;
    private IntPtr _cfUrlRef = IntPtr.Zero;

    // Stream state
    private AudioStreamInfo _streamInfo;
    private double _currentPts; // in milliseconds

    // Source format (original MP3 format)
    private int _sourceChannels;
    private int _sourceSampleRate;

    // Client format (output format - Float32 PCM)
    private int _clientChannels;
    private int _clientSampleRate;

    // Pre-allocated buffers for zero-allocation decode
    private readonly byte[] _decodeBuffer;
    private readonly GCHandle _bufferHandle;
    private readonly IntPtr _bufferPtr;

    // Total frames in file (for duration and EOF detection)
    private long _totalFrames;
    private long _currentFrame;

    private bool _disposed;
    private bool _isEOF;

    /// <summary>
    /// Default constructor (required for reflection-based creation).
    /// </summary>
    public CoreAudioMp3Decoder()
    {
        _audioFile = AudioToolboxInterop.ExtAudioFileRef.Invalid;

        // Pre-allocate decode buffer (Float32 = 4 bytes per sample)
        int bufferSize = DefaultSamplesPerFrame * 2 * sizeof(float); // Stereo max
        _decodeBuffer = new byte[bufferSize];
        _bufferHandle = GCHandle.Alloc(_decodeBuffer, GCHandleType.Pinned);
        _bufferPtr = _bufferHandle.AddrOfPinnedObject();
    }

    /// <inheritdoc/>
    public void InitializeFromFile(string filePath, int targetSampleRate, int targetChannels)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"MP3 file not found: {filePath}", filePath);

        try
        {
            // 1. Create CFURL from file path
            _cfUrlRef = AudioToolboxInterop.CreateCFURLFromPath(filePath);
            if (_cfUrlRef == IntPtr.Zero)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    $"Failed to create CFURL for file: {filePath}");

            // 2. Open ExtAudioFile
            int status = AudioToolboxInterop.ExtAudioFileOpenURL(_cfUrlRef, out _audioFile);
            //AudioToolboxInterop.ThrowIfError(status, "ExtAudioFileOpenURL");
            CoreAudioInterop.ThrowIfError(status, "ExtAudioFileOpenURL");

            if (!_audioFile.IsValid)
                throw new AudioException(
                    AudioErrorCategory.PlatformAPI,
                    "Failed to open ExtAudioFile (invalid handle)");

            // 3. Get file data format (original MP3 format)
            var fileFormat = GetFileDataFormat();

            _sourceChannels = (int)fileFormat.ChannelsPerFrame;
            _sourceSampleRate = (int)fileFormat.SampleRate;

            // 4. Get total frames in file
            _totalFrames = GetFileLengthFrames();

            // 5. Set client data format (output format: Float32 PCM)
            _clientChannels = targetChannels > 0 ? targetChannels : _sourceChannels;
            _clientSampleRate = targetSampleRate > 0 ? targetSampleRate : _sourceSampleRate;

            var clientFormat = AudioToolboxInterop.AudioStreamBasicDescription.CreateFloat32(
                _clientSampleRate,
                _clientChannels);

            SetClientDataFormat(clientFormat);

            // 6. Calculate duration
            double durationSeconds = (double)_totalFrames / _sourceSampleRate;
            TimeSpan duration = TimeSpan.FromSeconds(durationSeconds);

            // 7. Create AudioStreamInfo
            // IMPORTANT: Use CLIENT format (output format after conversion)
            // ExtAudioFile performs resampling/remixing, so we return the CLIENT format
            _streamInfo = new AudioStreamInfo(
                channels: _clientChannels,
                sampleRate: _clientSampleRate,
                duration: duration);

            _currentPts = 0.0;
            _currentFrame = 0;
            _isEOF = false;

            System.Diagnostics.Debug.WriteLine(
                $"[CoreAudio MP3] Opened: {filePath}, " +
                $"Source: {_sourceChannels}ch/{_sourceSampleRate}Hz, " +
                $"Client: {_clientChannels}ch/{_clientSampleRate}Hz, " +
                $"Frames: {_totalFrames}, Duration: {duration.TotalSeconds:F2}s");
        }
        catch (Exception ex) when (!(ex is AudioException))
        {
            // Cleanup on failure
            Dispose();
            throw new AudioException(
                AudioErrorCategory.PlatformAPI,
                $"Failed to initialize MP3 decoder: {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc/>
    public void InitializeFromStream(Stream stream, int targetSampleRate, int targetChannels)
    {
        throw new NotImplementedException(
            "Stream-based MP3 decoding not supported on macOS ExtAudioFile API. " +
            "ExtAudioFile requires file path or CFURLRef. " +
            "Consider writing stream to temporary file first.");
    }

    /// <inheritdoc/>
    public AudioStreamInfo GetStreamInfo()
    {
        return _streamInfo;
    }

    /// <inheritdoc/>
    public int DecodeFrame(Span<byte> outputBuffer, out double pts)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CoreAudioMp3Decoder));

        if (_isEOF)
        {
            pts = _currentPts;
            return 0; // EOF
        }

        if (!_audioFile.IsValid)
        {
            pts = 0.0;
            return -1; // Error
        }

        try
        {
            // Calculate how many frames to read (limited by buffer size)
            uint maxFramesToRead = (uint)(outputBuffer.Length / (_clientChannels * sizeof(float)));
            uint framesToRead = Math.Min(maxFramesToRead, DefaultSamplesPerFrame);

            // Setup AudioBufferList for interleaved Float32 data
            var bufferList = AudioToolboxInterop.AudioBufferList.CreateInterleaved(
                (uint)_clientChannels,
                _bufferPtr,
                framesToRead * (uint)_clientChannels * sizeof(float));

            // Read audio frames
            uint framesRead = framesToRead;
            int status = AudioToolboxInterop.ExtAudioFileRead(
                _audioFile,
                ref framesRead,
                ref bufferList);

            // Check status
            if (AudioToolboxInterop.OSStatus.IsError(status))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CoreAudio MP3] ExtAudioFileRead failed: OSStatus {status} " +
                    $"({AudioToolboxInterop.OSStatus.ToFourCC(status)})");
                pts = 0.0;
                return -1; // Error
            }

            // Check for EOF
            if (framesRead == 0)
            {
                System.Diagnostics.Debug.WriteLine("[CoreAudio MP3] End of file");
                _isEOF = true;
                pts = _currentPts;
                return 0; // EOF
            }

            // Calculate bytes decoded
            int bytesDecoded = (int)(framesRead * _clientChannels * sizeof(float));

            // CRITICAL: Calculate frame duration using CLIENT sample rate
            // framesRead is the number of frames at CLIENT sample rate after resampling
            // This ensures accurate PTS when ExtAudioFile performs resampling
            double frameDurationMs = (framesRead * 1000.0) / _clientSampleRate;

            // SIMPLE PTS CALCULATION (same as Windows/WAV decoders):
            // Current frame PTS, then increment for next frame
            double framePts = _currentPts;
            _currentPts += frameDurationMs;
            _currentFrame += framesRead;

            // Copy decoded data to output buffer
            if (bytesDecoded > outputBuffer.Length)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CoreAudio MP3] WARNING: Buffer overflow prevented. " +
                    $"Decoded {bytesDecoded} bytes, buffer size {outputBuffer.Length} bytes");
                bytesDecoded = outputBuffer.Length;
            }

            _decodeBuffer.AsSpan(0, bytesDecoded).CopyTo(outputBuffer);

            pts = framePts;

            //System.Diagnostics.Debug.WriteLine(
            //    $"[CoreAudio MP3] Decoded {framesRead} frames, {bytesDecoded} bytes, " +
            //    $"PTS: {framePts:F2}ms, duration: {frameDurationMs:F2}ms");

            return bytesDecoded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CoreAudio MP3] Decode exception: {ex.Message}");
            pts = 0.0;
            return -1; // Error
        }
    }

    /// <inheritdoc/>
    public bool Seek(long samplePosition)
    {
        if (_disposed)
            return false;

        if (!_audioFile.IsValid)
            return false;

        try
        {
            // IMPORTANT: samplePosition is in CLIENT sample rate (output after resampling)
            // But ExtAudioFileSeek expects SOURCE file frame position
            // Convert CLIENT position → SOURCE position if resampling is active
            long sourceFramePosition = samplePosition;
            if (_clientSampleRate != _sourceSampleRate)
            {
                // Convert: clientFrame * (sourceRate / clientRate)
                sourceFramePosition = (long)(samplePosition * ((double)_sourceSampleRate / _clientSampleRate));
            }

            // Clamp to valid range (in SOURCE frames)
            if (sourceFramePosition < 0)
                sourceFramePosition = 0;

            if (sourceFramePosition >= _totalFrames)
                sourceFramePosition = _totalFrames - 1;

            // Seek to SOURCE frame position
            int status = AudioToolboxInterop.ExtAudioFileSeek(_audioFile, sourceFramePosition);

            if (AudioToolboxInterop.OSStatus.IsError(status))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CoreAudio MP3] Seek failed: OSStatus {status} " +
                    $"({AudioToolboxInterop.OSStatus.ToFourCC(status)})");
                return false;
            }

            // Update state (in CLIENT sample rate)
            _currentFrame = samplePosition;
            _currentPts = (samplePosition * 1000.0) / _clientSampleRate;
            _isEOF = false;

            System.Diagnostics.Debug.WriteLine(
                $"[CoreAudio MP3] Seeked to CLIENT frame {samplePosition} (SOURCE frame {sourceFramePosition}), PTS: {_currentPts:F2}ms");

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CoreAudio MP3] Seek exception: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public double CurrentPts => _currentPts;

    /// <inheritdoc/>
    public bool IsEOF => _isEOF;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Dispose ExtAudioFile
        if (_audioFile.IsValid)
        {
            try
            {
                int status = AudioToolboxInterop.ExtAudioFileDispose(_audioFile);
                if (AudioToolboxInterop.OSStatus.IsError(status))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CoreAudio MP3] ExtAudioFileDispose warning: OSStatus {status}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CoreAudio MP3] ExtAudioFileDispose exception: {ex.Message}");
            }

            _audioFile = AudioToolboxInterop.ExtAudioFileRef.Invalid;
        }

        // Release CFURL
        if (_cfUrlRef != IntPtr.Zero)
        {
            try
            {
                AudioToolboxInterop.CFRelease(_cfUrlRef);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CoreAudio MP3] CFRelease warning: {ex.Message}");
            }

            _cfUrlRef = IntPtr.Zero;
        }

        // Free pinned buffer
        if (_bufferHandle.IsAllocated)
        {
            _bufferHandle.Free();
        }

        System.Diagnostics.Debug.WriteLine("[CoreAudio MP3] Decoder disposed");
    }

    #region Private Helper Methods

    /// <summary>
    /// Gets the file data format (original MP3 format).
    /// </summary>
    private AudioToolboxInterop.AudioStreamBasicDescription GetFileDataFormat()
    {
        uint size = (uint)Marshal.SizeOf<AudioToolboxInterop.AudioStreamBasicDescription>();
        IntPtr formatPtr = Marshal.AllocHGlobal((int)size);

        try
        {
            int status = AudioToolboxInterop.ExtAudioFileGetProperty(
                _audioFile,
                AudioToolboxInterop.ExtAudioFilePropertyID.FileDataFormat,
                ref size,
                formatPtr);

            //AudioToolboxInterop.ThrowIfError(status, "ExtAudioFileGetProperty(FileDataFormat)");
            CoreAudioInterop.ThrowIfError(status, "ExtAudioFileGetProperty(FileDataFormat)");

            return Marshal.PtrToStructure<AudioToolboxInterop.AudioStreamBasicDescription>(formatPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }
    }

    /// <summary>
    /// Sets the client data format (output format).
    /// </summary>
    private void SetClientDataFormat(AudioToolboxInterop.AudioStreamBasicDescription clientFormat)
    {
        uint size = (uint)Marshal.SizeOf<AudioToolboxInterop.AudioStreamBasicDescription>();
        IntPtr formatPtr = Marshal.AllocHGlobal((int)size);

        try
        {
            Marshal.StructureToPtr(clientFormat, formatPtr, false);

            int status = AudioToolboxInterop.ExtAudioFileSetProperty(
                _audioFile,
                AudioToolboxInterop.ExtAudioFilePropertyID.ClientDataFormat,
                size,
                formatPtr);

            //AudioToolboxInterop.ThrowIfError(status, "ExtAudioFileSetProperty(ClientDataFormat)");
            CoreAudioInterop.ThrowIfError(status, "ExtAudioFileSetProperty(ClientDataFormat)");
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }
    }

    /// <summary>
    /// Gets the total number of frames in the file.
    /// </summary>
    private long GetFileLengthFrames()
    {
        uint size = sizeof(long);
        IntPtr lengthPtr = Marshal.AllocHGlobal((int)size);

        try
        {
            int status = AudioToolboxInterop.ExtAudioFileGetProperty(
                _audioFile,
                AudioToolboxInterop.ExtAudioFilePropertyID.FileLengthFrames,
                ref size,
                lengthPtr);

            //AudioToolboxInterop.ThrowIfError(status, "ExtAudioFileGetProperty(FileLengthFrames)");
            CoreAudioInterop.ThrowIfError(status, "ExtAudioFileGetProperty(FileLengthFrames)");

            return Marshal.ReadInt64(lengthPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(lengthPtr);
        }
    }

    #endregion
}
