mod support;
use serde_json::json;
use support::recordings::*;

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL concurrent slot replacement reports the recording stale message"]
async fn two_file_replacements_preserve_winner_and_report_recording_not_generic_datei_conflict() {
	let mut s = Scenario::new().await;
	let mut peer = s.app.sign_in("verwaltung@liedertafel.test").await;
	let path = format!("/api/recordings/{}/dateien/original", s.recording);
	let mut next = Vec::new();
	for _ in 0..2 {
		let session = support::upload::stage(
			&mut s.editor,
			s.event,
			true,
			json!({"kind":"aufnahme","contentType":"video/mp4"}),
			&mp4(),
			"video/mp4",
			&s.storage,
		)
		.await;
		let (_, file) = support::upload::finish(&mut s.editor, &session).await;
		next.push(file["datei"]["id"].clone());
	}
	let old = s.revision.as_str().unwrap();
	// Force both real handlers past their read snapshots before either file CAS
	// commits. Only delivery scheduling is controlled; no auth/domain/SQL fake.
	sqlx::raw_sql(sqlx::AssertSqlSafe(format!(r#"CREATE FUNCTION recording_slot_barrier() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF OLD."Id"='{old}'::uuid THEN PERFORM pg_advisory_xact_lock(227001); END IF; RETURN NEW; END $$; CREATE TRIGGER recording_slot_barrier BEFORE UPDATE ON dateien FOR EACH ROW EXECUTE FUNCTION recording_slot_barrier();"#))).execute(&s.app.pool).await.unwrap();
	let mut lock = s.app.pool.begin().await.unwrap();
	sqlx::query("SELECT pg_advisory_xact_lock(227001)")
		.execute(&mut *lock)
		.await
		.unwrap();
	let mut editor = s.editor;
	let path1 = path.clone();
	let body1 = json!({"dateiId":next[0]});
	let body2 = json!({"dateiId":next[1]});
	let first = tokio::spawn(async move { editor.request("PUT", &path1, body1).await });
	let second = tokio::spawn(async move { peer.request("PUT", &path, body2).await });
	tokio::time::timeout(std::time::Duration::from_secs(5),async{loop{let count:i64=sqlx::query_scalar(r#"SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE dateien SET "DeletedAt"%'"#).fetch_one(&s.app.pool).await.unwrap();if count>=2{break;}tokio::time::sleep(std::time::Duration::from_millis(10)).await;}}).await.unwrap();
	lock.commit().await.unwrap();
	let a = first.await.unwrap();
	let b = second.await.unwrap();
	let (winner, loser) = if a.0 == 200 { (a, b) } else { (b, a) };
	assert_eq!(winner.0, 200, "{}", winner.1);
	assert_eq!(loser.0, 409, "{}", loser.1);
	assert_eq!(
		loser.1["title"],
		"Die Aufnahme wurde zwischenzeitlich geändert."
	);
	let selected = winner.1["recording"]["playback"]["dateiId"]
		.as_str()
		.unwrap();
	let blob = support::upload::blob(&s.app, uuid::Uuid::parse_str(selected).unwrap()).await;
	assert!(s.storage.objects.lock().unwrap().contains_key(&blob));
	s.app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL HTTP constraint-name mapping and unclassified failure controls"]
async fn only_named_recording_passage_constraints_become_friendly_conflicts() {
	let mut s = Scenario::new().await;
	for (code, constraint, wanted, message) in [
		(
			"23505",
			"IX_recording_passages_RecordingId_ProgrammeItemId",
			409,
			"Für diesen Programmpunkt gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.",
		),
		(
			"23503",
			"FK_recording_passages_programme_items_ProgrammeItemId",
			409,
			"Die Zeitmarke wurde zwischenzeitlich geändert.",
		),
		(
			"23503",
			"FK_recording_passages_recordings_RecordingId_EventId",
			409,
			"Die Zeitmarke wurde zwischenzeitlich geändert.",
		),
		(
			"23503",
			"FK_something_else",
			500,
			"Ein unerwarteter Fehler ist aufgetreten.",
		),
		(
			"23505",
			"IX_other",
			500,
			"Ein unerwarteter Fehler ist aufgetreten.",
		),
		(
			"23514",
			"FK_recording_passages_programme_items_ProgrammeItemId",
			500,
			"Ein unerwarteter Fehler ist aufgetreten.",
		),
	] {
		// Test-only SQL delivery fault. Inputs are this fixed literal matrix;
		// real schema-generated violations are independently checked below.
		sqlx::raw_sql(sqlx::AssertSqlSafe(format!(r#"CREATE OR REPLACE FUNCTION public.recording_test_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic delivery error' USING ERRCODE='{code}', CONSTRAINT='{constraint}'; END $$; DROP TRIGGER IF EXISTS recording_test_failure ON recording_passages; CREATE TRIGGER recording_test_failure BEFORE INSERT ON recording_passages FOR EACH ROW EXECUTE FUNCTION public.recording_test_failure();"#))).execute(&s.app.pool).await.unwrap();
		let(status,b)=s.editor.post(&s.path(),json!({"programmeItemId":s.entries[0]["id"],"startSeconds":1,"endSeconds":10,"expectedPlaybackDateiId":s.revision})).await;
		assert_eq!(status, wanted, "{b}");
		assert_eq!(b["title"], message);
		assert!(
			s.view(false).await["passages"]
				.as_array()
				.unwrap()
				.is_empty()
		);
	}
	s.app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL recording/passage multi-origin CAS and real constraint arbitration"]
async fn two_origins_cannot_overwrite_one_mark_or_attach_the_same_slot_twice() {
	let mut s = Scenario::new().await;
	let mut peer = s.app.sign_in("verwaltung@liedertafel.test").await;
	let path = s.path();
	let first = json!({"programmeItemId":s.entries[0]["id"],"startSeconds":1,"endSeconds":10,"expectedPlaybackDateiId":s.revision});
	let mut second = first.clone();
	second["startSeconds"] = json!(2);
	let (a, b) = tokio::join!(s.editor.post(&path, first), peer.post(&path, second));
	let mut codes = [a.0.as_u16(), b.0.as_u16()];
	codes.sort();
	assert_eq!(codes, [201, 409]);
	let p = s.view(false).await["passages"][0].clone();
	let pp = format!("{path}/{}", p["id"].as_str().unwrap());
	let a = json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":1,"startSeconds":3});
	let b = json!({"expectedPlaybackDateiId":s.revision,"expectedVersion":1,"startSeconds":4});
	let (a, b) = tokio::join!(
		s.editor.request("PATCH", &pp, a),
		peer.request("PATCH", &pp, b)
	);
	let mut codes = [a.0.as_u16(), b.0.as_u16()];
	codes.sort();
	assert_eq!(codes, [200, 409]);
	assert_eq!(s.view(false).await["passages"][0]["editor"]["version"], 2);
	let other = create(&mut s.editor, s.event, "Weitere Aufnahme", "video").await;
	let session = support::upload::stage(
		&mut s.editor,
		s.event,
		true,
		json!({"kind":"aufnahme","contentType":"video/mp4"}),
		&mp4(),
		"video/mp4",
		&s.storage,
	)
	.await;
	let (_, file) = support::upload::finish(&mut s.editor, &session).await;
	let body = json!({"dateiId":file["datei"]["id"]});
	let path1 = format!("/api/recordings/{}/dateien/playback", s.recording);
	let path2 = format!("/api/recordings/{other}/dateien/playback");
	let (a, b) = tokio::join!(
		s.editor.request("PUT", &path1, body.clone()),
		peer.request("PUT", &path2, body)
	);
	let mut codes = [a.0.as_u16(), b.0.as_u16()];
	codes.sort();
	assert_eq!(codes, [200, 409]);
	s.app.close().await;
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL exact current schema FK/check/unique constraints, no historical waiver"]
async fn recording_and_passage_constraints_arbitrate_real_sql_not_error_name_folklore() {
	let mut s = Scenario::new().await;
	let p = s.add(0, 1.0, 10.0).await;
	let pid = uuid::Uuid::parse_str(p["id"].as_str().unwrap()).unwrap();
	for (sql, code, name) in [
		(
			r#"UPDATE recording_passages SET "StartSeconds"=-1 WHERE "Id"=$1"#,
			"23514",
			"CK_recording_passages_start",
		),
		(
			r#"UPDATE recording_passages SET "EndSeconds"=0 WHERE "Id"=$1"#,
			"23514",
			"CK_recording_passages_order",
		),
		(
			r#"UPDATE recording_passages SET "ProgrammeItemId"='11111111-1111-1111-1111-111111111111' WHERE "Id"=$1"#,
			"23503",
			"FK_recording_passages_programme_items_ProgrammeItemId",
		),
		(
			r#"UPDATE recording_passages SET "EventId"='11111111-1111-1111-1111-111111111111' WHERE "Id"=$1"#,
			"23503",
			"FK_recording_passages_recordings_RecordingId_EventId",
		),
		(
			r#"UPDATE recording_passages SET "PlaybackDateiId"='11111111-1111-1111-1111-111111111111' WHERE "Id"=$1"#,
			"23503",
			"FK_recording_passages_dateien_PlaybackDateiId",
		),
	] {
		let err = sqlx::query(sql)
			.bind(pid)
			.execute(&s.app.pool)
			.await
			.unwrap_err();
		let err = err.as_database_error().unwrap();
		assert_eq!(err.code().as_deref(), Some(code));
		assert_eq!(err.constraint(), Some(name));
	}
	let err = sqlx::query(r#"DELETE FROM programme_items WHERE "Id"=$1"#)
		.bind(uuid::Uuid::parse_str(s.entries[0]["id"].as_str().unwrap()).unwrap())
		.execute(&s.app.pool)
		.await
		.unwrap_err();
	assert_eq!(
		err.as_database_error().unwrap().constraint(),
		Some("FK_recording_passages_programme_items_ProgrammeItemId")
	);
	let err=sqlx::query(r#"INSERT INTO recording_passages ("Id","RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","PlaybackDateiId","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion") SELECT gen_random_uuid(),"RecordingId","ProgrammeItemId","EventId","StartSeconds","EndSeconds","PlaybackDateiId","CreatedAt","CreatedByAccountId","UpdatedAt","UpdatedByAccountId","RowVersion" FROM recording_passages WHERE "Id"=$1"#).bind(pid).execute(&s.app.pool).await.unwrap_err();
	assert_eq!(
		err.as_database_error().unwrap().constraint(),
		Some("IX_recording_passages_RecordingId_ProgrammeItemId")
	);
	s.app.close().await;
}
