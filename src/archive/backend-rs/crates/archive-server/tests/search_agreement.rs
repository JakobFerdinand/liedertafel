//! #223 component of SearchAgreement. #232 must add the actual catalogue_search
//! adapter comparison; no chat route/tool is manufactured to claim that gate.
// HTTP/shared-selection fixture assertions live in a helper outside #[test].
#![allow(clippy::unwrap_used)]
mod support;
use archive_server::catalogue::{queries, search};
use serde_json::json;
use support::{App, catalogue::*};
async fn both(
	app: &App,
	member: &mut support::Client,
	q: &str,
) -> (serde_json::Value, Vec<String>, usize) {
	let (_, body) = member.get(&query(q)).await;
	let tokens = search::tokens_of(q);
	let rows = queries::lieder(&app.pool).await.unwrap();
	let notes = search::load_noten_texts(&app.pool, &tokens).await.unwrap();
	let matches = search::matching(&tokens, &rows, &notes, q);
	let titles = search::page(&matches, 1, 10)
		.iter()
		.map(|m| m.lied.title.clone())
		.collect();
	(body, titles, matches.len())
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL search component"]
async fn empty_query_shared_selection_matches_member_order_with_different_page_sizes() {
	let app = App::new().await;
	let mut member = app.member().await;
	for i in 0..12 {
		lied(&app, &format!("Lied {i:02}"), json!({})).await;
	}
	let (body, shared, total) = both(&app, &mut member, "").await;
	assert_eq!(total, 12);
	assert_eq!(body["total"], 12);
	assert_eq!(
		shared,
		(0..10).map(|i| format!("Lied {i:02}")).collect::<Vec<_>>()
	);
	assert_eq!(
		titles(&body),
		(0..12).map(|i| format!("Lied {i:02}")).collect::<Vec<_>>()
	);
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL search component"]
async fn exact_title_title_and_lyrics_have_same_order_in_member_and_shared_selection() {
	let app = App::new().await;
	let mut member = app.member().await;
	for (title, fields) in [
		("Mond Wald", json!({})),
		("Mond Waldlied", json!({})),
		("Kuckuck", json!({"composer":"Eduard Künneke"})),
		(
			"Anders",
			json!({"lyrics":"die wald ruht stille immond anders"}),
		),
	] {
		lied(&app, title, fields).await;
	}
	let (body, shared, total) = both(&app, &mut member, "mond wald").await;
	assert_eq!(titles(&body), ["Mond Wald", "Mond Waldlied", "Anders"]);
	assert_eq!(shared, titles(&body));
	assert_eq!(total, 3);
	assert_eq!(body["total"], 3);
	app.close().await;
}
