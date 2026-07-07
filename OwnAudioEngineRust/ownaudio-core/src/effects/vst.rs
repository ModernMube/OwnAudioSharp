//! VST3 plugin bridge — hosts an external plugin as a native effect.
//!
//! Unlike the built-in effects, a [`VstEffect`] owns no DSP state of its own.
//! The plugin instance is created, loaded, initialised and parameter-controlled
//! entirely on the C# control plane (the `OwnAudioVst` host); this effect only
//! receives an **opaque plugin handle** plus a **C ABI process function pointer**
//! and forwards each audio block to it on the audio thread.
//!
//! Because the plugin's `ProcessAudio` entry point works on planar (non-inter-
//! leaved) channel buffers, the effect keeps pre-allocated planar scratch and
//! deinterleaves the chain's interleaved buffer into it, calls the plugin, then
//! reinterleaves the plugin output back. All scratch is allocated once in
//! [`VstEffect::new`], so [`VstEffect::process`] never allocates.

use std::os::raw::c_void;

use super::{Effect, EffectType, PARAM_ENABLED, PARAM_MIX};

/// Planar audio buffer passed to the plugin's process callback.
///
/// Binary-compatible with the `AudioBufferC` struct in the `OwnAudioVst` native
/// host (`ownvst3_exports.h`): two arrays of channel pointers plus the channel
/// and sample counts. `inputs` and `outputs` point at arrays of `num_channels`
/// `f32*` planes, each holding `num_samples` samples.
#[repr(C)]
pub struct VstAudioBuffer {
    /// Array of `num_channels` input channel pointers (planar `f32`).
    pub inputs: *mut *mut f32,
    /// Array of `num_channels` output channel pointers (planar `f32`).
    pub outputs: *mut *mut f32,
    /// Number of channels addressed by `inputs` / `outputs`.
    pub num_channels: i32,
    /// Number of samples per channel plane.
    pub num_samples: i32,
}

/// Nullable C ABI process callback exported by the `OwnAudioVst` native host
/// (`VST3Plugin_ProcessAudio`), as it crosses the FFI boundary.
///
/// Receives the opaque plugin handle and a pointer to a [`VstAudioBuffer`],
/// processes one block in place into `outputs`, and returns `true` on success.
/// Modelled as an `Option` so the C# side can (in principle) pass a null pointer;
/// the FFI layer rejects that before an effect is ever built.
///
/// # Safety
/// Invoked on the audio thread. The callee must be real-time safe (no heap
/// allocation, no blocking) and the `handle` must remain valid for the entire
/// lifetime of the owning [`VstEffect`].
pub type VstProcessFn =
    Option<unsafe extern "C" fn(handle: *mut c_void, buffer: *mut VstAudioBuffer) -> bool>;

/// Hosts a single external VST3 plugin as an effect-chain entry.
///
/// See the module docs for the ownership and threading model. The handle and
/// process function pointer are supplied by the control plane; this type never
/// creates, loads or destroys the plugin.
pub struct VstEffect {
    /// Opaque plugin instance handle owned by the C# control plane.
    handle: *mut c_void,
    /// C ABI process entry point of the native host (non-null once constructed).
    process_fn: unsafe extern "C" fn(handle: *mut c_void, buffer: *mut VstAudioBuffer) -> bool,
    /// Active (wet) flag. When `false` the effect is *soft-bypassed*: the plugin
    /// is still driven every block (so it never goes cold), but its output is
    /// discarded and the dry signal passes through. See [`VstEffect::process`]
    /// and [`Effect::is_enabled`] for why bypass is not a chain-level skip.
    enabled: bool,
    /// Dry/wet mix in `[0, 1]` (`1.0` = fully wet, the natural insert default).
    mix: f32,
    /// Plugin processing latency in frames, reported to the mixer for plugin
    /// delay compensation. Constant for the effect's lifetime (set at construction
    /// from the host); stays reported while bypassed, since native bypass keeps the
    /// same latency.
    latency: u32,
    /// Maximum channel count the planar scratch was sized for.
    max_channels: usize,
    /// Maximum block size (samples per channel) the planar scratch was sized for.
    max_block: usize,
    /// Per-channel planar input scratch (`[channel][sample]`).
    in_planar: Vec<Vec<f32>>,
    /// Per-channel planar output scratch (`[channel][sample]`).
    out_planar: Vec<Vec<f32>>,
    /// Array of input plane pointers handed to the plugin (rebuilt per block).
    in_ptrs: Vec<*mut f32>,
    /// Array of output plane pointers handed to the plugin (rebuilt per block).
    out_ptrs: Vec<*mut f32>,
    /// Interleaved dry copy, used only when `mix < 1.0`.
    dry: Vec<f32>,
}

// SAFETY: the raw plugin `handle` and `process_fn` are not `Send` by default,
// but the effect is moved to the audio thread through the mixer command queue
// like every other effect. The C# control plane owns the handle and guarantees
// (a) it stays valid for the whole lifetime of this effect, and (b) the process
// callback may be invoked from the audio thread — exactly the same contract the
// stream trampolines rely on for their C# `user_data` pointers.
unsafe impl Send for VstEffect {}

impl VstEffect {
    /// Builds a VST bridge for `handle`, forwarding audio through `process_fn`.
    ///
    /// - `max_channels` — largest channel count the owning chain will present;
    ///   planar scratch is sized for this so `process` never reallocates.
    /// - `max_block` — largest block size (samples per channel) the chain will
    ///   present.
    ///
    /// Both bounds are clamped to at least 1. Blocks larger than these bounds
    /// are skipped at process time rather than reallocating on the audio thread.
    pub fn new(
        handle: *mut c_void,
        process_fn: unsafe extern "C" fn(handle: *mut c_void, buffer: *mut VstAudioBuffer) -> bool,
        max_channels: u16,
        max_block: usize,
        latency: u32,
    ) -> Self {
        let channels = (max_channels as usize).max(1);
        let block = max_block.max(1);

        let mut in_planar = Vec::with_capacity(channels);
        let mut out_planar = Vec::with_capacity(channels);
        for _ in 0..channels {
            in_planar.push(vec![0.0f32; block]);
            out_planar.push(vec![0.0f32; block]);
        }

        Self {
            handle,
            process_fn,
            enabled: true,
            mix: 1.0,
            latency,
            max_channels: channels,
            max_block: block,
            in_planar,
            out_planar,
            in_ptrs: vec![std::ptr::null_mut(); channels],
            out_ptrs: vec![std::ptr::null_mut(); channels],
            dry: vec![0.0f32; block * channels],
        }
    }
}

impl Effect for VstEffect {
    fn effect_type(&self) -> EffectType {
        EffectType::Vst
    }

    fn process(&mut self, buffer: &mut [f32], channels: u16) {
        let ch = channels as usize;
        // Guard against a buffer that outgrows the pre-allocated scratch; a real
        // allocation on the audio thread is never acceptable, so skip instead.
        if ch == 0 || ch > self.max_channels {
            return;
        }
        let frames = buffer.len() / ch;
        if frames == 0 || frames > self.max_block {
            return;
        }

        let wet = self.mix.clamp(0.0, 1.0);
        // A dry copy is only needed for an active partial mix; when soft-bypassed
        // the chain buffer already is the dry signal and is left untouched.
        let need_dry = self.enabled && wet < 1.0;
        if need_dry {
            let len = frames * ch;
            self.dry[..len].copy_from_slice(&buffer[..len]);
        }

        // Deinterleave the chain buffer into per-channel planar scratch.
        for c in 0..ch {
            let plane = &mut self.in_planar[c];
            for f in 0..frames {
                plane[f] = buffer[f * ch + c];
            }
        }
        for c in 0..ch {
            self.in_ptrs[c] = self.in_planar[c].as_mut_ptr();
            self.out_ptrs[c] = self.out_planar[c].as_mut_ptr();
        }

        let mut abc = VstAudioBuffer {
            inputs: self.in_ptrs.as_mut_ptr(),
            outputs: self.out_ptrs.as_mut_ptr(),
            num_channels: ch as i32,
            num_samples: frames as i32,
        };

        // Drive the plugin on EVERY block, even while soft-bypassed, so it never
        // goes cold. A plugin that has not been called for a while often does
        // non-RT-safe work (allocation, locking, worker-thread wake) on its first
        // resumed block; skipping it while disabled and resuming on re-enable
        // would stall the audio thread on every toggle. Keeping it warm makes the
        // per-block cost constant and the enable/disable transition glitch-free.
        //
        // SAFETY: `handle` and `process_fn` are valid for this effect's lifetime
        // (control-plane contract); the planar pointer arrays and their backing
        // scratch outlive the call. A panic can only originate inside foreign C
        // code, which aborts at its own ABI boundary, so no unwind crosses here.
        let ok = unsafe { (self.process_fn)(self.handle, &mut abc) };

        // Soft-bypassed (or the plugin failed): keep the dry input that the chain
        // buffer already holds — the plugin still ran, so it stays warm.
        if !self.enabled || !ok {
            return;
        }

        // Reinterleave the plugin output back into the chain buffer.
        for c in 0..ch {
            let plane = &self.out_planar[c];
            for f in 0..frames {
                buffer[f * ch + c] = plane[f];
            }
        }

        if wet < 1.0 {
            let dry_gain = 1.0 - wet;
            let len = frames * ch;
            for (out, &dry) in buffer[..len].iter_mut().zip(self.dry[..len].iter()) {
                *out = dry * dry_gain + *out * wet;
            }
        }
    }

    fn set_param(&mut self, param_id: u32, value: f32) -> bool {
        match param_id {
            PARAM_ENABLED => {
                self.enabled = value >= 0.5;
                true
            }
            PARAM_MIX => {
                self.mix = value.clamp(0.0, 1.0);
                true
            }
            _ => false,
        }
    }

    fn get_param(&self, param_id: u32) -> Option<f32> {
        match param_id {
            PARAM_ENABLED => Some(if self.enabled { 1.0 } else { 0.0 }),
            PARAM_MIX => Some(self.mix),
            _ => None,
        }
    }

    fn reset(&mut self) {
        for plane in &mut self.in_planar {
            plane.iter_mut().for_each(|s| *s = 0.0);
        }
        for plane in &mut self.out_planar {
            plane.iter_mut().for_each(|s| *s = 0.0);
        }
        self.dry.iter_mut().for_each(|s| *s = 0.0);
    }

    fn is_enabled(&self) -> bool {
        // Intentionally always active from the chain's perspective, so
        // [`EffectChain::process_all`] drives the plugin on every block and it
        // never goes cold. Bypass is handled inside [`VstEffect::process`] by
        // emitting the dry signal — turning it into a chain-level skip would
        // reintroduce the cold-resume stall on every enable/disable toggle.
        true
    }

    fn set_enabled(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    fn latency_samples(&self) -> u32 {
        self.latency
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A plugin stub that doubles every input sample into the output planes.
    unsafe extern "C" fn doubling_process(
        _handle: *mut c_void,
        buffer: *mut VstAudioBuffer,
    ) -> bool {
        let b = &*buffer;
        let ch = b.num_channels as usize;
        let n = b.num_samples as usize;
        for c in 0..ch {
            let inp = *b.inputs.add(c);
            let outp = *b.outputs.add(c);
            for f in 0..n {
                *outp.add(f) = *inp.add(f) * 2.0;
            }
        }
        true
    }

    unsafe extern "C" fn failing_process(
        _handle: *mut c_void,
        _buffer: *mut VstAudioBuffer,
    ) -> bool {
        false
    }

    #[test]
    fn processes_interleaved_stereo_through_the_plugin() {
        let mut fx = VstEffect::new(std::ptr::null_mut(), doubling_process, 2, 512, 0);
        // Two stereo frames: L0 R0 L1 R1.
        let mut buf = [1.0f32, -2.0, 0.5, 0.25];
        fx.process(&mut buf, 2);
        assert_eq!(buf, [2.0, -4.0, 1.0, 0.5]);
    }

    #[test]
    fn dry_wet_mix_blends_input_and_output() {
        let mut fx = VstEffect::new(std::ptr::null_mut(), doubling_process, 2, 512, 0);
        assert!(fx.set_param(PARAM_MIX, 0.5));
        let mut buf = [1.0f32, 1.0];
        fx.process(&mut buf, 2);
        // 0.5 * dry(1.0) + 0.5 * wet(2.0) = 1.5 on each channel.
        assert_eq!(buf, [1.5, 1.5]);
    }

    #[test]
    fn failed_plugin_call_leaves_buffer_untouched() {
        let mut fx = VstEffect::new(std::ptr::null_mut(), failing_process, 2, 512, 0);
        let mut buf = [0.3f32, -0.7];
        fx.process(&mut buf, 2);
        assert_eq!(buf, [0.3, -0.7]);
    }

    #[test]
    fn oversized_block_is_skipped_without_allocating() {
        let mut fx = VstEffect::new(std::ptr::null_mut(), doubling_process, 2, 2, 0);
        // 3 stereo frames exceeds the 2-sample scratch → left untouched.
        let mut buf = [1.0f32, 1.0, 1.0, 1.0, 1.0, 1.0];
        fx.process(&mut buf, 2);
        assert_eq!(buf, [1.0, 1.0, 1.0, 1.0, 1.0, 1.0]);
    }

    #[test]
    fn soft_bypass_passes_dry_but_keeps_the_plugin_warm() {
        let mut fx = VstEffect::new(std::ptr::null_mut(), doubling_process, 2, 64, 0);
        // Bypass via the shared enabled parameter.
        assert!(fx.set_param(PARAM_ENABLED, 0.0));
        assert_eq!(fx.get_param(PARAM_ENABLED), Some(0.0));

        // From the chain's perspective the effect stays active, so the plugin is
        // driven every block and never goes cold — the whole point of soft bypass.
        assert!(fx.is_enabled());

        // Yet the audible output is the untouched dry signal, not 2x.
        let mut buf = [1.0f32, -2.0];
        fx.process(&mut buf, 2);
        assert_eq!(buf, [1.0, -2.0]);

        // Re-enabling resumes the wet output with no cold-start gap.
        assert!(fx.set_param(PARAM_ENABLED, 1.0));
        let mut buf2 = [1.0f32, -2.0];
        fx.process(&mut buf2, 2);
        assert_eq!(buf2, [2.0, -4.0]);

        assert_eq!(fx.effect_type(), EffectType::Vst);
    }
}
