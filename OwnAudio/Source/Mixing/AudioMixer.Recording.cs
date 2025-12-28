using OwnaudioNET.Core;

namespace OwnaudioNET.Mixing;

public sealed partial class AudioMixer
{
    /// <summary>
    /// Starts recording the mixed audio output to a WAV file.
    /// </summary>
    /// <param name="filePath">Path to the output WAV file.</param>
    /// <exception cref="ArgumentException">Thrown when filePath is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already recording.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void StartRecording(string filePath)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        lock (_recorderLock)
        {
            if (_isRecording)
                throw new InvalidOperationException("Already recording. Call StopRecording() first.");

            try
            {
                _recorder = new WaveFileWriter(filePath, _config);
                _isRecording = true;
            }
            catch (Exception ex)
            {
                _recorder?.Dispose();
                _recorder = null;
                throw new InvalidOperationException($"Failed to start recording: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Stops recording and closes the WAV file.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if mixer is disposed.</exception>
    public void StopRecording()
    {
        ThrowIfDisposed();

        lock (_recorderLock)
        {
            if (!_isRecording)
                return;

            try
            {
                _recorder?.Dispose();
            }
            catch
            {
                // Ignore errors during cleanup
            }
            finally
            {
                _recorder = null;
                _isRecording = false;
            }
        }
    }
}
