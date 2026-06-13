using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Decoders.FFmpeg;

#region Constants

/// <summary>
/// Compile-time constants mirroring FFmpeg macro values used throughout
/// the interop layer (sample formats, error codes, channel layouts, time base).
/// </summary>
internal static class FFmpegConst
{
    /// <summary>
    /// AV_SAMPLE_FMT_FLT — 32-bit floating point, interleaved.
    /// Corresponds to the AVSampleFormat enum value 3 in libavutil.
    /// </summary>
    public const int AV_SAMPLE_FMT_FLT  = 3;

    /// <summary>
    /// AV_SAMPLE_FMT_FLTP — 32-bit floating point, planar.
    /// Corresponds to the AVSampleFormat enum value 9 in libavutil.
    /// </summary>
    public const int AV_SAMPLE_FMT_FLTP = 9;

    /// <summary>
    /// AVMEDIA_TYPE_AUDIO — identifies audio streams in AVFormatContext.
    /// Corresponds to the AVMediaType enum value 1 in libavutil.
    /// </summary>
    public const int AVMEDIA_TYPE_AUDIO = 1;

    /// <summary>
    /// AVERROR_EOF — internal FFmpeg end-of-file error code (-541478725).
    /// Returned by avcodec_receive_frame and av_read_frame when no more data is available.
    /// </summary>
    public const int AVERROR_EOF = unchecked((int)0xdfb9b0bb);

    /// <summary>
    /// AVERROR(EAGAIN) = -11 — decoder needs more input before producing output.
    /// Returned when the codec requires additional packets before a frame can be emitted.
    /// </summary>
    public const int AVERROR_EAGAIN = -11;

    /// <summary>
    /// AV_CH_LAYOUT_MONO — single-channel layout bitmask.
    /// Selects the center front speaker (AV_CH_FRONT_CENTER) as the sole channel.
    /// </summary>
    public const ulong AV_CH_LAYOUT_MONO   = 0x00000004UL;

    /// <summary>
    /// AV_CH_LAYOUT_STEREO — two-channel (L+R) layout bitmask.
    /// Selects front-left and front-right speakers in the native channel order.
    /// </summary>
    public const ulong AV_CH_LAYOUT_STEREO = 0x00000003UL;

    /// <summary>
    /// AV_NOPTS_VALUE — sentinel indicating an unknown presentation timestamp.
    /// Stored in AVFrame.pts and AVPacket.pts when the container provides no timing information.
    /// </summary>
    public const long AV_NOPTS_VALUE = long.MinValue;

    /// <summary>
    /// AV_TIME_BASE — internal time base unit (microseconds per second).
    /// Used to convert container-level duration values to seconds.
    /// </summary>
    public const int AV_TIME_BASE = 1_000_000;

    /// <summary>
    /// AV_CHANNEL_ORDER_NATIVE — native channel order flag introduced in FFmpeg 5.1.
    /// When set in AVChannelLayout.order, the u_mask field holds a bitmask of active channels.
    /// </summary>
    public const int AV_CHANNEL_ORDER_NATIVE = 1;
}

#endregion

#region AVChannelLayout

/// <summary>
/// AVChannelLayout structure introduced in FFmpeg 5.1 (libavutil).
/// Total size: 40 bytes — order(4) + nb_channels(4) + union u(8) + opaque(8) + padding(16).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVChannelLayout
{
    /// <summary>
    /// Channel ordering mode expressed as an AVChannelOrder enum value (int).
    /// Set to AV_CHANNEL_ORDER_NATIVE when u_mask specifies the layout bitmask.
    /// </summary>
    public int order;

    /// <summary>
    /// Total number of channels described by this layout.
    /// Must match the number of set bits in u_mask for native order layouts.
    /// </summary>
    public int nb_channels;

    /// <summary>
    /// Union field: channel bitmask when order is AV_CHANNEL_ORDER_NATIVE — 8 bytes.
    /// Each bit position corresponds to a specific AVChannel speaker position.
    /// </summary>
    public ulong u_mask;

    /// <summary>
    /// Opaque pointer reserved for custom channel map data — 8 bytes.
    /// Must be null when using the native bitmask channel order.
    /// </summary>
    public void* opaque;
}

#endregion

#region Opaque Context Structs

/// <summary>
/// AVFormatContext opaque handle — used as a pointer only; never dereferenced directly.
/// Full field access is provided by <see cref="AVFormatContextFull"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVFormatContext { }

/// <summary>
/// AVCodecContext opaque handle — used as a pointer only; never dereferenced directly.
/// Passed to codec functions such as avcodec_open2 and avcodec_send_packet.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVCodecContext { }

/// <summary>
/// AVCodec opaque handle — used as a pointer only; never dereferenced directly.
/// Returned by avcodec_find_decoder and passed to avcodec_alloc_context3.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVCodec { }

/// <summary>
/// AVCodecParameters opaque handle — used as a pointer only; never dereferenced directly.
/// Full field access is provided by <see cref="AVCodecParametersFull"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVCodecParameters { }

/// <summary>
/// SwrContext opaque handle — used as a pointer only; never dereferenced directly.
/// Allocated via swr_alloc_set_opts2 and freed via swr_free.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SwrContext { }

#endregion

#region AVStream

/// <summary>
/// Partial AVStream mapping containing only the fields required for seek
/// operations and time-base calculations during audio decoding.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVStream
{
    /// <summary>
    /// Pointer to the AVClass descriptor; used internally by FFmpeg for logging and options.
    /// </summary>
    public void* av_class;

    /// <summary>
    /// Zero-based stream index within the container, matching the value in AVPacket.stream_index.
    /// </summary>
    public int index;

    /// <summary>
    /// Format-specific stream identifier assigned by the container (e.g. PID in MPEG-TS).
    /// </summary>
    public int id;

    /// <summary>
    /// Pointer to the codec parameters that describe the encoding of this stream.
    /// Cast to <see cref="AVCodecParametersFull"/> for field-level access.
    /// </summary>
    public AVCodecParameters* codecpar;

    /// <summary>
    /// Format-private data; do not access directly.
    /// </summary>
    public void* priv_data;

    /// <summary>
    /// Fundamental unit of time for all timestamps in this stream (numerator / denominator seconds).
    /// </summary>
    public AVRational time_base;

    /// <summary>
    /// Presentation timestamp of the first frame in this stream, expressed in stream time-base units.
    /// </summary>
    public long start_time;

    /// <summary>
    /// Total duration of this stream in stream time-base units; may be zero if unknown.
    /// </summary>
    public long duration;

    /// <summary>
    /// Number of frames in this stream; may be zero if the container does not provide frame counts.
    /// </summary>
    public long nb_frames;

    /// <summary>
    /// Combination of AV_DISPOSITION_* flags describing special stream properties (e.g. default, forced).
    /// </summary>
    public int disposition;

    /// <summary>
    /// AVDiscard value controlling which frames the demuxer should discard for this stream.
    /// </summary>
    public int discard;

    /// <summary>
    /// Sample aspect ratio of video frames in this stream; irrelevant for audio streams.
    /// </summary>
    public AVRational sample_aspect_ratio;

    /// <summary>
    /// Pointer to the AVDictionary containing stream-level metadata (title, language, etc.).
    /// </summary>
    public void* metadata;

    /// <summary>
    /// Average frame rate of the stream expressed as a rational fraction.
    /// For audio streams this is typically 0/0 (unknown).
    /// </summary>
    public AVRational avg_frame_rate;
}

/// <summary>
/// AVRational — fractional time base expressed as numerator / denominator.
/// Used throughout FFmpeg to represent timestamps and frame rates without floating-point loss.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    /// <summary>
    /// Numerator of the rational value.
    /// </summary>
    public int num;

    /// <summary>
    /// Denominator of the rational value.
    /// A value of zero indicates an unknown or undefined rational.
    /// </summary>
    public int den;
}

#endregion

#region AVCodecParameters

/// <summary>
/// Partial AVCodecParameters mapping containing only the fields needed
/// to open a decoder and configure the resampler for audio output.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVCodecParametersFull
{
    /// <summary>
    /// AVMediaType value identifying whether this stream is audio, video, subtitle, etc.
    /// Compare against <see cref="FFmpegConst.AVMEDIA_TYPE_AUDIO"/> to detect audio streams.
    /// </summary>
    public int codec_type;

    /// <summary>
    /// AVCodecID value identifying the specific codec used to encode this stream.
    /// Passed to avcodec_find_decoder to locate the appropriate decoder.
    /// </summary>
    public int codec_id;

    /// <summary>
    /// Additional codec-specific codec tag from the container format (e.g. FOURCC for AVI).
    /// </summary>
    public uint codec_tag;

    /// <summary>
    /// Pointer to extra codec-specific data required for initialisation (e.g. Vorbis headers).
    /// </summary>
    public byte* extradata;

    /// <summary>
    /// Byte size of the extradata buffer pointed to by <see cref="extradata"/>.
    /// </summary>
    public int extradata_size;

    /// <summary>
    /// Pointer to an array of AVPacketSideData entries associated with this stream.
    /// </summary>
    public void* coded_side_data;

    /// <summary>
    /// Number of entries in the coded_side_data array.
    /// </summary>
    public int nb_coded_side_data;

    /// <summary>
    /// Pixel format (video) or sample format (audio) as an AVPixelFormat or AVSampleFormat integer.
    /// For audio this is the source sample format passed to the resampler.
    /// </summary>
    public int format;

    /// <summary>
    /// Average bitrate of the encoded stream in bits per second; zero if unknown.
    /// </summary>
    public long bit_rate;

    /// <summary>
    /// Number of bits per compressed audio sample; used for lossless codec depth reporting.
    /// </summary>
    public int bits_per_coded_sample;

    /// <summary>
    /// Number of bits per raw (uncompressed) sample stored in the decoded output.
    /// </summary>
    public int bits_per_raw_sample;

    /// <summary>
    /// Codec profile identifier (e.g. AAC-LC, HE-AAC); codec-specific meaning.
    /// </summary>
    public int profile;

    /// <summary>
    /// Codec level identifier; codec-specific meaning, typically indicates complexity constraints.
    /// </summary>
    public int level;

    /// <summary>
    /// Coded width of video frames in pixels; zero for audio streams.
    /// </summary>
    public int width;

    /// <summary>
    /// Coded height of video frames in pixels; zero for audio streams.
    /// </summary>
    public int height;

    /// <summary>
    /// Sample aspect ratio of video pixels; irrelevant for audio streams.
    /// </summary>
    public AVRational sample_aspect_ratio;

    /// <summary>
    /// Frame rate of the video stream; irrelevant for audio streams.
    /// </summary>
    public AVRational framerate;

    /// <summary>
    /// AVFieldOrder value describing the field order for interlaced video; irrelevant for audio.
    /// </summary>
    public int field_order;

    /// <summary>
    /// AVColorRange value indicating whether luma/chroma values use limited or full range.
    /// </summary>
    public int color_range;

    /// <summary>
    /// AVColorPrimaries value specifying the chromaticity coordinates of the source primaries.
    /// </summary>
    public int color_primaries;

    /// <summary>
    /// AVColorTransferCharacteristic value describing the opto-electrical transfer function.
    /// </summary>
    public int color_trc;

    /// <summary>
    /// AVColorSpace value specifying the YUV colorspace matrix coefficients.
    /// </summary>
    public int color_space;

    /// <summary>
    /// AVChromaLocation value indicating the position of chroma samples relative to luma.
    /// </summary>
    public int chroma_location;

    /// <summary>
    /// Number of leading samples to discard from the beginning of the first decoded frame.
    /// Relevant for codecs with encoder delay (e.g. MP3, AAC).
    /// </summary>
    public int video_delay;

    /// <summary>
    /// Channel layout descriptor for the audio stream, introduced in FFmpeg 5.1.
    /// Contains channel count, ordering mode, and bitmask for the source audio.
    /// </summary>
    public AVChannelLayout ch_layout;

    /// <summary>
    /// Sample rate of the audio stream in Hz as stored in the container.
    /// </summary>
    public int sample_rate;

    /// <summary>
    /// Block alignment of the encoded audio in bytes; used by some PCM and ADPCM codecs.
    /// </summary>
    public int block_align;

    /// <summary>
    /// Number of audio samples per encoded frame; zero if variable or unknown.
    /// </summary>
    public int frame_size;

    /// <summary>
    /// Number of samples to skip at the start of the stream to compensate for encoder delay.
    /// </summary>
    public int initial_padding;

    /// <summary>
    /// Number of padding samples appended at the end of the stream by the encoder.
    /// </summary>
    public int trailing_padding;

    /// <summary>
    /// Number of samples needed before a seek point to prime the decoder correctly.
    /// </summary>
    public int seek_preroll;
}

#endregion

#region AVFrame

/// <summary>
/// Partial AVFrame mapping containing only the fields required for audio decoding.
/// The data arrays are managed by FFmpeg and must never be freed manually.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFrame
{
    /// <summary>
    /// Per-channel data pointers (up to 8 channels).
    /// Planar formats store each channel in a separate pointer;
    /// interleaved formats use data[0] only.
    /// </summary>
    public fixed ulong data[8];

    /// <summary>
    /// Bytes per row for each data plane (linesize[i]).
    /// For audio, linesize[0] holds the total byte size of all samples in the frame.
    /// </summary>
    public fixed int linesize[8];

    /// <summary>
    /// Pointer to an array of data plane pointers that may extend beyond eight entries
    /// for formats with more than eight channels.
    /// </summary>
    public void* extended_data;

    /// <summary>
    /// Coded width of the video frame in pixels; zero for audio frames.
    /// </summary>
    public int width;

    /// <summary>
    /// Coded height of the video frame in pixels; zero for audio frames.
    /// </summary>
    public int height;

    /// <summary>
    /// Number of audio samples (per channel) contained in this frame.
    /// Used to calculate the byte size of the decoded payload.
    /// </summary>
    public int nb_samples;

    /// <summary>
    /// AVSampleFormat or AVPixelFormat of the frame data as an integer.
    /// For audio this matches the output format configured on the codec context.
    /// </summary>
    public int format;

    /// <summary>
    /// Non-zero if this frame is a key frame (I-frame); zero for dependent frames.
    /// </summary>
    public int key_frame;

    /// <summary>
    /// AVPictureType value indicating whether this is an I, P, or B frame.
    /// </summary>
    public int pict_type;

    /// <summary>
    /// Sample aspect ratio of the frame pixels; relevant for video only.
    /// </summary>
    public AVRational sample_aspect_ratio;

    /// <summary>
    /// Presentation timestamp of the frame in the stream's time-base units.
    /// May equal AV_NOPTS_VALUE when timing information is unavailable.
    /// </summary>
    public long pts;

    /// <summary>
    /// DTS (decode timestamp) copied from the packet that produced this frame.
    /// </summary>
    public long pkt_dts;

    /// <summary>
    /// Time base in which the pts and pkt_dts timestamps of this frame are expressed.
    /// </summary>
    public AVRational time_base;

    /// <summary>
    /// Sequential number of this frame in coding order, assigned by the codec.
    /// </summary>
    public int coded_picture_number;

    /// <summary>
    /// Sequential number of this frame in display order, assigned by the codec.
    /// </summary>
    public int display_picture_number;

    /// <summary>
    /// Quality factor of this frame as set by the encoder; higher is worse.
    /// </summary>
    public int quality;

    /// <summary>
    /// Opaque pointer that can carry caller-defined data through the codec pipeline.
    /// </summary>
    public void* opaque;

    /// <summary>
    /// Per-frame encoding error value for plane 0; used by error-concealment tools.
    /// </summary>
    public long error0;

    /// <summary>
    /// Per-frame encoding error value for plane 1; used by error-concealment tools.
    /// </summary>
    public long error1;

    /// <summary>
    /// Per-frame encoding error value for plane 2; used by error-concealment tools.
    /// </summary>
    public long error2;

    /// <summary>
    /// Per-frame encoding error value for plane 3; used by error-concealment tools.
    /// </summary>
    public long error3;

    /// <summary>
    /// Per-frame encoding error value for plane 4; used by error-concealment tools.
    /// </summary>
    public long error4;

    /// <summary>
    /// Per-frame encoding error value for plane 5; used by error-concealment tools.
    /// </summary>
    public long error5;

    /// <summary>
    /// Per-frame encoding error value for plane 6; used by error-concealment tools.
    /// </summary>
    public long error6;

    /// <summary>
    /// Per-frame encoding error value for plane 7; used by error-concealment tools.
    /// </summary>
    public long error7;

    /// <summary>
    /// Number of additional times this frame should be displayed before advancing.
    /// Used for pulldown and repeat-field flags in video; irrelevant for audio.
    /// </summary>
    public int repeat_pict;

    /// <summary>
    /// Non-zero if the frame is interlaced; zero for progressive and all audio frames.
    /// </summary>
    public int interlaced_frame;

    /// <summary>
    /// Non-zero if the top field is displayed first in an interlaced frame; zero otherwise.
    /// </summary>
    public int top_field_first;

    /// <summary>
    /// Non-zero if the palette has changed since the previous frame; relevant only for palettised video.
    /// </summary>
    public int palette_has_changed;

    /// <summary>
    /// Caller-set timestamp copied from the reordered packet; deprecated in newer FFmpeg versions.
    /// </summary>
    public long reordered_opaque;

    /// <summary>
    /// Sample rate of the audio in this frame in Hz, as set by the decoder.
    /// </summary>
    public int sample_rate;

    /// <summary>
    /// Channel layout of the decoded audio frame using the FFmpeg 5.1+ AVChannelLayout structure.
    /// </summary>
    public AVChannelLayout ch_layout;
}

#endregion

#region AVPacket

/// <summary>
/// Partial AVPacket mapping containing the fields required to
/// identify the stream index and route packets to the decoder.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVPacket
{
    /// <summary>
    /// Reference-counted buffer holding the packet payload; managed by FFmpeg internally.
    /// </summary>
    public void* buf;

    /// <summary>
    /// Presentation timestamp of the packet in the stream's time-base units.
    /// May be AV_NOPTS_VALUE when the container does not provide timing.
    /// </summary>
    public long pts;

    /// <summary>
    /// Decompression timestamp of the packet in the stream's time-base units.
    /// May be AV_NOPTS_VALUE when the container does not provide timing.
    /// </summary>
    public long dts;

    /// <summary>
    /// Pointer to the compressed payload bytes of this packet.
    /// </summary>
    public byte* data;

    /// <summary>
    /// Byte size of the compressed payload pointed to by <see cref="data"/>.
    /// </summary>
    public int size;

    /// <summary>
    /// Index of the stream this packet belongs to within its AVFormatContext.
    /// Used to filter packets for the target audio stream.
    /// </summary>
    public int stream_index;

    /// <summary>
    /// Combination of AV_PKT_FLAG_* flags (e.g. AV_PKT_FLAG_KEY for keyframes).
    /// </summary>
    public int flags;

    /// <summary>
    /// Pointer to an array of AVPacketSideData entries carrying auxiliary data.
    /// </summary>
    public void* side_data;

    /// <summary>
    /// Number of entries in the side_data array.
    /// </summary>
    public int side_data_elems;

    /// <summary>
    /// Duration of the packet in stream time-base units; zero if unknown.
    /// </summary>
    public long duration;

    /// <summary>
    /// Byte position of the packet in the stream file; -1 if unknown.
    /// </summary>
    public long pos;
}

#endregion

#region AVFormatContextFull

/// <summary>
/// Partial AVFormatContext mapping containing the stream list pointer
/// and container-level duration needed by the decoder.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFormatContextFull
{
    /// <summary>
    /// Pointer to the AVClass descriptor used by FFmpeg for logging and AVOption support.
    /// </summary>
    public void* av_class;

    /// <summary>
    /// Pointer to the input format (AVInputFormat) that demuxes this container; null for output.
    /// </summary>
    public void* iformat;

    /// <summary>
    /// Pointer to the output format (AVOutputFormat) used when muxing; null for input.
    /// </summary>
    public void* oformat;

    /// <summary>
    /// Format-private data allocated and managed by the input or output format handler.
    /// </summary>
    public void* priv_data;

    /// <summary>
    /// Pointer to the AVIOContext providing byte-level I/O for the container.
    /// </summary>
    public void* pb;

    /// <summary>
    /// Combination of AVFMTCTX_* flags set by the format handler during open.
    /// </summary>
    public int ctx_flags;

    /// <summary>
    /// Number of streams contained in this format context.
    /// </summary>
    public uint nb_streams;

    /// <summary>
    /// Pointer to the array of AVStream pointers, one per stream in the container.
    /// </summary>
    public AVStream** streams;

    /// <summary>
    /// Number of stream groups in this format context (FFmpeg 6.0+).
    /// </summary>
    public uint nb_stream_groups;

    /// <summary>
    /// Pointer to the array of stream group pointers (FFmpeg 6.0+).
    /// </summary>
    public void* stream_groups;

    /// <summary>
    /// Number of chapters defined in the container (e.g. DVD/MKV chapters).
    /// </summary>
    public uint nb_chapters;

    /// <summary>
    /// Pointer to the array of AVChapter pointers describing chapter boundaries.
    /// </summary>
    public void* chapters;

    /// <summary>
    /// Pointer to the null-terminated URL or file path of the opened media resource.
    /// </summary>
    public void* url;

    /// <summary>
    /// Start time of the first stream in AV_TIME_BASE units; AV_NOPTS_VALUE if unknown.
    /// </summary>
    public long start_time;

    /// <summary>
    /// Total container duration in AV_TIME_BASE microsecond units; used to compute TimeSpan.
    /// </summary>
    public long duration;

    /// <summary>
    /// Overall bitrate of the container in bits per second; zero if unknown.
    /// </summary>
    public long bit_rate;
}

#endregion

#region P/Invoke — NativeMethods

/// <summary>
/// FFmpeg 7/8 native function bindings compiled with the AOT-compatible
/// <see cref="LibraryImportAttribute"/> source generator.
/// </summary>
internal static unsafe partial class NativeMethods
{
    /// <summary>
    /// Native library name for the avformat module.
    /// Used as the library argument in LibraryImport attributes for demuxer functions.
    /// </summary>
    private const string AvFormat   = "avformat";

    /// <summary>
    /// Native library name for the avcodec module.
    /// Used as the library argument in LibraryImport attributes for codec and packet functions.
    /// </summary>
    private const string AvCodec    = "avcodec";

    /// <summary>
    /// Native library name for the avutil module.
    /// Used as the library argument in LibraryImport attributes for frame and utility functions.
    /// </summary>
    private const string AvUtil     = "avutil";

    /// <summary>
    /// Native library name for the swresample module.
    /// Used as the library argument in LibraryImport attributes for audio resampling functions.
    /// </summary>
    private const string SwResample = "swresample";

    #region avformat

    /// <summary>
    /// Opens a media file and allocates an AVFormatContext for it.
    /// </summary>
    [LibraryImport(AvFormat, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int avformat_open_input(
        AVFormatContext** ps,
        string url,
        IntPtr fmt,
        IntPtr options);

    /// <summary>
    /// Reads stream information from the container into the format context.
    /// </summary>
    [LibraryImport(AvFormat)]
    public static partial int avformat_find_stream_info(
        AVFormatContext* ic,
        IntPtr options);

    /// <summary>
    /// Closes the input and frees the AVFormatContext.
    /// </summary>
    [LibraryImport(AvFormat)]
    public static partial void avformat_close_input(AVFormatContext** s);

    /// <summary>
    /// Reads the next packet from the format context into the provided AVPacket.
    /// </summary>
    [LibraryImport(AvFormat)]
    public static partial int av_read_frame(AVFormatContext* s, AVPacket* pkt);

    /// <summary>
    /// Seeks to the given timestamp in the specified stream (or the best stream when index is -1).
    /// </summary>
    [LibraryImport(AvFormat)]
    public static partial int av_seek_frame(
        AVFormatContext* s,
        int stream_index,
        long timestamp,
        int flags);

    #endregion

    #region avcodec

    /// <summary>
    /// Finds a registered decoder for the given AVCodecID.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial AVCodec* avcodec_find_decoder(int id);

    /// <summary>
    /// Allocates a new AVCodecContext for the given codec.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

    /// <summary>
    /// Copies stream codec parameters from AVCodecParameters into an AVCodecContext.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial int avcodec_parameters_to_context(
        AVCodecContext* codec,
        AVCodecParameters* par);

    /// <summary>
    /// Opens the codec for decoding with the given context and options.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial int avcodec_open2(
        AVCodecContext* avctx,
        AVCodec* codec,
        IntPtr options);

    /// <summary>
    /// Frees an AVCodecContext and all associated resources.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial void avcodec_free_context(AVCodecContext** avctx);

    /// <summary>
    /// Submits a raw compressed packet to the decoder for processing.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

    /// <summary>
    /// Retrieves one decoded audio frame from the decoder output queue.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);

    /// <summary>
    /// Discards buffered data in the decoder, typically called after a seek operation.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial void avcodec_flush_buffers(AVCodecContext* avctx);

    #endregion

    #region avutil

    /// <summary>
    /// Allocates and zeroes an AVPacket on the heap.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial AVPacket* av_packet_alloc();

    /// <summary>
    /// Frees an AVPacket and all data buffers referenced by it.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial void av_packet_free(AVPacket** pkt);

    /// <summary>
    /// Releases the internal data buffer of an AVPacket without freeing the struct itself.
    /// </summary>
    [LibraryImport(AvCodec)]
    public static partial void av_packet_unref(AVPacket* pkt);

    /// <summary>
    /// Allocates and zeroes an AVFrame on the heap.
    /// </summary>
    [LibraryImport(AvUtil)]
    public static partial AVFrame* av_frame_alloc();

    /// <summary>
    /// Frees an AVFrame and all data buffers referenced by it.
    /// </summary>
    [LibraryImport(AvUtil)]
    public static partial void av_frame_free(AVFrame** frame);

    /// <summary>
    /// Releases the internal data buffers of an AVFrame without freeing the struct itself.
    /// </summary>
    [LibraryImport(AvUtil)]
    public static partial void av_frame_unref(AVFrame* frame);

    /// <summary>
    /// Returns the version of the avutil library as a packed integer.
    /// Used during startup to confirm that the library loaded correctly.
    /// </summary>
    [LibraryImport(AvUtil)]
    public static partial uint avutil_version();

    #endregion

    #region swresample

    /// <summary>
    /// Allocates and configures a SwrContext using the FFmpeg 5.1+ AVChannelLayout API.
    /// The context converts between the given input and output channel layout, sample format,
    /// and sample rate.
    /// </summary>
    [LibraryImport(SwResample)]
    public static partial int swr_alloc_set_opts2(
        SwrContext** ps,
        AVChannelLayout* out_ch_layout,
        int out_sample_fmt,
        int out_sample_rate,
        AVChannelLayout* in_ch_layout,
        int in_sample_fmt,
        int in_sample_rate,
        int log_offset,
        IntPtr log_ctx);

    /// <summary>
    /// Initializes a previously configured SwrContext and prepares it for conversion.
    /// </summary>
    [LibraryImport(SwResample)]
    public static partial int swr_init(SwrContext* s);

    /// <summary>
    /// Converts audio samples applying channel remapping, format conversion,
    /// and sample-rate resampling in a single pass.
    /// </summary>
    [LibraryImport(SwResample)]
    public static partial int swr_convert(
        SwrContext* s,
        byte** out_arg,
        int out_count,
        byte** in_arg,
        int in_count);

    /// <summary>
    /// Frees a SwrContext and all resources held by it.
    /// </summary>
    [LibraryImport(SwResample)]
    public static partial void swr_free(SwrContext** s);

    #endregion
}

#endregion
