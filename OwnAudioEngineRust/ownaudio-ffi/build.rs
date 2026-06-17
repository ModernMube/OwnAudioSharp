fn main() {
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
