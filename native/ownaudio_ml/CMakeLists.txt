cmake_minimum_required(VERSION 3.20)
project(ownaudio_ml VERSION 3.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# ── ONNX Runtime (optional) ───────────────────────────────────────────────────
# Set ONNXRUNTIME_ROOT to enable full ML inference:
#   cmake -DONNXRUNTIME_ROOT=/path/to/onnxruntime ..
# When not set the library builds as a stub (all functions return 0 / empty results).
# CI builds use stub mode; pass DONNXRUNTIME_ROOT for production builds.
option(OWNAUDIO_ML_ENABLE_ONNX "Link against ONNX Runtime for real ML inference" OFF)

if(DEFINED ONNXRUNTIME_ROOT)
    set(OWNAUDIO_ML_ENABLE_ONNX ON)
endif()

if(OWNAUDIO_ML_ENABLE_ONNX)
    find_library(ONNXRUNTIME_LIB
        NAMES onnxruntime
        PATHS "${ONNXRUNTIME_ROOT}/lib"
        NO_DEFAULT_PATH
        REQUIRED)
    message(STATUS "ONNX Runtime: ${ONNXRUNTIME_LIB}")
else()
    message(STATUS "ONNX Runtime: not linked – building stub library")
endif()

# ── Sources ───────────────────────────────────────────────────────────────────
add_library(ownaudio_ml SHARED
    src/ownaudio_ml.cpp
    src/vocal_separator.cpp
    src/chord_detector.cpp
    src/audio_analyzer.cpp
)

target_include_directories(ownaudio_ml
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}"
)

target_compile_definitions(ownaudio_ml PRIVATE OWNAUDIO_ML_BUILD)

# MSVC does not define M_PI unless _USE_MATH_DEFINES is set before <cmath>
if(WIN32)
    target_compile_definitions(ownaudio_ml PRIVATE _USE_MATH_DEFINES)
endif()

if(OWNAUDIO_ML_ENABLE_ONNX)
    target_include_directories(ownaudio_ml PRIVATE "${ONNXRUNTIME_ROOT}/include")
    target_link_libraries(ownaudio_ml PRIVATE ${ONNXRUNTIME_LIB})
    target_compile_definitions(ownaudio_ml PRIVATE OWNAUDIO_ML_HAS_ONNXRUNTIME)
endif()

# ── Platform install paths ────────────────────────────────────────────────────
if(WIN32)
    set(RUNTIME_DEST "${CMAKE_SOURCE_DIR}/../../OwnAudio/ML/runtimes/win-x64/native")
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR STREQUAL "arm64")
        set(RUNTIME_DEST "${CMAKE_SOURCE_DIR}/../../OwnAudio/ML/runtimes/osx-arm64/native")
    else()
        set(RUNTIME_DEST "${CMAKE_SOURCE_DIR}/../../OwnAudio/ML/runtimes/osx-x64/native")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR STREQUAL "aarch64")
        set(RUNTIME_DEST "${CMAKE_SOURCE_DIR}/../../OwnAudio/ML/runtimes/linux-arm64/native")
    else()
        set(RUNTIME_DEST "${CMAKE_SOURCE_DIR}/../../OwnAudio/ML/runtimes/linux-x64/native")
    endif()
endif()

install(TARGETS ownaudio_ml DESTINATION "${RUNTIME_DEST}")
