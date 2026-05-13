/**
 * ownaudio_ml.h – Public C API for the ownaudio_ml native shared library.
 *
 * The library wraps ONNX Runtime via its stable C API so the managed C# layer
 * can be fully AOT-compatible.  P/Invoke bindings are in OwnAudio.ML/Interop/NativeMl.cs.
 *
 * All functions return 0 on success and a negative error code on failure.
 */

#pragma once

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(_WIN64)
#   ifdef OWNAUDIO_ML_BUILD
#       define OWNAUDIO_ML_API __declspec(dllexport)
#   else
#       define OWNAUDIO_ML_API __declspec(dllimport)
#   endif
#else
#   define OWNAUDIO_ML_API __attribute__((visibility("default")))
#endif

/* ── Lifecycle ────────────────────────────────────────────────────────────── */

/**
 * Initialise the ML runtime.  Must be called before any inference function.
 * @param model_directory  Path to the directory containing .onnx model files.
 * @return 0 on success, negative error code on failure.
 */
OWNAUDIO_ML_API int ownaudio_ml_init(const char* model_directory);

/**
 * Shut down the ML runtime and release all native resources.
 */
OWNAUDIO_ML_API void ownaudio_ml_shutdown(void);

/* ── Vocal / Stem Separation ─────────────────────────────────────────────── */

typedef struct {
    float* vocals;          /* interleaved stereo vocals */
    float* instrumental;    /* interleaved stereo instrumental */
    int    sample_count;    /* total number of float values in each array */
} OwnAudioMlSeparationResult;

/**
 * Separate audio into vocals and instrumental stems using the HTDemucs model.
 * @param input        Stereo interleaved float samples.
 * @param sample_count Total number of float values in @p input.
 * @param sample_rate  Sample rate in Hz (typically 44100 or 48000).
 * @param result       Output struct; call ownaudio_ml_free_separation_result when done.
 */
OWNAUDIO_ML_API int ownaudio_ml_separate_vocals(
    const float*                  input,
    int                           sample_count,
    int                           sample_rate,
    OwnAudioMlSeparationResult*   result);

/**
 * Separate audio using one or more MDX models.
 * When multiple models are named their outputs are averaged (ensemble).
 * @param input        Stereo interleaved float samples.
 * @param sample_count Total number of float values in @p input.
 * @param sample_rate  Sample rate in Hz.
 * @param model_names  Comma-separated logical model names, e.g. "best" or "best,default".
 * @param result       Output struct; call ownaudio_ml_free_separation_result when done.
 */
OWNAUDIO_ML_API int ownaudio_ml_separate_mdx(
    const float*                  input,
    int                           sample_count,
    int                           sample_rate,
    const char*                   model_names,
    OwnAudioMlSeparationResult*   result);

/**
 * Free memory allocated by ownaudio_ml_separate_vocals or ownaudio_ml_separate_mdx.
 */
OWNAUDIO_ML_API void ownaudio_ml_free_separation_result(OwnAudioMlSeparationResult* result);

/* ── Chord Detection ─────────────────────────────────────────────────────── */

typedef struct {
    float start_time;       /* seconds */
    float end_time;         /* seconds */
    char  chord_name[32];   /* e.g. "Cmaj", "Am", "G7" – null-terminated */
    float confidence;       /* [0, 1] */
} OwnAudioMlChordResult;

/**
 * Detect chords in audio.
 * @param input        Float audio samples (mono or stereo interleaved).
 * @param sample_count Number of float values.
 * @param sample_rate  Sample rate in Hz.
 * @param results      Caller-allocated array of OwnAudioMlChordResult.
 * @param max_results  Capacity of @p results.
 * @param result_count Filled with the actual number of chords detected.
 */
OWNAUDIO_ML_API int ownaudio_ml_detect_chords(
    const float*          input,
    int                   sample_count,
    int                   sample_rate,
    OwnAudioMlChordResult* results,
    int                   max_results,
    int*                  result_count);

/* ── Audio Analysis (EQ Matching) ────────────────────────────────────────── */

typedef struct {
    float frequency_bands[30];  /* 30-band energy values */
    float rms_level;            /* dBFS */
    float peak_level;           /* dBFS */
    float loudness;             /* LUFS */
    float dynamic_range;        /* dB */
} OwnAudioMlSpectrum;

/**
 * Analyse the frequency spectrum of audio.
 */
OWNAUDIO_ML_API int ownaudio_ml_analyze_spectrum(
    const float*      input,
    int               sample_count,
    int               sample_rate,
    OwnAudioMlSpectrum* result);

/**
 * Calculate per-band EQ adjustments to match source spectrum to target.
 * @param eq_adjustments_out  Caller-allocated float array of size @p band_count (gain in dB).
 */
OWNAUDIO_ML_API int ownaudio_ml_calculate_eq_adjustments(
    const OwnAudioMlSpectrum* source,
    const OwnAudioMlSpectrum* target,
    float*                    eq_adjustments_out,
    int                       band_count);

/* ── BasicPitch Audio-to-MIDI Note Detection ─────────────────────────────── */

typedef struct {
    float* contours;   /* [frame_count × freq_bins] row-major */
    float* notes;      /* [frame_count × 88] row-major */
    float* onsets;     /* [frame_count × 88] row-major */
    int    frame_count;
    int    freq_bins;  /* = 264 (88 semitones × 3 bins per semitone) */
} OwnAudioMlNotesPrediction;

/**
 * Predict notes from audio using the BasicPitch model.
 * @param input        Mono float samples at @p sample_rate Hz.
 * @param sample_count Number of samples.
 * @param sample_rate  Sample rate in Hz (BasicPitch expects 22050 Hz).
 * @param result       Output struct; call ownaudio_ml_free_notes_result when done.
 */
OWNAUDIO_ML_API int ownaudio_ml_predict_notes(
    const float*               input,
    int                        sample_count,
    int                        sample_rate,
    OwnAudioMlNotesPrediction* result);

/**
 * Free memory allocated by ownaudio_ml_predict_notes.
 */
OWNAUDIO_ML_API void ownaudio_ml_free_notes_result(OwnAudioMlNotesPrediction* result);

/**
 * Load the BasicPitch ONNX model from disk.
 * @param path Absolute path to the basicpitch.onnx file.
 */
OWNAUDIO_ML_API int ownaudio_ml_load_basic_pitch_model(const char* path);

/* ── Model Management ────────────────────────────────────────────────────── */

/**
 * Load a specific model from disk (can be called after ownaudio_ml_init).
 * @param model_name  Logical name, e.g. "htdemucs", "nmp".
 * @param path        Absolute path to the .onnx file.
 */
OWNAUDIO_ML_API int ownaudio_ml_load_model(const char* model_name, const char* path);

/**
 * Check whether a model has been loaded.
 * @return 1 if loaded, 0 if not, negative on error.
 */
OWNAUDIO_ML_API int ownaudio_ml_is_model_loaded(const char* model_name);

#ifdef __cplusplus
} /* extern "C" */
#endif
