using Ownaudio.Decoders;
using System.Buffers;

namespace OwnaudioNET.Visualization
{
    public partial class WaveAvaloniaDisplay : Avalonia.Controls.Control
    {
        const int _maxsample = 10000;

        // load waveform from file; maxSamples caps kept samples (rest gets downsampled),
        // preferFFmpeg is legacy and ignored
        public bool LoadFromAudioFile(string filePath, int maxSamples = 100000, bool preferFFmpeg = false,
            int channels = 1, int sampleRate = 44100)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
            if (!File.Exists(filePath)) return false;

            ResetView();
            try {
                using var decoder = AudioDecoderFactory.Create(filePath, sampleRate, channels);
                return ProcessDecoderData(decoder, maxSamples);
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"decoder failed: {ex.Message}");
                return false;
            }
        }

        // same but off the UI thread
        public Task<bool> LoadFromAudioFileAsync(string filePath, int maxSamples = 100000, bool preferFFmpeg = false,
            int channels = 1, int sampleRate = 44100, CancellationToken cancellationToken = default)
            => Task.Run(() => LoadFromAudioFile(filePath, maxSamples, preferFFmpeg, channels, sampleRate), cancellationToken);

        // load waveform from stream, audioFormat tells the decoder what's inside (wav/mp3/flac)
        public bool LoadFromAudioStream(Stream stream, AudioFormat audioFormat, bool preferFFmpeg = false,
            int channels = 1, int sampleRate = 44100)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            ResetView();
            try {
                using var decoder = AudioDecoderFactory.Create(stream, audioFormat, sampleRate, channels);
                return ProcessDecoderData(decoder, _maxsample);
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"stream decoder failed: {ex.Message}");
                return false;
            }
        }

        // wipe old data + zoom/scroll state
        void ResetView()
        {
            _audioData = null;
            ZoomFactor = 1.0;
            ScrollOffset = 0.0;
            InvalidateVisual();
        }

        // decode whole stream, downsample to maxSamples, channels averaged to mono
        bool ProcessDecoderData(IAudioDecoder decoder, int maxSamples)
        {
            var info = decoder.StreamInfo;
            if (info.Channels == 0 || info.SampleRate == 0) return false;

            long estimated = (long)(info.Duration.TotalSeconds * info.SampleRate * info.Channels);
            int step = Math.Max(1, (int)(estimated / maxSamples));
            int ch = info.Channels, counter = 0;

            var samples = new List<float>(maxSamples);
            var byteBuffer = ArrayPool<byte>.Shared.Rent(4096 * ch * sizeof(float));
            try {
                while (samples.Count < maxSamples)
                {
                    var result = decoder.ReadFrames(byteBuffer);
                    if (result.IsEOF || !result.IsSucceeded) break;
                    if (result.FramesRead == 0) continue;

                    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                        byteBuffer.AsSpan(0, result.FramesRead * ch * sizeof(float)));

                    for (int i = 0; i + ch <= floats.Length && samples.Count < maxSamples; i += ch)
                    {
                        if (counter++ % step == 0)
                        {
                            float s = 0f;
                            for (int c = 0; c < ch; c++) s += floats[i + c];
                            samples.Add(s / ch);
                        }
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(byteBuffer); }

            if (samples.Count == 0) return false;
            _audioData = samples.ToArray();
            InvalidateVisual();
            return true;
        }
    }
}
