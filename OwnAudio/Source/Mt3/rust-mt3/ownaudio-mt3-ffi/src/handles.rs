//! Opaque handle types the managed layer holds as `SafeHandle` pointers.

/// A loaded MT3 model — encoder, decoder pair and vocabulary.
///
/// Never constructed on this side; the pointer handed out is really a `Box<Mt3Transcriber>`.
#[repr(C)]
pub struct Mt3TranscriberHandle {
    _private: [u8; 0],
}
