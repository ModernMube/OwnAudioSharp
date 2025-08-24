using System;
using System.IO;
using System.Runtime.InteropServices;
using Ownaudio.Exceptions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using FFmpeg.AutoGen;

namespace Ownaudio.Decoders.FFmpeg;

/// <summary>
/// A class that uses FFmpeg for decoding and demuxing specified audio source.
/// This class cannot be inherited.
/// Process description:
/// 
/// <para>Implements: <see cref="IAudioDecoder"/>.</para>
/// </summary>
public sealed unsafe class FFmpegDecoder : IAudioDecoder
{
    private const int StreamBufferSize = 4096; // Increased buffer size
    private const AVMediaType MediaType = AVMediaType.AVMEDIA_TYPE_AUDIO;
    private readonly object _syncLock = new object();
    private readonly AVFormatContext* _formatCtx;
    private readonly AVCodecContext* _codecCtx;
    private readonly AVPacket* _currentPacket;
    private readonly AVFrame* _currentFrame;
    private readonly FFmpegResampler _resampler;
    private avio_alloc_context_read_packet _reads;
    private avio_alloc_context_seek _seeks;
    private readonly int _streamIndex;
    private readonly Stream _inputStream;
    private readonly byte[] _inputStreamBuffer;
    private bool _disposed;

    // Pre-allocated buffer for better performance
    private byte[] _tempBuffer = new byte[1024 * 16]; // 16KB initial size
    private AudioFrame _decoderAudioFrame;

#nullable disable
    /// <summary>
    /// FFmpegDecoder constructor that initializes the object using a URL or stream.
    /// If we use a URL, we check that the url parameter is not null.
    /// If we do not use a URL, we check the stream.
    /// Create a new FFmpeg format context.
    /// Initialize a packet for the decoded data.
    /// Initialize a frame to store each audio sample.
    /// </summary>
    /// <param name="url">File</param>
    /// <param name="stream">Link</param>
    /// <param name="options">Decoder options</param>
    /// <param name="useUrl">url or not url</param>
    private FFmpegDecoder(string url, Stream stream, FFmpegDecoderOptions options, bool useUrl = false)
    {
        if (useUrl)
            Ensure.NotNull(url, nameof(url));
        else
            Ensure.NotNull(stream, nameof(stream));

        _formatCtx = ffmpeg.avformat_alloc_context();

        if (!useUrl)
        {
            _reads = ReadsImpl;
            _seeks = SeeksImpl;

            _inputStream = stream;
            _inputStreamBuffer = new byte[StreamBufferSize];

            var buffer = (byte*)ffmpeg.av_malloc(StreamBufferSize);
            var avio = ffmpeg.avio_alloc_context(buffer, StreamBufferSize, 0, null, _reads, null, _seeks);

            Ensure.That<FFmpegException>(avio != null, "FFmpeg - Unable to allocate avio context.");

            _formatCtx->pb = avio;
        }

        AVDictionary* dict = null;

        ffmpeg.av_dict_set_int(&dict, "stimeout", 10, 0);
        ffmpeg.av_dict_set_int(&dict, "timeout", 10, 0);

        var formatCtx = _formatCtx;
        ffmpeg.avformat_open_input(&formatCtx, useUrl ? url : null, null, &dict).FFGuard();
        ffmpeg.av_dict_free(&dict);

        ffmpeg.avformat_find_stream_info(_formatCtx, null).FFGuard();

        AVCodec* codec = null;
        _streamIndex = ffmpeg.av_find_best_stream(_formatCtx, MediaType, -1, -1, &codec, 0).FFGuard();

        // Optimized: disable unused streams in one pass
        for (var i = 0; i < _formatCtx->nb_streams; i++)
        {
            if (i != _streamIndex)
                _formatCtx->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);

        ffmpeg.avcodec_parameters_to_context(_codecCtx, _formatCtx->streams[_streamIndex]->codecpar).FFGuard();
        ffmpeg.avcodec_open2(_codecCtx, codec, null).FFGuard();

        options ??= new FFmpegDecoderOptions(2, OwnAudio.DefaultOutputDevice.DefaultSampleRate);

        AVChannelLayout* channelLayout;
        if(_codecCtx->ch_layout.nb_channels <= 0)
            ffmpeg.av_channel_layout_default(&_codecCtx->ch_layout, 2);

        channelLayout = &_codecCtx->ch_layout;

        _resampler = new FFmpegResampler(
            channelLayout,
            _codecCtx->sample_rate,
            _codecCtx->sample_fmt,
            options.Channels,
            options.SampleRate);

        var rational = ffmpeg.av_q2d(_formatCtx->streams[_streamIndex]->time_base);
        var duration = _formatCtx->streams[_streamIndex]->duration * rational * 1000.00;
        duration = duration > 0 ? duration : _formatCtx->duration / 1000.00;

        StreamInfo = new AudioStreamInfo(_codecCtx->ch_layout.nb_channels, _codecCtx->sample_rate, duration.Milliseconds(), _codecCtx->bits_per_raw_sample);

        _currentPacket = ffmpeg.av_packet_alloc();
        _currentFrame = ffmpeg.av_frame_alloc();
    }

    /// <summary>
    /// Initializes <see cref="FFmpegDecoder"/> by providing audio URL.
    /// The audio URL can be URL or path to local audio file.
    /// </summary>
    /// <param name="url">Audio URL or audio file path to decode.</param>
    /// <param name="options">An optional FFmpeg decoder options.</param>
    /// <exception cref="ArgumentNullException">Thrown when the given url is <c>null</c>.</exception>
    /// <exception cref="FFmpegException">Thrown when errors occured during setups.</exception>
    public FFmpegDecoder(string url, FFmpegDecoderOptions options = default) : this(url, null, options, true)
    {
    }

    /// <summary>
    /// Initializes <see cref="FFmpegDecoder"/> by providing source audio stream.
    /// </summary>
    /// <param name="stream">Source of audio stream to decode.</param>
    /// <param name="options">An optional FFmpeg decoder options.</param>
    /// <exception cref="ArgumentNullException">Thrown when the given stream is <c>null</c>.</exception>
    /// <exception cref="FFmpegException">Thrown when errors occured during setups.</exception>
    public FFmpegDecoder(Stream stream, FFmpegDecoderOptions options = default) : this(null, stream, options)
    {
    }

    /// <summary>
    /// Audio stream info <see cref="AudioStreamInfo"/>
    /// </summary>
    public AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// It processes the specified number of frames
    /// </summary>
    /// <returns></returns>
    public AudioDecoderResult DecodeNextFrame()
    {
        lock (_syncLock)
        {
            return DecodeFrameInternal();
        }
    }

    /// <summary>
    /// Extracted core decoding logic for better maintainability
    /// </summary>
    /// <returns></returns>
    private AudioDecoderResult DecodeFrameInternal()
    {
        ffmpeg.av_frame_unref(_currentFrame);

        while (true)
        {
            int code;
            do
            {
                ffmpeg.av_packet_unref(_currentPacket);
                code = ffmpeg.av_read_frame(_formatCtx, _currentPacket);

                if (code.FFIsError())
                {
                    ffmpeg.av_packet_unref(_currentPacket);
                    return new AudioDecoderResult(null, false, code.FFIsEOF(), code.FFErrorToText());
                }
            } while (_currentPacket->stream_index != _streamIndex);

            ffmpeg.avcodec_send_packet(_codecCtx, _currentPacket);
            ffmpeg.av_packet_unref(_currentPacket);

            code = ffmpeg.avcodec_receive_frame(_codecCtx, _currentFrame);
            
            if (code != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                break;
        }

        if (_currentFrame->ch_layout.nb_channels <= 0)
            ffmpeg.av_channel_layout_default(&_currentFrame->ch_layout, 2);

        if (!_resampler.TryConvert(_currentFrame, out var data, out var error))
            return new AudioDecoderResult(null, false, false, error);

        var pts = _currentFrame->best_effort_timestamp >= 0
            ? _currentFrame->best_effort_timestamp
            : (_currentFrame->pts >= 0 ? _currentFrame->pts : 0);

        var presentationTime = Math.Round(pts * ffmpeg.av_q2d(_formatCtx->streams[_streamIndex]->time_base) * 1000.0, 2);
        
        _decoderAudioFrame = new AudioFrame(presentationTime, data);
        return new AudioDecoderResult(_decoderAudioFrame, true, false);
    }

    /// <summary>
    /// Finding the position in the Stream for the given timestamp. In the event of an error, enter the text of the error
    /// </summary>
    /// <param name="position">Position time</param>
    /// <param name="error">Error text</param>
    /// <returns></returns>
    public bool TrySeek(TimeSpan position, out string error)
    {
        lock (_syncLock)
        {
            var tb = _formatCtx->streams[_streamIndex]->time_base;
            var pos = (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE);
            var ts = ffmpeg.av_rescale_q(pos, ffmpeg.av_get_time_base_q(), tb);
            var code = ffmpeg.avformat_seek_file(_formatCtx, _streamIndex, 0, ts, long.MaxValue, 0);

            ffmpeg.avcodec_flush_buffers(_codecCtx);
            error = code.FFIsError() ? code.FFErrorToText() : null;

            return !code.FFIsError();
        }
    }

    /// <summary>
    /// It processes all the frames. And returns them in an AudioDecoderResult
    /// </summary>
    /// <param name="position">Current position from which to continue</param>
    /// <returns>Audio decoder result</returns>
    public AudioDecoderResult DecodeAllFrames(TimeSpan position = default)
    {
        lock (_syncLock)
        {
            // Use List<byte[]> instead of MemoryStream for better performance
            var frameDataList = new System.Collections.Generic.List<byte[]>();
            double lastPresentationTime = 0;
            int totalLength = 0;

            while (true)
            {
                var frameResult = DecodeFrameInternal();

                if (frameResult.IsSucceeded)
                {
                    frameDataList.Add(frameResult.Frame.Data);
                    totalLength += frameResult.Frame.Data.Length;
                    lastPresentationTime = frameResult.Frame.PresentationTime;
                }
                else if (frameResult.IsEOF)
                {
                    break;
                }
                else
                {
                    return new AudioDecoderResult(null, false, false, frameResult.ErrorMessage);
                }
            }

            // Efficiently combine all frame data
            var combinedData = new byte[totalLength];
            int offset = 0;
            
            foreach (var frameData in frameDataList)
            {
                Array.Copy(frameData, 0, combinedData, offset, frameData.Length);
                offset += frameData.Length;
            }
            
            TrySeek(position, out var error);

            return new AudioDecoderResult(new AudioFrame(lastPresentationTime, combinedData), true, false);
        }
    }

    private int ReadsImpl(void* opaque, byte* buf, int buf_size)
    {
        buf_size = Math.Min(buf_size, StreamBufferSize);
        var length = _inputStream.Read(_inputStreamBuffer, 0, buf_size);
        Marshal.Copy(_inputStreamBuffer, 0, (IntPtr)buf, length);

        return length;
    }

    private long SeeksImpl(void* opaque, long offset, int whence)
    {
        return whence switch
        {
            ffmpeg.AVSEEK_SIZE => _inputStream.Length,
            < 3 => _inputStream.Seek(offset, (SeekOrigin)whence),
            _ => -1
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var packet = _currentPacket;
        ffmpeg.av_packet_free(&packet);

        var frame = _currentFrame;
        ffmpeg.av_frame_free(&frame);

        var formatCtx = _formatCtx;
        if (_inputStream != null)
        {
            ffmpeg.av_freep(&formatCtx->pb->buffer);
            ffmpeg.avio_context_free(&formatCtx->pb);
        }

        ffmpeg.avformat_close_input(&formatCtx);
        fixed (AVCodecContext** avCodecCtxPtr = &_codecCtx)
        {
            ffmpeg.avcodec_free_context(avCodecCtxPtr);
        }

        _resampler?.Dispose();
        _reads = null;
        _seeks = null;
        _disposed = true;
    }
    #nullable restore
}
