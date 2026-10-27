// libten_vad is not vendored (its license has extra conditions, see its LICENSE).
// Point TEN_VAD_LIB_DIR at the directory holding libten_vad.so, e.g. ten-vad/lib/Linux/x64,
// and put the same directory on LD_LIBRARY_PATH when running.
fn main() {
    println!("cargo:rerun-if-env-changed=TEN_VAD_LIB_DIR");
    if let Ok(dir) = std::env::var("TEN_VAD_LIB_DIR") {
        println!("cargo:rustc-link-search=native={dir}");
    }
    println!("cargo:rustc-link-lib=dylib=ten_vad");
}
