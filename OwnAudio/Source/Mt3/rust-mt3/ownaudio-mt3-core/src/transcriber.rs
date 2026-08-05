//! The thing the FFI layer actually holds: models plus vocabulary, audio in, notes out.

use crate::error::Result;
use crate::events::{Note, NoteDecoder};
use crate::preprocess;
use crate::session::{Decoder, Encoder};
use crate::vocab::Vocabulary;

/// Where the four exported files live. All of them come out of the same export run — mixing a
/// vocabulary with a decoder it was not dumped from produces confident nonsense.
#[derive(Debug, Clone)]
pub struct ModelPaths {
    /// Encoder graph, raw audio in.
    pub encoder: String,
    /// Decoder primed with the start token.
    pub decoder_init: String,
    /// Decoder step with a KV cache.
    pub decoder_step: String,
    /// Token codec description.
    pub vocab: String,
}

/// Knobs worth exposing; the defaults are what the reference implementation uses.
#[derive(Debug, Clone, Copy, Default)]
pub struct TranscribeOptions {
    /// ONNX Runtime intra-op threads. Zero lets the runtime decide.
    pub threads: u16,
    /// Drop percussion before returning. Chord detection wants this on.
    pub skip_drums: bool,
}

/// A loaded MT3 model. Loading is expensive, transcribing is not thread-safe — keep one per
/// worker, or lock around it.
pub struct Mt3Transcriber {
    encoder: Encoder,
    decoder: Decoder,
    vocab: Vocabulary,
    options: TranscribeOptions,
}

impl Mt3Transcriber {
    /// Loads the graphs and the vocabulary. Fails fast if any of the four files is missing.
    pub fn load(paths: &ModelPaths, options: TranscribeOptions) -> Result<Self> {
        let vocab = Vocabulary::from_file(&paths.vocab)?;
        let threads = options.threads;

        Ok(Self {
            encoder: Encoder::load(&paths.encoder, threads)?,
            decoder: Decoder::load(&paths.decoder_init, &paths.decoder_step, threads)?,
            vocab,
            options,
        })
    }

    /// Rate the audio is resampled to before it reaches the encoder.
    pub fn sample_rate(&self) -> u32 {
        self.vocab.sample_rate
    }

    /// Transcribes a whole track. `progress` is called with 0..1 after each segment, which is the
    /// only feedback there is — a long song is minutes of work.
    pub fn transcribe(
        &mut self,
        samples: &[f32],
        sample_rate: u32,
        channels: u16,
        mut progress: impl FnMut(f64),
    ) -> Result<Vec<Note>> {
        let mono = preprocess::to_mono(samples, channels);
        let audio = preprocess::resample(&mono, sample_rate, self.vocab.sample_rate)?;
        let segments = preprocess::segments(&audio, self.vocab.segment_samples());

        let mut decoder_state = NoteDecoder::new(&self.vocab);
        let mut tokens = Vec::with_capacity(self.vocab.max_target_length);

        for (index, segment) in segments.iter().enumerate() {
            let hidden = self.encoder.run(segment)?;
            self.decoder.generate(&hidden, &self.vocab, &mut tokens)?;
            decoder_state.push_segment(&tokens, index as f64 * self.vocab.segment_duration());

            progress((index + 1) as f64 / segments.len() as f64);
        }

        let dropped = decoder_state.dropped_tokens();
        if dropped > 0 {
            log::debug!("MT3 decoder skipped {dropped} tokens it could not place");
        }

        let track_end = audio.len() as f64 / self.vocab.sample_rate as f64;
        let notes = decoder_state.finish(track_end);

        Ok(match self.options.skip_drums {
            true => notes.into_iter().filter(|n| !n.is_drum).collect(),
            false => notes,
        })
    }
}
