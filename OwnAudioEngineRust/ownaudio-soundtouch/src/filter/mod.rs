//! FIR / anti-alias filtering used by the rate transposer.

pub mod anti_alias;
pub mod fir_filter;

pub use anti_alias::AntiAliasFilter;
pub use fir_filter::FirFilter;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fir_impulse_response_matches_coefficients() {
        // Feeding a unit impulse through an FIR yields the (time-reversed) tap
        // weights as the output.  With 8 taps and a leading impulse, output[0]
        // is the dot of the impulse-aligned window with the coefficients.
        let mut fir = FirFilter::new();
        // result_div_factor = 0 → no scaling.
        let coeffs = [1.0f32, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0];
        fir.set_coefficients(&coeffs, 0).unwrap();

        // mono: 16 input samples, impulse at index 0
        let mut src = [0.0f32; 16];
        src[0] = 1.0;
        let mut dest = [0.0f32; 16];
        let n = fir.evaluate(&mut dest, &src, 16, 1);
        assert_eq!(n, 16 - 8);
        // output[0] = sum src[0+i]*coeff[i] = coeff[0] since only src[0]=1
        assert_eq!(dest[0], 1.0);
        // all other outputs are zero (impulse moved out of the window)
        assert!(dest[1..n].iter().all(|&v| v == 0.0));
    }

    #[test]
    fn fir_dc_gain_is_coefficient_sum() {
        let mut fir = FirFilter::new();
        let coeffs = [0.125f32; 8]; // sums to 1.0 → unity DC gain
        fir.set_coefficients(&coeffs, 0).unwrap();
        let src = [1.0f32; 32];
        let mut dest = [0.0f32; 32];
        let n = fir.evaluate(&mut dest, &src, 32, 1);
        for &v in &dest[..n] {
            assert!((v - 1.0).abs() < 1e-6);
        }
    }

    #[test]
    fn anti_alias_is_low_pass_unity_at_dc() {
        // A correctly designed low-pass passes DC with ~unity gain.
        let aa = AntiAliasFilter::new(64);
        let src = [1.0f32; 256];
        let mut dest = [0.0f32; 256];
        let n = aa.evaluate(&mut dest, &src, 256, 1);
        assert!(n > 0);
        // Steady-state DC output should be close to 1.0.
        let mid = dest[n / 2];
        assert!((mid - 1.0).abs() < 0.05, "DC gain {mid} not near unity");
    }

    #[test]
    fn anti_alias_attenuates_nyquist() {
        // Alternating +1/-1 is the Nyquist tone; a low-pass must strongly
        // attenuate it relative to DC.
        let aa = AntiAliasFilter::new(64);
        let mut aa2 = AntiAliasFilter::new(64);
        aa2.set_cutoff_freq(0.25);
        let src: Vec<f32> = (0..256)
            .map(|i| if i % 2 == 0 { 1.0 } else { -1.0 })
            .collect();
        let mut dest = vec![0.0f32; 256];
        let n = aa2.evaluate(&mut dest, &src, 256, 1);
        let peak = dest[..n].iter().fold(0.0f32, |a, &b| a.max(b.abs()));
        assert!(peak < 0.5, "Nyquist tone not attenuated: peak {peak}");
        let _ = aa;
    }
}
