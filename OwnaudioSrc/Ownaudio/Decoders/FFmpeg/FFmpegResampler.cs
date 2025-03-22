using System;
using Ownaudio.Exceptions;
using Ownaudio.Utilities;
using Ownaudio.Utilities.Extensions;
using FFmpeg.AutoGen;

namespace Ownaudio.Decoders.FFmpeg;

internal sealed unsafe class FFmpegResampler : IDisposable
{
    private const int LogOffset = 0;
    private readonly SwrContext* _swrCtx;
    private readonly AVFrame* _dstFrame;
    private readonly AVChannelLayout* _dstChannelLayout;
    private readonly int _dstChannels;
    private readonly int _dstSampleRate;
    private readonly int _bytesPerSample;
    private bool _disposed;

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
            // Beállítja a konverziós paramétereket az új API szerint
            int ret = ffmpeg.swr_alloc_set_opts2(
            swrCtxPtr, // SwrContext referencia
            _dstChannelLayout, OwnAudio.Constants.FFmpegSampleFormat, _dstSampleRate, // Cél paraméterek
            srcChannelLayout, srcSampleFormat, srcSampleRate, // Forrás paraméterek
            LogOffset, null // Extra paraméterek
            );
        }

        Ensure.That<FFmpegException>(_swrCtx != null, "FFmpeg - Unable to allocate swr context.");
        ffmpeg.swr_init(_swrCtx).FFGuard();

        _dstFrame = ffmpeg.av_frame_alloc();
    }

#nullable disable
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
