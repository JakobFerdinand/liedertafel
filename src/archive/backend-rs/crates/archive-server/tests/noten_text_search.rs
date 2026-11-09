mod support;
use serde_json::json;
use support::{App, catalogue::*};

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn words_only_in_live_completed_noten_find_lied_for_members_and_editors() {
	let app = App::new().await;
	let mut member = app.member().await;
	let mut editor = app.sign_in("redaktion@liedertafel.test").await;
	let id = lied(&app, "Waldfahrt", json!({})).await;
	lied(&app, "Heimweh", json!({})).await;
	let d = datei(&app, id, false, "noten", "application/pdf").await;
	text(&app, d, "Im Wald da singen die Nachtigallen", 2, 0).await;
	for client in [&mut member, &mut editor] {
		for (q, expected) in [
			("nachtigallen", vec!["Waldfahrt"]),
			("waldfahrt nachtigallen", vec!["Waldfahrt"]),
			("heimweh nachtigallen", vec![]),
			("kuckuck", vec![]),
		] {
			assert_eq!(titles(&client.get(&query(q)).await.1), expected);
		}
	}
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP gate"]
async fn trash_other_kinds_incomplete_jobs_and_trashed_parent_contribute_nothing() {
	let app = App::new().await;
	let mut member = app.member().await;
	let id = lied(&app, "Abendlied", json!({})).await;
	for (kind, status, trash, word) in [
		("noten", 2, true, "veraltete"),
		("sonstiges", 2, false, "sonstwort"),
		("noten", 0, false, "wartend"),
		("noten", 1, false, "laufend"),
		("noten", 3, false, "keintext"),
		("noten", 4, false, "fehlerwort"),
	] {
		let d = datei(&app, id, false, kind, "application/pdf").await;
		text(&app, d, word, status, 0).await;
		if trash {
			sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
				.bind(d)
				.execute(&app.pool)
				.await
				.unwrap();
		}
		assert_eq!(member.get(&query(word)).await.1["total"], 0);
	}
	let d = datei(&app, id, false, "noten", "application/pdf").await;
	text(&app, d, "Der Mond ist aufgegangen", 2, 0).await;
	assert_eq!(titles(&member.get(&query("MOND")).await.1), ["Abendlied"]);
	sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
		.bind(id)
		.execute(&app.pool)
		.await
		.unwrap();
	assert_eq!(member.get(&query("mond")).await.1["total"], 0);
	app.close().await;
}
