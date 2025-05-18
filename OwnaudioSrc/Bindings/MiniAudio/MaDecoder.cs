using System;
using System.Buffers;
using System.IO;
using Ownaudio.Exceptions;
using static Ownaudio.Bindings.Miniaudio.MaBinding;

namespace Ownaudio.MiniAudio
{
    /// <summary>
    /// Audio decoder using the miniaudio library.
    /// </summary>
    public sealed unsafe class MiniAudioDecoder : IDisposable
    {
        private readonly IntPtr _decoder;
        private readonly Stream _stream;
        private readonly DecoderReadProc _readCallback;
        private readonly DecoderSeekProc _seekCallback;
        private bool _endOfStreamReached;
        private byte[] _readBuffer;
        private short[]? _shortBuffer;
        private int[]? _intBuffer;
        private byte[]? _byteBuffer;
        private readonly object _syncLock = new();
        private int _channels;

        /// <summary>
        /// Format of the audio decoder.
        /// </summary>
        internal MaFormat SampleFormat { get; }

        /// <summary>
        /// Total length of the audio data in samples.
        /// </summary>
        public int Length { get; private set; }

        /// <summary>
        /// Indicates whether the decoder has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Event that occurs when the decoder reaches the end of the stream.
        /// </summary>
        public event EventHandler<EventArgs>? EndOfStreamReached;

        /// <summary>
        /// Creates a new decoder from the specified stream.
        /// </summary>
        /// <param name="stream">The audio stream to decode.</param>
        /// <param name="sampleFormat">The audio format.</param>
        /// <param name="channels">The number of channels.</param>
        /// <param name="sampleRate">The sample rate.</param>
        public MiniAudioDecoder(Stream stream, EngineAudioFormat sampleFormat = EngineAudioFormat.F32, int channels = 2, int sampleRate = 44100)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            SampleFormat = (MaFormat)sampleFormat;
            _readBuffer = ArrayPool<byte>.Shared.Rent(4096);

            var config = sf_allocate_decoder_config(SampleFormat, (uint)channels, (uint)sampleRate);

            _decoder = sf_allocate_decoder();
            var result = ma_decoder_init(
                _readCallback = ReadCallback,
                _seekCallback = SeekCallback,
                IntPtr.Zero,
                config,
                _decoder);

            if (result != MaResult.Success)
                throw new Exception($"Failed to initialize the decoder. Error: {result}");

            ulong length;
            result = ma_decoder_get_length_in_pcm_frames(_decoder, out length);
            if (result != MaResult.Success)
                throw new Exception($"Failed to query the decoder length. Error: {result}");

            Length = (int)length * channels;
            _channels = channels;
            _endOfStreamReached = false;
        }

        /// <summary>
        /// Decodes the next chunk of audio data into the provided buffer.
        /// </summary>
        /// <param name="buffer">The float array to store the decoded audio samples.</param>
        /// <param name="offset">The zero-based index in the buffer at which to begin storing the decoded samples.</param>
        /// <param name="count">The maximum number of frames to read.</param>
        /// <returns>The actual number of frames (not samples) that were decoded and stored in the buffer.</returns>
        public long Decode(float[] buffer, int offset, int count)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(MiniAudioDecoder));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            ulong framesToRead = (ulong)count;
            ulong framesRead;

            fixed (float* pBufferStart = &buffer[0])
            {
                int bytesPerFrame = sizeof(float) * _channels;
                IntPtr nativeBuffer = (IntPtr)(pBufferStart + offset * _channels);

                MaResult result = ma_decoder_read_pcm_frames(
                    _decoder,
                    nativeBuffer,
                    framesToRead,
                    out framesRead
                );

                if (result != MaResult.Success)
                    MiniaudioException.ThrowIfError(result, "Miniaudio Decoder Error");
            }

            return (long)framesRead;
        }

        /// <summary>
        /// Gets a native pointer to the appropriate buffer based on the sample format.
        /// </summary>
        /// <param name="samples">The float samples span that will eventually hold the data.</param>
        /// <returns>A pointer to the appropriate native buffer.</returns>
        private IntPtr GetNativeBufferPointer(Span<float> samples)
        {
            switch (SampleFormat)
            {
                case MaFormat.S16:
                    _shortBuffer = ArrayPool<short>.Shared.Rent(samples.Length);
                    fixed (short* pSamples = _shortBuffer)
                        return (IntPtr)pSamples;
                case MaFormat.S24:
                    _byteBuffer = ArrayPool<byte>.Shared.Rent(samples.Length * 3);
                    fixed (byte* pSamples = _byteBuffer)
                        return (IntPtr)pSamples;
                case MaFormat.S32:
                    _intBuffer = ArrayPool<int>.Shared.Rent(samples.Length);
                    fixed (int* pSamples = _intBuffer)
                        return (IntPtr)pSamples;
                case MaFormat.U8:
                    _byteBuffer = ArrayPool<byte>.Shared.Rent(samples.Length);
                    fixed (byte* pSamples = _byteBuffer)
                        return (IntPtr)pSamples;
                case MaFormat.F32:
                    fixed (float* pSamples = samples)
                        return (IntPtr)pSamples;
                default:
                    throw new NotSupportedException($"Sample format {SampleFormat} is not supported.");
            }
        }

        /// <summary>
        /// Converts samples from the native format to float if necessary.
        /// </summary>
        /// <param name="samples">The target float samples span.</param>
        /// <param name="framesRead">Number of frames read.</param>
        /// <param name="nativeBuffer">Pointer to the native buffer containing the samples.</param>
        /// <param name="channels">Number of audio channels.</param>
        private void ConvertToFloatIfNecessary(Span<float> samples, uint framesRead, IntPtr nativeBuffer, int channels)
        {
            var sampleCount = (int)framesRead * channels;
            switch (SampleFormat)
            {
                case MaFormat.S16:
                    var shortSpan = new Span<short>(nativeBuffer.ToPointer(), sampleCount);
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = shortSpan[i] / (float)short.MaxValue;
                    if (_shortBuffer != null)
                        ArrayPool<short>.Shared.Return(_shortBuffer);
                    _shortBuffer = null;
                    break;
                case MaFormat.S24:
                    var s24Bytes = new Span<byte>(nativeBuffer.ToPointer(), sampleCount * 3); // 3 bytes per sample
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var sample24 = (s24Bytes[i * 3] << 0) | (s24Bytes[i * 3 + 1] << 8) | (s24Bytes[i * 3 + 2] << 16);
                        if ((sample24 & 0x800000) != 0) // Sign extension for negative values
                            sample24 |= unchecked((int)0xFF000000);
                        samples[i] = sample24 / 8388608f; // 2^23
                    }
                    if (_byteBuffer != null)
                        ArrayPool<byte>.Shared.Return(_byteBuffer);
                    _byteBuffer = null;
                    break;
                case MaFormat.S32:
                    var int32Span = new Span<int>(nativeBuffer.ToPointer(), sampleCount);
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = int32Span[i] / (float)int.MaxValue;
                    if (_intBuffer != null)
                        ArrayPool<int>.Shared.Return(_intBuffer);
                    _intBuffer = null;
                    break;
                case MaFormat.U8:
                    var byteSpan = new Span<byte>(nativeBuffer.ToPointer(), sampleCount);
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = (byteSpan[i] - 128) / 128f; // Scale U8 to range -1.0 to 1.0
                    if (_byteBuffer != null)
                        ArrayPool<byte>.Shared.Return(_byteBuffer);
                    _byteBuffer = null;
                    break;
            }
        }

        /// <summary>
        /// Seeks to the specified position in the decoder.
        /// </summary>
        /// <param name="offset">The position in samples.</param>
        /// <param name="channels">The number of channels.</param>
        /// <returns>True if the positioning was successful.</returns>
        public bool Seek(int offset, int channels)
        {
            lock (_syncLock)
            {
                if (Length == 0)
                {
                    ulong length = 0;
                    var decoderresult = ma_decoder_get_length_in_pcm_frames(_decoder, out length);
                    if (decoderresult != MaResult.Success || (int)length == 0)
                        return false;
                    Length = (int)length * channels;
                }

                _endOfStreamReached = false;
                var result = ma_decoder_seek_to_pcm_frame(_decoder, (ulong)(offset / channels));
                return result == MaResult.Success;
            }
        }

        /// <summary>
        /// Callback invoked by the miniaudio library to read data from the stream.
        /// </summary>
        /// <param name="pDecoder">Pointer to the decoder.</param>
        /// <param name="pBufferOut">Pointer to the output buffer.</param>
        /// <param name="bytesToRead">Number of bytes to read.</param>
        /// <param name="pBytesRead">Number of bytes actually read.</param>
        /// <returns>Result code of the read operation.</returns>
        private MaResult ReadCallback(IntPtr pDecoder, IntPtr pBufferOut, ulong bytesToRead, out ulong pBytesRead)
        {
            lock (_syncLock)
            {
                if (!_stream.CanRead || _endOfStreamReached)
                {
                    pBytesRead = 0;
                    return MaResult.NoDataAvailable;
                }

                var size = (int)bytesToRead;
                if (_readBuffer.Length < size)
                    Array.Resize(ref _readBuffer, size);

                var read = _stream.Read(_readBuffer, 0, size);
                
                if (read == 0 && !_endOfStreamReached)
                {
                    _endOfStreamReached = true;
                    EndOfStreamReached?.Invoke(this, EventArgs.Empty);
                }

                fixed (byte* pReadBuffer = _readBuffer)
                {
                    Buffer.MemoryCopy(pReadBuffer, (void*)pBufferOut, size, read);
                }

                Array.Clear(_readBuffer, 0, read);

                pBytesRead = (ulong)read;
                return MaResult.Success;
            }
        }

        /// <summary>
        /// Callback invoked by the miniaudio library to seek within the stream.
        /// </summary>
        /// <param name="pDecoder">Pointer to the decoder.</param>
        /// <param name="byteOffset">Byte offset to seek to.</param>
        /// <param name="origin">Origin of the seek operation.</param>
        /// <returns>Result code of the seek operation.</returns>
        private MaResult SeekCallback(IntPtr pDecoder, long byteOffset, SeekPoint origin)
        {
            lock (_syncLock)
            {
                if (!_stream.CanSeek)
                    return MaResult.NoDataAvailable;

                if (byteOffset >= 0 && byteOffset < _stream.Length - 1)
                    _stream.Seek(byteOffset, origin == SeekPoint.FromCurrent ? SeekOrigin.Current : SeekOrigin.Begin);

                return MaResult.Success;
            }
        }

        /// <summary>
        /// Releases all resources used by the decoder.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer for the decoder class.
        /// </summary>
        ~MiniAudioDecoder()
        {
            Dispose(false);
        }

        /// <summary>
        /// Releases resources used by the decoder.
        /// </summary>
        /// <param name="disposeManaged">Whether to dispose managed resources.</param>
        private void Dispose(bool disposeManaged)
        {
            lock (_syncLock)
            {
                if (IsDisposed)
                    return;

                if (disposeManaged)
                {
                    if (_shortBuffer != null)
                    {
                        ArrayPool<short>.Shared.Return(_shortBuffer);
                        _shortBuffer = null;
                    }

                    if (_intBuffer != null)
                    {
                        ArrayPool<int>.Shared.Return(_intBuffer);
                        _intBuffer = null;
                    }

                    if (_byteBuffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(_byteBuffer);
                        _byteBuffer = null;
                    }
                }

                GC.KeepAlive(_readCallback);
                GC.KeepAlive(_seekCallback);

                ma_decoder_uninit(_decoder);
                ma_free(_decoder);

                IsDisposed = true;
            }
        }
    }
}
