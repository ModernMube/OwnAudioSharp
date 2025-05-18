using System;
using System.Buffers;
using System.IO;
using System.Diagnostics;
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

            IntPtr config_ptr_to_free = IntPtr.Zero;
            try
            {
                config_ptr_to_free = sf_allocate_decoder_config(SampleFormat, (uint)channels, (uint)sampleRate);
                
                if (config_ptr_to_free == IntPtr.Zero)
                    throw new OwnaudioException("Failed to allocate decoder config."); 

                _decoder = sf_allocate_decoder();
                if (_decoder == IntPtr.Zero)
                {
                    if (config_ptr_to_free != IntPtr.Zero)
                    {
                        ma_free(config_ptr_to_free, IntPtr.Zero, "Decoder config dispose due to decoder allocation failure");
                        config_ptr_to_free = IntPtr.Zero;
                    }
                    throw new OwnaudioException("Failed to allocate decoder.");
                }

                var result = ma_decoder_init(
                    _readCallback = ReadCallback,
                    _seekCallback = SeekCallback,
                    IntPtr.Zero, 
                    config_ptr_to_free,
                    _decoder);

                if (result != MaResult.Success)
                {
                    if (_decoder != IntPtr.Zero)
                    {
                        ma_free(_decoder, IntPtr.Zero, $"Decoder dispose due to init failure ({result})");
                    }
                    if (config_ptr_to_free != IntPtr.Zero)
                    {
                        ma_free(config_ptr_to_free, IntPtr.Zero, $"Decoder config dispose due to init failure ({result})");
                    }
                    throw new OwnaudioException($"Failed to initialize the decoder. Error: {result}");
                }

                if (config_ptr_to_free != IntPtr.Zero)
                {
                    ma_free(config_ptr_to_free, IntPtr.Zero, "Decoder config dispose after successful init");
                    config_ptr_to_free = IntPtr.Zero;
                }

                ulong length;
                result = ma_decoder_get_length_in_pcm_frames(_decoder, out length);
                if (result != MaResult.Success)
                {
                    ma_decoder_uninit(_decoder);
                    ma_free(_decoder,  IntPtr.Zero, $"Decoder dispose due to get_length failure ({result})");
                    throw new OwnaudioException($"Failed to query the decoder length. Error: {result}");
                }

                Length = (int)length * channels;
                _channels = channels;
                _endOfStreamReached = false;
            }
            catch (Exception)
            {
                if (config_ptr_to_free != IntPtr.Zero)
                {
                    ma_free(config_ptr_to_free, IntPtr.Zero, "Decoder config dispose due to an exception during constructor");
                }
                throw;
            }
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

            if (offset < 0 || count < 0 || (offset + (long)count * _channels) > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count), "Buffer is too small for the requested offset and count of frames.");

            ulong framesToRead = (ulong)count;
            ulong framesRead;

            fixed (float* pBufferStart = &buffer[0])
            {
                IntPtr nativeBuffer = (IntPtr)(pBufferStart + offset);

                MaResult result = ma_decoder_read_pcm_frames(
                    _decoder,
                    nativeBuffer,
                    framesToRead,
                    out framesRead
                );

                if (result != MaResult.Success && result != MaResult.AtEnd)
                {
                    MiniaudioException.ThrowIfError(result, "Miniaudio Decoder Error");
                }
            }

            return (long)framesRead;
        }

        /// <summary>
        /// Converts samples from the native format to float if necessary.
        /// FIGYELEM: Ez a metódus a GetNativeBufferPointer által potenciálisan
        /// érvénytelenített pointerrel dolgozhatott. Ha a GetNativeBufferPointer
        /// nincs használatban, ez a metódus sem releváns.
        /// </summary>
        private void ConvertToFloatIfNecessary(Span<float> samples, uint framesRead, IntPtr nativeBuffer, int channels)
        {
            if (nativeBuffer == IntPtr.Zero && SampleFormat != MaFormat.F32)
            {
                if (SampleFormat != MaFormat.F32)
                    Debug.WriteLine("ConvertToFloatIfNecessary: nativeBuffer is IntPtr.Zero, skipping conversion for non-F32 format.");
                return;
            }

            var sampleCount = (int)framesRead * channels;

            if (SampleFormat == MaFormat.F32) return;

            Debug.WriteLineIf(nativeBuffer == IntPtr.Zero, "ConvertToFloatIfNecessary called with IntPtr.Zero for non-F32 format, which is unexpected.");

            switch (SampleFormat)
            {
                case MaFormat.S16:
                    if (_shortBuffer == null || nativeBuffer == IntPtr.Zero) break;
                    var shortSpan = new Span<short>(nativeBuffer.ToPointer(), sampleCount);
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = shortSpan[i] / 32767.0f;
                    break;
                case MaFormat.S24:
                    if (_byteBuffer == null || nativeBuffer == IntPtr.Zero) break;
                    var s24Bytes = new Span<byte>(nativeBuffer.ToPointer(), sampleCount * 3);
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var sample24 = (s24Bytes[i * 3] << 0) | (s24Bytes[i * 3 + 1] << 8) | (s24Bytes[i * 3 + 2] << 16);
                        if ((sample24 & 0x800000) != 0)
                            sample24 |= unchecked((int)0xFF000000);
                        samples[i] = sample24 / 8388608.0f;
                    }
                    break;
                case MaFormat.S32:
                    if (_intBuffer == null || nativeBuffer == IntPtr.Zero) break;
                    var int32Span = new Span<int>(nativeBuffer.ToPointer(), sampleCount);
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = int32Span[i] / 2147483647.0f;
                    break;
                case MaFormat.U8:
                    if (_byteBuffer == null || nativeBuffer == IntPtr.Zero) break;
                    var byteSpan = new Span<byte>(nativeBuffer.ToPointer(), sampleCount);
                    for (var i = 0; i < sampleCount; i++)
                        samples[i] = (byteSpan[i] - 128) / 128.0f;
                    break;
            }

            if (_shortBuffer != null && SampleFormat == MaFormat.S16) { ArrayPool<short>.Shared.Return(_shortBuffer); _shortBuffer = null; }
            if (_intBuffer != null && SampleFormat == MaFormat.S32) { ArrayPool<int>.Shared.Return(_intBuffer); _intBuffer = null; }
            if (_byteBuffer != null && (SampleFormat == MaFormat.S24 || SampleFormat == MaFormat.U8)) { ArrayPool<byte>.Shared.Return(_byteBuffer); _byteBuffer = null; }
        }

        /// <summary>
        /// Seeks to the specified position in the decoder.
        /// </summary>
        /// <param name="offsetInSamples">The position in total samples (frames * channels).</param>
        /// <param name="channelsForFrameCalc">The number of channels to calculate PCM frames for seeking.</param>
        /// <returns>True if the positioning was successful.</returns>
        public bool Seek(int offsetInSamples, int channelsForFrameCalc)
        {
            lock (_syncLock)
            {
                if (IsDisposed) return false;
                if (channelsForFrameCalc <= 0) return false;

                ulong pcmFrameIndex = (ulong)(offsetInSamples / channelsForFrameCalc);

                _endOfStreamReached = false;
                var result = ma_decoder_seek_to_pcm_frame(_decoder, pcmFrameIndex);
                return result == MaResult.Success;
            }
        }

        /// <summary>
        /// Callback invoked by the miniaudio library to read data from the stream.
        /// </summary>
        private MaResult ReadCallback(IntPtr pDecoder, IntPtr pBufferOut, ulong bytesToRead, out ulong pBytesRead)
        {
            lock (_syncLock)
            {
                if (IsDisposed)
                {
                    pBytesRead = 0;
                    return MaResult.Error;
                }

                if (!_stream.CanRead || _endOfStreamReached)
                {
                    pBytesRead = 0;
                    return _endOfStreamReached ? MaResult.AtEnd : MaResult.NoDataAvailable;
                }

                var size = (int)bytesToRead;
                if (_readBuffer == null)
                {
                    pBytesRead = 0;
                    return MaResult.Error;
                }

                if (_readBuffer.Length < size)
                {
                    try
                    {
                        ArrayPool<byte>.Shared.Return(_readBuffer, clearArray: false);
                    }
                    catch (ArgumentException ex)
                    {
                        Debug.WriteLine($"ArrayPool.Return failed in ReadCallback (buffer may have been returned concurrently or is not from pool): {ex.Message}");
                    }
                    _readBuffer = ArrayPool<byte>.Shared.Rent(size);
                }

                var read = 0;
                try
                {
                    read = _stream.Read(_readBuffer, 0, size);
                }
                catch (ObjectDisposedException)
                {
                    _endOfStreamReached = true;
                    pBytesRead = 0;
                    return MaResult.Error;
                }

                if (read == 0 && !_endOfStreamReached)
                {
                    _endOfStreamReached = true;
                    EndOfStreamReached?.Invoke(this, EventArgs.Empty);
                }

                if (read > 0)
                {
                    fixed (byte* pReadBuffer = _readBuffer)
                    {
                        Buffer.MemoryCopy(pReadBuffer, (void*)pBufferOut, bytesToRead, (ulong)read);
                    }
                    Array.Clear(_readBuffer, 0, read);
                }

                pBytesRead = (ulong)read;
                return MaResult.Success;
            }
        }

        /// <summary>
        /// Callback invoked by the miniaudio library to seek within the stream.
        /// </summary>
        private MaResult SeekCallback(IntPtr pDecoder, long byteOffset, SeekPoint origin)
        {
            lock (_syncLock) //
            {
                if (IsDisposed)
                {
                    return MaResult.Error;
                }

                if (!_stream.CanSeek)
                    return MaResult.FormatNotSupported;

                try
                {
                    SeekOrigin seekOrigin;
                    switch (origin)
                    {
                        case SeekPoint.FromCurrent:
                            seekOrigin = SeekOrigin.Current;
                            break;
                        case SeekPoint.FromStart:
                        default:
                            seekOrigin = SeekOrigin.Begin;
                            break;
                    }

                    _stream.Seek(byteOffset, seekOrigin);
                    _endOfStreamReached = false;
                }
                catch (IOException)
                {
                    return MaResult.IoError;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return MaResult.InvalidArgs;
                }
                catch (ObjectDisposedException)
                {
                    return MaResult.Error;
                }

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

                GC.KeepAlive(_readCallback);
                GC.KeepAlive(_seekCallback);

                if (_decoder != IntPtr.Zero)
                {
                    Debug.WriteLine($"MiniAudioDecoder: Disposing native decoder {_decoder}");
                    ma_decoder_uninit(_decoder);
                    Debug.WriteLine($"MiniAudioDecoder: Uninit completed for {_decoder}. Now freeing memory.");
                    ma_free(_decoder, IntPtr.Zero, "Decoder dispose...");
                    Debug.WriteLine($"MiniAudioDecoder: Memory freed for former {_decoder}.");
                }


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

                    if (_readBuffer != null)
                    {
                        try
                        {
                            ArrayPool<byte>.Shared.Return(_readBuffer, clearArray: false);
                        }
                        catch (ArgumentException ex)
                        {
                            Debug.WriteLine($"ArrayPool.Return failed for _readBuffer in Dispose (buffer may have been returned concurrently or is not from pool): {ex.Message}");
                        }
                        _readBuffer = null!;
                    }
                    EndOfStreamReached = null;
                }
                IsDisposed = true;
            }
        }
    }
}
