mod support;
use serde_json::json;
use support::recordings::*;

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL Zeitmarke edit/review/delete CAS and playback identity"]
async fn editing_and_review_preserve_noops_refuse_stale_state_and_delete_only_the_mark() {
	let mut s = Scenario::new().await;
	let one = s.add(0, 100.0, 200.0).await;
	let two = s.add(1, 300.0, 500.0).await;
	s.publish().await;
	let path = format!("{}/{}", s.path(), one["id"].as_str().unwrap());
	let(status,b)=s.editor.request("PATCH",&path,json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":1,"startSeconds":110.5,"endSeconds":215})).await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["passage"]["editor"]["version"], 2);
	assert_eq!(b["passage"]["liedTitle"], "Erstes Lied");
	let same = b["passage"].clone();
	let(status,b)=s.editor.request("PATCH",&path,json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":2,"startSeconds":110.5,"endSeconds":215})).await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["passage"], same);
	let (status, b) = s
		.editor
		.request(
			"PATCH",
			&path,
			json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":1,"startSeconds":50}),
		)
		.await;
	assert_eq!(status, 409);
	assert_eq!(b["title"], "Die Zeitmarke wurde zwischenzeitlich geändert.");
	// Frozen EditingMovesTimes: a current token preserves bounds errors,
	// unlike a stale token which is refused before looking at the times.
	for (bounds, message) in [
		(
			json!({"startSeconds":500}),
			"Das Ende muss nach dem Anfang liegen.",
		),
		(
			json!({"endSeconds":2500}),
			"Das Ende liegt hinter dem Ende der Aufnahme.",
		),
	] {
		let mut body = bounds;
		body["expectedPlaybackDateiId"] = s.revision.clone();
		body["expectedVersion"] = json!(2);
		let (status, b) = s.editor.request("PATCH", &path, body).await;
		assert_eq!(status, 400, "{b}");
		assert_eq!(b["title"], message);
		assert_eq!(s.view(false).await["passages"][0], same);
	}
	let old = s.revision.clone();
	s.replace().await;
	assert_eq!(
		s.view(true).await["passages"][0]["timestampState"],
		"needsReview"
	);
	assert!(s.view(true).await["passages"][0]["startSeconds"].is_null());
	assert_eq!(s.view(false).await["passages"][0]["startSeconds"], 110.5);
	let (status, b) = s
		.editor
		.request(
			"PATCH",
			&path,
			json!({"expectedPlaybackDateiId":old,"expectedVersion":2,"startSeconds":100,"endSeconds":200}),
		)
		.await;
	assert_eq!(status, 409);
	assert_eq!(
		b["title"],
		"Die Datei der Aufnahme wurde zwischenzeitlich ersetzt."
	);
	let (status, b) = s
		.editor
		.request(
			"PATCH",
			&path,
			json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":2,"startSeconds":100}),
		)
		.await;
	assert_eq!(status, 400);
	assert_eq!(
		b["title"],
		"Zum Prüfen einer Zeitmarke bitte Anfang und Ende angeben."
	);
	let review = format!("{}/review", s.path());
	let selection =
		json!([{"id":one["id"],"expectedVersion":2},{"id":two["id"],"expectedVersion":1}]);
	let (status, b) = s
		.editor
		.post(
			&review,
			json!({"expectedPlaybackDateiId":s.revision,"passages":selection}),
		)
		.await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["passages"][0]["editor"]["version"], 3);
	assert_eq!(b["passages"][1]["editor"]["version"], 2);
	assert_eq!(b["passages"][0]["timestampState"], "current");
	assert_eq!(s.view(true).await["passages"][0]["startSeconds"], 110.5);
	let selection =
		json!([{"id":one["id"],"expectedVersion":3},{"id":two["id"],"expectedVersion":2}]);
	let (status, b) = s
		.editor
		.post(
			&review,
			json!({"expectedPlaybackDateiId":s.revision,"passages":selection}),
		)
		.await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["passages"][0]["editor"]["version"], 3);
	assert_eq!(
		s.editor
			.post(&format!("{path}/delete"), json!({"expectedVersion":1}))
			.await
			.0,
		409
	);
	assert_eq!(
		s.editor
			.post(&format!("{path}/delete"), json!({"expectedVersion":3}))
			.await
			.0,
		204
	);
	assert_eq!(
		s.editor.post(&format!("{path}/delete"), json!({})).await.0,
		404
	);
	assert_eq!(s.view(false).await["entries"].as_array().unwrap().len(), 3);
	assert_eq!(s.view(false).await["passages"].as_array().unwrap().len(), 1);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL complete recording/Programme/history roundtrip"]
async fn editor_indexes_two_lieder_in_one_video_and_one_of_them_in_a_second_recording() {
	let mut s = Scenario::new().await;
	let before = s.history(0, true).await;
	let one = s.add(0, 12.5, 301.25).await;
	let two = s.add(1, 320.0, 700.0).await;
	assert_eq!(one["programmeItemId"], s.entries[0]["id"]);
	assert_eq!(one["liedId"], s.lieder[0].to_string());
	assert_eq!(one["liedTitle"], "Erstes Lied");
	assert!(one.get("evidenceStatus").is_none());
	assert_eq!(one["timestampState"], "current");
	assert_eq!(one["editor"]["version"], 1);
	assert_eq!(two["liedTitle"], "Zweites Lied");
	let audio = create(&mut s.editor, s.event, "Tonmitschnitt", "audio").await;
	assert_eq!(
		attach(
			&mut s.editor,
			s.event,
			audio,
			"original",
			b"ID3synthetic",
			"audio/mpeg",
			&s.storage
		)
		.await
		.0,
		200
	);
	let rev = s
		.editor
		.get(&format!("/api/recordings/{audio}/passages"))
		.await
		.1["playbackDateiId"]
		.clone();
	let(status,b)=s.editor.post(&format!("/api/recordings/{audio}/passages"),json!({"programmeItemId":s.entries[0]["id"],"startSeconds":5,"endSeconds":290,"expectedPlaybackDateiId":rev})).await;
	assert_eq!(status, 201, "{b}");
	assert_ne!(b["passage"]["id"], one["id"]);
	assert_eq!(s.member.get(&s.path()).await.0, 404);
	s.publish().await;
	assert_eq!(
		s.editor
			.request(
				"PATCH",
				&format!("/api/recordings/{audio}"),
				json!({"isPublished":true})
			)
			.await
			.0,
		200
	);
	let member = s.view(true).await;
	assert!(member.get("entries").is_none());
	assert!(member.get("playbackDateiId").is_none());
	assert!(member["passages"][0]["editor"].is_null());
	assert_eq!(member["passages"][0]["startSeconds"], 12.5);
	assert_eq!(member["passages"][1]["liedTitle"], "Zweites Lied");
	assert!(member["passages"][0].get("createdByAccountId").is_none());
	let history = s.history(0, true).await;
	let links = history["auftritte"][0]["recordings"].as_array().unwrap();
	assert_eq!(links.len(), 2);
	assert_eq!(links[0]["recordingLabel"], "Tonmitschnitt");
	assert_eq!(links[1]["passageId"], one["id"]);
	assert_eq!(links[1]["kind"], "video");
	assert_eq!(links[1]["timestampState"], "current");
	assert_eq!(history["counts"], before["counts"]);
	assert_eq!(history["total"], before["total"]);
	assert!(
		s.history(2, true).await["auftritte"][0]["recordings"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL whole playback without marks and editor entry candidates"]
async fn whole_recording_plays_unmarked_lieder_stay_unmarked_and_editors_list_entries_in_order() {
	let mut s = Scenario::new().await;
	s.publish().await;
	let before = s.view(false).await;
	assert_eq!(
		before["entries"]
			.as_array()
			.unwrap()
			.iter()
			.map(|e| e["position"].as_i64().unwrap())
			.collect::<Vec<_>>(),
		[1, 2, 3]
	);
	assert!(
		before["entries"]
			.as_array()
			.unwrap()
			.iter()
			.all(|e| e["passageId"].is_null())
	);
	assert!(
		s.view(true).await["passages"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	assert_eq!(
		s.member
			.get(&format!("/api/recordings/{}/access", s.recording))
			.await
			.1["playbackState"],
		"ready"
	);
	let p = s.add(0, 100.0, 400.0).await;
	assert_eq!(s.view(false).await["entries"][0]["passageId"], p["id"]);
	assert_eq!(
		s.member
			.get(&format!("/api/recordings/{}/access", s.recording))
			.await
			.1["playbackState"],
		"ready"
	);
	assert!(
		s.history(1, true).await["auftritte"][0]["recordings"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	let empty = event(&mut s.editor).await;
	let rec = create(&mut s.editor, empty, "Ohne Programm", "audio").await;
	assert!(
		s.editor
			.get(&format!("/api/recordings/{rec}/passages"))
			.await
			.1["entries"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL all timestamp bounds, own event, duplicate/idempotent and parent gates"]
async fn bounds_event_ownership_duplicates_and_parent_identity_are_validated() {
	let mut s = Scenario::new().await;
	let path = s.path();
	let rev = s.revision.clone();
	let entry = s.entries[0]["id"].clone();
	for (start, end, message) in [
		(json!(-1), json!(50), "Der Anfang ist ungültig."),
		(json!(null), json!(50), "Der Anfang ist ungültig."),
		(json!(10), json!(null), "Das Ende ist ungültig."),
		(
			json!(100),
			json!(100),
			"Das Ende muss nach dem Anfang liegen.",
		),
		(
			json!(100),
			json!(50),
			"Das Ende muss nach dem Anfang liegen.",
		),
		(
			json!(100),
			json!(2500),
			"Das Ende liegt hinter dem Ende der Aufnahme.",
		),
		(
			json!(100),
			json!(200000),
			"Das Ende liegt hinter dem Ende der Aufnahme.",
		),
		(json!(172801), json!(172802), "Der Anfang ist ungültig."),
		(json!(0), json!(-1), "Das Ende ist ungültig."),
	] {
		let(status,b)=s.editor.post(&path,json!({"programmeItemId":entry,"startSeconds":start,"endSeconds":end,"expectedPlaybackDateiId":rev})).await;
		assert_eq!(status, 400, "{b}");
		assert_eq!(b["title"], message);
	}
	let foreign_event = event(&mut s.editor).await;
	let (_, foreign) = s
		.editor
		.request(
			"PUT",
			&format!("/api/events/{foreign_event}/programme/items"),
			json!({"items":[{"liedId":s.lieder[0]}]}),
		)
		.await;
	for entry in [
		json!(null),
		json!(uuid::Uuid::new_v4()),
		foreign["programme"]["items"][0]["id"].clone(),
	] {
		let (status, b) = s
			.editor
			.post(
				&path,
				json!({"programmeItemId":entry,"startSeconds":1,"endSeconds":50,"expectedPlaybackDateiId":rev}),
			)
			.await;
		assert_eq!(status, 404, "{b}");
		assert_eq!(
			b["title"],
			"Der Programmpunkt gehört nicht zum Programm dieses Auftritts."
		);
	}
	assert!(
		s.view(false).await["passages"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	let p = s.add(0, 900.0, 2000.0).await;
	let(status,b)=s.editor.post(&path,json!({"programmeItemId":entry,"startSeconds":900,"endSeconds":2000,"expectedPlaybackDateiId":rev})).await;
	assert_eq!(status, 200);
	assert_eq!(b["passage"], p);
	let (status, b) = s
		.editor
		.post(
			&path,
			json!({"programmeItemId":entry,"startSeconds":10,"endSeconds":20,"expectedPlaybackDateiId":rev}),
		)
		.await;
	assert_eq!(status, 409);
	assert_eq!(
		b["title"],
		"Für diesen Programmpunkt gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene."
	);
	let empty = create(&mut s.editor, s.event, "Ohne Datei", "video").await;
	let (status, b) = s
		.editor
		.post(
			&format!("/api/recordings/{empty}/passages"),
			json!({"programmeItemId":entry,"startSeconds":1,"endSeconds":5,"expectedPlaybackDateiId":rev}),
		)
		.await;
	assert_eq!(status, 409);
	assert_eq!(
		b["title"],
		"Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen."
	);
	assert_eq!(
		s.editor
			.post(
				&format!("/api/recordings/{}/passages", uuid::Uuid::new_v4()),
				json!({"programmeItemId":entry,"startSeconds":1,"endSeconds":5,"expectedPlaybackDateiId":rev})
			)
			.await
			.0,
		404
	);
	let wrong = format!(
		"/api/recordings/{empty}/passages/{}",
		p["id"].as_str().unwrap()
	);
	assert_eq!(
		s.editor
			.request("PATCH", &wrong, json!({"expectedPlaybackDateiId":rev}))
			.await
			.0,
		404
	);
	assert_eq!(
		s.editor.post(&format!("{wrong}/delete"), json!({})).await.0,
		404
	);
	let own = format!("{}/{}", path, p["id"].as_str().unwrap());
	let (status, b) = s
		.editor
		.request(
			"PATCH",
			&own,
			json!({"expectedPlaybackDateiId":rev,"programmeItemId":s.entries[1]["id"],"startSeconds":901}),
		)
		.await;
	assert_eq!(status, 200);
	assert_eq!(b["passage"]["programmeItemId"], entry);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL all recording and Zeitmarke auth paths with live membership"]
async fn authorization_holds_on_every_path_and_rechecks_downgrade_revocation() {
	let mut s = Scenario::new().await;
	s.publish().await;
	let p = s.add(0, 10.0, 100.0).await;
	let path = s.path();
	let pid = p["id"].as_str().unwrap();
	let mut anonymous = s.app.client();
	anonymous.csrf().await;
	assert_eq!(anonymous.get(&path).await.0, 401);
	assert_eq!(s.member.get(&path).await.0, 200);
	for (method, url, body) in [
		(
			"POST",
			path.clone(),
			json!({"programmeItemId":s.entries[1]["id"],"startSeconds":1,"endSeconds":5,"expectedPlaybackDateiId":s.revision}),
		),
		(
			"PATCH",
			format!("{path}/{pid}"),
			json!({"startSeconds":2,"expectedPlaybackDateiId":s.revision}),
		),
		("POST", format!("{path}/{pid}/delete"), json!({})),
		(
			"POST",
			format!("{path}/review"),
			json!({"expectedPlaybackDateiId":s.revision,"passages":[{"id":pid,"expectedVersion":1}]}),
		),
	] {
		assert_eq!(anonymous.request(method, &url, body.clone()).await.0, 401);
		assert_eq!(s.member.request(method, &url, body.clone()).await.0, 403);
		let token = std::mem::take(&mut s.editor.token);
		let (status, b) = s.editor.request(method, &url, body).await;
		assert_eq!(status, 400);
		assert_eq!(b["title"], "Ungültiger Sicherheitstoken.");
		s.editor.token = token;
	}
	assert_eq!(s.view(false).await["passages"][0]["startSeconds"], 10.0);
	// A second editor may edit any Zeitmarke, but not another creator's files.
	let mut second = s.app.sign_in("redaktion@liedertafel.test").await;
	assert_eq!(
		second
			.request(
				"PATCH",
				&format!("{path}/{pid}"),
				json!({"expectedPlaybackDateiId":s.revision,"startSeconds":11})
			)
			.await
			.0,
		200
	);
	sqlx::query(r#"DELETE FROM "AspNetUserRoles" WHERE "UserId"=(SELECT "Id" FROM "AspNetUsers" WHERE "Email"='redaktion@liedertafel.test')"#).execute(&s.app.pool).await.unwrap();
	sqlx::query(r#"INSERT INTO "AspNetUserRoles" ("UserId","RoleId") SELECT u."Id",r."Id" FROM "AspNetUsers" u CROSS JOIN "AspNetRoles" r WHERE u."Email"='redaktion@liedertafel.test' AND r."Name"='Member'"#).execute(&s.app.pool).await.unwrap();
	assert_eq!(second.post(&path, json!({})).await.0, 403);
	sqlx::query(r#"UPDATE "AspNetUsers" SET "LockoutEnabled"=true,"LockoutEnd"=now()+interval '1 day' WHERE "Email"='redaktion@liedertafel.test'"#).execute(&s.app.pool).await.unwrap();
	second.csrf().await;
	assert_eq!(second.post(&path, json!({})).await.0, 401);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording publication controls passage/history projections"]
async fn unpublished_withdrawn_and_trashed_event_recordings_never_reach_members() {
	let mut s = Scenario::new().await;
	s.add(0, 10.0, 100.0).await;
	let draft = create(&mut s.editor, s.event, "Entwurf", "video").await;
	let (status, b) = attach(
		&mut s.editor,
		s.event,
		draft,
		"original",
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	assert_eq!(status, 200, "{b}");
	let(status,b)=s.editor.post(&format!("/api/recordings/{draft}/passages"),json!({"programmeItemId":s.entries[0]["id"],"expectedPlaybackDateiId":b["recording"]["playback"]["dateiId"],"startSeconds":20,"endSeconds":120})).await;
	assert_eq!(status, 201, "{b}");
	assert_eq!(s.member.get(&s.path()).await.0, 404);
	assert!(
		s.history(0, true).await["auftritte"][0]["recordings"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	assert_eq!(
		s.history(0, false).await["auftritte"][0]["recordings"][0]["isPublished"],
		false
	);
	s.publish().await;
	assert_eq!(
		s.member
			.get(&format!("/api/recordings/{draft}/passages"))
			.await
			.0,
		404
	);
	let editor = s.history(0, false).await;
	assert_eq!(
		editor["auftritte"][0]["recordings"]
			.as_array()
			.unwrap()
			.len(),
		2
	);
	assert_eq!(
		editor["auftritte"][0]["recordings"][0]["recordingLabel"],
		"Entwurf"
	);
	assert_eq!(
		editor["auftritte"][0]["recordings"][0]["isPublished"],
		false
	);
	assert_eq!(
		s.history(0, true).await["auftritte"][0]["recordings"]
			.as_array()
			.unwrap()
			.len(),
		1
	);
	assert_eq!(
		s.editor
			.request(
				"PATCH",
				&format!("/api/recordings/{}", s.recording),
				json!({"isPublished":false})
			)
			.await
			.0,
		200
	);
	assert_eq!(s.member.get(&s.path()).await.0, 404);
	assert!(
		s.history(0, true).await["auftritte"][0]["recordings"]
			.as_array()
			.unwrap()
			.is_empty()
	);
	s.publish().await;
	assert_eq!(
		s.editor
			.post(
				&format!("/api/events/{}/delete", s.event),
				json!({"rowVersion":0})
			)
			.await
			.0,
		204
	);
	assert_eq!(s.member.get(&s.path()).await.0, 404);
	assert_eq!(
		s.member
			.get(&format!("/api/recordings/{}/access", s.recording))
			.await
			.0,
		404
	);
	assert_eq!(s.editor.get(&s.path()).await.0, 200);
	assert_eq!(
		s.editor
			.get(&format!("/api/events/{}/recordings", s.event))
			.await
			.0,
		404
	);
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL marked stable Programme references block deletion and survive reorder"]
async fn programme_entry_with_zeitmarken_cannot_be_removed_behind_their_back() {
	let mut s = Scenario::new().await;
	let mark = s.add(1, 400.0, 800.0).await;
	let path = format!("/api/events/{}/programme/items", s.event);
	let (status, b) = s
		.editor
		.request(
			"PUT",
			&path,
			json!({"rowVersion":1,"items":[{"id":s.entries[0]["id"],"liedId":s.lieder[0]}]}),
		)
		.await;
	assert_eq!(status, 409);
	assert!(
		b["title"]
			.as_str()
			.unwrap()
			.starts_with("Zeitmarken vorhanden:")
	);
	assert!(b["title"].as_str().unwrap().contains("„Zweites Lied“"));
	assert!(b["title"].as_str().unwrap().contains("„Video Kamera 1“"));
	let items = json!([{"id":s.entries[1]["id"],"liedId":s.lieder[1]},{"id":s.entries[0]["id"],"liedId":s.lieder[0]}]);
	assert_eq!(
		s.editor
			.request("PUT", &path, json!({"rowVersion":1,"items":items}))
			.await
			.0,
		200
	);
	assert_eq!(s.view(false).await["entries"][0]["passageId"], mark["id"]);
	assert_eq!(s.view(false).await["passages"][0]["position"], 1);
	assert_eq!(
		s.editor
			.post(
				&format!("{}/{}/delete", s.path(), mark["id"].as_str().unwrap()),
				json!({})
			)
			.await
			.0,
		204
	);
	assert_eq!(
		s.editor
			.request(
				"PUT",
				&path,
				json!({"rowVersion":2,"items":[{"id":s.entries[0]["id"],"liedId":s.lieder[0]}]})
			)
			.await
			.0,
		200
	);
	s.app.close().await;
}
