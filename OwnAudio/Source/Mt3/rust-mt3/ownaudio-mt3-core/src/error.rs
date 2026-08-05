//! Everything that can go wrong between "here is a wav" and "here are the notes".

use thiserror::Error;

/// Errors surfaced by the transcriber. The FFI layer maps these onto C error codes.
#[derive(Debug, Error)]
pub enum Mt3Error {
    /// A model or vocabulary file was not where we were told to look.
    #[error("model file not found: {0}")]
    ModelNotFound(String),

    /// The file is there but ONNX Runtime refused it.
    #[error("failed to load model {path}: {message}")]
    ModelLoad {
        /// Which file.
        path: String,
        /// What the runtime said. Kept as text because `ort`'s errors carry the builder they came
        /// from and are neither `Send` nor `Sync`.
        message: String,
    },

    /// vocab.json is malformed, or describes a codec we cannot decode.
    #[error("invalid vocabulary: {0}")]
    Vocab(String),

    /// A session ran but gave back something we did not expect — wrong rank, wrong name, NaNs.
    #[error("inference failed: {0}")]
    Inference(String),

    /// Resampling the input to the model's rate blew up.
    #[error("resampling failed: {0}")]
    Resample(String),

    /// Reading a file off disk failed.
    #[error(transparent)]
    Io(#[from] std::io::Error),
}

/// Shorthand used throughout the crate.
pub type Result<T> = std::result::Result<T, Mt3Error>;
