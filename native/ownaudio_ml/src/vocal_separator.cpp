/**
 * vocal_separator.cpp – HTDemucs vocal separation via ONNX Runtime C API.
 *
 * TODO: Replace stubs with real inference using the htdemucs.onnx session.
 */

#include "../ownaudio_ml.h"
#include <cstdlib>
#include <cstring>

extern "C" {

OWNAUDIO_ML_API int ownaudio_ml_separate_vocals(
    const float*                input,
    int                         sample_count,
    int                         sample_rate,
    OwnAudioMlSeparationResult* result)
{
    (void)sample_rate;
    if (!input || sample_count <= 0 || !result) return -1;

    // TODO: run HTDemucs ONNX session
    result->sample_count = sample_count;
    result->vocals       = (float*)calloc((size_t)sample_count, sizeof(float));
    result->instrumental = (float*)calloc((size_t)sample_count, sizeof(float));

    if (!result->vocals || !result->instrumental)
    {
        free(result->vocals);
        free(result->instrumental);
        return -2; // out of memory
    }

    memcpy(result->vocals,       input, (size_t)sample_count * sizeof(float));
    memcpy(result->instrumental, input, (size_t)sample_count * sizeof(float));

    return 0;
}

OWNAUDIO_ML_API void ownaudio_ml_free_separation_result(OwnAudioMlSeparationResult* result)
{
    if (!result) return;
    free(result->vocals);
    free(result->instrumental);
    result->vocals       = nullptr;
    result->instrumental = nullptr;
    result->sample_count = 0;
}

} // extern "C"
