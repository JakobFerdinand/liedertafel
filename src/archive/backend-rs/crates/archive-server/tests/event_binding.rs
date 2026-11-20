//! HTTP controls from actual frozen 7-DTO /157-observation probe (see docs/events-parity.md).
//! Binding failures are distinguished from domain invalidity, with no write effects.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::json;
use support::App;
use uuid::Uuid;
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL raw typed event DTOs"]
async fn each_typed_occurrence_is_checked_before_last_case_insensitive_replacement() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let (_, b) = e
		.post("/api/events", json!({"kind":"concert","title":"Initial"}))
		.await;
	let id = b["event"]["id"].as_str().unwrap();
	let patch = format!("/api/events/{id}");
	// Frozen Accepts routing falls back to the API miss before auth/binding
	// without a JSON media type; vendor +json is accepted. This is distinct
	// from optional zero bytes *with* application/json.
	for content_type in [
		None,
		Some("text/plain"),
		Some("application/json"),
		Some("application/problem+json"),
		Some("application/vnd.test+json"),
	] {
		let mut builder = axum::http::Request::builder()
			.method("PATCH")
			.uri(&patch)
			.header(
				"cookie",
				e.cookies
					.iter()
					.map(|(k, v)| format!("{k}={v}"))
					.collect::<Vec<_>>()
					.join("; "),
			)
			.header("X-CSRF-TOKEN", &e.token);
		if let Some(ct) = content_type {
			builder = builder.header("content-type", ct);
		}
		let request = builder.body(axum::body::Body::from("{}")).unwrap();
		let response = tower::ServiceExt::oneshot(e.router.clone(), request)
			.await
			.unwrap();
		assert_eq!(
			response.status().as_u16(),
			if content_type.is_none() || content_type == Some("text/plain") {
				404
			} else {
				200
			}
		);
	}
	let programme = format!("{patch}/programme/items");
	let file = support::catalogue::datei(
		&app,
		Uuid::parse_str(id).unwrap(),
		true,
		"sonstiges",
		"application/pdf",
	)
	.await;
	let file = format!("/api/dateien/{file}");
	for (method, path, body) in [
		("POST", "/api/events", r#"{"title":42,"title":"Last"}"#),
		("POST", "/api/events", r#"{"Title":42,"title":"Last"}"#),
		("POST", "/api/events", r#"{"venue":42}"#),
		("POST", "/api/events", r#"{"notes":false}"#),
		("POST", "/api/events", r#"{"sourceNote":[]}"#),
		("PATCH", patch.as_str(), r#"{"date":[]}"#),
		(
			"PATCH",
			patch.as_str(),
			r#"{"date":{"year":"bad","year":1950}}"#,
		),
		(
			"PATCH",
			patch.as_str(),
			r#"{"date":{"year":false},"DATE":{}}"#,
		),
		(
			"PATCH",
			patch.as_str(),
			r#"{"expectedRowVersion":false,"expectedRowVersion":1}"#,
		),
		(
			"PUT",
			programme.as_str(),
			r#"{"items":[{"note":42,"note":"A"}]}"#,
		),
		(
			"PUT",
			programme.as_str(),
			r#"{"items":[{"liedId":true,"liedId":null}]}"#,
		),
		("PUT", programme.as_str(), r#"{"items":{},"items":[]}"#),
		(
			"PUT",
			programme.as_str(),
			r#"{"items":[{"note":42}],"items":[]}"#,
		),
		(
			"PATCH",
			file.as_str(),
			r#"{"description":false,"description":"A"}"#,
		),
	] {
		let before = e.get(&patch).await.1;
		let (s, b) = e.raw(method, path, body).await;
		assert_eq!(s, 400, "{body}: {b}");
		assert_eq!(b["title"], "Die Anfrage ist ungültig.");
		assert_eq!(e.get(&patch).await.1, before, "no partial effects: {body}");
	}
	for body in ["[]", "42", "true", r#""x""#, "{"] {
		for (method, path) in [
			("POST", "/api/events"),
			("PATCH", patch.as_str()),
			("PUT", programme.as_str()),
			("PATCH", file.as_str()),
		] {
			let (s, b) = e.raw(method, path, body).await;
			assert_eq!(s, 400, "{body} {path}");
			assert_eq!(b["title"], "Die Anfrage ist ungültig.");
		}
	}
	for body in [
		r#"{"TITLE":"First","title":"Last"}"#,
		r#"{"title":"First","TITLE":"Last"}"#,
		r#"{"title":null,"title":"Last"}"#,
	] {
		let (s, b) = e.raw("PATCH", &patch, body).await;
		assert_eq!(s, 200, "{b}");
		assert_eq!(b["event"]["title"], "Last");
	}
	let (s, b) = e
		.raw(
			"PATCH",
			&patch,
			r#"{"date":{"year":1950},"DATE":{"month":5}}"#,
		)
		.await;
	assert_eq!(s, 400);
	assert_eq!(b["title"], "Das Datum ist unvollständig.");
	for body in [
		r#"{"date":{"YEAR":"1950","junk":[null,true]}}"#,
		r#"{"unknown":{"title":42},"rowVersion":true}"#,
		r#"{"unknown":{"title":1e10000},"rowVersion":true}"#,
	] {
		assert_eq!(e.raw("PATCH", &patch, body).await.0, 200);
	}
	for value in ["true", "false"] {
		assert_eq!(
			e.raw(
				"PATCH",
				&patch,
				&format!(r#"{{"date":{{"approximate":{value}}}}}"#)
			)
			.await
			.0,
			200
		);
	}
	for value in ["null", r#""true""#, "0"] {
		for (method, path, key) in [
			("POST", "/api/events", "dateApproximate"),
			("PATCH", patch.as_str(), "approximate"),
		] {
			let body = if key == "approximate" {
				format!(r#"{{"date":{{"approximate":{value}}}}}"#)
			} else {
				format!(r#"{{"{key}":{value}}}"#)
			};
			assert_eq!(
				e.raw(method, path, &body).await.1["title"],
				"Die Anfrage ist ungültig."
			);
		}
	}
	for value in ["1950", r#""1950""#, r#""+1950""#, r#""01950""#, "null"] {
		let (s, b) = e
			.raw(
				"PATCH",
				&patch,
				&format!(r#"{{"date":{{"year":{value}}}}}"#),
			)
			.await;
		assert_eq!(s, 200, "{value}: {b}");
	}
	for value in [
		r#"" 1950 ""#,
		"1950.0",
		"1.95e3",
		r#""1950.0""#,
		"2147483648",
		"-2147483649",
		"true",
	] {
		assert_eq!(
			e.raw(
				"PATCH",
				&patch,
				&format!(r#"{{"date":{{"year":{value}}}}}"#)
			)
			.await
			.1["title"],
			"Die Anfrage ist ungültig."
		);
	}
	for value in [
		"1",
		r#""1""#,
		r#""01""#,
		"4294967295",
		r#""4294967295""#,
		"null",
	] {
		for (method, path, key) in [
			("PATCH", patch.as_str(), "expectedRowVersion"),
			("PUT", programme.as_str(), "rowVersion"),
			("PATCH", file.as_str(), "rowVersion"),
		] {
			let (s, b) = e
				.raw(method, path, &format!(r#"{{"{key}":{value}}}"#))
				.await;
			assert!(s == 200 || s == 409, "{path} {value}: {s} {b}");
			assert_ne!(b["title"], "Die Anfrage ist ungültig.");
		}
	}
	for value in [
		r#""+1""#,
		r#"" 1 ""#,
		"1.0",
		"1e0",
		"4294967296",
		"-1",
		r#""-1""#,
		"true",
	] {
		for (method, path, key) in [
			("PATCH", patch.as_str(), "expectedRowVersion"),
			("PUT", programme.as_str(), "rowVersion"),
			("PATCH", file.as_str(), "rowVersion"),
		] {
			let (s, b) = e
				.raw(method, path, &format!(r#"{{"{key}":{value}}}"#))
				.await;
			assert_eq!(s, 400);
			assert_eq!(b["title"], "Die Anfrage ist ungültig.");
		}
	}
	app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL typed Guid and opaque raw JSON"]
async fn draft_guid_dictionary_lexemes_raw_utf16_length_last_outer_value_and_bodyless_delete() {
	let app = App::new().await;
	let mut e = app.admin().await;
	let id = Uuid::parse_str("55555555-5555-5555-5555-555555555555").unwrap();
	let path = format!("/api/auftritt-entwuerfe/{id}");
	for guid in [
		"55555555555555555555555555555555",
		"{55555555-5555-5555-5555-555555555555}",
		"(55555555-5555-5555-5555-555555555555)",
		"urn:uuid:55555555-5555-5555-5555-555555555555",
		" 55555555-5555-5555-5555-555555555555 ",
		"",
	] {
		let (s, b) = e
			.raw("PUT", &path, &format!(r#"{{"entwurfId":"{guid}"}}"#))
			.await;
		assert_eq!(s, 400);
		assert_eq!(b["title"], "Die Anfrage ist ungültig.");
	}
	for (raw, valid) in [
		(r#"{"version":1}"#, true),
		(r#"{"Version":1}"#, false),
		(r#"{"version":"1"}"#, false),
		(r#"{"version":1.0}"#, false),
		(r#"{"version":1e0}"#, false),
		(r#"{"version":2,"version":1}"#, true),
		(r#"{"version":1,"version":2}"#, false),
		(r#"{"version":1,"Version":2}"#, true),
		(r#"{"version":1,"dateien":[true],"werte":false}"#, true),
		("[]", false),
		("null", false),
		("42", false),
		(r#""text""#, false),
	] {
		let body =
			format!(r#"{{"entwurfId":"{id}","titel":" A ","dateien":"1","formular":{raw}}}"#);
		let (s, b) = e.raw("PUT", &path, &body).await;
		assert_eq!(s, if valid { 200 } else { 400 }, "{raw}: {b}");
		if valid {
			let actual: String =
				sqlx::query_scalar(r#"SELECT "Form" FROM auftritt_entwuerfe WHERE "Id"=$1"#)
					.bind(id)
					.fetch_one(&app.pool)
					.await
					.unwrap();
			assert_eq!(actual, raw);
		}
	}
	for (fields, status) in [
		(r#""formular":42,"FORMULAR":{"version":1}"#, 200),
		(r#""formular":{"version":1},"FORMULAR":42"#, 400),
		(
			r#""dateien":false,"dateien":1,"formular":{"version":1}"#,
			400,
		),
		(r#""titel":false,"titel":"A","formular":{"version":1}"#, 400),
	] {
		let body = format!(r#"{{"entwurfId":"{id}","titel":"A","dateien":1,{fields}}}"#);
		assert_eq!(e.raw("PUT", &path, &body).await.0, status, "{body}");
	}
	// Whitespace and escaped spellings count in GetRawText's UTF-16 length.
	for (length, status) in [(100_000, 200), (100_001, 400)] {
		let raw = format!("{{\"version\":1{} }}", " ".repeat(length - 14));
		assert_eq!(raw.encode_utf16().count(), length);
		let body = format!(r#"{{"entwurfId":"{id}","titel":"","dateien":0,"formular":{raw}}}"#);
		assert_eq!(e.raw("PUT", &path, &body).await.0, status);
	}
	let raw = r#"{ "version":1,"n":1e10000,"n":1.000,"opaque":{"Date":false},"escaped":"\u0061" }"#;
	assert_eq!(
		e.raw(
			"PUT",
			&path,
			&format!(r#"{{"entwurfId":"{id}","titel":"A","dateien":0,"formular":{raw}}}"#)
		)
		.await
		.0,
		200
	);
	let (status, headers, bytes) = e.wire("GET", &path, "").await;
	assert_eq!(status, 200);
	assert_eq!(headers["cache-control"], "no-store");
	assert!(std::str::from_utf8(&bytes).unwrap().contains(raw));
	// Constrained Guid paths deliberately differ from typed JSON D-format IDs.
	for spelling in [
		id.simple().to_string(),
		format!("{{{id}}}"),
		format!("({id})"),
		format!(" {id} "),
		"{0x55555555,0x5555,0x5555,{0x55,0x55,0x55,0x55,0x55,0x55,0x55,0x55}}".into(),
	] {
		let encoded = url::form_urlencoded::byte_serialize(spelling.as_bytes())
			.collect::<String>()
			.replace('+', "%20");
		let (s, _, _) = e
			.wire("GET", &format!("/api/auftritt-entwuerfe/{encoded}"), "")
			.await;
		assert_eq!(s, 200, "{spelling}");
	}
	for spelling in [format!("urn:uuid:{id}"), "not-a-guid".into()] {
		let encoded = url::form_urlencoded::byte_serialize(spelling.as_bytes()).collect::<String>();
		let (s, b) = e.get(&format!("/api/auftritt-entwuerfe/{encoded}")).await;
		assert_eq!(s, 404);
		assert_eq!(b["title"], "API-Endpunkt nicht gefunden.");
	}
	assert_eq!(e.raw("DELETE", &path, "[broken").await.0, 204);
	assert_eq!(e.raw("DELETE", &path, "42").await.0, 204);
	app.close().await;
}
