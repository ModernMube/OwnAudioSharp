fn main() {
    if std::env::var("CARGO_FEATURE_ASIO").is_ok() {
        validate_asio_sdk();
    }

    let crate_dir = std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR not set");
    let output_dir = std::path::PathBuf::from(&crate_dir).join("include");
    std::fs::create_dir_all(&output_dir).expect("Failed to create include/ directory");

    let config = cbindgen::Config::from_file(
        std::path::PathBuf::from(&crate_dir).join("cbindgen.toml"),
    )
    .expect("Unable to read cbindgen.toml");

    cbindgen::Builder::new()
        .with_crate(&crate_dir)
        .with_config(config)
        .generate()
        .expect("Unable to generate C bindings")
        .write_to_file(output_dir.join("ownaudio_ffi.h"));
}

/// Validates that ASIO_SDK_DIR points to a valid Steinberg ASIO SDK installation.
///
/// The Steinberg ASIO SDK is available from https://www.steinberg.net/developers/
/// under a dual licence (proprietary + GPLv2).  Download it, extract it, and set
/// the ASIO_SDK_DIR environment variable to the SDK root directory before building
/// with --features asio.
fn validate_asio_sdk() {
    let sdk_dir = std::env::var("ASIO_SDK_DIR").unwrap_or_else(|_| {
        panic!(
            "ASIO_SDK_DIR environment variable is not set.\n\
             Download the ASIO SDK from https://www.steinberg.net/developers/, \
             extract it, and set:\n\
             ASIO_SDK_DIR=<path to extracted SDK root>"
        )
    });

    let header_path = std::path::Path::new(&sdk_dir).join("common").join("asio.h");
    if !header_path.exists() {
        panic!(
            "ASIO SDK header not found at: {}\n\
             Expected 'common/asio.h' inside ASIO_SDK_DIR={}.\n\
             Re-download the SDK from https://www.steinberg.net/developers/ and verify the path.",
            header_path.display(),
            sdk_dir
        );
    }
}
