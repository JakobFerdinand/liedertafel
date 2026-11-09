// The external frozen-assembly fixture and HTTP assertions must fail loudly on malformed setup.
#![allow(clippy::unwrap_used)]
mod support;
use serde_json::{Value, json};
use std::sync::Arc;
use support::{App, storage::FakeStorage, upload::*};
fn oracle() -> Vec<Value> {
	serde_json::from_str(include_str!(
		"../../../tests/fixtures/upload-binding-oracle.json"
	))
	.unwrap()
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL raw-body frozen .NET binding differential"]
async fn raw_occurrences_match_frozen_binding_for_lied_patch_upload_and_nested_drafts() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut mismatches = Vec::new();
	let mut checked = 0;
	for case in oracle().into_iter().filter(|c| c["kind"] != "finalize") {
		let kind = case["kind"].as_str().unwrap();
		let raw = case["raw"].as_str().unwrap();
		let bound = &case["bound"];
		let owner = if matches!(kind, "patch" | "upload") {
			Some(create(&mut editor, "base").await)
		} else {
			None
		};
		let draft_id = "11111111-1111-1111-1111-111111111111";
		let (method, path, success) = match kind {
			"create" => ("POST", "/api/lieder".to_owned(), 201),
			"patch" => ("PATCH", format!("/api/lieder/{}", owner.unwrap()), 200),
			"upload" => (
				"POST",
				format!("/api/lieder/{}/dateien/upload-session", owner.unwrap()),
				201,
			),
			"draft" => ("PUT", format!("/api/lied-entwuerfe/{draft_id}"), 200),
			_ => unreachable!(),
		};
		let unknown = if kind == "draft" {
			&bound["lied"]["unknown"]
		} else {
			&bound["unknown"]
		};
		let accepted = case["accepted"] == true && unknown.as_array().is_none_or(|v| v.is_empty());
		let expected = if accepted { success } else { 400 };
		let calls = storage.calls.lock().unwrap().len();
		let (status, response) = editor.raw(method, &path, raw).await;
		checked += 1;
		if status.as_u16() != expected {
			mismatches
				.push(json!({"label":case["label"],"expected":expected,"actual":status.as_u16()}));
			continue;
		}
		if !accepted {
			if storage.calls.lock().unwrap().len() != calls {
				mismatches.push(json!({"label":case["label"],"unexpectedProviderCall":true}));
			}
			continue;
		}
		match kind {
			"create" | "patch" => {
				let actual = &response["lied"];
				if actual["title"] != bound["title"] {
					mismatches.push(json!({"label":case["label"],"field":"title","actual":actual["title"],"expected":bound["title"]}));
				}
				if let Some(value) = bound["values"].get("composer")
					&& !value.is_null()
					&& actual["composer"] != *value
				{
					mismatches.push(json!({"label":case["label"],"field":"composer"}));
				}
			}
			"upload" => {
				let blob = storage.calls.lock().unwrap().last().unwrap().1.clone();
				storage.store(&blob, &pdf(64), "application/pdf");
				let (s, b) = editor
					.post(
						&format!(
							"/api/upload-sessions/{}/finalize",
							response["uploadSessionId"].as_str().unwrap()
						),
						json!({"sizeBytes":bound["sizeBytes"],"fileName":bound["fileName"]}),
					)
					.await;
				if s != 200 || b["datei"]["fileName"] != bound["fileName"] {
					mismatches.push(
						json!({"label":case["label"],"finalizeStatus":s.as_u16(),"fileName":b["datei"]["fileName"]}),
					);
				}
			}
			"draft" => {
				let (_, loaded) = editor.get(&path).await;
				let actual = &loaded["entwurf"];
				if actual["lied"]["title"] != bound["lied"]["title"] {
					mismatches.push(json!({"label":case["label"],"field":"draftTitle"}));
				}
				if let Some(files) = bound["dateien"].as_array() {
					for (i, f) in files.iter().enumerate() {
						if actual["dateien"][i]["name"] != f["name"] {
							mismatches.push(json!({"label":case["label"],"field":"draftFileName"}));
						}
						if !f["lesen"].is_null()
							&& actual["dateien"][i]["lesen"]["anfrageId"] != f["lesen"]["anfrageId"]
						{
							mismatches
								.push(json!({"label":case["label"],"field":"draftReadingRequest"}));
						}
					}
				}
			}
			_ => unreachable!(),
		}
	}
	app.close().await;
	assert_eq!(checked, 84);
	assert!(
		mismatches.is_empty(),
		"raw frozen differential mismatches: {mismatches:?}"
	);
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL finalize binding before state/provider effects"]
async fn raw_finalize_binding_rejects_all_invalid_shapes_and_occurrences_before_pending_or_idempotent_paths()
 {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut mismatches = Vec::new();
	let mut checked = 0;
	for finalized in [false, true] {
		for case in oracle().into_iter().filter(|c| c["kind"] == "finalize") {
			let owner = create(&mut editor, "Finalize binding").await;
			let session = stage(
				&mut editor,
				owner,
				false,
				json!({"isPartitur":true,"sizeBytes":64,"fileName":"base.pdf"}),
				&pdf(64),
				"application/pdf",
				&storage,
			)
			.await;
			if finalized {
				assert_eq!(finish(&mut editor, &session).await.0, 200);
			}
			let id = uuid::Uuid::parse_str(session["uploadSessionId"].as_str().unwrap()).unwrap();
			let before: Value =
				sqlx::query_scalar(r#"SELECT to_json(s) FROM upload_sessions s WHERE "Id"=$1"#)
					.bind(id)
					.fetch_one(&app.pool)
					.await
					.unwrap();
			let calls = storage.calls.lock().unwrap().len();
			let (s, b) = editor
				.raw(
					"POST",
					&format!("/api/upload-sessions/{id}/finalize"),
					case["raw"].as_str().unwrap(),
				)
				.await;
			let accepted = case["accepted"] == true;
			checked += 1;
			let mismatch = accepted
				&& !finalized
				&& case["bound"]["sizeBytes"].as_i64().is_some_and(|n| n != 64);
			if s != if mismatch {
				409
			} else if accepted {
				200
			} else {
				400
			} {
				mismatches.push(
					json!({"label":case["label"],"finalized":finalized,"status":s.as_u16(),"body":b}),
				);
			}
			if !accepted {
				let after: Value =
					sqlx::query_scalar(r#"SELECT to_json(s) FROM upload_sessions s WHERE "Id"=$1"#)
						.bind(id)
						.fetch_one(&app.pool)
						.await
						.unwrap();
				if after != before || storage.calls.lock().unwrap().len() != calls {
					mismatches
						.push(json!({"label":case["label"],"finalized":finalized,"effects":true}));
				}
			}
		}
	}
	// HTTP optional body binding, not inferred from JsonSerializer.Deserialize("").
	for raw in ["", "null", "{}"] {
		let owner = create(&mut editor, "Optional body").await;
		let session = stage(
			&mut editor,
			owner,
			false,
			json!({"isPartitur":true}),
			&pdf(64),
			"application/pdf",
			&storage,
		)
		.await;
		let (s, b) = editor
			.raw(
				"POST",
				&format!(
					"/api/upload-sessions/{}/finalize",
					session["uploadSessionId"].as_str().unwrap()
				),
				raw,
			)
			.await;
		assert_eq!(s, 200, "{b}");
	}
	app.close().await;
	assert_eq!(checked, 54);
	assert!(
		mismatches.is_empty(),
		"finalize raw differential: {mismatches:?}"
	);
}
