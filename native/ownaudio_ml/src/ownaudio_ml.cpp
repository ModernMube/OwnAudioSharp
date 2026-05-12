/**
 * ownaudio_ml.cpp – Lifecycle: init / shutdown / model management.
 */

#include "../ownaudio_ml.h"
#include <cstdlib>
#include <cstring>

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
// Defined in chord_detector.cpp
extern void chord_detector_try_init(const char* model_directory);
extern void chord_detector_shutdown_ort();
#endif

static bool g_initialized          = false;
static char g_model_directory[4096] = {};

extern "C" {

OWNAUDIO_ML_API int ownaudio_ml_init(const char* model_directory)
{
    if (!model_directory) return -1;
    strncpy(g_model_directory, model_directory, sizeof(g_model_directory) - 1);
    g_initialized = true;

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
    chord_detector_try_init(g_model_directory);
#endif
    return 0;
}

OWNAUDIO_ML_API void ownaudio_ml_shutdown(void)
{
#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
    chord_detector_shutdown_ort();
#endif
    g_initialized       = false;
    g_model_directory[0] = '\0';
}

OWNAUDIO_ML_API int ownaudio_ml_load_model(const char* model_name, const char* path)
{
    if (!g_initialized || !model_name || !path) return -1;
    // TODO: vocal separator ONNX session (htdemucs)
    return 0;
}

OWNAUDIO_ML_API int ownaudio_ml_is_model_loaded(const char* model_name)
{
    if (!g_initialized || !model_name) return -1;
    return 0;
}

}  // extern "C"
