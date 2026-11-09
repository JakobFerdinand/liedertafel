mod support;
use serde_json::json;
use support::{App, catalogue::*};

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn a_phrase_read_from_a_scan_finds_the_lied_and_names_source() {
	let app = App::new().await;
	let mut member = app.member().await;
	let id = lied(&app, "Waldfahrt", json!({})).await;
	lied(&app, "Heimweh", json!({})).await;
	let d = datei(&app, id, false, "noten", "application/pdf").await;
	text(&app, d, "Im Wald grünen die Tannen", 2, 1).await;
	let (status, body) = member.get(&query("tannen")).await;
	assert_eq!(status, 200);
	assert_eq!(titles(&body), ["Waldfahrt"]);
	assert_eq!(body["lieder"][0]["matchIn"], json!(["in den Noten"]));
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn liedtext_and_creators_are_searched_and_named() {
	let app = App::new().await;
	let mut member = app.member().await;
	for (t, f) in [
		("Peter und der Drache", json!({"composer":"Eduard Mörike"})),
		("Wanderschaft", json!({"arranger":"Emma Weiß"})),
		("Ohne Angaben", json!({"lyricist":"Matthias Claudius"})),
		(
			"Stille Frische",
			json!({"lyrics":"Im grünen Walde ruht der Tag"}),
		),
	] {
		lied(&app, t, f).await;
	}
	for (q, labels) in [
		("moerike", vec!["beim Komponisten"]),
		("weiss", vec!["beim Bearbeiter"]),
		("claudius", vec!["beim Texter"]),
		("ruht", vec!["im Liedtext"]),
		("frische walde", vec!["im Titel", "im Liedtext"]),
	] {
		let (_, body) = member.get(&query(q)).await;
		assert_eq!(body["total"], 1);
		assert_eq!(body["lieder"][0]["matchIn"], json!(labels));
	}
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn cold_catalogue_with_near_500_lieder_stays_fast() {
	let app = App::new().await;
	let mut member = app.member().await;
	for i in 0..499 {
		let id = lied(&app, &format!("Lied {i:04}"), json!({})).await;
		let d = datei(&app, id, false, "noten", "application/pdf").await;
		if i == 250 {
			text(
				&app,
				d,
				"Nur gestrichelt Paula steht dort tief im Bergwald",
				2,
				1,
			)
			.await;
		}
	}
	let start = std::time::Instant::now();
	let (_, body) = member.get(&query("paula bergwald")).await;
	assert_eq!(titles(&body), ["Lied 0250"]);
	assert_eq!(body["lieder"][0]["matchIn"], json!(["in den Noten"]));
	assert!(start.elapsed() < std::time::Duration::from_secs(10));
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn replaced_noten_stops_being_found_from_baseline_replacement_rows() {
	let app = App::new().await;
	let mut member = app.member().await;
	let id = lied(&app, "Waldfahrt", json!({})).await;
	let old = datei(&app, id, false, "noten", "application/pdf").await;
	text(&app, old, "Im Wald grünen die Tannen", 2, 1).await;
	assert_eq!(member.get(&query("tannen")).await.1["total"], 1);
	let new = datei(&app, id, false, "noten", "application/pdf").await;
	text(&app, new, "Nur Wolken ziehen über das Land", 2, 1).await;
	sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now(),"ReplacedByDateiId"=$2 WHERE "Id"=$1"#)
		.bind(old)
		.bind(new)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(member.get(&query("tannen")).await.1["total"], 0);
	assert_eq!(
		member.get(&query("wolken")).await.1["lieder"][0]["matchIn"],
		json!(["in den Noten"])
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn german_spell_variants_match_umlauts_and_sz() {
	let app = App::new().await;
	let mut member = app.member().await;
	lied(
		&app,
		"Weinheimer Straße",
		json!({"lyrics":"Es grünt so grün im Kuhtal"}),
	)
	.await;
	for q in ["strasse", "straße", "gruen", "grün"] {
		assert_eq!(
			titles(&member.get(&query(q)).await.1),
			["Weinheimer Straße"]
		);
	}
	app.close().await;
}
