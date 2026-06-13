using System;
using System.Runtime.InteropServices;
using Logger;
using Ownaudio.Core;
using Ownaudio.Core.Common;

namespace Ownaudio.Decoders.FFmpeg;

/// <summary>
/// IAudioDecoder implementation backed by FFmpeg 7/8 dynamic libraries.
/// Uses a GC-free decode loop and a NativeMemory-based resample buffer
/// to deliver interleaved float32 audio with zero managed-heap allocations
/// after construction.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe. Single-threaded use only.</para>
/// <para><b>GC behaviour:</b> Allocates in the constructor; zero allocations inside ReadFrames.</para>
/// <para><b>Platform:</b> Windows, Linux, macOS. FFmpeg is generally not available on Android/iOS.</para>
/// </remarks>
public sealed unsafe class FFmpegDecoder : IAudioDecoder
{
    #region Fields

    /// <summary>
    /// Native AVFormatContext pointer that holds the demuxer state
    /// for the opened media container.
    /// </summary>
    private AVFormatContext* _formatCtx;

    /// <summary>
    /// Native AVCodecContext pointer used to decode compressed audio packets
    /// into raw PCM frames.
    /// </summary>
    private AVCodecContext*  _codecCtx;

    /// <summary>
    /// Native SwrContext pointer responsible for channel remapping,
    /// sample-format conversion, and optional sample-rate resampling.
    /// </summary>
    private SwrContext*      _swrCtx;

    /// <summary>
    /// Reusable AVFrame pointer that receives one decoded frame per
    /// avcodec_receive_frame call.
    /// </summary>
    private AVFrame*         _frame;

    /// <summary>
    /// Reusable AVPacket pointer used to read compressed data packets
    /// from the container via av_read_frame.
    /// </summary>
    private AVPacket*        _packet;

    /// <summary>
    /// Zero-based index of the first audio stream found in the container,
    /// used to filter incoming packets by stream_index.
    /// </summary>
    private int              _audioStreamIndex;

    /// <summary>
    /// Pointer to the unmanaged memory block that receives converted float32
    /// samples from the SwrContext on each decode iteration.
    /// </summary>
    private byte* _resampleBuf;

    /// <summary>
    /// Total allocated size of <see cref="_resampleBuf"/> in bytes.
    /// Grows on demand when a frame produces more samples than the initial estimate.
    /// </summary>
    private int   _resampleBufCapacity;

    /// <summary>
    /// Number of unconsumed bytes currently waiting in <see cref="_resampleBuf"/>,
    /// starting at <see cref="_resampleBufOffset"/>.
    /// </summary>
    private int   _resampleBufLen;

    /// <summary>
    /// Byte offset into <see cref="_resampleBuf"/> where the next unconsumed
    /// sample byte begins.
    /// </summary>
    private int   _resampleBufOffset;

    /// <summary>
    /// Presentation timestamp of the most recently decoded frame,
    /// expressed in milliseconds relative to stream start.
    /// </summary>
    private double _currentPts;

    /// <summary>
    /// Indicates that the demuxer has reached the end of the stream
    /// and no further packets can be read.
    /// </summary>
    private bool   _eof;

    /// <summary>
    /// Guards against double-disposal; set to true on the first Dispose call.
    /// </summary>
    private bool   _disposed;

    /// <summary>
    /// Output sample rate in Hz requested by the caller at construction time.
    /// Replaces the source rate when greater than zero.
    /// </summary>
    private int    _targetSampleRate;

    /// <summary>
    /// Output channel count requested by the caller at construction time.
    /// Replaces the source channel count when greater than zero.
    /// </summary>
    private int    _targetChannels;

    /// <summary>
    /// Upper bound for samples per frame used to size the initial resample buffer:
    /// 64 ms at 48 kHz with 2 channels and float32 encoding.
    /// </summary>
    private const int MaxSamplesPerFrame = 4096;

    #endregion

    #region Properties

    /// <summary>
    /// Audio stream metadata read from the container during construction,
    /// adjusted to the requested target sample rate and channel count.
    /// </summary>
    public AudioStreamInfo StreamInfo { get; private set; }

    #endregion

    #region Constructor

    /// <summary>
    /// Creates an FFmpeg-backed decoder for the given audio file.
    /// Opens the container, selects the first audio stream, opens the codec,
    /// and configures the resampler to produce interleaved float32 output.
    /// </summary>
    /// <param name="filePath">
    /// Full path to the audio file.
    /// </param>
    /// <param name="targetSampleRate">
    /// Desired output sample rate in Hz. Pass 0 to keep the source rate.
    /// </param>
    /// <param name="targetChannels">
    /// Desired output channel count. Pass 0 to keep the source channel count.
    /// </param>
    /// <exception cref="AudioException">
    /// Thrown when FFmpeg is unavailable, the file cannot be opened,
    /// or codec initialization fails.
    /// </exception>
    public FFmpegDecoder(string filePath, int targetSampleRate = 0, int targetChannels = 0)
    {
        FFmpegLoader.Initialize();

        if (!FFmpegConfig.IsAvailable)
            throw new AudioException(AudioErrorCategory.PlatformAPI, "FFmpeg is not available.");

        _targetSampleRate = targetSampleRate;
        _targetChannels   = targetChannels;

        OpenFile(filePath);
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Opens the media container, locates the first audio stream, allocates the codec
    /// context, configures the resampler, and pre-allocates the native resample buffer.
    /// </summary>
    /// <param name="filePath">
    /// Full path to the audio file to open.
    /// </param>
    /// <exception cref="AudioException">
    /// Thrown for any FFmpeg error during container open, stream discovery,
    /// codec allocation, or buffer allocation.
    /// </exception>
    private void OpenFile(string filePath)
    {
        AVFormatContext* fmtCtx = null;
        int ret = NativeMethods.avformat_open_input(&fmtCtx, filePath, IntPtr.Zero, IntPtr.Zero);
        if (ret < 0)
            throw new AudioException(AudioErrorCategory.IO, $"FFmpeg: failed to open file: {filePath} (code: {ret})") { FilePath = filePath };

        _formatCtx = fmtCtx;

        ret = NativeMethods.avformat_find_stream_info(_formatCtx, IntPtr.Zero);
        if (ret < 0)
            throw new AudioException(AudioErrorCategory.FileFormat, $"FFmpeg: failed to retrieve stream info (code: {ret})");

        AVFormatContextFull* fullCtx = (AVFormatContextFull*)_formatCtx;
        _audioStreamIndex = -1;

        for (int i = 0; i < (int)fullCtx->nb_streams; i++)
        {
            AVStream* stream = fullCtx->streams[i];
            AVCodecParametersFull* par = (AVCodecParametersFull*)stream->codecpar;
            if (par->codec_type == FFmpegConst.AVMEDIA_TYPE_AUDIO)
            {
                _audioStreamIndex = i;
                break;
            }
        }

        if (_audioStreamIndex < 0)
            throw new AudioException(AudioErrorCategory.FileFormat, "FFmpeg: no audio stream found in file.");

        AVStream* audioStream = fullCtx->streams[_audioStreamIndex];
        AVCodecParametersFull* codecPar = (AVCodecParametersFull*)audioStream->codecpar;

        AVCodec* codec = NativeMethods.avcodec_find_decoder(codecPar->codec_id);
        if (codec == null)
            throw new AudioException(AudioErrorCategory.Decoding, $"FFmpeg: no decoder found for codec_id={codecPar->codec_id}.");

        _codecCtx = NativeMethods.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            throw new AudioException(AudioErrorCategory.OutOfMemory, "FFmpeg: failed to allocate AVCodecContext.");

        ret = NativeMethods.avcodec_parameters_to_context(_codecCtx, (AVCodecParameters*)codecPar);
        if (ret < 0)
            throw new AudioException(AudioErrorCategory.Decoding, $"FFmpeg: parameter copy failed (code: {ret}).");

        ret = NativeMethods.avcodec_open2(_codecCtx, codec, IntPtr.Zero);
        if (ret < 0)
            throw new AudioException(AudioErrorCategory.Decoding, $"FFmpeg: avcodec_open2 failed (code: {ret}).");

        int srcRate     = codecPar->sample_rate;
        int srcChannels = codecPar->ch_layout.nb_channels;
        int outRate     = _targetSampleRate > 0 ? _targetSampleRate : srcRate;
        int outChannels = _targetChannels   > 0 ? _targetChannels   : srcChannels;

        _targetSampleRate = outRate;
        _targetChannels   = outChannels;

        TimeSpan duration = fullCtx->duration > 0
            ? TimeSpan.FromSeconds((double)fullCtx->duration / FFmpegConst.AV_TIME_BASE)
            : TimeSpan.Zero;

        StreamInfo = new AudioStreamInfo(outChannels, outRate, duration, codecPar->bits_per_coded_sample);

        InitSwrContext(codecPar, srcRate, srcChannels, outRate, outChannels);

        _frame  = NativeMethods.av_frame_alloc();
        _packet = NativeMethods.av_packet_alloc();

        if (_frame == null || _packet == null)
            throw new AudioException(AudioErrorCategory.OutOfMemory, "FFmpeg: frame/packet allocation failed.");

        int bufBytes = MaxSamplesPerFrame * outChannels * sizeof(float);
        _resampleBuf = (byte*)NativeMemory.Alloc((nuint)bufBytes);
        _resampleBufCapacity = bufBytes;

        Log.Info($"FFmpegDecoder ready: {filePath}, {srcChannels}ch@{srcRate}Hz -> {outChannels}ch@{outRate}Hz, duration={duration.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Allocates and initialises the SwrContext that converts raw decoded audio
    /// from the source layout and format into interleaved float32 output.
    /// </summary>
    /// <param name="codecPar">
    /// Codec parameters from which the source format and layout are read.
    /// </param>
    /// <param name="srcRate">Source sample rate in Hz.</param>
    /// <param name="srcChannels">Source channel count.</param>
    /// <param name="outRate">Target output sample rate in Hz.</param>
    /// <param name="outChannels">Target output channel count.</param>
    /// <exception cref="AudioException">
    /// Thrown when swr_alloc_set_opts2 or swr_init returns a negative error code.
    /// </exception>
    private void InitSwrContext(AVCodecParametersFull* codecPar, int srcRate, int srcChannels, int outRate, int outChannels)
    {
        AVChannelLayout outLayout = new()
        {
            order       = FFmpegConst.AV_CHANNEL_ORDER_NATIVE,
            nb_channels = outChannels,
            u_mask      = outChannels == 1
                            ? FFmpegConst.AV_CH_LAYOUT_MONO
                            : FFmpegConst.AV_CH_LAYOUT_STEREO
        };

        AVChannelLayout inLayout = codecPar->ch_layout;

        if (inLayout.order == 0 && inLayout.nb_channels == 0)
        {
            inLayout = new AVChannelLayout
            {
                order       = FFmpegConst.AV_CHANNEL_ORDER_NATIVE,
                nb_channels = srcChannels,
                u_mask      = srcChannels == 1
                                ? FFmpegConst.AV_CH_LAYOUT_MONO
                                : FFmpegConst.AV_CH_LAYOUT_STEREO
            };
        }

        SwrContext* swr = null;
        int ret = NativeMethods.swr_alloc_set_opts2(
            &swr,
            &outLayout,
            FFmpegConst.AV_SAMPLE_FMT_FLT,
            outRate,
            &inLayout,
            codecPar->format,
            srcRate,
            0,
            IntPtr.Zero);

        if (ret < 0 || swr == null)
            throw new AudioException(AudioErrorCategory.Decoding, $"FFmpeg: swr_alloc_set_opts2 failed (code: {ret}).");

        ret = NativeMethods.swr_init(swr);
        if (ret < 0)
        {
            NativeMethods.swr_free(&swr);
            throw new AudioException(AudioErrorCategory.Decoding, $"FFmpeg: swr_init failed (code: {ret}).");
        }

        _swrCtx = swr;
    }

    #endregion

    #region ReadFrames

    /// <summary>
    /// Writes decoded audio data into the provided buffer as interleaved float32 samples.
    /// The decode loop produces zero managed-heap allocations; all intermediate data
    /// is held in the pre-allocated NativeMemory resample buffer.
    /// </summary>
    /// <param name="buffer">
    /// Destination byte buffer sized as a multiple of (channels × sizeof(float)).
    /// </param>
    /// <returns>
    /// An <see cref="AudioDecoderResult"/> with the number of frames written and the current PTS,
    /// or an EOF/error result when the stream ends or a fatal error occurs.
    /// </returns>
    public AudioDecoderResult ReadFrames(byte[] buffer)
    {
        if (_disposed)
            return AudioDecoderResult.CreateError("FFmpegDecoder is already disposed.");

        int bytesNeeded  = buffer.Length;
        int bytesWritten = 0;

        fixed (byte* pOut = buffer)
        {
            while (bytesWritten < bytesNeeded)
            {
                if (_resampleBufLen > 0)
                {
                    int available = _resampleBufLen;
                    int needed    = bytesNeeded - bytesWritten;
                    int toCopy    = available < needed ? available : needed;

                    Buffer.MemoryCopy(
                        _resampleBuf + _resampleBufOffset,
                        pOut + bytesWritten,
                        needed,
                        toCopy);

                    bytesWritten       += toCopy;
                    _resampleBufOffset += toCopy;
                    _resampleBufLen    -= toCopy;

                    if (_resampleBufLen == 0)
                        _resampleBufOffset = 0;

                    continue;
                }

                if (_eof)
                    break;

                int ret = NativeMethods.av_read_frame(_formatCtx, _packet);
                if (ret == FFmpegConst.AVERROR_EOF || ret < 0)
                {
                    NativeMethods.avcodec_send_packet(_codecCtx, null);
                    FlushDecoder(pOut, bytesNeeded, ref bytesWritten);
                    _eof = true;
                    break;
                }

                if (_packet->stream_index != _audioStreamIndex)
                {
                    NativeMethods.av_packet_unref(_packet);
                    continue;
                }

                ret = NativeMethods.avcodec_send_packet(_codecCtx, _packet);
                NativeMethods.av_packet_unref(_packet);

                if (ret < 0 && ret != FFmpegConst.AVERROR_EAGAIN)
                    continue;

                DrainDecoder(pOut, bytesNeeded, ref bytesWritten);
            }
        }

        if (bytesWritten == 0 && _eof)
            return AudioDecoderResult.CreateEOF();

        int framesRead = bytesWritten / (_targetChannels * sizeof(float));
        return AudioDecoderResult.CreateSuccess(framesRead, _currentPts);
    }

    /// <summary>
    /// Pulls all available decoded frames from the codec output queue and
    /// passes each one to <see cref="ConvertAndBuffer"/> until the queue is empty.
    /// </summary>
    /// <param name="pOut">Pointer to the start of the caller's output byte buffer.</param>
    /// <param name="bytesNeeded">Total byte capacity of the output buffer.</param>
    /// <param name="bytesWritten">Running count of bytes already written; updated in place.</param>
    private void DrainDecoder(byte* pOut, int bytesNeeded, ref int bytesWritten)
    {
        while (true)
        {
            int ret = NativeMethods.avcodec_receive_frame(_codecCtx, _frame);
            if (ret == FFmpegConst.AVERROR_EAGAIN || ret == FFmpegConst.AVERROR_EOF)
                break;
            if (ret < 0)
                break;

            ConvertAndBuffer(pOut, bytesNeeded, ref bytesWritten);
            NativeMethods.av_frame_unref(_frame);
        }
    }

    /// <summary>
    /// Sends a null flush packet to the codec and then drains all remaining
    /// frames, ensuring the codec's internal delay buffer is emptied at end-of-stream.
    /// </summary>
    /// <param name="pOut">Pointer to the start of the caller's output byte buffer.</param>
    /// <param name="bytesNeeded">Total byte capacity of the output buffer.</param>
    /// <param name="bytesWritten">Running count of bytes already written; updated in place.</param>
    private void FlushDecoder(byte* pOut, int bytesNeeded, ref int bytesWritten)
    {
        DrainDecoder(pOut, bytesNeeded, ref bytesWritten);
    }

    /// <summary>
    /// Converts the current decoded frame through the SwrContext into interleaved float32,
    /// copies as many bytes as possible directly into the caller's buffer, and stores
    /// any surplus in the internal resample overflow buffer for the next call.
    /// </summary>
    /// <param name="pOut">Pointer to the start of the caller's output byte buffer.</param>
    /// <param name="bytesNeeded">Total byte capacity of the output buffer.</param>
    /// <param name="bytesWritten">Running count of bytes already written; updated in place.</param>
    private void ConvertAndBuffer(byte* pOut, int bytesNeeded, ref int bytesWritten)
    {
        byte** inPtrs = (byte**)_frame->extended_data;

        int maxOutSamples = _frame->nb_samples + 256;
        int maxOutBytes   = maxOutSamples * _targetChannels * sizeof(float);

        if (maxOutBytes > _resampleBufCapacity)
        {
            NativeMemory.Free(_resampleBuf);
            _resampleBuf = (byte*)NativeMemory.Alloc((nuint)maxOutBytes);
            _resampleBufCapacity = maxOutBytes;
        }

        byte* outPtr   = _resampleBuf;
        byte** outPtrs = &outPtr;

        int convertedSamples = NativeMethods.swr_convert(
            _swrCtx, outPtrs, maxOutSamples, inPtrs, _frame->nb_samples);

        if (convertedSamples <= 0)
            return;

        int convertedBytes = convertedSamples * _targetChannels * sizeof(float);

        if (_frame->pts != FFmpegConst.AV_NOPTS_VALUE)
        {
            AVFormatContextFull* fullCtx = (AVFormatContextFull*)_formatCtx;
            AVStream* stream = fullCtx->streams[_audioStreamIndex];
            double tbNum = stream->time_base.num;
            double tbDen = stream->time_base.den > 0 ? stream->time_base.den : 1;
            _currentPts  = _frame->pts * (tbNum / tbDen) * 1000.0;
        }

        int spaceLeft  = bytesNeeded - bytesWritten;
        int directCopy = convertedBytes < spaceLeft ? convertedBytes : spaceLeft;

        if (directCopy > 0)
        {
            Buffer.MemoryCopy(_resampleBuf, pOut + bytesWritten, spaceLeft, directCopy);
            bytesWritten += directCopy;
        }

        int leftover = convertedBytes - directCopy;
        if (leftover > 0)
        {
            _resampleBufOffset = directCopy;
            _resampleBufLen    = leftover;
        }
        else
        {
            _resampleBufOffset = 0;
            _resampleBufLen    = 0;
        }
    }

    #endregion

    #region Seek

    /// <summary>
    /// Seeks to the specified position in the audio stream.
    /// Flushes the codec buffer and clears the resample overflow buffer
    /// so that the next ReadFrames call returns samples from the new position.
    /// </summary>
    /// <param name="position">
    /// The target playback position.
    /// </param>
    /// <param name="error">
    /// Contains an error description when the method returns <c>false</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the seek succeeded; <c>false</c> otherwise.
    /// </returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
        if (_disposed) { error = "Decoder is disposed."; return false; }

        long timestamp = (long)(position.TotalSeconds * FFmpegConst.AV_TIME_BASE);

        int ret = NativeMethods.av_seek_frame(_formatCtx, -1, timestamp, 1);
        if (ret < 0)
        {
            error = $"FFmpeg seek failed (code: {ret}).";
            return false;
        }

        NativeMethods.avcodec_flush_buffers(_codecCtx);
        _resampleBufLen    = 0;
        _resampleBufOffset = 0;
        _eof               = false;
        _currentPts        = position.TotalMilliseconds;

        error = string.Empty;
        return true;
    }

    #endregion

    #region Dispose

    /// <summary>
    /// Releases all FFmpeg native resources in the correct teardown order
    /// and frees the NativeMemory resample buffer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_frame != null)
        {
            AVFrame* f = _frame;
            NativeMethods.av_frame_free(&f);
            _frame = null;
        }

        if (_packet != null)
        {
            AVPacket* p = _packet;
            NativeMethods.av_packet_free(&p);
            _packet = null;
        }

        if (_swrCtx != null)
        {
            SwrContext* s = _swrCtx;
            NativeMethods.swr_free(&s);
            _swrCtx = null;
        }

        if (_codecCtx != null)
        {
            AVCodecContext* c = _codecCtx;
            NativeMethods.avcodec_free_context(&c);
            _codecCtx = null;
        }

        if (_formatCtx != null)
        {
            AVFormatContext* fc = _formatCtx;
            NativeMethods.avformat_close_input(&fc);
            _formatCtx = null;
        }

        if (_resampleBuf != null)
        {
            NativeMemory.Free(_resampleBuf);
            _resampleBuf = null;
        }
    }

    #endregion
}
