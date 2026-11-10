mod support;
use serde_json::json;
use std::sync::Arc;
use support::recordings::*;
use support::{
	App,
	storage::FakeStorage,
	upload::{finish, stage},
};

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording visibility, creator slots and validation"]
async fn members_never_reach_unpublished_recordings_and_mutations_need_editor_and_csrf() {
	let mut s = Scenario::new().await;
	let path = format!("/api/recordings/{}", s.recording);
	assert_eq!(s.member.get(&format!("{path}/access")).await.0, 404);
	assert!(
		s.member
			.get(&format!("/api/events/{}/recordings", s.event))
			.await
			.1["recordings"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	let calls = [
		(
			"POST",
			format!("/api/events/{}/recordings", s.event),
			json!({"label":"X","kind":"audio"}),
		),
		("PATCH", path.clone(), json!({"label":"X"})),
		(
			"PUT",
			format!("{path}/dateien/original"),
			json!({"dateiId":s.revision}),
		),
	];
	for (method, path, b) in calls {
		assert_eq!(s.member.request(method, &path, b.clone()).await.0, 403);
		let token = std::mem::take(&mut s.editor.token);
		assert_eq!(s.editor.request(method, &path, b.clone()).await.0, 400);
		s.editor.token = token;
		let mut anonymous = s.app.client();
		anonymous.csrf().await;
		assert_eq!(anonymous.request(method, &path, b).await.0, 401);
	}
	let mut second = s.app.sign_in("redaktion@liedertafel.test").await;
	assert_eq!(
		second
			.request("PATCH", &path, json!({"label":"Jede Redaktion"}))
			.await
			.0,
		200
	);
	assert_eq!(
		second
			.request(
				"PUT",
				&format!("{path}/dateien/playback"),
				json!({"dateiId":s.revision})
			)
			.await
			.0,
		403
	);
	s.publish().await;
	assert_eq!(s.member.get(&format!("{path}/access")).await.0, 200);
	assert_eq!(
		s.member
			.get(&format!(
				"/api/dateien/{}/access",
				s.revision.as_str().unwrap()
			))
			.await
			.0,
		404
	);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL current recording validation and playback-slot replacement"]
async fn empty_still_images_foreign_files_and_unplayable_copies_are_refused_without_losing_old_slot()
 {
	let mut s = Scenario::new().await;
	let old = s.revision.clone();
	let mut heic = mp4();
	heic[8..12].copy_from_slice(b"heic");
	for (bytes, content) in [
		(heic.as_slice(), "video/mp4"),
		(b"\0\0\0\x14ftypqt  ".as_slice(), "video/quicktime"),
	] {
		let (status, b) = attach(
			&mut s.editor,
			s.event,
			s.recording,
			"playback",
			bytes,
			content,
			&s.storage,
		)
		.await;
		assert_eq!(status, 422, "{b}");
		assert_eq!(
			b["title"],
			"Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme."
		);
	}
	assert_eq!(s.view(false).await["playbackDateiId"], old);
	let foreign = event(&mut s.editor).await;
	let session = stage(
		&mut s.editor,
		foreign,
		true,
		json!({"kind":"aufnahme","contentType":"video/mp4"}),
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	let (_, b) = finish(&mut s.editor, &session).await;
	let (status, b) = s
		.editor
		.request(
			"PUT",
			&format!("/api/recordings/{}/dateien/original", s.recording),
			json!({"dateiId":b["datei"]["id"]}),
		)
		.await;
	assert_eq!(status, 404, "{b}");
	// Attachment uses DateiUploads' title, not the generic Datei-read title.
	assert_eq!(b["title"], "Datei nicht gefunden.");
	let session = stage(
		&mut s.editor,
		s.event,
		true,
		json!({"kind":"aufnahme","contentType":"video/mp4"}),
		b"",
		"video/mp4",
		&s.storage,
	)
	.await;
	assert_eq!(finish(&mut s.editor, &session).await.0, 422);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL duration and trash state tied to actual playback"]
async fn measured_duration_belongs_to_the_file_members_play_and_a_trashed_file_empties_the_slot() {
	let mut s = Scenario::new().await;
	let path = format!("/api/recordings/{}", s.recording);
	let old = s.revision.clone();
	let (status, b) = attach(
		&mut s.editor,
		s.event,
		s.recording,
		"original",
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	assert_eq!(status, 200, "{b}");
	assert!(b["recording"]["durationSeconds"].is_null());
	assert_ne!(b["recording"]["playback"]["dateiId"], old);
	let file = b["recording"]["playback"]["dateiId"].as_str().unwrap();
	assert_eq!(
		s.editor
			.post(
				&format!("/api/dateien/{file}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	let b = s
		.editor
		.get(&format!("/api/events/{}/recordings", s.event))
		.await
		.1;
	assert_eq!(b["recordings"][0]["playback"]["state"], "missing");
	assert!(b["recordings"][0]["editor"]["original"]["file"].is_null());
	assert_eq!(s.editor.get(&format!("{path}/access")).await.0, 404);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL preserved original and policy-exact member downloads"]
async fn unplayable_original_can_publish_but_gets_no_view_ticket_and_downloads_the_selected_playback_file()
 {
	let mut s = Scenario::new().await;
	let id = create(&mut s.editor, s.event, "Kamera", "video").await;
	let path = format!("/api/recordings/{id}");
	let (status, b) = attach(
		&mut s.editor,
		s.event,
		id,
		"original",
		b"\0\0\0\x14ftypqt  ",
		"video/quicktime",
		&s.storage,
	)
	.await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["recording"]["playback"]["state"], "needsPlaybackCopy");
	let original = b["recording"]["editor"]["original"]["file"]["dateiId"].clone();
	assert_eq!(
		s.editor
			.request("PATCH", &path, json!({"isPublished":true}))
			.await
			.0,
		200
	);
	s.storage.calls.lock().unwrap().clear();
	let (status, b) = s.member.get(&format!("{path}/access")).await;
	assert_eq!(status, 200);
	assert!(b["viewUrl"].is_null());
	assert!(b["downloadUrl"].is_null());
	assert!(s.storage.calls.lock().unwrap().is_empty());
	assert_eq!(
		s.editor
			.request("PATCH", &path, json!({"downloadEnabled":true}))
			.await
			.0,
		200
	);
	let b = s.member.get(&format!("{path}/access")).await.1;
	assert_eq!(b["dateiId"], original);
	assert!(b["viewUrl"].is_null());
	assert!(b["downloadUrl"].is_string());
	let (status, b) = attach(
		&mut s.editor,
		s.event,
		id,
		"playback",
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	assert_eq!(status, 200);
	let copy = b["recording"]["playback"]["dateiId"].clone();
	let b = s.member.get(&format!("{path}/access")).await.1;
	assert_eq!(b["dateiId"], copy);
	assert_eq!(b["source"], "playbackCopy");
	assert!(b["viewUrl"].is_string());
	assert!(b["downloadUrl"].is_string());
	assert_eq!(
		s.editor
			.get(&format!(
				"/api/dateien/{}/access",
				original.as_str().unwrap()
			))
			.await
			.0,
		200
	);
	assert_eq!(
		s.member
			.get(&format!(
				"/api/dateien/{}/access",
				original.as_str().unwrap()
			))
			.await
			.0,
		404
	);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording metadata shape/noop/UTF16/concurrency/validation order"]
async fn metadata_bounds_nulls_noops_expected_tokens_attribution_and_no_future_routes() {
	let mut s = Scenario::new().await;
	let path = format!("/api/recordings/{}", s.recording);
	let before = s
		.editor
		.get(&format!("/api/events/{}/recordings", s.event))
		.await
		.1["recordings"][0]
		.clone();
	for b in [
		json!({}),
		json!(null),
		json!({"label":" Video Kamera 1 ","durationSeconds":2000,"downloadEnabled":false,"isPublished":false}),
	] {
		let (status, b) = s.editor.request("PATCH", &path, b).await;
		assert_eq!(status, 200);
		assert_eq!(b["recording"], before);
	}
	for (body, message) in [
		(json!({"label":" "}), "Die Bezeichnung ist erforderlich."),
		(
			json!({"label":"😀".repeat(101)}),
			"Die Bezeichnung ist zu lang.",
		),
		(json!({"durationSeconds":0}), "Die Dauer ist ungültig."),
		(json!({"durationSeconds":172801}), "Die Dauer ist ungültig."),
	] {
		let (status, b) = s.editor.request("PATCH", &path, body).await;
		assert_eq!(status, 400, "{b}");
		assert_eq!(b["title"], message);
	}
	assert_eq!(
		s.editor
			.request("PATCH", &path, json!({"expectedVersion":0}))
			.await
			.0,
		409
	);
	for (kind, label, status) in [
		("Audio", "OK", 400),
		("audio", " ", 400),
		(" audio ", "X", 201),
	] {
		assert_eq!(
			s.editor
				.post(
					&format!("/api/events/{}/recordings", s.event),
					json!({"kind":kind,"label":label})
				)
				.await
				.0,
			status
		);
	}
	assert_eq!(s.editor.get(&path).await.0, 404);
	assert_eq!(s.editor.request("DELETE", &path, json!({})).await.0, 404);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording publication and direct-ticket policy"]
async fn publishing_needs_a_file_and_downloads_stay_off_until_enabled() {
	let mut app = App::new().await;
	let storage = Arc::new(FakeStorage::default());
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let (s, b) = editor
		.post("/api/events", json!({"title":"Konzert","kind":"concert"}))
		.await;
	assert_eq!(s, 201, "{b}");
	let event = b["event"]["id"].as_str().unwrap();
	let (s, b) = editor
		.post(
			&format!("/api/events/{event}/recordings"),
			json!({"label":" Ton ","kind":"audio"}),
		)
		.await;
	assert_eq!(s, 201, "{b}");
	let id = b["recording"]["id"].as_str().unwrap();
	let path = format!("/api/recordings/{id}");
	assert_eq!(b["recording"]["label"], "Ton");
	assert_eq!(b["recording"]["editor"]["version"], 0);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"isPublished":true}))
			.await
			.0,
		409
	);
	let session = stage(
		&mut editor,
		uuid::Uuid::parse_str(event).unwrap(),
		true,
		json!({"kind":"aufnahme","contentType":"audio/mpeg"}),
		b"ID3synthetic recording",
		"audio/mpeg",
		&storage,
	)
	.await;
	let (s, b) = finish(&mut editor, &session).await;
	assert_eq!(s, 200, "{b}");
	let file = b["datei"]["id"].as_str().unwrap();
	let (s, b) = editor
		.request(
			"PUT",
			&format!("{path}/dateien/original"),
			json!({"dateiId":file}),
		)
		.await;
	assert_eq!(s, 200, "{b}");
	assert_eq!(b["recording"]["playback"]["state"], "ready");
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"isPublished":true}))
			.await
			.0,
		200
	);
	let (s, b) = member.get(&format!("{path}/access")).await;
	assert_eq!(s, 200, "{b}");
	assert!(b["downloadUrl"].is_null());
	assert!(b["viewUrl"].is_string());
	assert_eq!(
		member.get(&format!("/api/dateien/{file}/access")).await.0,
		404
	);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"downloadEnabled":true}))
			.await
			.0,
		200
	);
	let (_, b) = member.get(&format!("{path}/access")).await;
	assert!(b["downloadUrl"].is_string());
	app.close().await;
}
