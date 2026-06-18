use std::path::PathBuf;

fn main() {
    // Propagate the ABI version constant so Rust code can embed it via env!().
    println!("cargo:rustc-env=OWNAUDIO_ABI_VERSION=1");

    // Read version.json from the workspace root (two levels above this crate).
    let workspace_root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("ownaudio-ffi has no parent directory")
        .parent()
        .expect("OwnAudioEngineRust has no parent directory")
        .to_owned();

    let version_json = workspace_root.join("version.json");
    if version_json.exists() {
        let content = std::fs::read_to_string(&version_json)
            .unwrap_or_else(|_| String::from(r#"{"version":"0.0.0-local"}"#));

        let version = extract_version(&content).unwrap_or("0.0.0-local");

        // Emit as OWNAUDIO_VERSION so ffi_abi.rs can embed it at compile time.
        println!("cargo:rustc-env=OWNAUDIO_VERSION={version}");

        // Re-run when version.json changes.
        println!("cargo:rerun-if-changed={}", version_json.display());
    } else {
        println!("cargo:rustc-env=OWNAUDIO_VERSION=0.0.0-local");
    }

    // Allow CI to override the version via an environment variable.
    println!("cargo:rerun-if-env-changed=OWNAUDIO_VERSION");

    if std::env::var("CARGO_FEATURE_ASIO").is_ok() {
        validate_asio_sdk();
    }

    let crate_dir =
        std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR not set");
    let output_dir = PathBuf::from(&crate_dir).join("include");
    std::fs::create_dir_all(&output_dir).expect("Failed to create include/ directory");

    let config = cbindgen::Config::from_file(
        PathBuf::from(&crate_dir).join("cbindgen.toml"),
    )
    .expect("Unable to read cbindgen.toml");

    cbindgen::Builder::new()
        .with_crate(&crate_dir)
        .with_config(config)
        .generate()
        .expect("Unable to generate C bindings")
        .write_to_file(output_dir.join("ownaudio_ffi.h"));
}

/// Extracts the version string from a minimal `{"version":"x.y.z"}` JSON file.
///
/// Uses a simple byte-scan instead of pulling in a JSON parser as a build
/// dependency.  The format is controlled by version.json in this repo, so
/// a full-featured parser is unnecessary.
fn extract_version(content: &str) -> Option<&str> {
    let key = "\"version\"";
    let key_pos = content.find(key)?;
    let after_key = &content[key_pos + key.len()..];
    let colon_pos = after_key.find(':')?;
    let after_colon = after_key[colon_pos + 1..].trim_start();
    if after_colon.starts_with('"') {
        let inner = &after_colon[1..];
        let end = inner.find('"')?;
        Some(&inner[..end])
    } else {
        None
    }
}

/// Validates that ASIO_SDK_DIR points to a valid Steinberg ASIO SDK installation.
///
/// The Steinberg ASIO SDK is available from https://www.steinberg.net/developers/
/// under a dual licence (proprietary + GPLv2).  Download it, extract it, and set
/// the ASIO_SDK_DIR environment variable to the SDK root directory before building
/// with --features asio.
fn validate_asio_sdk() {
    let sdk_dir = match std::env::var("ASIO_SDK_DIR") {
        Ok(d) => d,
        Err(_) => {
            // Emit a warning instead of a hard panic so CI can fall back to WASAPI-only.
            println!(
                "cargo:warning=ASIO_SDK_DIR is not set. \
                 Building without ASIO support (WASAPI-only fallback). \
                 Download the ASIO SDK from https://www.steinberg.net/developers/ \
                 and set ASIO_SDK_DIR=<path> to enable ASIO."
            );
            // Signal to the linker that ASIO is unavailable; the feature flag
            // still compiles but host_api.rs will return HostApiNotAvailable.
            return;
        }
    };

    let header_path =
        std::path::Path::new(&sdk_dir).join("common").join("asio.h");
    if !header_path.exists() {
        println!(
            "cargo:warning=ASIO SDK header not found at: {}. \
             Expected 'common/asio.h' inside ASIO_SDK_DIR={}. \
             Re-download the SDK from https://www.steinberg.net/developers/ and verify the path.",
            header_path.display(),
            sdk_dir
        );
    }
}
