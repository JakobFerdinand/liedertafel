//! Explicit dormant pure-logic compatibility seam, required by #218/#221.
use archive_server::auth::verify_identity_password;
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::Value;

#[tokio::test]
async fn dotnet_identity_pbkdf2_v2_and_all_v3_prfs_verify_with_bounded_work() {
	let vectors: Vec<Value> = serde_json::from_str(include_str!(
		"../../../tests/fixtures/identity-passwords.json"
	))
	.unwrap();
	for vector in vectors {
		let hash = vector["hash"].as_str().unwrap();
		let password = vector["password"].as_str().unwrap();
		assert!(
			verify_identity_password(hash, password).await,
			"{}",
			vector["mode"]
		);
		assert!(!verify_identity_password(hash, "wrong").await);
		// Identity V3 permits subkeys >=16 bytes, including a shortened valid
		// prefix. Truncate below its minimum, not merely by one Base64 quantum.
		assert!(!verify_identity_password(&hash[..hash.len() / 2], password).await);
		let mut bytes = STANDARD.decode(hash).unwrap();
		if bytes[0] == 1 {
			bytes[5..9].copy_from_slice(&u32::MAX.to_be_bytes());
			assert!(!verify_identity_password(&STANDARD.encode(&bytes), password).await);
			bytes[5..9].copy_from_slice(&1000u32.to_be_bytes());
			bytes[1..5].copy_from_slice(&9u32.to_be_bytes());
			assert!(!verify_identity_password(&STANDARD.encode(&bytes), password).await);
		}
	}
	assert!(!verify_identity_password("invalid-base64!", "secret").await);
	assert!(!verify_identity_password(&"A".repeat(10000), "secret").await);
}
