#![allow(clippy::unwrap_used)] // Frozen pure matcher contract literals.
use archive_server::reading::programme::{self, MatchableLied, ProgrammMatch};
fn normalize(text: &str, bare: bool) -> String {
	programme::normalize(text, bare).unwrap()
}
fn match_title(text: &str, lieder: &[MatchableLied]) -> ProgrammMatch {
	programme::match_title(text, lieder).unwrap()
}
use uuid::Uuid;
fn lied(id: u128, title: &str) -> MatchableLied {
	MatchableLied {
		id: Uuid::from_u128(id),
		title: title.into(),
	}
}
#[test]
fn exact_umlaut_and_bare_titles_are_sure_but_duplicates_require_editor_choice() {
	assert_eq!(
		normalize(" SÜSSER die, Glocken! ", false),
		"suesser die glocken"
	);
	assert_eq!(normalize("Süßer die Glocken", true), "susser die glocken");
	let songs = vec![lied(2, "Süßer die Glocken nie klingen")];
	let matched = match_title("SUESSER DIE GLOCKEN NIE KLINGEN!", &songs);
	assert_eq!(matched.lied.unwrap().id, songs[0].id);
	assert!(matched.sure);
	let matched = match_title("Ave Maria", &[lied(2, "Ave Maria"), lied(1, "Ave Maria")]);
	assert!(matched.lied.is_none());
	assert!(!matched.sure);
	assert_eq!(
		matched.candidates.iter().map(|l| l.id).collect::<Vec<_>>(),
		vec![Uuid::from_u128(1), Uuid::from_u128(2)]
	);
}
#[test]
fn actual_frozen_normalization_and_preselection_observations_match() {
	let observations: serde_json::Value = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-oracle.json"
	))
	.unwrap();
	for row in observations.as_array().unwrap() {
		match row["kind"].as_str() {
			Some("normalize") => assert_eq!(
				normalize(
					row["text"].as_str().unwrap(),
					row["bare"].as_bool().unwrap()
				),
				row["value"],
				"{row}"
			),
			Some("match") => {
				let songs = row["lieder"]
					.as_array()
					.unwrap()
					.iter()
					.map(|l| MatchableLied {
						id: Uuid::parse_str(l["Id"].as_str().unwrap()).unwrap(),
						title: l["Title"].as_str().unwrap().into(),
					})
					.collect::<Vec<_>>();
				let found = match_title(row["title"].as_str().unwrap(), &songs);
				assert_eq!(found.sure, row["result"]["Sure"], "{row}");
				assert_eq!(
					found.lied.map(|l| l.id.to_string()),
					row["result"]["Lied"]["Id"].as_str().map(str::to_owned),
					"{row}"
				);
				assert_eq!(
					found
						.candidates
						.iter()
						.map(|l| l.id.to_string())
						.collect::<Vec<_>>(),
					row["result"]["Candidates"]
						.as_array()
						.unwrap()
						.iter()
						.map(|l| l["Id"].as_str().unwrap().to_owned())
						.collect::<Vec<_>>(),
					"{row}"
				);
			}
			_ => {}
		}
	}
}
#[test]
fn actual_frozen_exhaustive_unicode_scalar_contexts_match() {
	let rows: Vec<serde_json::Value> = serde_json::from_str(include_str!(
		"../../../tests/fixtures/231-programme-unicode.json"
	))
	.unwrap();
	let rows = rows
		.iter()
		.map(|r| (r["cp"].as_u64().unwrap() as u32, r))
		.collect::<std::collections::HashMap<_, _>>();
	for cp in 0..=0x10ffff {
		let Some(scalar) = char::from_u32(cp) else {
			continue;
		};
		if cp == 65534 {
			assert!(programme::normalize(&format!("a{scalar}b"), false).is_err());
			continue;
		}
		let text = format!("a{scalar}b");
		for bare in [false, true] {
			let expected = rows
				.get(&cp)
				.map(|r| {
					r[if bare { "bare" } else { "full" }]
						.as_str()
						.unwrap()
						.to_owned()
				})
				.unwrap_or_else(|| {
					if cp <= 65535 {
						text.clone()
					} else {
						"a b".into()
					}
				});
			assert_eq!(normalize(&text, bare), expected, "U+{cp:04X}, bare={bare}");
		}
	}
}
