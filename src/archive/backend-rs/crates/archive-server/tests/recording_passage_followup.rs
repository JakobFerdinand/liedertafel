mod support;
use serde_json::json;
use support::recordings::*;
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL stale playback and exact selected atomic review"]
async fn outdated_playback_revision_is_refused_on_every_write_and_review_is_exact_atomic_and_versioned()
 {
	let mut s = Scenario::new().await;
	let one = s.add(0, 10.0, 100.0).await;
	let two = s.add(1, 200.0, 300.0).await;
	let old = s.revision.clone();
	s.replace().await;
	let path = s.path();
	let ppath = format!("{}/{}", path, one["id"].as_str().unwrap());
	let selection = json!([{"id":one["id"],"expectedVersion":1}]);
	for (method, url, b) in [
		(
			"POST",
			path.clone(),
			json!({"programmeItemId":s.entries[2]["id"],"startSeconds":1,"endSeconds":5,"expectedPlaybackDateiId":old}),
		),
		(
			"PATCH",
			ppath.clone(),
			json!({"startSeconds":11,"endSeconds":101,"expectedVersion":1,"expectedPlaybackDateiId":old}),
		),
		(
			"POST",
			format!("{path}/review"),
			json!({"expectedPlaybackDateiId":old,"passages":selection}),
		),
	] {
		let (status, b) = s.editor.request(method, &url, b).await;
		assert_eq!(status, 409);
		assert_eq!(
			b["title"],
			"Die Datei der Aufnahme wurde zwischenzeitlich ersetzt."
		);
	}
	for (method, url) in [
		("POST", path.clone()),
		("PATCH", ppath.clone()),
		("POST", format!("{path}/review")),
	] {
		let (status, b) = s.editor.request(method, &url, json!({})).await;
		assert_eq!(status, 400);
		assert_eq!(b["title"], "Die Dateiversion der Aufnahme fehlt.");
	}
	let review = format!("{path}/review");
	for passages in [
		json!(null),
		json!([]),
		json!([null]),
		json!([{}]),
		json!([{"id":one["id"]}]),
	] {
		let (status, b) = s
			.editor
			.post(
				&review,
				json!({"expectedPlaybackDateiId":s.revision,"passages":passages}),
			)
			.await;
		assert_eq!(status, 400);
		assert_eq!(
			b["title"],
			"Es wurden keine Zeitmarken zum Bestätigen angegeben."
		);
	}
	assert_eq!(s.editor.post(&review,json!({"expectedPlaybackDateiId":s.revision,"passages":[{"id":uuid::Uuid::new_v4(),"expectedVersion":1}]})).await.0,404);
	let(status,b)=s.editor.post(&review,json!({"expectedPlaybackDateiId":s.revision,"passages":[{"id":one["id"],"expectedVersion":1},{"id":two["id"],"expectedVersion":8}]})).await;
	assert_eq!(status, 409);
	assert_eq!(b["title"], "Die Zeitmarke wurde zwischenzeitlich geändert.");
	let unchanged = s.view(false).await;
	for p in unchanged["passages"].as_array().unwrap() {
		assert_eq!(p["timestampState"], "needsReview");
		assert_eq!(p["editor"]["version"], 1);
	}
	let (status, b) = s
		.editor
		.post(
			&review,
			json!({"expectedPlaybackDateiId":s.revision,"passages":selection}),
		)
		.await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["passages"][0]["timestampState"], "current");
	assert_eq!(b["passages"][1]["timestampState"], "needsReview");
	assert_eq!(
		s.editor
			.post(
				&review,
				json!({"expectedPlaybackDateiId":s.revision,"passages":selection})
			)
			.await
			.0,
		409
	);
	// Repeated ids are deduplicated for effects, but every supplied token is checked.
	assert_eq!(s.editor.post(&review,json!({"expectedPlaybackDateiId":s.revision,"passages":[{"id":two["id"],"expectedVersion":1},{"id":two["id"],"expectedVersion":9}]})).await.0,409);
	s.app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL lost playback, member timestamp withholding and reanchor both bounds"]
async fn lost_or_replaced_playback_is_honest_in_passages_and_history_until_both_bounds_are_verified()
 {
	let mut s = Scenario::new().await;
	s.publish().await;
	let p = s.add(0, 100.0, 300.0).await;
	let old = s.revision.clone();
	let path = s.path();
	let ppath = format!("{}/{}", path, p["id"].as_str().unwrap());
	let (status, b) = attach(
		&mut s.editor,
		s.event,
		s.recording,
		"original",
		b"\0\0\0\x14ftypqt  ",
		"video/quicktime",
		&s.storage,
	)
	.await;
	assert_eq!(status, 200, "{b}");
	assert!(s.view(false).await["playbackDateiId"].is_null());
	assert_eq!(
		s.view(true).await["passages"][0]["timestampState"],
		"needsReview"
	);
	assert!(s.view(true).await["passages"][0]["startSeconds"].is_null());
	assert!(s.history(0, true).await["auftritte"][0]["recordings"][0]["startSeconds"].is_null());
	assert_eq!(
		s.history(0, false).await["auftritte"][0]["recordings"][0]["startSeconds"],
		100.0
	);
	for (method, url, b) in [
		(
			"POST",
			path.clone(),
			json!({"programmeItemId":s.entries[1]["id"],"expectedPlaybackDateiId":old,"startSeconds":1,"endSeconds":5}),
		),
		(
			"PATCH",
			ppath.clone(),
			json!({"expectedPlaybackDateiId":old,"startSeconds":11,"endSeconds":101}),
		),
		(
			"POST",
			format!("{path}/review"),
			json!({"expectedPlaybackDateiId":old,"passages":[{"id":p["id"],"expectedVersion":1}]}),
		),
	] {
		let (status, b) = s.editor.request(method, &url, b).await;
		assert_eq!(status, 409);
		assert_eq!(
			b["title"],
			"Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen."
		);
	}
	s.replace().await;
	assert_eq!(
		s.editor
			.request(
				"PATCH",
				&ppath,
				json!({"expectedPlaybackDateiId":s.revision,"startSeconds":90})
			)
			.await
			.0,
		400
	);
	let(status,b)=s.editor.request("PATCH",&ppath,json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":1,"startSeconds":90,"endSeconds":310})).await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(b["passage"]["timestampState"], "current");
	assert_eq!(b["passage"]["editor"]["playbackDateiId"], s.revision);
	assert_eq!(s.view(true).await["passages"][0]["startSeconds"], 90.0);
	assert_eq!(
		s.editor.post(&format!("{ppath}/delete"), json!({})).await.0,
		204
	);
	s.app.close().await;
}
