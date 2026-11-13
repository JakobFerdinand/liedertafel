fn main() {
	// Stable Rust cannot track newly added files from migrate! alone.
	println!("cargo:rerun-if-changed=migrations");
}
