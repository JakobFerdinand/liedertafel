use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use rand::Rng;
use sha2::{Digest, Sha256};

pub(super) fn opaque() -> String {
	URL_SAFE_NO_PAD.encode(rand::rng().random::<[u8; 32]>())
}
pub(super) fn digest(value: impl AsRef<[u8]>) -> Vec<u8> {
	Sha256::digest(value).to_vec()
}
pub(super) fn token_digest(value: &str) -> Option<Vec<u8>> {
	(value.len() == 43 && URL_SAFE_NO_PAD.decode(value).ok()?.len() == 32).then(|| digest(value))
}
pub(super) fn code_hash(code: &str, salt: &[u8]) -> Vec<u8> {
	let mut hash = Sha256::new();
	hash.update(salt);
	hash.update(code.as_bytes());
	hash.finalize().to_vec()
}
pub fn normalize_email(email: Option<&str>) -> Option<String> {
	let email = email?;
	if email.encode_utf16().count() > 320 {
		return None;
	}
	let email = email.trim();
	let address: lettre::message::Mailbox = email.parse().ok()?;
	if address.email.to_string().contains('@') {
		// Identity uses invariant *simple* uppercase, not Rust's full expanding
		// string case mapping. In particular ß must stay ß (not SS); existing
		// German member addresses otherwise become unfindable after the port.
		Some(
			email
				.chars()
				.map(|c| {
					let mut upper = c.to_uppercase();
					let first = upper.next().unwrap_or(c);
					if upper.next().is_some() || matches!(c, '\u{0131}' | '\u{017f}') {
						c
					} else {
						first
					}
				})
				.collect(),
		)
	} else {
		None
	}
}

/// Dormant compatibility seam only: no password route, creation, or rehash.
/// Bounded before allocation/CPU; CPU work is off the async request executor.
pub async fn verify_identity_password(encoded: &str, password: &str) -> bool {
	if encoded.len() > 2048 || password.len() > 4096 {
		return false;
	}
	static WORK: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(2);
	let Ok(permit) = WORK.acquire().await else {
		return false;
	};
	let encoded = encoded.to_owned();
	let password = password.to_owned();
	tokio::task::spawn_blocking(move || {
		let _permit = permit;
		verify_password(&encoded, &password)
	})
	.await
	.unwrap_or(false)
}
fn verify_password(encoded: &str, password: &str) -> bool {
	use base64::engine::general_purpose::STANDARD;
	use subtle::ConstantTimeEq;
	let Ok(bytes) = STANDARD.decode(encoded) else {
		return false;
	};
	let (prf, iterations, salt, expected) = match bytes.first() {
		Some(0) if bytes.len() == 49 => (0, 1000, &bytes[1..17], &bytes[17..]),
		Some(1) if bytes.len() >= 13 => {
			let read = |start| {
				bytes
					.get(start..start + 4)
					.and_then(|b| b.try_into().ok())
					.map(u32::from_be_bytes)
			};
			let (Some(prf), Some(iterations), Some(salt_len)) = (read(1), read(5), read(9)) else {
				return false;
			};
			let salt_len = salt_len as usize;
			if !(16..=64).contains(&salt_len)
				|| bytes.len() < 13 + salt_len + 16
				|| bytes.len() > 13 + salt_len + 64
				|| !(1..=600_000).contains(&iterations)
			{
				return false;
			}
			(
				prf,
				iterations,
				&bytes[13..13 + salt_len],
				&bytes[13 + salt_len..],
			)
		}
		_ => return false,
	};
	let mut actual = vec![0; expected.len()];
	match prf {
		0 => pbkdf2::pbkdf2_hmac::<sha1::Sha1>(password.as_bytes(), salt, iterations, &mut actual),
		1 => {
			pbkdf2::pbkdf2_hmac::<sha2::Sha256>(password.as_bytes(), salt, iterations, &mut actual)
		}
		2 => {
			pbkdf2::pbkdf2_hmac::<sha2::Sha512>(password.as_bytes(), salt, iterations, &mut actual)
		}
		_ => return false,
	}
	bool::from(actual.ct_eq(expected))
}
