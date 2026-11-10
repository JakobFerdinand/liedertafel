//! Raw HTTP controls from the actual frozen 8-DTO/90-observation recording probe.
mod support;
use serde_json::json;
use support::recordings::*;
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording wire-order typed JSON/Double/Guid boundary"]
async fn recording_dtos_validate_every_occurrence_skip_unknowns_and_keep_double_domain_errors() {
	let mut s = Scenario::new().await;
	let path = format!("/api/recordings/{}", s.recording);
	let p = s.add(0, 1.0, 10.0).await;
	let pp = format!("{}/{}", s.path(), p["id"].as_str().unwrap());
	let review = format!("{}/review", s.path());
	for (method, url, body) in [
		("PATCH", path.clone(), r#"{"label":42,"label":"A"}"#),
		("PATCH", path.clone(), r#"{"downloadEnabled":1}"#),
		(
			"PATCH",
			path.clone(),
			r#"{"durationSeconds":false,"DURATIONSECONDS":1}"#,
		),
		("PATCH", path.clone(), r#"{"expectedVersion":"+1"}"#),
		("PATCH", path.clone(), r#"{"durationSeconds":" 1 "}"#),
		(
			"POST",
			review.clone(),
			r#"{"passages":[{"expectedVersion":false}],"PASSAGES":[]}"#,
		),
		("POST", review.clone(), r#"{"passages":{},"passages":[]}"#),
		(
			"PATCH",
			pp.clone(),
			r#"{"expectedPlaybackDateiId":true,"EXPECTEDPLAYBACKDATEIID":null}"#,
		),
	] {
		let before = s.view(false).await;
		let (status, b) = s.editor.raw(method, &url, body).await;
		assert_eq!(status, 400, "{body}: {b}");
		assert_eq!(b["title"], "Die Anfrage ist ungültig.");
		assert_eq!(s.view(false).await, before);
	}
	for value in ["[]", "42", "true", r#""x""#, "{"] {
		for (method, url) in [
			("PATCH", path.clone()),
			("POST", review.clone()),
			("POST", format!("{pp}/delete")),
			("PUT", format!("{path}/dateien/playback")),
		] {
			let (status, b) = s.editor.raw(method, &url, value).await;
			assert_eq!(status, 400, "{b}");
			assert_eq!(b["title"], "Die Anfrage ist ungültig.");
		}
	}
	for body in [
		r#"{"LABEL":"First","label":"Last"}"#,
		r#"{"label":"First","LABEL":"Last"}"#,
		r#"{"label":null,"label":"Last"}"#,
	] {
		let (status, b) = s.editor.raw("PATCH", &path, body).await;
		assert_eq!(status, 200);
		assert_eq!(b["recording"]["label"], "Last");
	}
	assert_eq!(
		s.editor
			.raw(
				"PATCH",
				&path,
				r#"{"unknown":1e999,"downloadEnabled":null}"#
			)
			.await
			.0,
		200
	);
	for v in [
		"1.0", "1e0", r#""1""#, r#""+1""#, r#""1e3""#, r#"".5""#, r#""1.""#,
	] {
		let (status, b) = s
			.editor
			.raw("PATCH", &path, &format!(r#"{{"durationSeconds":{v}}}"#))
			.await;
		assert_eq!(status, 200, "{v}: {b}");
	}
	for v in [r#""NaN""#, r#""Infinity""#, r#""-Infinity""#, "1e400"] {
		let (status, b) = s
			.editor
			.raw("PATCH", &path, &format!(r#"{{"durationSeconds":{v}}}"#))
			.await;
		assert_eq!(status, 400, "{v}: {b}");
		assert_eq!(b["title"], "Die Dauer ist ungültig.");
	}
	for text in [
		s.revision.as_str().unwrap().replace('-', ""),
		format!("{{{}}}", s.revision.as_str().unwrap()),
		format!(" {} ", s.revision.as_str().unwrap()),
		format!("urn:uuid:{}", s.revision.as_str().unwrap()),
	] {
		let (status, b) = s
			.editor
			.request("PATCH", &pp, json!({"expectedPlaybackDateiId":text}))
			.await;
		assert_eq!(status, 400);
		assert_eq!(b["title"], "Die Anfrage ist ungültig.");
	}
	// Same CLR parser for string paths, deliberately broader than JSON Guid D.
	let compact = s.recording.simple().to_string();
	assert_eq!(
		s.editor
			.get(&format!("/api/recordings/{compact}/access"))
			.await
			.0,
		200
	);
	assert_eq!(s.editor.raw("PATCH", &path, "null").await.0, 200);
	assert_eq!(s.editor.raw("PATCH", &path, "").await.0, 200);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL millisecond rounding and duration tolerance from frozen Math.Round"]
async fn timestamps_round_to_even_milliseconds_before_validation_and_allow_one_second_tolerance() {
	let mut s = Scenario::new().await;
	for (start, end, want_start, want_end) in
		[(1.2345, 1.2355, 1.234, 1.236), (0.0005, 0.0015, 0.0, 0.002)]
	{
		let body = json!({"expectedPlaybackDateiId":s.revision,"programmeItemId":s.entries[0]["id"],"startSeconds":start,"endSeconds":end});
		let (status, b) = s.editor.post(&s.path(), body).await;
		assert_eq!(status, 201, "{b}");
		assert_eq!(b["passage"]["startSeconds"], want_start);
		assert_eq!(b["passage"]["endSeconds"], want_end);
		assert_eq!(
			s.editor
				.post(
					&format!(
						"{}/{}/delete",
						s.path(),
						b["passage"]["id"].as_str().unwrap()
					),
					json!({})
				)
				.await
				.0,
			204
		);
	}
	let body = json!({"expectedPlaybackDateiId":s.revision,"programmeItemId":s.entries[0]["id"],"startSeconds":1999,"endSeconds":2001});
	assert_eq!(s.editor.post(&s.path(), body).await.0, 201);
	let body = json!({"expectedPlaybackDateiId":s.revision,"programmeItemId":s.entries[1]["id"],"startSeconds":1999,"endSeconds":2001.001});
	assert_eq!(s.editor.post(&s.path(), body).await.0, 400);
	s.app.close().await;
}
