//! Actual frozen typed-property observations through raw HTTP and migrated PG.
// Synthetic fixture/setup/snapshot assertions fail loudly at the requested seam.
#![allow(clippy::unwrap_used)]
mod support;
use serde_json::{Value, json};
use std::sync::Arc;
use support::{App, catalogue, storage::FakeStorage};

fn oracle() -> Vec<Value> {
	serde_json::from_str(include_str!(
		"../../../tests/fixtures/typed-binding-oracle.json"
	))
	.unwrap()
}
async fn row(app: &App, table: &str, id: uuid::Uuid) -> Value {
	// Callers supply only the three fixed catalogue table names below.
	sqlx::query_scalar(sqlx::AssertSqlSafe(format!(
		"SELECT to_json(t) FROM {table} t WHERE \"Id\"=$1"
	)))
	.bind(id)
	.fetch_one(&app.pool)
	.await
	.unwrap()
}
async fn state(app: &App) -> Value {
	let mut state = serde_json::Map::new();
	for table in [
		"lieder",
		"events",
		"dateien",
		"upload_sessions",
		"lied_entwuerfe",
		"lied_entwurf_dateien",
	] {
		let rows:Value=sqlx::query_scalar(sqlx::AssertSqlSafe(format!("SELECT coalesce(json_agg(v ORDER BY v::text),'[]'::json) FROM (SELECT row_to_json(t) v FROM {table} t) s"))).fetch_one(&app.pool).await.unwrap();
		state.insert(table.into(), rows);
	}
	Value::Object(state)
}

#[tokio::test]
#[ignore = "explicit PostgreSQL ordinary delete uint versus custom strict patch differential"]
async fn raw_delete_uint_matches_web_options_on_all_three_routes_while_patch_stays_strict() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut differences = Vec::new();
	let mut checked = 0;
	for case in oracle().into_iter().filter(|c| {
		matches!(
			c["kind"].as_str(),
			Some("delete-file" | "delete-owner" | "patch")
		)
	}) {
		let kind = case["kind"].as_str().unwrap();
		for route in if kind == "delete-owner" {
			vec!["lieder", "events"]
		} else if kind == "delete-file" {
			vec!["dateien"]
		} else {
			vec!["patch"]
		} {
			let lied = catalogue::lied(&app, "Typed rowVersion", json!({})).await;
			let (table, id) = match route {
				"events" => ("events", catalogue::event(&app).await),
				"dateien" => (
					"dateien",
					catalogue::datei(&app, lied, false, "sonstiges", "application/pdf").await,
				),
				_ => ("lieder", lied),
			};
			let path = if route == "patch" {
				format!("/api/lieder/{id}")
			} else {
				format!("/api/{route}/{id}/delete")
			};
			let bound = case["bound"].as_u64();
			let expected = if case["accepted"] != true {
				400
			} else if bound == Some(1) || (kind == "patch" && bound.is_none()) {
				if kind == "patch" { 200 } else { 204 }
			} else {
				409
			};
			let before = row(&app, table, id).await;
			let calls = storage.calls.lock().unwrap().len();
			let (s, b) = editor
				.raw(
					if kind == "patch" { "PATCH" } else { "POST" },
					&path,
					case["raw"].as_str().unwrap(),
				)
				.await;
			checked += 1;
			if s.as_u16() != expected {
				differences.push(json!({"case":case,"route":route,"status":s.as_u16(),"body":b}));
			}
			let after = row(&app, table, id).await;
			if expected >= 400 {
				assert_eq!(before, after, "rejected request changed {route}: {case}");
				assert_eq!(storage.calls.lock().unwrap().len(), calls);
			} else if s.as_u16() == expected {
				assert_eq!(after["RowVersion"], 2);
				if kind != "patch" {
					assert!(!after["DeletedAt"].is_null());
				}
			}
		}
	}
	app.close().await;
	assert_eq!(checked, 476);
	assert!(
		differences.is_empty(),
		"typed uint differences: {differences:?}"
	);
}

#[tokio::test]
#[ignore = "explicit PostgreSQL System.Text.Json typed Guid formats before upload/draft effects"]
async fn typed_guids_reject_non_d_formats_per_occurrence_without_restricting_string_ids() {
	const ID: &str = "11111111-aaaa-4bbb-8ccc-111111111111";
	let id = uuid::Uuid::parse_str(ID).unwrap();
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let owner = catalogue::lied(&app, "Typed GUID", json!({})).await;
	let file = catalogue::datei(&app, owner, false, "sonstiges", "application/pdf").await;
	sqlx::query("UPDATE dateien SET \"Id\"=$1 WHERE \"Id\"=$2")
		.bind(id)
		.bind(file)
		.execute(&app.pool)
		.await
		.unwrap();
	let event = catalogue::event(&app).await;
	sqlx::query("UPDATE events SET \"Id\"=$1 WHERE \"Id\"=$2")
		.bind(id)
		.bind(event)
		.execute(&app.pool)
		.await
		.unwrap();
	let mut differences = Vec::new();
	let mut checked = 0;
	for case in oracle().into_iter().filter(|c| {
		matches!(
			c["kind"].as_str(),
			Some("upload-replacement" | "upload-recorded-at" | "draft-id" | "draft-file")
		)
	}) {
		let kind = case["kind"].as_str().unwrap();
		let upload = kind.starts_with("upload");
		let raw = case["raw"].as_str().unwrap();
		let raw = if upload {
			format!(
				r#"{{"kind":"{}","fileName":"binding.pdf","sizeBytes":64,{}}}"#,
				if kind == "upload-recorded-at" {
					"aufnahme"
				} else {
					"sonstiges"
				},
				&raw[1..raw.len() - 1]
			)
		} else {
			raw.to_owned()
		};
		let path = if upload {
			format!("/api/lieder/{owner}/dateien/upload-session")
		} else {
			format!("/api/lied-entwuerfe/{id}")
		};
		let accepted = case["accepted"] == true;
		let expected = if !accepted || (!upload && case["bound"].is_null()) {
			400
		} else if upload {
			201
		} else {
			200
		};
		let before = state(&app).await;
		let calls = storage.calls.lock().unwrap().len();
		let (s, b) = editor
			.raw(if upload { "POST" } else { "PUT" }, &path, &raw)
			.await;
		checked += 1;
		if s.as_u16() != expected {
			differences.push(json!({"case":case,"status":s.as_u16(),"body":b}));
		}
		if expected >= 400 {
			// Collect rather than stop at the first bad format: red evidence must
			// cover each Guid field, even if an old parser saved the first request.
			if state(&app).await != before || storage.calls.lock().unwrap().len() != calls {
				differences.push(json!({"kind":kind,"label":case["label"],"effects":true}));
			}
			if !accepted && b["title"] != "Die Anfrage ist ungültig." {
				differences
					.push(json!({"kind":kind,"label":case["label"],"notBindingRejection":true}));
			}
		} else if s.as_u16() == expected {
			if upload {
				let session: Value =
					sqlx::query_scalar("SELECT to_json(s) FROM upload_sessions s WHERE \"Id\"=$1")
						.bind(
							uuid::Uuid::parse_str(b["uploadSessionId"].as_str().unwrap()).unwrap(),
						)
						.fetch_one(&app.pool)
						.await
						.unwrap();
				assert_eq!(
					session[if kind == "upload-replacement" {
						"ReplacesDateiId"
					} else {
						"RecordedAtEventId"
					}],
					case["bound"]
				);
			} else {
				let (_, loaded) = editor.get(&path).await;
				assert_eq!(loaded["entwurf"]["entwurfId"], ID);
				if kind == "draft-file" {
					assert_eq!(loaded["entwurf"]["dateien"][0]["id"], case["bound"]);
				}
			}
		}
	}
	// AccountId is a string DTO followed by its unchanged domain parser, not a
	// typed Guid JSON property. Compact/braced strings must still reach it.
	let member: uuid::Uuid = sqlx::query_scalar(
		"SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\"='mitglied@liedertafel.test'",
	)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	for account in [member.simple().to_string(), format!("{{{member}}}")] {
		let (s, b) = editor
			.raw(
				"POST",
				"/api/admin/members/role",
				&json!({"accountId":account,"role":"Member"}).to_string(),
			)
			.await;
		assert_eq!(s, 200, "{b}");
	}
	// Suggestion values are ordinary strings: this wrong domain value reaches
	// draft validation rather than being interpreted as a typed Guid property.
	let before = state(&app).await;
	let(s,b)=editor.raw("PUT",&format!("/api/lied-entwuerfe/{ID}"),&json!({"entwurfId":ID,"lied":{"title":"String dictionary"},"vorschlaege":{"title":ID.replace('-',"")}}).to_string()).await;
	assert_eq!(s, 400);
	assert_eq!(b["title"], "Der Entwurf ist ungültig.");
	assert_eq!(state(&app).await, before);
	app.close().await;
	assert_eq!(checked, 215);
	assert!(
		differences.is_empty(),
		"typed Guid differences: {differences:?}"
	);
}
