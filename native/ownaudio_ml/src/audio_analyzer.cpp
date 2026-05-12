/**
 * audio_analyzer.cpp – FFT-based 30-band spectrum analysis and EQ matching.
 * No ONNX Runtime required – pure DSP using a Cooley-Tukey radix-2 FFT.
 *
 * Band layout: 30 ISO 1/3-octave-inspired centres from 25 Hz to 20 kHz
 * (matches the 30-element frequency_bands[] array in OwnAudioMlSpectrum).
 */

#include "../ownaudio_ml.h"
#include <cstring>
#include <cmath>
#include <vector>
#include <complex>
#include <algorithm>

// 30 band centre frequencies (Hz) – logarithmically distributed 25 Hz … 20 kHz
static const float k_bands[30] = {
      25.0f,   31.5f,   40.0f,   50.0f,   63.0f,   80.0f,
     100.0f,  125.0f,  160.0f,  200.0f,  250.0f,  315.0f,
     400.0f,  500.0f,  630.0f,  800.0f, 1000.0f, 1250.0f,
    1600.0f, 2000.0f, 2500.0f, 3150.0f, 4000.0f, 5000.0f,
    6300.0f, 8000.0f,10000.0f,12500.0f,16000.0f,20000.0f
};

// ── In-place Cooley-Tukey radix-2 DIT FFT ────────────────────────────────────
static void fft_inplace(std::complex<float>* x, int n)
{
    for (int i = 1, j = 0; i < n; ++i) {
        int bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(x[i], x[j]);
    }
    for (int len = 2; len <= n; len <<= 1) {
        float ang = -2.0f * (float)M_PI / (float)len;
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
}

static int next_pow2(int n)
{
    int p = 1;
    while (p < n) p <<= 1;
    return p;
}

// RMS energy for a frequency band centred on centre_hz (±23 % bandwidth)
static float band_rms_energy(const std::complex<float>* fft, int fft_size,
                              float centre_hz, int sample_rate)
{
    float bw   = centre_hz * 0.23f;
    float f_lo = std::max(0.0f, centre_hz - bw * 0.5f);
    float f_hi = std::min((float)sample_rate * 0.5f, centre_hz + bw * 0.5f);
    int   b_lo = (int)(f_lo * (float)fft_size / (float)sample_rate);
    int   b_hi = (int)(f_hi * (float)fft_size / (float)sample_rate) + 1;
    b_lo = std::max(0, b_lo);
    b_hi = std::min(fft_size / 2, b_hi);
    if (b_lo >= b_hi) return 0.0f;

    double sum = 0.0;
    for (int b = b_lo; b < b_hi; ++b) {
        double mag = std::abs(fft[b]);
        sum += mag * mag;
    }
    double rms = std::sqrt(sum / (b_hi - b_lo));
    return (float)(rms / (fft_size * 0.5));  // normalise by half-spectrum
}

extern "C" {

OWNAUDIO_ML_API int ownaudio_ml_analyze_spectrum(
    const float*        input,
    int                 sample_count,
    int                 sample_rate,
    OwnAudioMlSpectrum* result)
{
    if (!input || sample_count <= 0 || !result) return -1;
    memset(result, 0, sizeof(OwnAudioMlSpectrum));

    // Pick an FFT size appropriate for the sample rate (must be power-of-2)
    int fft_size;
    if      (sample_rate >= 96000) fft_size = 32768;
    else if (sample_rate >= 48000) fft_size = 16384;
    else                           fft_size = 8192;
    if (fft_size > sample_count)   fft_size = next_pow2(sample_count);

    const int hop = fft_size / 4;   // 75 % overlap

    // Pre-compute Hann window
    std::vector<float> hann(fft_size);
    for (int i = 0; i < fft_size; ++i)
        hann[i] = 0.5f * (1.0f - std::cos(2.0f * (float)M_PI * i / (fft_size - 1)));

    std::vector<std::complex<float>> fft_buf(fft_size);

    double band_acc[30] = {};
    double rms_sum      = 0.0;
    float  peak         = 0.0f;
    int    wins         = 0;

    for (int start = 0; start + fft_size <= sample_count; start += hop) {
        for (int i = 0; i < fft_size; ++i) {
            float s = input[start + i];
            fft_buf[i] = std::complex<float>(s * hann[i], 0.0f);
            rms_sum += (double)s * s;
            float as = s < 0.0f ? -s : s;
            if (as > peak) peak = as;
        }

        fft_inplace(fft_buf.data(), fft_size);

        for (int b = 0; b < 30; ++b)
            band_acc[b] += band_rms_energy(fft_buf.data(), fft_size,
                                           k_bands[b], sample_rate);
        ++wins;
    }

    if (wins > 0) {
        for (int b = 0; b < 30; ++b)
            result->frequency_bands[b] = (float)(band_acc[b] / wins);
    }

    const double rms = std::sqrt(rms_sum / sample_count);
    result->rms_level     = (rms  > 1e-10)  ? 20.0f * (float)std::log10(rms)   : -120.0f;
    result->peak_level    = (peak > 1e-10f)  ? 20.0f * (float)std::log10(peak) : -120.0f;
    // Approximate integrated loudness: LUFS ≈ RMS dBFS – 0.691
    result->loudness      = result->rms_level - 0.691f;
    result->dynamic_range = result->peak_level - result->rms_level;

    return 0;
}

OWNAUDIO_ML_API int ownaudio_ml_calculate_eq_adjustments(
    const OwnAudioMlSpectrum* source,
    const OwnAudioMlSpectrum* target,
    float*                    eq_out,
    int                       band_count)
{
    if (!source || !target || !eq_out || band_count <= 0) return -1;

    const int bands = band_count < 30 ? band_count : 30;
    for (int i = 0; i < bands; ++i) {
        float s = source->frequency_bands[i];
        float t = target->frequency_bands[i];
        eq_out[i] = (s > 1e-10f && t > 1e-10f)
                    ? 20.0f * std::log10(t / s)
                    : 0.0f;
    }
    return 0;
}

} // extern "C"
