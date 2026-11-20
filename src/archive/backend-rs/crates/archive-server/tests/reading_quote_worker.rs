// Delivery-only model below the actual gateway/SQL/writer; no native/live model.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Real SQL differential assertions.
mod support;
use archive_server::{
	ai::{Script, ScriptStep, Update, Usage},
	auth::Clock,
	queue::{Disposition, Message, envelope},
	reading::{Job, Options},
};
use serde_json::{Value, json};
use std::sync::Arc;
#[tokio::test]
#[ignore = "explicit PostgreSQL: frozen Unicode acceptance/drop and locked late-editor write"]
async fn actual_worker_writes_only_frozen_verified_unicode_and_never_a_late_editor_field() {
	let data: Value =
		serde_json::from_str(include_str!("fixtures/229-reading-quotes.json")).unwrap();
	for (name, late_editor) in [
		("dotted-i-refuse", false),
		("sigma-verify", false),
		("sigma-verify", true),
	] {
		let row = data["quotes"]
			.as_array()
			.unwrap()
			.iter()
			.find(|r| r["name"] == name)
			.unwrap();
		let app = support::App::new_scoped("reading229").await;
		let delivered = Arc::new(tokio::sync::Notify::new());
		let answer=json!({"felder":[{"feld":"composer","wert":row["value"],"sicherheit":"sicher","zitat":row["quote"]}]}).to_string();
		let script = Script::handler({
			let delivered = delivered.clone();
			move |_| {
				delivered.notify_one();
				vec![ScriptStep::Update(Update {
					text: answer.clone(),
					usage: Usage::reported(100, 20),
					..Default::default()
				})]
			}
		});
		let h = support::reading::Harness::new(&app, vec![], script, Options::default());
		let (owner, id) = h.seed(&app, 20).await;
		sqlx::query(r#"UPDATE extraction_jobs SET "Status"=2,"Text"=$2 WHERE "DateiId"=$1"#)
			.bind(id)
			.bind(row["text"].as_str().unwrap())
			.execute(&app.pool)
			.await
			.unwrap();
		let mut editor = if late_editor {
			let mut tx = app.pool.begin().await.unwrap();
			sqlx::query(r#"SELECT "Id" FROM lieder WHERE "Id"=$1 FOR UPDATE"#)
				.bind(owner)
				.fetch_one(&mut *tx)
				.await
				.unwrap();
			Some(tx)
		} else {
			None
		};
		let worker = Arc::new(h.worker);
		let deadline = app.time.now() + chrono::Duration::minutes(1);
		let task = tokio::spawn(async move {
			worker
				.handle(
					&Message {
						id: "unicode".into(),
						receipt: "local".into(),
						body: envelope("reading", id),
						dequeue_count: 1,
					},
					deadline,
					&Default::default(),
				)
				.await
		});
		tokio::time::timeout(std::time::Duration::from_secs(5), delivered.notified())
			.await
			.unwrap();
		if let Some(mut tx) = editor.take() {
			sqlx::query(
				r#"UPDATE lieder SET "Composer"='Eigene Eingabe',"RowVersion"="RowVersion"+1 WHERE "Id"=$1"#,
			)
			.bind(owner)
			.execute(&mut *tx)
			.await
			.unwrap();
			tx.commit().await.unwrap();
		}
		assert_eq!(task.await.unwrap().unwrap(), Disposition::Delete);
		let song = archive_server::catalogue::queries::lied(&app.pool, owner)
			.await
			.unwrap()
			.unwrap();
		let expected = if late_editor {
			Some("Eigene Eingabe")
		} else if row["accepted"] == true {
			row["value"].as_str()
		} else {
			None
		};
		assert_eq!(
			song.composer.as_deref(),
			expected,
			"{name} late_editor={late_editor}"
		);
		assert_eq!(
			Job::load(&app.pool, id)
				.await
				.unwrap()
				.unwrap()
				.fields_applied,
			i32::from(!late_editor && row["accepted"] == true)
		);
		assert_eq!(h.script.requests().len(), 1);
		assert!(h.gateway.drain().await);
		let usage: (i64, i64, i32) = sqlx::query_as(
			r#"SELECT "CostMicroEur","ReservedMicroEur","Calls" FROM ai_usage_entries"#,
		)
		.fetch_one(&app.pool)
		.await
		.unwrap();
		assert_eq!(usage, (182, 0, 1));
		app.close().await;
	}
}
