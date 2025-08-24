using System;
using Ownaudio.Exceptions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using FFmpeg.AutoGen;

namespace Ownaudio.Decoders.FFmpeg;

/// <summary>
/// Provides audio resampling functionality using FFmpeg's libswresample library.
/// This class handles sample rate conversion, channel layout changes, and audio format conversion.
/// </summary>
internal sealed unsafe class FFmpegResampler : IDisposable
{
    /// <summary>
    /// Log offset used for FFmpeg operations.
    /// </summary>
    private const int LogOffset = 0;

    /// <summary>
    /// Pointer to the FFmpeg software resampler context.
    /// </summary>
    private readonly SwrContext* _swrCtx;

    /// <summary>
    /// Pointer to the destination audio frame used for resampling operations.
    /// </summary>
    private readonly AVFrame* _dstFrame;

    /// <summary>
    /// Pointer to the destination channel layout configuration.
    /// </summary>
    private readonly AVChannelLayout* _dstChannelLayout;

    /// <summary>
    /// Number of channels in the destination audio format.
    /// </summary>
    private readonly int _dstChannels;

    /// <summary>
    /// Sample rate of the destination audio format in Hz.
    /// </summary>
    private readonly int _dstSampleRate;

    /// <summary>
    /// Number of bytes per audio sample in the destination format.
    /// </summary>
    private readonly int _bytesPerSample;

    /// <summary>
    /// Indicates whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the FFmpegResampler class with the specified audio format parameters.
    /// </summary>
    /// <param name="srcChannelLayout">Pointer to the source channel layout configuration.</param>
    /// <param name="srcSampleRate">Source sample rate in Hz.</param>
    /// <param name="srcSampleFormat">Source audio sample format.</param>
    /// <param name="dstChannels">Destination number of channels.</param>
    /// <param name="dstSampleRate">Destination sample rate in Hz.</param>
    /// <exception cref="FFmpegException">Thrown when FFmpeg context allocation or initialization fails.</exception>
    public FFmpegResampler(
        AVChannelLayout* srcChannelLayout,
        int srcSampleRate,
        AVSampleFormat srcSampleFormat,
        int dstChannels,
        int dstSampleRate)
    {
        _dstChannels = dstChannels;
        _dstSampleRate = dstSampleRate;

        _dstChannelLayout = (AVChannelLayout*)ffmpeg.av_malloc((ulong)sizeof(AVChannelLayout));
        ffmpeg.av_channel_layout_default(_dstChannelLayout, _dstChannels);
        _bytesPerSample = ffmpeg.av_get_bytes_per_sample(OwnAudio.Constants.FFmpegSampleFormat);

        _swrCtx = ffmpeg.swr_alloc();
        if (_swrCtx == null)
            throw new FFmpegException("FFmpeg - Unable to allocate SwrContext.");

        fixed (SwrContext** swrCtxPtr = &_swrCtx)
        {
            int ret = ffmpeg.swr_alloc_set_opts2(
            swrCtxPtr,
            _dstChannelLayout, OwnAudio.Constants.FFmpegSampleFormat, _dstSampleRate,
            srcChannelLayout, srcSampleFormat, srcSampleRate,
            LogOffset, null
            );
        }

        Ensure.That<FFmpegException>(_swrCtx != null, "FFmpeg - Unable to allocate swr context.");
        ffmpeg.swr_init(_swrCtx).FFGuard();

        _dstFrame = ffmpeg.av_frame_alloc();
    }

#nullable disable
    /// <summary>
    /// Attempts to convert an audio frame from the source format to the destination format.
    /// </summary>
    /// <param name="source">Pointer to the source audio frame to convert.</param>
    /// <param name="result">When this method returns, contains the converted audio data as a byte array if conversion succeeded; otherwise, null.</param>
    /// <param name="error">When this method returns, contains an error message if conversion failed; otherwise, null.</param>
    /// <returns>true if the conversion succeeded; otherwise, false.</returns>
    /// <remarks>
    /// This method performs sample rate conversion, channel layout transformation, and format conversion
    /// as specified during resampler initialization. The output is always in the destination format
    /// configured in the constructor.
    /// </remarks>
    public bool TryConvert(AVFrame* source, out byte[] result, out string error)
    {
        result = null;
        error = null;

        if (source == null)
        {
            error = "Source frame is null.";
            return false;
        }

        ffmpeg.av_frame_unref(_dstFrame);

        _dstFrame->ch_layout.nb_channels = _dstChannels;
        _dstFrame->sample_rate = _dstSampleRate;
        _dstFrame->ch_layout = *_dstChannelLayout;
        _dstFrame->format = (int)OwnAudio.Constants.FFmpegSampleFormat;

        int dstSampleCount = (int)ffmpeg.av_rescale_rnd(
            (long)source->nb_samples, (long)_dstFrame->sample_rate, (long)source->sample_rate, AVRounding.AV_ROUND_UP);

        int bufferSize = ffmpeg.av_samples_get_buffer_size(
            null, _dstFrame->ch_layout.nb_channels, dstSampleCount, (AVSampleFormat)_dstFrame->format, 1);

        if (bufferSize <= 0)
        {
            error = "Failed to calculate buffer size.";
            return false;
        }

        result = new byte[bufferSize];
        fixed (byte* resultPtr = result)
        {
            byte** dstData = stackalloc byte*[1];
            dstData[0] = resultPtr;

            int convertedSamples = ffmpeg.swr_convert(
                _swrCtx, dstData, dstSampleCount, source->extended_data, source->nb_samples);

            if (convertedSamples < 0)
            {
                error = "Failed to convert audio samples.";
                return false;
            }
        }

        return true;
    }

#nullable restore

    /// <summary>
    /// Releases all resources used by the FFmpegResampler instance.
    /// </summary>
    /// <remarks>
    /// This method frees the allocated FFmpeg contexts and frames. It's safe to call this method multiple times.
    /// After disposal, the resampler instance should not be used for further operations.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var dstFrame = _dstFrame;
        ffmpeg.av_frame_free(&dstFrame);

        var swrCtx = _swrCtx;
        ffmpeg.swr_free(&swrCtx);

        ffmpeg.av_free(_dstChannelLayout);

        _disposed = true;
    }
}
