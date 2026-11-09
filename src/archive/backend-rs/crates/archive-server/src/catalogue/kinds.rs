//! Frozen DateiKindRules and container recognition. This inspects headers, not codecs.
pub(super) const UNSUPPORTED: &str = "Dieser Dateityp ist für diese Art nicht erlaubt.";
const AUDIO: &[&str] = &[
	"audio/mpeg",
	"audio/mp4",
	"audio/x-m4a",
	"audio/wav",
	"audio/ogg",
];
const OFFICE: &[&str] = &[
	"application/vnd.openxmlformats-officedocument.wordprocessingml.document",
	"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
	"application/vnd.openxmlformats-officedocument.presentationml.presentation",
];
pub(super) fn default_type(kind: &str) -> &'static str {
	if matches!(kind, "uebe" | "aufnahme") {
		"audio/mpeg"
	} else {
		"application/pdf"
	}
}
pub(super) fn allowed(kind: &str, content: &str) -> bool {
	match kind {
		"noten" => content == "application/pdf",
		"uebe" => AUDIO.contains(&content) || ["audio/midi", "audio/x-midi"].contains(&content),
		"aufnahme" => {
			AUDIO.contains(&content)
				|| [
					"audio/webm",
					"video/mp4",
					"video/quicktime",
					"video/webm",
					"video/x-matroska",
					"video/x-msvideo",
				]
				.contains(&content)
		}
		"sonstiges" => {
			[
				"application/pdf",
				"image/jpeg",
				"image/png",
				"image/webp",
				"text/plain",
			]
			.contains(&content)
				|| OFFICE.contains(&content)
		}
		_ => false,
	}
}
pub(super) fn invalid_message(kind: &str) -> &'static str {
	match kind {
		"noten" => "Die Datei ist kein gültiges PDF.",
		"uebe" => "Die Datei ist keine gültige Audio- oder MIDI-Datei.",
		"aufnahme" => "Die Datei ist keine gültige Audio- oder Videoaufnahme.",
		_ => "Die Datei passt nicht zu ihrem Dateityp.",
	}
}
pub(super) fn accepts(kind: &str, header: Option<&[u8]>, content: &str) -> bool {
	let content = content.to_ascii_lowercase();
	if !allowed(kind, &content) {
		return false;
	}
	let Some(h) = header else {
		return false;
	};
	match kind {
		"noten" => h.starts_with(b"%PDF-"),
		"sonstiges" => match content.as_str() {
			"application/pdf" => h.starts_with(b"%PDF-"),
			"image/jpeg" => h.starts_with(&[255, 216, 255]),
			"image/png" => h.starts_with(b"\x89PNG\r\n\x1a\n"),
			"image/webp" => h.starts_with(b"RIFF") && h.get(8..12) == Some(b"WEBP"),
			"text/plain" => !h.contains(&0),
			_ => h.starts_with(b"PK\x03\x04"),
		},
		_ => {
			if h.is_empty() {
				return false;
			}
			if ["audio/midi", "audio/x-midi"].contains(&content.as_str()) {
				return h.starts_with(b"MThd");
			}
			let subtype = |s: &str| {
				let sub = s.split('/').nth(1).unwrap_or("");
				if sub == "x-m4a" {
					"mp4".to_owned()
				} else if sub == "octet-stream" {
					String::new()
				} else {
					sub.to_owned()
				}
			};
			let detected = detect(h, content.starts_with("video/"));
			let family = subtype(&content);
			!family.is_empty() && subtype(detected) == family
		}
	}
}
pub(super) fn detect(h: &[u8], video: bool) -> &'static str {
	if h.len() >= 12 && h.get(4..8) == Some(b"ftyp") {
		let brand = &h[8..12];
		if brand == b"qt  " {
			return "video/quicktime";
		}
		if brand.starts_with(b"3g") {
			return "video/3gpp";
		}
		if [b"M4A ", b"M4B "].iter().any(|b| b.as_slice() == brand) {
			return "audio/mp4";
		}
		if [
			b"isom", b"iso2", b"iso3", b"iso4", b"iso5", b"iso6", b"mp41", b"mp42", b"avc1",
			b"M4V ", b"dash", b"mmp4", b"MSNV",
		]
		.iter()
		.any(|b| b.as_slice() == brand)
		{
			return if video { "video/mp4" } else { "audio/mp4" };
		}
		return "application/octet-stream";
	}
	if h.starts_with(b"\x1a\x45\xdf\xa3") {
		return if h.windows(4).any(|s| s == b"webm") {
			if video { "video/webm" } else { "audio/webm" }
		} else {
			"video/x-matroska"
		};
	}
	if h.starts_with(b"RIFF") {
		if h.get(8..12) == Some(b"WAVE") {
			return "audio/wav";
		}
		if h.get(8..12) == Some(b"AVI ") {
			return "video/x-msvideo";
		}
	}
	if h.starts_with(b"ID3") || mpeg(h) {
		return "audio/mpeg";
	}
	if h.starts_with(b"fLaC") {
		return "audio/flac";
	}
	if h.starts_with(b"OggS") {
		return if video { "video/ogg" } else { "audio/ogg" };
	}
	"application/octet-stream"
}
fn mpeg(h: &[u8]) -> bool {
	h.len() >= 4
		&& h[0] == 255
		&& (h[1] & 0xe0) == 0xe0
		&& ((h[1] >> 3) & 3) != 1
		&& ((h[1] >> 1) & 3) == 1
		&& !matches!(h[2] >> 4, 0 | 15)
		&& ((h[2] >> 2) & 3) != 3
}
