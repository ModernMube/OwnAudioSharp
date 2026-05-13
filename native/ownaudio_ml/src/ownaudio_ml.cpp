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

// Defined in vocal_separator.cpp
extern void vocal_separator_try_load(const char* model_path);
extern void vocal_separator_shutdown_ort();
extern bool vocal_separator_is_loaded();

// Defined in mdx_separator.cpp
extern void mdx_try_load(const char* model_name, const char* path);
extern bool mdx_is_loaded(const char* model_name);
extern void mdx_shutdown();
#endif

static bool g_initialized           = false;
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
    vocal_separator_shutdown_ort();
    mdx_shutdown();
#endif
    g_initialized        = false;
    g_model_directory[0] = '\0';
}

// MDX model names: "best", "default", "karaoke" and any user-defined names.
// HTDemucs model name: "htdemucs".
static bool is_mdx_model(const char* model_name)
{
    return strcmp(model_name, "htdemucs") != 0;
}

OWNAUDIO_ML_API int ownaudio_ml_load_model(const char* model_name, const char* path)
{
    if (!g_initialized || !model_name || !path) return -1;

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
    if (strcmp(model_name, "htdemucs") == 0) {
        vocal_separator_try_load(path);
        return vocal_separator_is_loaded() ? 0 : -2;
    }
    // All other names → MDX pipeline
    mdx_try_load(model_name, path);
    return mdx_is_loaded(model_name) ? 0 : -2;
#else
    return 0;
#endif
}

OWNAUDIO_ML_API int ownaudio_ml_is_model_loaded(const char* model_name)
{
    if (!g_initialized || !model_name) return -1;

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
    if (strcmp(model_name, "htdemucs") == 0)
        return vocal_separator_is_loaded() ? 1 : 0;
    return mdx_is_loaded(model_name) ? 1 : 0;
#else
    return 0;
#endif
}

}  // extern "C"
