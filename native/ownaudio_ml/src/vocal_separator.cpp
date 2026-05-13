/**
 * vocal_separator.cpp – HTDemucs vocal/stem separation via ONNX Runtime C API.
 *
 * Pipeline (mirrors HTDemucsAudioSeparator.cs):
 *   1. De-interleave stereo input
 *   2. Chunk audio with margin-trimming and reflection padding
 *   3. STFT each chunk  (HTDemucs _spec(): pad = hop/2*3, trim z[2:2+le])
 *   4. Run HTDemucs:  inputs  = [waveform[1,2,N], spectrogram[1,2,2048,T,2]]
 *                    outputs = [freq_branch[1,4,4,F,T], time_branch[1,4,2,N]]
 *   5. ISTFT freq branch + add time branch (dual-branch merge)
 *   6. Cosine crossfade overlap-add with margin trimming
 *   7. Vocals = stem[3];  Instrumental = stem[0]+stem[1]+stem[2]
 *   8. Re-interleave output
 *
 * Without OWNAUDIO_ML_HAS_ONNXRUNTIME: identity stub (vocals = input).
 * With    OWNAUDIO_ML_HAS_ONNXRUNTIME: requires the model loaded via
 *   vocal_separator_try_load() (called from ownaudio_ml_load_model).
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

// ── HTDemucs model constants ──────────────────────────────────────────────────
static constexpr int    kNFft      = 4096;
static constexpr int    kHopLen    = 1024;
static constexpr int    kFreqBins  = kNFft / 2;        // 2048 positive-freq bins
static constexpr int    kPad       = kHopLen / 2 * 3;  // 1536 – reflection pad
static constexpr int    kStemCount = 4;                // [drums, bass, other, vocals]
static constexpr int    kVocIdx    = 3;                // vocals stem index
static constexpr int    kChans     = 2;                // stereo

// Default chunking parameters (scaled by sample_rate at runtime)
static constexpr double kChunkSec     = 10.0;
static constexpr double kMarginSec    = 1.0;
static constexpr double kCrossfadeSec = 0.5;

// ─────────────────────────────────────────────────────────────────────────────
#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
#  include <onnxruntime_c_api.h>
#  ifdef _WIN32
#    include <windows.h>
#  endif

// ── ORT globals (vocal separator only) ───────────────────────────────────────
static const OrtApi*  g_api     = nullptr;
static OrtEnv*        g_env     = nullptr;
static OrtSession*    g_session = nullptr;
static int            g_seg_len = 0;  // from model metadata; 0 = use default

// ── Cooley-Tukey radix-2 DIT FFT/IFFT ───────────────────────────────────────
static void fft_dit(std::complex<float>* x, int n, bool inv)
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
    if (inv) {
        float s = 1.0f / (float)n;
        for (int i = 0; i < n; ++i) x[i] *= s;
    }
}

// ── Hann window ───────────────────────────────────────────────────────────────
static void make_hann(std::vector<float>& w, int n)
{
    w.resize(n);
    for (int i = 0; i < n; ++i)
        w[i] = 0.5f * (1.0f - std::cos(2.0f * (float)M_PI * i / n));
}

// ── STFT ─────────────────────────────────────────────────────────────────────
// Mirrors HTDemucs _spec():
//   pad       = kHopLen/2*3
//   le        = ceil(n_samples / kHopLen)
//   right_pad = pad + le*kHopLen - n_samples
//   raw frames = le+4;  trim → z[2 : 2+le]
//
// out_ri: flat [kFreqBins * le * 2]  (f, t, ri – row-major)
// n_frames set to le.
static void compute_stft(
    const float*              sig,
    int                       n_samples,
    std::vector<float>&       out_ri,
    int&                      n_frames,
    const std::vector<float>& hann)
{
    int le        = (int)std::ceil((double)n_samples / kHopLen);
    int right_pad = kPad + le * kHopLen - n_samples;
    int pad_len   = n_samples + kPad + right_pad;

    std::vector<float> padded(pad_len, 0.0f);
    // Left reflection
    for (int i = 0; i < kPad; ++i)
        padded[i] = sig[std::min(kPad - i, n_samples - 1)];
    // Original signal
    std::copy(sig, sig + n_samples, padded.data() + kPad);
    // Right reflection
    for (int i = 0; i < right_pad; ++i)
        padded[kPad + n_samples + i] = sig[std::max(0, n_samples - 2 - i)];

    int nf_raw = le + 4;
    std::vector<float> raw_ri((size_t)kFreqBins * nf_raw * 2, 0.0f);

    std::vector<std::complex<float>> frame(kNFft);
    for (int t = 0; t < nf_raw; ++t) {
        int fs = t * kHopLen;
        for (int i = 0; i < kNFft; ++i) {
            int   idx = fs + i;
            float s   = (idx < pad_len) ? padded[idx] : 0.0f;
            frame[i]  = { s * hann[i], 0.0f };
        }
        fft_dit(frame.data(), kNFft, false);
        for (int f = 0; f < kFreqBins; ++f) {
            raw_ri[(f * nf_raw + t) * 2 + 0] = frame[f].real();
            raw_ri[(f * nf_raw + t) * 2 + 1] = frame[f].imag();
        }
    }

    // Trim: z[2 : 2+le]
    out_ri.assign((size_t)kFreqBins * le * 2, 0.0f);
    for (int f = 0; f < kFreqBins; ++f)
        for (int t = 0; t < le; ++t) {
            out_ri[(f * le + t) * 2 + 0] = raw_ri[(f * nf_raw + (2 + t)) * 2 + 0];
            out_ri[(f * le + t) * 2 + 1] = raw_ri[(f * nf_raw + (2 + t)) * 2 + 1];
        }
    n_frames = le;
}

// ── ISTFT ─────────────────────────────────────────────────────────────────────
// Mirrors HTDemucs _ispec():
//   prepend 2 zero frames, append 1 zero frame + 2 zero frames
//   OLA with Hann² normalisation; remove kPad left-padding from output.
//
// spec_ri: flat [kFreqBins * n_frames * 2]  (f, t, ri)
static void compute_istft(
    const float*              spec_ri,
    int                       n_frames,
    int                       target_length,
    float*                    output,
    const std::vector<float>& hann)
{
    int nfp = n_frames + 5; // 2 before + n_frames + 1 zero + 2 after

    // Padded spectrogram [kFreqBins * nfp * 2]
    std::vector<float> pspec((size_t)kFreqBins * nfp * 2, 0.0f);
    for (int f = 0; f < kFreqBins; ++f)
        for (int t = 0; t < n_frames; ++t) {
            int dst = (f * nfp + (2 + t)) * 2;
            int src = (f * n_frames + t) * 2;
            pspec[dst    ] = spec_ri[src    ];
            pspec[dst + 1] = spec_ri[src + 1];
        }

    int hop_ceil = (int)std::ceil((double)target_length / kHopLen) * kHopLen;
    int out_len  = hop_ceil + 2 * kPad;

    std::vector<float> recon(out_len, 0.0f);
    std::vector<float> wsum(out_len, 0.0f);
    std::vector<std::complex<float>> frame(kNFft);

    for (int t = 0; t < nfp; ++t) {
        for (int f = 0; f < kFreqBins; ++f)
            frame[f] = { pspec[(f * nfp + t) * 2 + 0],
                         pspec[(f * nfp + t) * 2 + 1] };
        for (int f = kFreqBins; f < kNFft; ++f)
            frame[f] = { 0.0f, 0.0f };
        // Hermitian symmetry for real signal
        for (int f = 1; f < kNFft / 2; ++f)
            frame[kNFft - f] = std::conj(frame[f]);

        fft_dit(frame.data(), kNFft, true); // normalises by 1/N

        int fs = t * kHopLen;
        for (int i = 0; i < kNFft; ++i) {
            int idx = fs + i;
            if (idx >= 0 && idx < out_len) {
                float wv   = hann[i];
                recon[idx] += frame[i].real() * wv;
                wsum[idx]  += wv * wv;
            }
        }
    }

    // Extract, remove kPad, normalise
    for (int i = 0; i < target_length; ++i) {
        int s = i + kPad;
        if (s < out_len)
            output[i] = (wsum[s] > 1e-10f) ? recon[s] / wsum[s] : recon[s];
        else
            output[i] = 0.0f;
    }
}

// ── Run one stereo chunk through HTDemucs ────────────────────────────────────
// chunk_L, chunk_R: per-channel arrays of length chunk_len
// out_stems: flat [kStemCount * kChans * chunk_len]
//   access: out_stems[(stem * kChans + ch) * chunk_len + i]
// Returns 0 on success.
static int run_htdemucs(
    const float*              chunk_L,
    const float*              chunk_R,
    int                       chunk_len,
    std::vector<float>&       out_stems,
    const std::vector<float>& hann)
{
    if (!g_session || !g_api) return -10;

    // ── Waveform tensor [1, 2, chunk_len] ────────────────────────────────────
    std::vector<float> wave((size_t)2 * chunk_len);
    std::copy(chunk_L, chunk_L + chunk_len, wave.data());
    std::copy(chunk_R, chunk_R + chunk_len, wave.data() + chunk_len);
    int64_t wave_shape[3] = { 1, 2, (int64_t)chunk_len };

    // ── STFT per channel ──────────────────────────────────────────────────────
    std::vector<float> stft_L, stft_R;
    int nf_L = 0, nf_R = 0;
    compute_stft(chunk_L, chunk_len, stft_L, nf_L, hann);
    compute_stft(chunk_R, chunk_len, stft_R, nf_R, hann);
    int n_frames = nf_L;

    // ── Spectrogram tensor [1, 2, kFreqBins, n_frames, 2] ───────────────────
    // Flat layout: ch*(kFreqBins*n_frames*2) + f*(n_frames*2) + t*2 + ri
    size_t spec_total = (size_t)2 * kFreqBins * n_frames * 2;
    std::vector<float> spec(spec_total, 0.0f);
    for (int f = 0; f < kFreqBins; ++f)
        for (int t = 0; t < n_frames; ++t) {
            int src = (f * n_frames + t) * 2;
            int d0  = (0 * kFreqBins * n_frames + f * n_frames + t) * 2; // ch=L
            int d1  = (1 * kFreqBins * n_frames + f * n_frames + t) * 2; // ch=R
            spec[d0    ] = stft_L[src    ];  spec[d0 + 1] = stft_L[src + 1];
            spec[d1    ] = stft_R[src    ];  spec[d1 + 1] = stft_R[src + 1];
        }
    int64_t spec_shape[5] = { 1, 2, (int64_t)kFreqBins, (int64_t)n_frames, 2 };

    // ── Create ORT tensors ────────────────────────────────────────────────────
    OrtMemoryInfo* mem_info = nullptr;
    g_api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &mem_info);

    OrtValue*  wave_val = nullptr;
    OrtValue*  spec_val = nullptr;
    OrtStatus* st;

    st = g_api->CreateTensorWithDataAsOrtValue(
            mem_info, wave.data(), wave.size() * sizeof(float),
            wave_shape, 3, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &wave_val);
    if (st) {
        g_api->ReleaseStatus(st);
        g_api->ReleaseMemoryInfo(mem_info);
        return -11;
    }

    st = g_api->CreateTensorWithDataAsOrtValue(
            mem_info, spec.data(), spec.size() * sizeof(float),
            spec_shape, 5, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &spec_val);
    if (st) {
        g_api->ReleaseStatus(st);
        g_api->ReleaseValue(wave_val);
        g_api->ReleaseMemoryInfo(mem_info);
        return -12;
    }

    // ── Model input / output names ────────────────────────────────────────────
    OrtAllocator* alloc = nullptr;
    g_api->GetAllocatorWithDefaultOptions(&alloc);

    size_t n_in = 0, n_out = 0;
    g_api->SessionGetInputCount(g_session, &n_in);
    g_api->SessionGetOutputCount(g_session, &n_out);

    if (n_in < 1 || n_out < 1) {
        g_api->ReleaseValue(wave_val);
        g_api->ReleaseValue(spec_val);
        g_api->ReleaseMemoryInfo(mem_info);
        return -13;
    }

    std::vector<char*> in_names(n_in), out_names(n_out);
    for (size_t i = 0; i < n_in;  ++i)
        g_api->SessionGetInputName(g_session,  i, alloc, &in_names[i]);
    for (size_t i = 0; i < n_out; ++i)
        g_api->SessionGetOutputName(g_session, i, alloc, &out_names[i]);

    // ── Run inference ─────────────────────────────────────────────────────────
    // Support both 1-input (waveform only) and 2-input (waveform + spec) models.
    std::vector<const char*> run_in_names;
    std::vector<OrtValue*>   run_in_vals;
    run_in_names.push_back(in_names[0]);
    run_in_vals.push_back(wave_val);
    if (n_in >= 2) {
        run_in_names.push_back(in_names[1]);
        run_in_vals.push_back(spec_val);
    }

    std::vector<const char*> run_out_names(n_out);
    for (size_t i = 0; i < n_out; ++i) run_out_names[i] = out_names[i];
    std::vector<OrtValue*> run_out_vals(n_out, nullptr);

    st = g_api->Run(g_session, nullptr,
                    run_in_names.data(), run_in_vals.data(), run_in_names.size(),
                    run_out_names.data(), n_out, run_out_vals.data());

    // Free name strings before checking error
    for (size_t i = 0; i < n_in;  ++i) alloc->Free(alloc, in_names[i]);
    for (size_t i = 0; i < n_out; ++i) alloc->Free(alloc, out_names[i]);
    g_api->ReleaseValue(wave_val);
    g_api->ReleaseValue(spec_val);
    g_api->ReleaseMemoryInfo(mem_info);

    if (st) {
        g_api->ReleaseStatus(st);
        return -14;
    }

    // ── Extract stems from model output ───────────────────────────────────────
    out_stems.assign((size_t)kStemCount * kChans * chunk_len, 0.0f);

    auto get_dims = [&](OrtValue* v, std::vector<int64_t>& dims) {
        OrtTensorTypeAndShapeInfo* ti = nullptr;
        g_api->GetTensorTypeAndShape(v, &ti);
        size_t nd = 0;
        g_api->GetDimensionsCount(ti, &nd);
        dims.resize(nd);
        g_api->GetDimensions(ti, dims.data(), nd);
        g_api->ReleaseTensorTypeAndShapeInfo(ti);
    };

    if (n_out >= 2 && run_out_vals[0] && run_out_vals[1]) {
        // ── Dual-branch: freq [1,S,4,F,T] + time [1,S,C,N] ──────────────────
        std::vector<int64_t> fd, td;
        get_dims(run_out_vals[0], fd);
        get_dims(run_out_vals[1], td);

        if (fd.size() >= 5 && td.size() >= 4) {
            int n_stems = (int)fd[1];
            int fF      = (int)fd[3]; // freq bins in output
            int fT      = (int)fd[4]; // time frames in output
            int tCh     = (int)td[2];
            int tS      = (int)td[3]; // samples in time branch

            float* fdata = nullptr;
            float* tdata = nullptr;
            g_api->GetTensorMutableData(run_out_vals[0], (void**)&fdata);
            g_api->GetTensorMutableData(run_out_vals[1], (void**)&tdata);

            std::vector<float> spec_ch((size_t)fF * fT * 2);
            std::vector<float> ch_wave(chunk_len, 0.0f);

            for (int s = 0; s < std::min(n_stems, kStemCount); ++s) {
                // Freq branch per channel: complex channels [L_re, L_im, R_re, R_im]
                // freq output stride: [1, S, 4, fF, fT]
                // flat: s*(4*fF*fT) + c*(fF*fT) + f*fT + t
                int stem_off = s * (4 * fF * fT);

                for (int ch = 0; ch < kChans; ++ch) {
                    int rc = ch * 2;      // real component index in the 4-ch dim
                    int ic = ch * 2 + 1;  // imag component index

                    int eff_f = std::min(fF, kFreqBins);
                    for (int f = 0; f < eff_f; ++f)
                        for (int t = 0; t < fT; ++t) {
                            spec_ch[(f * fT + t) * 2 + 0] =
                                fdata[stem_off + rc * fF * fT + f * fT + t];
                            spec_ch[(f * fT + t) * 2 + 1] =
                                fdata[stem_off + ic * fF * fT + f * fT + t];
                        }

                    compute_istft(spec_ch.data(), fT, chunk_len, ch_wave.data(), hann);

                    // Merge: freq_branch + time_branch
                    int copy  = std::min(tS, chunk_len);
                    int t_off = s * (tCh * tS) + ch * tS;
                    for (int i = 0; i < chunk_len; ++i) {
                        float ts = (i < copy) ? tdata[t_off + i] : 0.0f;
                        out_stems[(s * kChans + ch) * chunk_len + i] = ch_wave[i] + ts;
                    }
                }
            }
        }

    } else if (n_out == 1 && run_out_vals[0]) {
        // ── Single-output fallback: [1, stems, channels, samples] ─────────────
        std::vector<int64_t> od;
        get_dims(run_out_vals[0], od);
        if (od.size() >= 4) {
            int n_stems = (int)od[1];
            int tCh     = (int)od[2];
            int tS      = (int)od[3];
            float* data = nullptr;
            g_api->GetTensorMutableData(run_out_vals[0], (void**)&data);
            int copy = std::min(tS, chunk_len);
            for (int s = 0; s < std::min(n_stems, kStemCount); ++s)
                for (int ch = 0; ch < std::min(tCh, kChans); ++ch) {
                    int off = s * (tCh * tS) + ch * tS;
                    for (int i = 0; i < copy; ++i)
                        out_stems[(s * kChans + ch) * chunk_len + i] = data[off + i];
                }
        }
    }

    for (size_t i = 0; i < n_out; ++i)
        if (run_out_vals[i]) g_api->ReleaseValue(run_out_vals[i]);

    return 0;
}

// ── Full HTDemucs pipeline with chunking ─────────────────────────────────────
static int separate_vocals_onnx(
    const float*                input,
    int                         sample_count,
    int                         sample_rate,
    OwnAudioMlSeparationResult* result)
{
    if (!g_session) return -20;

    int total = sample_count / 2; // per-channel frame count (stereo → /2)
    if (total <= 0) return -21;

    // De-interleave input
    std::vector<float> audio_L(total), audio_R(total);
    for (int i = 0; i < total; ++i) {
        audio_L[i] = input[i * 2    ];
        audio_R[i] = input[i * 2 + 1];
    }

    // Pre-compute Hann window once
    std::vector<float> hann;
    make_hann(hann, kNFft);

    // Chunk parameters
    int chunk_len = (g_seg_len > 0)
                    ? g_seg_len
                    : (int)(kChunkSec * sample_rate);
    chunk_len = std::min(chunk_len, total + 1); // no bigger than audio

    int margin    = (int)(kMarginSec    * sample_rate);
    int crossfade = (int)(kCrossfadeSec * sample_rate);

    // Guard against degenerate sizes
    int valid_size = chunk_len - 2 * margin;
    if (valid_size <= 0) {
        margin     = chunk_len / 4;
        crossfade  = chunk_len / 8;
        valid_size = chunk_len - 2 * margin;
    }
    if (crossfade >= valid_size) crossfade = valid_size / 2;
    int stride = valid_size - crossfade;
    if (stride <= 0) stride = 1;

    // Per-channel output accumulators
    std::vector<float> voc_L(total, 0.0f), voc_R(total, 0.0f);
    std::vector<float> ins_L(total, 0.0f), ins_R(total, 0.0f);

    std::vector<float> win_L(chunk_len), win_R(chunk_len);
    std::vector<float> stems_out;

    int target_pos = 0;
    while (target_pos < total) {
        int win_start = target_pos - margin;

        // Extract window with reflection padding at boundaries
        for (int i = 0; i < chunk_len; ++i) {
            int src = win_start + i;
            if (src < 0)      src = -src;
            if (src >= total) src = 2 * total - src - 2;
            src      = std::max(0, std::min(total - 1, src));
            win_L[i] = audio_L[src];
            win_R[i] = audio_R[src];
        }

        int err = run_htdemucs(win_L.data(), win_R.data(), chunk_len,
                               stems_out, hann);
        if (err != 0) return err;

        // ── Overlap-add with cosine crossfade (mirrors ApplyTrimmedOverlapAdd) ─
        int trim_len = std::min(valid_size, total - target_pos);

        for (int ch = 0; ch < kChans; ++ch) {
            std::vector<float>& voc_ch = (ch == 0) ? voc_L : voc_R;
            std::vector<float>& ins_ch = (ch == 0) ? ins_L : ins_R;

            for (int i = 0; i < trim_len; ++i) {
                int src_i = margin + i;  // trim margin from chunk output
                int dst_i = target_pos + i;
                if (dst_i >= total) break;

                float voc_s = stems_out[(kVocIdx * kChans + ch) * chunk_len + src_i];
                float ins_s = 0.0f;
                for (int s = 0; s < kStemCount; ++s)
                    if (s != kVocIdx)
                        ins_s += stems_out[(s * kChans + ch) * chunk_len + src_i];

                if (target_pos == 0 || i >= crossfade) {
                    voc_ch[dst_i] = voc_s;
                    ins_ch[dst_i] = ins_s;
                } else {
                    // Cosine crossfade: fade_out previous, fade_in current
                    float pos      = (float)i / (float)crossfade;
                    float ang      = pos * (float)M_PI * 0.5f;
                    float fade_in  = std::sin(ang);
                    float fade_out = std::cos(ang);
                    voc_ch[dst_i]  = voc_ch[dst_i] * fade_out + voc_s * fade_in;
                    ins_ch[dst_i]  = ins_ch[dst_i] * fade_out + ins_s * fade_in;
                }
            }
        }

        target_pos += stride;
    }

    // ── Re-interleave and fill result ─────────────────────────────────────────
    result->sample_count = sample_count;
    result->vocals       = (float*)malloc((size_t)sample_count * sizeof(float));
    result->instrumental = (float*)malloc((size_t)sample_count * sizeof(float));
    if (!result->vocals || !result->instrumental) {
        free(result->vocals);
        free(result->instrumental);
        result->vocals = result->instrumental = nullptr;
        result->sample_count = 0;
        return -30;
    }
    for (int i = 0; i < total; ++i) {
        result->vocals      [i * 2    ] = voc_L[i];
        result->vocals      [i * 2 + 1] = voc_R[i];
        result->instrumental[i * 2    ] = ins_L[i];
        result->instrumental[i * 2 + 1] = ins_R[i];
    }
    return 0;
}

// ── Lifecycle hooks (called from ownaudio_ml.cpp) ─────────────────────────────

void vocal_separator_try_load(const char* model_path)
{
    if (!model_path) return;

    // Initialise ORT environment once
    if (!g_env) {
        g_api = OrtGetApiBase()->GetApi(ORT_API_VERSION);
        if (!g_api) return;
        OrtStatus* st = g_api->CreateEnv(
            ORT_LOGGING_LEVEL_WARNING, "ownaudio_ml_vs", &g_env);
        if (st) {
            g_api->ReleaseStatus(st);
            g_env = nullptr;
            return;
        }
    }

    // Release any previously loaded session
    if (g_session) {
        g_api->ReleaseSession(g_session);
        g_session = nullptr;
    }

    OrtSessionOptions* opts = nullptr;
    g_api->CreateSessionOptions(&opts);
    g_api->SetIntraOpNumThreads(opts, 0); // use all CPU cores

#ifdef _WIN32
    int wlen = MultiByteToWideChar(CP_UTF8, 0, model_path, -1, nullptr, 0);
    std::vector<wchar_t> wpath((size_t)wlen);
    MultiByteToWideChar(CP_UTF8, 0, model_path, -1, wpath.data(), wlen);
    OrtStatus* st = g_api->CreateSession(g_env, wpath.data(), opts, &g_session);
#else
    OrtStatus* st = g_api->CreateSession(g_env, model_path, opts, &g_session);
#endif
    g_api->ReleaseSessionOptions(opts);

    if (st) {
        g_api->ReleaseStatus(st);
        g_session = nullptr;
        return;
    }

    // Auto-detect fixed segment length from model input shape [1, 2, N]
    g_seg_len = 0;
    OrtTypeInfo* ti = nullptr;
    if (!g_api->SessionGetInputTypeInfo(g_session, 0, &ti)) {
        const OrtTensorTypeAndShapeInfo* si = nullptr;
        if (!g_api->CastTypeInfoToTensorInfo(ti, &si)) {
            size_t nd = 0;
            g_api->GetDimensionsCount(si, &nd);
            if (nd >= 3) {
                std::vector<int64_t> dims(nd);
                g_api->GetDimensions(si, dims.data(), nd);
                if (dims[2] > 0) g_seg_len = (int)dims[2];
            }
        }
        g_api->ReleaseTypeInfo(ti);
    }
}

void vocal_separator_shutdown_ort()
{
    if (g_api) {
        if (g_session) { g_api->ReleaseSession(g_session); g_session = nullptr; }
        if (g_env)     { g_api->ReleaseEnv(g_env);         g_env     = nullptr; }
    }
    g_api     = nullptr;
    g_seg_len = 0;
}

bool vocal_separator_is_loaded()
{
    return g_session != nullptr;
}

#endif // OWNAUDIO_ML_HAS_ONNXRUNTIME

// ── Public C API ──────────────────────────────────────────────────────────────
extern "C" {

OWNAUDIO_ML_API int ownaudio_ml_separate_vocals(
    const float*                input,
    int                         sample_count,
    int                         sample_rate,
    OwnAudioMlSeparationResult* result)
{
    if (!input || sample_count <= 0 || !result) return -1;

#ifdef OWNAUDIO_ML_HAS_ONNXRUNTIME
    if (g_session) {
        int err = separate_vocals_onnx(input, sample_count, sample_rate, result);
        if (err == 0) return 0;
        // On error fall through to identity stub
    }
#else
    (void)sample_rate;
#endif

    // Identity stub: vocals = input copy, instrumental = silence
    result->sample_count = sample_count;
    result->vocals       = (float*)malloc((size_t)sample_count * sizeof(float));
    result->instrumental = (float*)calloc((size_t)sample_count, sizeof(float));
    if (!result->vocals || !result->instrumental) {
        free(result->vocals);
        free(result->instrumental);
        result->vocals = result->instrumental = nullptr;
        return -2;
    }
    memcpy(result->vocals, input, (size_t)sample_count * sizeof(float));
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
