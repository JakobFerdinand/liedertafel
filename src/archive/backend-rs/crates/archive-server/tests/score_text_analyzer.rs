use archive_server::extraction::score::analyze;
#[test]
fn voice_part_and_syllables() {
	let a = analyze(Some(
		"Tenor 1 A♭\n44 .å\nHo -\nåê\nsi -\nå\nan -\nå\nna,\n.å\nHo -\nåê\nsi -\nå\nan -\nå\nna,",
	));
	assert_eq!(a.facts.voice.as_deref(), Some("Tenor 1"));
	assert_eq!(a.facts.musical_key.as_deref(), Some("A♭"));
	assert_eq!(a.facts.time_signature.as_deref(), Some("4/4"));
	assert_eq!(a.clean_text, "Tenor 1 A♭ 44 Hosianna, Hosianna,");
}
#[test]
fn heading_staff_and_signatures() {
	for (text, expected) in [
		("1. Tenor\nFreude", "Tenor 1"),
		("II. Bass\nFreude", "Bass 2"),
		("Bass II\nFreude", "Bass 2"),
		("BARITON\nFreude", "Bariton"),
		("Tenor 1\nTenor 2\nFreude", "Tenor 1+2"),
		("Sopran\nAlt\nFreude", "Partitur"),
	] {
		assert_eq!(analyze(Some(text)).facts.voice.as_deref(), Some(expected));
	}
	for (text, expected) in [
		("Abendlied in As-Dur", "As-Dur"),
		("Abendlied d moll", "d-Moll"),
		("Evening Song\nBb major", "B♭-Dur"),
		("Evening Song\nF# minor", "f♯-Moll"),
		("Bass 2 F#\n34", "F♯"),
		("Lied b moll", "b-Moll"),
	] {
		assert_eq!(
			analyze(Some(text)).facts.musical_key.as_deref(),
			Some(expected)
		);
	}
	for (text, expected) in [
		("Bass 1\n3/4\nla", "3/4"),
		("Bass 1\n68\nla", "6/8"),
		("Bass 1\n4\n4\nla", "4/4"),
	] {
		assert_eq!(
			analyze(Some(text)).facts.time_signature.as_deref(),
			Some(expected)
		);
	}
}
#[test]
fn credits_tempo_noise_and_heading_bounds() {
	let a = analyze(Some(
		"Abendlied\nText: Matthias Claudius   Musik: J. A. P. Schulz\nSatz:\nFranz Huber\nAndante å = 72\nDer Mond ist auf - ge - gan - gen\n© 1998 Musikverlag Beispiel",
	));
	assert_eq!(a.facts.lyricist.as_deref(), Some("Matthias Claudius"));
	assert_eq!(a.facts.composer.as_deref(), Some("J. A. P. Schulz"));
	assert_eq!(a.facts.arranger.as_deref(), Some("Franz Huber"));
	assert_eq!(a.facts.tempo.as_deref(), Some("Andante, 72 Schläge/min"));
	assert!(a.clean_text.contains("Der Mond ist aufgegangen"));
	let a = analyze(Some("Lied\nWorte und Weise: Hans Baumann"));
	assert_eq!(a.facts.composer, a.facts.lyricist);
	assert_eq!(a.facts.composer.as_deref(), Some("Hans Baumann"));
	assert_eq!(analyze(Some("Wien - Graz")).clean_text, "Wien - Graz");
	for text in [None, Some(""), Some("å åê .å")] {
		assert!(analyze(text).clean_text.is_empty());
	}
	let text = format!(
		"Abendlied\n{}\nAlt\nund Jung singt der Bass im Chor\n44",
		"la la la la\n".repeat(20)
	);
	let a = analyze(Some(&text));
	assert!(a.facts.voice.is_none());
	assert!(a.facts.musical_key.is_none());
	assert!(a.facts.time_signature.is_none());
}
