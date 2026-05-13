/**
 * mdx_separator.cpp – MDX vocal/instrumental separation via ONNX Runtime C API.
 *
 * Pipeline (mirrors AudioSeparatorMultimodel.cs):
 *   1. De-interleave stereo input
 *   2. Zero-pad audio by trim = nFft/2 on each side, right-pad to multiple of genSize
 *   3. Split into sub-chunks of ChunkSize = hop*(dimT-1) with stride genSize
 *   4. For each sub-chunk:
 *      a. Reflection-pad with nFft/2 and apply STFT → tensor [1, 4, dimF, dimT]
 *      b. Noise reduction: run model twice (normal + negated input), average
 *      c. ISTFT → [2, ChunkSize] waveform
 *      d. Extract valid middle genSize samples (trim the nFft/2 border)
 *   5. Compute complement: if output = Instrumental → vocals = original - instr
 *                          if output = Vocals       → instr  = original - vocals
 *   6. Multi-model support: average vocals/instr across all requested sessions
 *   7. Re-interleave and return
 *
 * Model parameters (auto-detected from ONNX input shape):
 *   dimF = input_shape[2], dimT = input_shape[3]
 *   nFft = 6144 (standard MDX default), hop = 1024
 *
 * Without OWNAUDIO_ML_HAS_ONNXRUNTIME: all load calls are no-ops.
 */

#include "../ownaudio_ml.h"
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <vector>
#include <complex>
#include <algorithm>

#ifndef M_PI
#  define M_PI 3.14159265358979323846
#endif

static constexpr int kMdxDefaultNFft = 6144;
static constexpr int kMdxDefaultHop  = 1024;
static constexpr int kMdxDefaultDimF = 2048;
static constexpr int kMdxDefaultDimT = 256;   // 2^8
static constexpr int kMdxMaxSessions = 8;

// ─────────────────────────────────────────────────────────────────────────────
#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
#  include <onnxruntime_c_api.h>
#  ifdef _WIN32
#    include <windows.h>
#  endif

// Shared ORT environment — defined in vocal_separator.cpp
extern const OrtApi* g_api;
extern OrtEnv*       g_env;

// ── Session record ────────────────────────────────────────────────────────────
struct MdxSession {
    char        name[64];
    OrtSession* session;
    int         dim_f;
    int         dim_t;
    int         n_fft;
    int         hop;
    bool        output_is_vocals;
    bool        loaded;
};

static MdxSession g_mdx_sessions[kMdxMaxSessions] = {};
static int        g_mdx_n = 0;

// ── Cooley-Tukey radix-2 DIT FFT ─────────────────────────────────────────────
static void mdx_fft(std::complex<float>* x, int n, bool inv)
{
    for (int i = 1, j = 0; i < n; ++i) {
        int bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(x[i], x[j]);
    }
    float sign = inv ? +1.0f : -1.0f;
    for (int len = 2; len <= n; len <<= 1) {
        float ang = sign * 2.0f * (float)M_PI / (float)len;
        std::complex<float> wlen(std::cos(ang), std::sin(ang));
        for (int i = 0; i < n; i += len) {
            std::complex<float> w(1.0f, 0.0f);
            for (int j = 0; j < len / 2; ++j) {
                auto u = x[i + j];
                auto v = x[i + j + len / 2] * w;
                x[i + j]           = u + v;
                x[i + j + len / 2] = u - v;
                w *= wlen;
            }
        }
    }
    if (inv)
        for (int i = 0; i < n; ++i) x[i] /= (float)n;
}

static void mdx_make_hann(std::vector<float>& w, int n)
{
    w.resize(n);
    for (int i = 0; i < n; ++i)
        w[i] = 0.5f * (1.0f - std::cos(2.0f * (float)M_PI * i / (float)n));
}

// ── STFT ──────────────────────────────────────────────────────────────────────
// tensor [4, dim_f, dim_t]: L_real, L_imag, R_real, R_imag
static void mdx_stft(
    const float* L, const float* R, int chunk_size,
    float* tensor,
    int n_fft, int hop, int dim_f, int dim_t,
    const std::vector<float>& hann)
{
    int pad = n_fft / 2;
    int padded_len = chunk_size + 2 * pad;

    std::vector<float> padded(padded_len);
    std::vector<std::complex<float>> frame(n_fft);

    for (int ch = 0; ch < 2; ++ch) {
        const float* sig = (ch == 0) ? L : R;

        // Reflection padding
        for (int i = 0; i < pad; ++i)
            padded[i] = sig[std::min(pad - 1 - i, chunk_size - 1)];
        for (int i = 0; i < chunk_size; ++i)
            padded[pad + i] = sig[i];
        for (int i = 0; i < pad; ++i)
            padded[pad + chunk_size + i] = sig[std::max(0, chunk_size - 1 - i)];

        int base_r = (ch * 2)     * dim_f * dim_t;
        int base_i = (ch * 2 + 1) * dim_f * dim_t;

        for (int t = 0; t < dim_t; ++t) {
            int fs = t * hop;
            for (int i = 0; i < n_fft; ++i)
                frame[i] = std::complex<float>(padded[fs + i] * hann[i], 0.0f);

            mdx_fft(frame.data(), n_fft, false);

            for (int f = 0; f < dim_f; ++f) {
                tensor[base_r + f * dim_t + t] = frame[f].real();
                tensor[base_i + f * dim_t + t] = frame[f].imag();
            }
        }
    }
}

// ── ISTFT ─────────────────────────────────────────────────────────────────────
// waves [2, chunk_size]: channel-major
static void mdx_istft(
    const float* tensor,
    float* waves, int chunk_size,
    int n_fft, int hop, int dim_f, int dim_t,
    const std::vector<float>& hann)
{
    int total = (dim_t - 1) * hop + n_fft;
    int pad   = n_fft / 2;

    std::vector<float>               recon(total, 0.0f);
    std::vector<float>               wsum (total, 0.0f);
    std::vector<std::complex<float>> frame(n_fft);

    for (int ch = 0; ch < 2; ++ch) {
        std::fill(recon.begin(), recon.end(), 0.0f);
        std::fill(wsum .begin(), wsum .end(), 0.0f);

        int base_r = (ch * 2)     * dim_f * dim_t;
        int base_i = (ch * 2 + 1) * dim_f * dim_t;

        for (int t = 0; t < dim_t; ++t) {
            std::fill(frame.begin(), frame.end(), std::complex<float>(0.0f, 0.0f));
            for (int f = 0; f < dim_f; ++f)
                frame[f] = std::complex<float>(tensor[base_r + f * dim_t + t],
                                               tensor[base_i + f * dim_t + t]);

            // Hermitian symmetry
            for (int f = 1; f < n_fft / 2; ++f)
                frame[n_fft - f] = std::conj(frame[f]);

            mdx_fft(frame.data(), n_fft, true);

            int fs = t * hop;
            for (int i = 0; i < n_fft; ++i) {
                float w = hann[i];
                recon[fs + i] += frame[i].real() * w;
                wsum [fs + i] += w * w;
            }
        }

        float* out = waves + ch * chunk_size;
        for (int i = 0; i < chunk_size; ++i) {
            int src = i + pad;
            out[i] = (wsum[src] > 1e-10f) ? (recon[src] / wsum[src]) : recon[src];
        }
    }
}

// ── Single ONNX pass ──────────────────────────────────────────────────────────
static int mdx_run_onnx(
    OrtSession* session,
    const float* stft_in, float* stft_out,
    int64_t dim_f, int64_t dim_t)
{
    const OrtMemoryInfo* mem_info = nullptr;
    g_api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault,
                                const_cast<OrtMemoryInfo**>(&mem_info));

    size_t n = (size_t)(4 * dim_f * dim_t);
    int64_t shape[4] = {1, 4, dim_f, dim_t};

    OrtValue* in_val = nullptr;
    g_api->CreateTensorWithDataAsOrtValue(
        mem_info,
        const_cast<float*>(stft_in),
        n * sizeof(float),
        shape, 4,
        ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT,
        &in_val);

    const char* input_names[]  = {"input"};
    const char* output_names[] = {"output"};
    OrtValue* out_val = nullptr;
    OrtStatus* st = g_api->Run(session, nullptr,
                                input_names, (const OrtValue* const*)&in_val, 1,
                                output_names, 1, &out_val);
    int rc = 0;
    if (st) {
        g_api->ReleaseStatus(st);
        rc = -10;
    } else {
        float* p = nullptr;
        g_api->GetTensorMutableData(out_val, (void**)&p);
        memcpy(stft_out, p, n * sizeof(float));
    }

    if (out_val) g_api->ReleaseOrtValue(out_val);
    if (in_val)  g_api->ReleaseOrtValue(in_val);
    g_api->ReleaseMemoryInfo(const_cast<OrtMemoryInfo*>(mem_info));
    return rc;
}

// Noise-reduction: (model(x) - model(-x)) / 2
static int mdx_run_with_denoise(
    OrtSession* session,
    const float* stft_in, float* stft_out,
    int64_t dim_f, int64_t dim_t)
{
    size_t n = (size_t)(4 * dim_f * dim_t);

    std::vector<float> pos(n), neg_in(n), neg_out(n);

    int rc = mdx_run_onnx(session, stft_in, pos.data(), dim_f, dim_t);
    if (rc != 0) return rc;

    for (size_t i = 0; i < n; ++i) neg_in[i] = -stft_in[i];

    rc = mdx_run_onnx(session, neg_in.data(), neg_out.data(), dim_f, dim_t);
    if (rc != 0) return rc;

    for (size_t i = 0; i < n; ++i)
        stft_out[i] = 0.5f * pos[i] - 0.5f * neg_out[i];

    return 0;
}

// ── Single-session separation ─────────────────────────────────────────────────
static int mdx_separate_session(
    const float* input, int sample_count,
    const MdxSession& sess,
    std::vector<float>& out_vocals,
    std::vector<float>& out_instr)
{
    if (!sess.loaded || !sess.session) return -1;

    int mono_n     = sample_count / 2;
    int n_fft      = sess.n_fft;
    int hop        = sess.hop;
    int dim_f      = sess.dim_f;
    int dim_t      = sess.dim_t;
    int trim       = n_fft / 2;
    int chunk_size = hop * (dim_t - 1);
    int gen_size   = chunk_size - 2 * trim;
    if (gen_size <= 0) return -2;

    // De-interleave
    std::vector<float> L(mono_n), R(mono_n);
    for (int i = 0; i < mono_n; ++i) { L[i] = input[i*2]; R[i] = input[i*2+1]; }

    // Zero-pad: [trim zeros] + audio + [pad_right zeros] + [trim zeros]
    int n_pad     = (gen_size - (mono_n % gen_size)) % gen_size;
    int n_batches = (mono_n + n_pad) / gen_size;
    int plen      = trim + mono_n + n_pad + trim;

    std::vector<float> padL(plen, 0.0f), padR(plen, 0.0f);
    for (int i = 0; i < mono_n; ++i) { padL[trim+i] = L[i]; padR[trim+i] = R[i]; }

    std::vector<float> hann;
    mdx_make_hann(hann, n_fft);

    size_t n_spec = (size_t)(4 * dim_f * dim_t);
    std::vector<float> stft_buf(n_spec), stft_out(n_spec), waves(2 * chunk_size);

    std::vector<float> stem_L(mono_n, 0.0f), stem_R(mono_n, 0.0f);

    for (int bi = 0; bi < n_batches; ++bi) {
        int offset = bi * gen_size;

        std::vector<float> chL(chunk_size, 0.0f), chR(chunk_size, 0.0f);
        for (int i = 0; i < chunk_size; ++i) {
            if (offset + i < plen) { chL[i] = padL[offset+i]; chR[i] = padR[offset+i]; }
        }

        mdx_stft(chL.data(), chR.data(), chunk_size,
                 stft_buf.data(), n_fft, hop, dim_f, dim_t, hann);

        int rc = mdx_run_with_denoise(sess.session, stft_buf.data(), stft_out.data(),
                                      (int64_t)dim_f, (int64_t)dim_t);
        if (rc != 0) return rc;

        mdx_istft(stft_out.data(), waves.data(), chunk_size,
                  n_fft, hop, dim_f, dim_t, hann);

        int dest = bi * gen_size;
        for (int j = 0; j < gen_size && dest + j < mono_n; ++j) {
            stem_L[dest+j] = waves[0 * chunk_size + trim + j];
            stem_R[dest+j] = waves[1 * chunk_size + trim + j];
        }
    }

    // Complement
    std::vector<float> voc_L(mono_n), voc_R(mono_n);
    std::vector<float> ins_L(mono_n), ins_R(mono_n);

    if (sess.output_is_vocals) {
        for (int i = 0; i < mono_n; ++i) {
            voc_L[i] = stem_L[i];      voc_R[i] = stem_R[i];
            ins_L[i] = L[i]-stem_L[i]; ins_R[i] = R[i]-stem_R[i];
        }
    } else {
        for (int i = 0; i < mono_n; ++i) {
            ins_L[i] = stem_L[i];      ins_R[i] = stem_R[i];
            voc_L[i] = L[i]-stem_L[i]; voc_R[i] = R[i]-stem_R[i];
        }
    }

    // Re-interleave
    out_vocals.resize(sample_count);
    out_instr .resize(sample_count);
    for (int i = 0; i < mono_n; ++i) {
        out_vocals[i*2]   = voc_L[i]; out_vocals[i*2+1] = voc_R[i];
        out_instr [i*2]   = ins_L[i]; out_instr [i*2+1] = ins_R[i];
    }
    return 0;
}

// ── Dimension auto-detect ─────────────────────────────────────────────────────
static void mdx_detect_dims(OrtSession* session, int& dim_f, int& dim_t)
{
    OrtTypeInfo* ti = nullptr;
    g_api->SessionGetInputTypeInfo(session, 0, &ti);
    const OrtTensorTypeAndShapeInfo* si = nullptr;
    g_api->CastTypeInfoToTensorInfo(ti, &si);
    size_t nd = 0;
    g_api->GetDimensionsCount(si, &nd);
    if (nd >= 4) {
        int64_t dims[4] = {};
        g_api->GetDimensions(si, dims, nd);
        if (dims[2] > 0) dim_f = (int)dims[2];
        if (dims[3] > 0) dim_t = (int)dims[3];
    }
    g_api->ReleaseTypeInfo(ti);
}

// ── Output type auto-detect ───────────────────────────────────────────────────
static bool mdx_detect_vocals_output(OrtSession* session, const char* model_name)
{
    OrtAllocator* alloc = nullptr;
    g_api->GetAllocatorWithDefaultOptions(&alloc);
    size_t n_out = 0;
    g_api->SessionGetOutputCount(session, &n_out);
    for (size_t i = 0; i < n_out; ++i) {
        char* nm = nullptr;
        g_api->SessionGetOutputName(session, i, alloc, &nm);
        if (nm) {
            std::string s(nm);
            alloc->Free(alloc, nm);
            for (auto& c : s) c = (char)tolower((unsigned char)c);
            if (s.find("vocal") != std::string::npos || s.find("voice") != std::string::npos)
                return true;
            if (s.find("instr")   != std::string::npos ||
                s.find("karaoke") != std::string::npos ||
                s.find("music")   != std::string::npos)
                return false;
        }
    }
    // Fallback: model name
    std::string mn(model_name);
    for (auto& c : mn) c = (char)tolower((unsigned char)c);
    return mn.find("vocal") != std::string::npos || mn.find("voice") != std::string::npos;
}

// ── Public hooks (called from ownaudio_ml.cpp) ────────────────────────────────

void mdx_try_load(const char* model_name, const char* path)
{
    if (!g_api || !g_env || !model_name || !path) return;

    MdxSession* slot = nullptr;
    for (int i = 0; i < g_mdx_n; ++i)
        if (strcmp(g_mdx_sessions[i].name, model_name) == 0) { slot = &g_mdx_sessions[i]; break; }

    if (!slot) {
        if (g_mdx_n >= kMdxMaxSessions) return;
        slot = &g_mdx_sessions[g_mdx_n++];
        memset(slot, 0, sizeof(*slot));
        strncpy(slot->name, model_name, sizeof(slot->name) - 1);
    }

    if (slot->session) { g_api->ReleaseSession(slot->session); slot->session = nullptr; }
    slot->loaded = false;

    OrtSessionOptions* opts = nullptr;
    g_api->CreateSessionOptions(&opts);
    g_api->SetSessionGraphOptimizationLevel(opts, ORT_ENABLE_ALL);

    OrtStatus* st = nullptr;
#ifdef _WIN32
    int wlen = MultiByteToWideChar(CP_UTF8, 0, path, -1, nullptr, 0);
    std::vector<wchar_t> wpath(wlen);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath.data(), wlen);
    st = g_api->CreateSession(g_env, wpath.data(), opts, &slot->session);
#else
    st = g_api->CreateSession(g_env, path, opts, &slot->session);
#endif
    g_api->ReleaseSessionOptions(opts);

    if (st) { g_api->ReleaseStatus(st); return; }

    slot->dim_f = kMdxDefaultDimF;
    slot->dim_t = kMdxDefaultDimT;
    mdx_detect_dims(slot->session, slot->dim_f, slot->dim_t);
    slot->n_fft = kMdxDefaultNFft;
    slot->hop   = kMdxDefaultHop;
    slot->output_is_vocals = mdx_detect_vocals_output(slot->session, model_name);
    slot->loaded = true;
}

bool mdx_is_loaded(const char* model_name)
{
    for (int i = 0; i < g_mdx_n; ++i)
        if (strcmp(g_mdx_sessions[i].name, model_name) == 0)
            return g_mdx_sessions[i].loaded;
    return false;
}

void mdx_shutdown()
{
    for (int i = 0; i < g_mdx_n; ++i)
        if (g_mdx_sessions[i].session) g_api->ReleaseSession(g_mdx_sessions[i].session);
    memset(g_mdx_sessions, 0, sizeof(g_mdx_sessions));
    g_mdx_n = 0;
}

// ── C API ─────────────────────────────────────────────────────────────────────

// model_names: comma-separated, e.g. "best" or "best,default"
// When multiple models are listed their outputs are averaged.
extern "C" OWNAUDIO_ML_API int ownaudio_ml_separate_mdx(
    const float*                input,
    int                         sample_count,
    int                         /*sample_rate*/,
    const char*                 model_names,
    OwnAudioMlSeparationResult* result)
{
    if (!input || !result || !model_names) return -1;

    std::vector<std::string> names;
    for (std::string s(model_names); !s.empty(); ) {
        size_t c = s.find(',');
        std::string tok = (c == std::string::npos) ? s : s.substr(0, c);
        if (!tok.empty()) names.push_back(tok);
        if (c == std::string::npos) break;
        s = s.substr(c + 1);
    }
    if (names.empty()) return -1;

    std::vector<float> sum_v(sample_count, 0.0f), sum_i(sample_count, 0.0f);
    int n_valid = 0;

    for (const auto& nm : names) {
        for (int i = 0; i < g_mdx_n; ++i) {
            if (!g_mdx_sessions[i].loaded) continue;
            if (strcmp(g_mdx_sessions[i].name, nm.c_str()) != 0) continue;

            std::vector<float> voc, ins;
            if (mdx_separate_session(input, sample_count, g_mdx_sessions[i], voc, ins) == 0) {
                for (int k = 0; k < sample_count; ++k) { sum_v[k] += voc[k]; sum_i[k] += ins[k]; }
                ++n_valid;
            }
            break;
        }
    }

    if (n_valid == 0) return -2;

    float* ov = (float*)malloc(sample_count * sizeof(float));
    float* oi = (float*)malloc(sample_count * sizeof(float));
    if (!ov || !oi) { free(ov); free(oi); return -3; }

    float inv = 1.0f / (float)n_valid;
    for (int i = 0; i < sample_count; ++i) { ov[i] = sum_v[i]*inv; oi[i] = sum_i[i]*inv; }

    result->vocals       = ov;
    result->instrumental = oi;
    result->sample_count = sample_count;
    return 0;
}

#else  // !OWNAUDIO_ML_HAS_ONNXRUNTIME

void mdx_try_load(const char*, const char*) {}
bool mdx_is_loaded(const char*) { return false; }
void mdx_shutdown() {}

extern "C" OWNAUDIO_ML_API int ownaudio_ml_separate_mdx(
    const float* input, int sample_count, int,
    const char*, OwnAudioMlSeparationResult* result)
{
    if (!input || !result) return -1;
    float* v = (float*)malloc(sample_count * sizeof(float));
    float* s = (float*)calloc(sample_count, sizeof(float));
    if (!v || !s) { free(v); free(s); return -3; }
    memcpy(v, input, sample_count * sizeof(float));
    result->vocals = v; result->instrumental = s; result->sample_count = sample_count;
    return 0;
}

#endif  // OWNAUDIO_ML_HAS_ONNXRUNTIME
