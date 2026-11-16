//! Frozen SearchAgreement: actual catalogue_search loop vs member HTTP search.
// HTTP/shared-selection fixture assertions live in a helper outside #[test].
#![allow(clippy::unwrap_used)]
mod support;
use serde_json::json;
use support::{App, catalogue::*};
async fn both(
	app: &mut App,
	member: &mut support::Client,
	q: &str,
) -> (serde_json::Value, Vec<String>, usize) {
	let (_, body) = member.get(&query(q)).await;
	let payload = support::chat::invoke(app, "catalogue_search", json!({"query":q,"page":1})).await;
	let titles = payload["lieder"]
		.as_array()
		.unwrap()
		.iter()
		.map(|m| m["title"].as_str().unwrap().to_owned())
		.collect();
	(
		body,
		titles,
		payload["totalCount"].as_u64().unwrap() as usize,
	)
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL search component"]
async fn empty_query_actual_tool_matches_member_order_with_different_page_sizes() {
	let mut app = App::new_scoped("chat232_agreement_browse").await;
	let mut member = app.member().await;
	for i in 0..12 {
		lied(&app, &format!("Lied {i:02}"), json!({})).await;
	}
	let (body, shared, total) = both(&mut app, &mut member, "").await;
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
async fn exact_title_title_and_lyrics_have_same_order_in_member_and_actual_tool() {
	let mut app = App::new_scoped("chat232_agreement_rank").await;
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
	let (body, shared, total) = both(&mut app, &mut member, "mond wald").await;
	assert_eq!(titles(&body), ["Mond Wald", "Mond Waldlied", "Anders"]);
	assert_eq!(shared, titles(&body));
	assert_eq!(total, 3);
	assert_eq!(body["total"], 3);
	app.close().await;
}
