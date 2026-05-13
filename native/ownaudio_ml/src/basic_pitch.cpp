/**
 * basic_pitch.cpp – BasicPitch audio-to-MIDI note detection using ONNX Runtime C API.
 *
 * Implements: ownaudio_ml_predict_notes, ownaudio_ml_free_notes_result,
 *             ownaudio_ml_load_basic_pitch_model
 *
 * The BasicPitch model outputs three tensors:
 *   - contours : [frames, N_FREQ_BINS_CONTOURS] (= [frames, 264])
 *   - notes    : [frames, 88]
 *   - onsets   : [frames, 88]
 */

#include "../ownaudio_ml.h"

#ifdef HAVE_ONNXRUNTIME
#  include <onnxruntime_c_api.h>
#endif

#include <cstdlib>
#include <cstring>
#include <cmath>

// ── Constants ─────────────────────────────────────────────────────────────────

static const int BP_SAMPLE_RATE          = 22050;
static const int BP_FFT_HOP              = 256;
static const int BP_AUDIO_WINDOW_LEN     = 2;
static const int BP_N_OVERLAPPING_FRAMES = 30;
static const int BP_OVERLAP_LEN          = BP_N_OVERLAPPING_FRAMES * BP_FFT_HOP;
static const int BP_AUDIO_N_SAMPLES      = BP_SAMPLE_RATE * BP_AUDIO_WINDOW_LEN - BP_FFT_HOP;
static const int BP_HOP_SIZE             = BP_AUDIO_N_SAMPLES - BP_OVERLAP_LEN;
static const int BP_ANNOT_N_FRAMES       = (BP_SAMPLE_RATE / BP_FFT_HOP) * BP_AUDIO_WINDOW_LEN;
static const int BP_N_SEMITONES          = 88;
static const int BP_CONTOUR_BINS         = 3;
static const int BP_N_FREQ_BINS_CONTOURS = BP_N_SEMITONES * BP_CONTOUR_BINS;

// ── State ─────────────────────────────────────────────────────────────────────

#ifdef HAVE_ONNXRUNTIME
static const OrtApi*    g_ort            = nullptr;
static OrtEnv*          g_env            = nullptr;
static OrtSession*      g_bp_session     = nullptr;
static OrtSessionOptions* g_bp_opts      = nullptr;
#endif

static bool g_basic_pitch_loaded = false;

// ── Internal helpers ──────────────────────────────────────────────────────────

static void free_notes_internal(OwnAudioMlNotesPrediction* r)
{
    if (!r) return;
    free(r->contours);
    free(r->notes);
    free(r->onsets);
    r->contours    = nullptr;
    r->notes       = nullptr;
    r->onsets      = nullptr;
    r->frame_count = 0;
    r->freq_bins   = 0;
}

// ── Public API ────────────────────────────────────────────────────────────────

extern "C" {

OWNAUDIO_ML_API int ownaudio_ml_load_basic_pitch_model(const char* path)
{
    if (!path || path[0] == '\0') return -1;

#ifdef HAVE_ONNXRUNTIME
    if (!g_ort || !g_env) return -2;

    if (g_bp_session) {
        g_ort->ReleaseSession(g_bp_session);
        g_bp_session = nullptr;
    }
    if (!g_bp_opts) {
        OrtStatus* s = g_ort->CreateSessionOptions(&g_bp_opts);
        if (s) { g_ort->ReleaseStatus(s); return -3; }
        g_ort->SetSessionGraphOptimizationLevel(g_bp_opts, ORT_ENABLE_ALL);
    }

    OrtStatus* s = g_ort->CreateSession(g_env, path, g_bp_opts, &g_bp_session);
    if (s) {
        g_ort->ReleaseStatus(s);
        g_bp_session = nullptr;
        return -4;
    }
    g_basic_pitch_loaded = true;
    return 0;
#else
    (void)path;
    return -99; /* built without ONNX Runtime */
#endif
}

OWNAUDIO_ML_API int ownaudio_ml_predict_notes(
    const float*               input,
    int                        sample_count,
    int                        sample_rate,
    OwnAudioMlNotesPrediction* result)
{
    if (!input || sample_count <= 0 || !result) return -1;

    result->contours    = nullptr;
    result->notes       = nullptr;
    result->onsets      = nullptr;
    result->frame_count = 0;
    result->freq_bins   = BP_N_FREQ_BINS_CONTOURS;

#ifdef HAVE_ONNXRUNTIME
    if (!g_bp_session) return -2;

    /* Resample to 22050 Hz if needed (simple linear for now) */
    const float* audio = input;
    float* resampled   = nullptr;
    int n_samples      = sample_count;

    if (sample_rate != BP_SAMPLE_RATE && sample_rate > 0) {
        double ratio    = (double)BP_SAMPLE_RATE / sample_rate;
        int out_len     = (int)(sample_count * ratio);
        resampled       = (float*)malloc(out_len * sizeof(float));
        if (!resampled) return -3;
        for (int i = 0; i < out_len; ++i) {
            double src  = i / ratio;
            int    lo   = (int)src;
            double frac = src - lo;
            int    hi   = lo + 1 < sample_count ? lo + 1 : lo;
            resampled[i] = (float)(input[lo] * (1.0 - frac) + input[hi] * frac);
        }
        audio    = resampled;
        n_samples = out_len;
    }

    /* Pad to multiple of HOP_SIZE */
    int total_windows = (n_samples + BP_HOP_SIZE - 1) / BP_HOP_SIZE;
    int padded_len    = total_windows * BP_HOP_SIZE + BP_OVERLAP_LEN * 2;
    float* padded     = (float*)calloc(padded_len, sizeof(float));
    if (!padded) { free(resampled); return -3; }
    memcpy(padded + BP_OVERLAP_LEN, audio, n_samples * sizeof(float));

    free(resampled);

    /* Collect per-window outputs */
    int total_frames = 0;
    int n_windows    = 0;
    /* Count total output frames */
    for (int w = 0; w < total_windows; ++w) {
        total_frames += BP_ANNOT_N_FRAMES;
        ++n_windows;
    }

    size_t contour_stride = (size_t)BP_N_FREQ_BINS_CONTOURS;
    size_t note_stride    = (size_t)BP_N_SEMITONES;
    float* out_contours   = (float*)calloc((size_t)total_frames * contour_stride, sizeof(float));
    float* out_notes      = (float*)calloc((size_t)total_frames * note_stride,    sizeof(float));
    float* out_onsets     = (float*)calloc((size_t)total_frames * note_stride,    sizeof(float));
    if (!out_contours || !out_notes || !out_onsets) {
        free(padded); free(out_contours); free(out_notes); free(out_onsets);
        return -3;
    }

    OrtMemoryInfo* mem_info = nullptr;
    g_ort->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &mem_info);

    int frame_offset = 0;
    for (int w = 0; w < n_windows; ++w) {
        const float* win_ptr = padded + w * BP_HOP_SIZE;
        int          win_len = BP_AUDIO_N_SAMPLES + BP_OVERLAP_LEN;

        int64_t in_shape[3] = {1, 1, win_len};
        OrtValue* in_tensor  = nullptr;
        OrtStatus* s = g_ort->CreateTensorWithDataAsOrtValue(
            mem_info, (void*)win_ptr, (size_t)win_len * sizeof(float),
            in_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &in_tensor);
        if (s) { g_ort->ReleaseStatus(s); break; }

        const char* in_names[]  = {"serving_default_input_2:0"};
        const char* out_names[] = {
            "StatefulPartitionedCall:1",  /* contours */
            "StatefulPartitionedCall:2",  /* notes    */
            "StatefulPartitionedCall:0"   /* onsets   */
        };
        OrtValue* out_tensors[3] = {nullptr, nullptr, nullptr};

        s = g_ort->Run(g_bp_session, nullptr,
                       in_names, (const OrtValue**)&in_tensor, 1,
                       out_names, 3, out_tensors);
        g_ort->ReleaseValue(in_tensor);
        if (s) { g_ort->ReleaseStatus(s); break; }

        /* Copy middle BP_ANNOT_N_FRAMES rows (trim overlap) */
        auto copy_tensor = [&](OrtValue* tv, float* dst, size_t stride) {
            float* data = nullptr;
            g_ort->GetTensorMutableData(tv, (void**)&data);
            int start_row = BP_N_OVERLAPPING_FRAMES;
            int end_row   = start_row + BP_ANNOT_N_FRAMES;
            size_t bytes  = (size_t)BP_ANNOT_N_FRAMES * stride * sizeof(float);
            memcpy(dst + (size_t)frame_offset * stride,
                   data + (size_t)start_row * stride, bytes);
            g_ort->ReleaseValue(tv);
        };

        copy_tensor(out_tensors[0], out_contours, contour_stride);
        copy_tensor(out_tensors[1], out_notes,    note_stride);
        copy_tensor(out_tensors[2], out_onsets,   note_stride);
        frame_offset += BP_ANNOT_N_FRAMES;
    }

    g_ort->ReleaseMemoryInfo(mem_info);
    free(padded);

    result->contours    = out_contours;
    result->notes       = out_notes;
    result->onsets      = out_onsets;
    result->frame_count = frame_offset;
    result->freq_bins   = (int)contour_stride;
    return 0;
#else
    (void)sample_rate;
    /* Stub: return empty tensors */
    int frames          = sample_count / BP_FFT_HOP + 1;
    result->frame_count = frames;
    result->freq_bins   = BP_N_FREQ_BINS_CONTOURS;
    result->contours    = (float*)calloc((size_t)frames * BP_N_FREQ_BINS_CONTOURS, sizeof(float));
    result->notes       = (float*)calloc((size_t)frames * BP_N_SEMITONES,          sizeof(float));
    result->onsets      = (float*)calloc((size_t)frames * BP_N_SEMITONES,          sizeof(float));
    return (result->contours && result->notes && result->onsets) ? 0 : -3;
#endif
}

OWNAUDIO_ML_API void ownaudio_ml_free_notes_result(OwnAudioMlNotesPrediction* result)
{
    free_notes_internal(result);
}

} /* extern "C" */
