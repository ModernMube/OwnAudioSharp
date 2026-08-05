//! ONNX Runtime sessions and the autoregressive decode loop.
//!
//! Three graphs come out of the export: the encoder (raw audio in, hidden states out — the mel
//! spectrogram lives inside it), a decoder primed with the start token, and a decoder step that
//! takes one token plus the KV cache. The split matters: without the cache the decoder would
//! re-attend over the whole prefix at every one of a thousand steps per segment, which turns a
//! four-minute song into an afternoon.

use std::borrow::Cow;

use ort::session::{builder::GraphOptimizationLevel, Session, SessionInputValue};
use ort::value::{DynValue, Value};

use crate::error::{Mt3Error, Result};
use crate::vocab::Vocabulary;

/// A float tensor on its way in or out of a session. Deliberately not `ndarray` — tying this
/// crate to whatever version `ort` happens to re-export is not worth the two lines it saves.
#[derive(Debug, Clone)]
pub struct Tensor {
    /// Dimensions, outermost first.
    pub shape: Vec<usize>,
    /// Row-major payload.
    pub data: Vec<f32>,
}

impl Tensor {
    fn into_value(self) -> Result<DynValue> {
        Value::from_array((self.shape, self.data))
            .map(|t| t.into_dyn())
            .map_err(inference)
    }

    fn to_value(&self) -> Result<DynValue> {
        self.clone().into_value()
    }
}

/// Loads a session off disk with a sensible thread count.
fn open(path: &str, threads: u16) -> Result<Session> {
    if !std::path::Path::new(path).is_file() {
        return Err(Mt3Error::ModelNotFound(path.to_string()));
    }

    // `ort`'s builder errors hand the half-built builder back inside the error, so they cannot be
    // stored or forwarded — flatten each one to text at the point it happens.
    let failed = |e: &dyn std::fmt::Display| Mt3Error::ModelLoad {
        path: path.to_string(),
        message: e.to_string(),
    };

    let mut builder = Session::builder()
        .map_err(|e| failed(&e))?
        .with_optimization_level(GraphOptimizationLevel::Level3)
        .map_err(|e| failed(&e))?;

    if threads > 0 {
        builder = builder
            .with_intra_threads(threads as usize)
            .map_err(|e| failed(&e))?;
    }

    builder.commit_from_file(path).map_err(|e| failed(&e))
}

/// Pulls a float tensor out of a session result by name.
fn extract(outputs: &ort::session::SessionOutputs, name: &str) -> Result<Tensor> {
    let value = outputs
        .get(name)
        .ok_or_else(|| Mt3Error::Inference(format!("model produced no output named {name}")))?;

    let (shape, data) = value.try_extract_tensor::<f32>().map_err(inference)?;

    Ok(Tensor {
        shape: shape.iter().map(|d| *d as usize).collect(),
        data: data.to_vec(),
    })
}

/// The encoder graph. One pass per audio segment.
pub struct Encoder {
    session: Session,
    input: String,
    output: String,
}

impl Encoder {
    /// Opens the encoder and remembers its single input/output name.
    pub fn load(path: &str, threads: u16) -> Result<Self> {
        let session = open(path, threads)?;
        let input = first_name(
            session.inputs().iter().map(|i| i.name().to_string()),
            path,
            "input",
        )?;
        let output = first_name(
            session.outputs().iter().map(|o| o.name().to_string()),
            path,
            "output",
        )?;

        Ok(Self {
            session,
            input,
            output,
        })
    }

    /// Runs one segment of raw audio through, giving back `[1, frames, hidden]` hidden states.
    pub fn run(&mut self, audio: &[f32]) -> Result<Tensor> {
        let input = Tensor {
            shape: vec![1, audio.len()],
            data: audio.to_vec(),
        };

        let outputs = self
            .session
            .run(ort::inputs![self.input.as_str() => input.into_value()?])
            .map_err(inference)?;

        extract(&outputs, &self.output)
    }
}

/// The two decoder graphs, driven as one greedy generator.
pub struct Decoder {
    init: Session,
    step: Session,
    /// `past_*` input name paired with the `present_*` output it is fed from.
    cache: Vec<(String, String)>,
}

impl Decoder {
    /// Opens both decoder graphs and pairs up their KV cache tensors.
    pub fn load(init_path: &str, step_path: &str, threads: u16) -> Result<Self> {
        let init = open(init_path, threads)?;
        let step = open(step_path, threads)?;

        // `past_key_values.0.decoder.key` in, `present.0.decoder.key` out — the export keeps the
        // suffixes aligned, so pairing on the tail of the name is enough.
        let mut cache: Vec<(String, String)> = step
            .inputs()
            .iter()
            .filter(|i| i.name().starts_with("past"))
            .filter_map(|i| {
                let suffix = i.name().trim_start_matches("past_key_values");
                step.outputs()
                    .iter()
                    .find(|o| o.name().starts_with("present") && o.name().ends_with(suffix))
                    .map(|o| (i.name().to_string(), o.name().to_string()))
            })
            .collect();
        cache.sort();

        if cache.is_empty() {
            return Err(Mt3Error::Inference(format!(
                "{step_path} exposes no past/present cache tensors — export it with use_cache=True"
            )));
        }

        Ok(Self { init, step, cache })
    }

    /// Greedily generates tokens for one segment, stopping at EOS or `max_target_length`.
    ///
    /// The encoder states go in once and are then borrowed by every step — they are the largest
    /// tensor in the loop, and copying them per token would dominate the runtime.
    pub fn generate(
        &mut self,
        encoder_states: &Tensor,
        vocab: &Vocabulary,
        out: &mut Vec<u32>,
    ) -> Result<()> {
        out.clear();

        let encoder_value = encoder_states.to_value()?;
        let (mut token, mut cache) = {
            let outputs = self
                .init
                .run(ort::inputs![
                    "input_ids" => token_value(vocab.start_token())?,
                    "encoder_hidden_states" => &encoder_value,
                ])
                .map_err(inference)?;

            (argmax_last(&outputs)?, snapshot(&outputs, &self.cache)?)
        };

        for _ in 0..vocab.max_target_length {
            if token == vocab.eos_token() {
                break;
            }
            out.push(token);

            let mut inputs: Vec<(Cow<'static, str>, SessionInputValue)> =
                Vec::with_capacity(self.cache.len() + 2);
            inputs.push(("input_ids".into(), token_value(token)?.into()));
            inputs.push(("encoder_hidden_states".into(), (&encoder_value).into()));

            for ((past, _), tensor) in self.cache.iter().zip(cache) {
                inputs.push((past.clone().into(), tensor.into_value()?.into()));
            }

            let outputs = self.step.run(inputs).map_err(inference)?;
            token = argmax_last(&outputs)?;
            cache = snapshot(&outputs, &self.cache)?;
        }

        Ok(())
    }
}

/// Copies the `present_*` tensors out of a run so the sessions can be borrowed again.
fn snapshot(
    outputs: &ort::session::SessionOutputs,
    cache: &[(String, String)],
) -> Result<Vec<Tensor>> {
    cache
        .iter()
        .map(|(_, present)| extract(outputs, present))
        .collect()
}

/// One token as the `[1, 1]` int64 tensor the decoders expect.
fn token_value(token: u32) -> Result<DynValue> {
    Value::from_array((vec![1usize, 1], vec![token as i64]))
        .map(|t| t.into_dyn())
        .map_err(inference)
}

/// Picks the highest-scoring token from the last position of a `[1, seq, vocab]` logit tensor.
fn argmax_last(outputs: &ort::session::SessionOutputs) -> Result<u32> {
    let logits = extract(outputs, "logits")?;

    let vocab_size = *logits
        .shape
        .last()
        .ok_or_else(|| Mt3Error::Inference("logits tensor has no dimensions".to_string()))?;
    if vocab_size == 0 || logits.data.len() < vocab_size {
        return Err(Mt3Error::Inference("logits tensor is empty".to_string()));
    }

    let last = &logits.data[logits.data.len() - vocab_size..];
    let best = last
        .iter()
        .enumerate()
        .max_by(|a, b| a.1.total_cmp(b.1))
        .map(|(index, _)| index)
        .expect("the slice is non-empty, checked just above");

    Ok(best as u32)
}

fn first_name(mut names: impl Iterator<Item = String>, path: &str, what: &str) -> Result<String> {
    names
        .next()
        .ok_or_else(|| Mt3Error::Inference(format!("{path} declares no {what}")))
}

fn inference<E: std::fmt::Display>(err: E) -> Mt3Error {
    Mt3Error::Inference(err.to_string())
}
