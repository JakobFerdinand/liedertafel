use archive_server::catalogue::{Lied, search::*};
use std::collections::HashMap;
use uuid::Uuid;
fn lied(title: &str) -> Lied {
	Lied {
		id: Uuid::new_v4(),
		title: title.into(),
		..Default::default()
	}
}

#[test]
fn tokens_are_folded_and_drop_empty() {
	assert_eq!(tokens_of("ÜBER den  Wolken"), ["uber", "den", "wolken"]);
	assert!(tokens_of("  \t ").is_empty());
	assert_eq!(
		tokens_of("a\tb c"),
		["a\tb", "c"],
		"only spaces split tokens, not every whitespace"
	);
	for variant in ["Müller", "Mueller", "Muller"] {
		assert_eq!(fold(Some(variant)), "muller");
	}
	assert_eq!(fold(Some("Weiß Straße")), "weiss strasse");
}
#[test]
fn fields_without_search_labels_are_not_searched() {
	let row = Lied {
		voice_configuration: Some("einzigartigtrommelwande".into()),
		musical_key: Some("tonartwort".into()),
		language: Some("sprachwort".into()),
		occasion: Some("anlasswort".into()),
		..lied("Titel")
	};
	for q in ["trommelwande", "tonartwort", "sprachwort", "anlasswort"] {
		assert!(
			matching(
				&tokens_of(q),
				std::slice::from_ref(&row),
				&HashMap::new(),
				q
			)
			.is_empty()
		);
	}
}
#[test]
fn fields_expose_only_title_creators_lyrics_and_noten_in_order() {
	let row = Lied {
		composer: Some("warmwort".into()),
		arranger: Some("warmwort".into()),
		lyricist: Some("warmwort".into()),
		lyrics: Some("warmwort".into()),
		..lied("Titel")
	};
	assert_eq!(
		fields_of(&row, Some("notenwort"))
			.iter()
			.map(|f| f.label)
			.collect::<Vec<_>>(),
		[
			"im Titel",
			"beim Komponisten",
			"beim Bearbeiter",
			"beim Texter",
			"im Liedtext",
			"in den Noten"
		]
	);
}
#[test]
fn every_token_must_stand_in_a_field() {
	let row = Lied {
		composer: Some("Jakob Hosianna Komponist".into()),
		..lied("Hosianna")
	};
	assert_eq!(
		matching(
			&tokens_of("hosianna komponist"),
			std::slice::from_ref(&row),
			&HashMap::new(),
			"x y"
		)
		.len(),
		1
	);
	assert!(matching(&tokens_of("hosianna fehlt"), &[row], &HashMap::new(), "x y").is_empty());
}
#[test]
fn ranking_prefers_exact_title_then_title_then_rest() {
	let rows = [
		Lied {
			lyrics: Some("uber wolken lieber Jakob".into()),
			..lied("Anderes")
		},
		lied("Uber Wolken"),
		lied("Uber Wolken Morgen"),
	];
	let matches = matching(
		&tokens_of("uber wolken"),
		&rows,
		&HashMap::new(),
		"uber wolken",
	);
	assert_eq!(
		matches
			.iter()
			.map(|m| (m.lied.title.as_str(), m.rank))
			.collect::<Vec<_>>(),
		[
			("Uber Wolken", 0),
			("Uber Wolken Morgen", 1),
			("Anderes", 2)
		]
	);
}
#[test]
fn equal_ranks_order_by_folded_title_then_id() {
	let mut a = lied("Mond");
	a.id = Uuid::from_u128(1);
	let mut b = lied("mond");
	b.id = Uuid::from_u128(2);
	let rows = [b, a];
	let matches = matching(&tokens_of("mond"), &rows, &HashMap::new(), "mond");
	assert_eq!(
		matches.iter().map(|m| m.lied.id).collect::<Vec<_>>(),
		[Uuid::from_u128(1), Uuid::from_u128(2)]
	);
}
#[test]
fn empty_queries_list_all_in_title_order() {
	let rows = [lied("Z"), lied("A")];
	let matches = matching(&[], &rows, &HashMap::new(), "");
	assert_eq!(
		matches
			.iter()
			.map(|m| (m.lied.title.as_str(), m.rank))
			.collect::<Vec<_>>(),
		[("A", 2), ("Z", 2)]
	);
}
#[test]
fn paging_takes_only_requested_window() {
	let rows = [lied("Lied 0"), lied("Lied 1"), lied("Lied 2")];
	let matches = matching(&[], &rows, &HashMap::new(), "");
	assert_eq!(page(&matches, 1, 2).len(), 2);
	assert_eq!(page(&matches, 2, 2).len(), 1);
	assert!(page(&matches, 3, 2).is_empty());
	// Frozen C# uses unchecked Int32 multiplication before LINQ Skip. The
	// executed .NET oracle gives -40 (Skip treats it as zero), not an empty page.
	assert_eq!(page(&matches, i32::MAX, 20).len(), 3);
	assert_eq!(requested_page(None), 1);
	assert_eq!(requested_page(Some(-1)), 1);
}
