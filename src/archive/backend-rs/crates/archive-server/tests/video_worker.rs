#![allow(clippy::unwrap_used)] // Actual CLI finite-lifetime assertions.
mod support;
use archive_server::{
	auth::SystemClock,
	queue::{AzureQueue, Queue},
	storage::{BlobStorage, StorageConfig},
};
use std::sync::Arc;
use support::videos::{CLIP, Video};
use support::{App, catalogue::*};
use uuid::Uuid;

#[test]
fn video_option_defaults_preserve_the_frozen_getter_matrix() {
	let options = archive_server::videos::Options::default();
	assert_eq!(options.queue.queue_name, "archive-videos");
	assert_eq!(options.queue.batch_size, 1);
	assert_eq!(options.queue.visibility_fit_batch_size(), 1);
	assert_eq!(
		options.queue.stale_running_after,
		chrono::Duration::hours(1)
	);
	assert_eq!(options.queue.time_budget, chrono::Duration::hours(8));
	assert_eq!(options.ffmpeg_path, "ffmpeg");
	assert_eq!(options.ffprobe_path, "ffprobe");
	assert_eq!(options.max_input_bytes, 10 * 1024 * 1024 * 1024);
	assert_eq!(options.queue.max_attempts, 3);
	assert_eq!(options.queue.redisplay_after, chrono::Duration::hours(1));
	assert_eq!(options.queue.max_dispatch_per_run, 100);
	assert_eq!(
		options.queue.visibility_backoff,
		chrono::Duration::minutes(1)
	);
	assert_eq!(options.queue.receive_visibility, chrono::Duration::hours(8));
	assert_eq!(options.queue.max_messages_per_run, 10);
	assert_eq!(options.queue.drain_budget, chrono::Duration::hours(10));
}

#[tokio::test]
async fn maintenance_pauses_both_video_commands_before_dependencies() {
	for value in [
		"true",
		"TRUE",
		" true ",
		"\tTrUe\r\n",
		"\u{00a0}true\u{00a0}",
	] {
		for command in ["--transcode-queue", "--dispatch-videos"] {
			let output = tokio::time::timeout(
				std::time::Duration::from_secs(3),
				tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
					.arg(command)
					.env("ARCHIVE_ENVIRONMENT", "Production")
					.env("Archive__MaintenanceMode", value)
					.env("PORT", "0")
					.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
					.env("ConnectionStrings__archive-db", "invalid")
					.env("ConnectionStrings__archive-queues", "invalid")
					.kill_on_drop(true)
					.output(),
			)
			.await;
			assert!(
				output.is_ok(),
				"{command} must complete finitely before opening providers"
			);
			assert!(output.unwrap().unwrap().status.success());
		}
	}
}

#[tokio::test]
#[ignore = "explicit real PG/private Queue typed maintenance CLI flag matrix"]
async fn invalid_maintenance_refuses_work_before_dispatch_and_false_values_allow_it() {
	let mut failures = Vec::new();
	// Actual .NET10 GetValue<bool> controls from the framework oracle. Blank
	// present values are invalid, unlike an absent key's false default.
	for command in ["--dispatch-videos", "--transcode-queue"] {
		for value in [
			"not-bool",
			"1",
			"0",
			"yes",
			"",
			" ",
			"\u{feff}true",
			"true\u{200b}",
		] {
			let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
			v.seed_job(0, 0).await;
			sqlx::query(r#"UPDATE video_jobs SET "LastEnqueuedAt"=NULL WHERE "DateiId"=$1"#)
				.bind(v.id)
				.execute(&v.app.pool)
				.await
				.unwrap();
			let output = v
				.command(command, &[("Archive__MaintenanceMode", value.into())])
				.output()
				.await
				.unwrap();
			let status = v.status().await;
			if output.status.success()
				|| status["status"] != "queued"
				|| status["attemptCount"] != 0
				|| !status["lastEnqueuedAt"].is_null()
			{
				failures.push((
					command,
					value,
					output.status.code(),
					status["status"].clone(),
					status["attemptCount"].clone(),
				));
			}
			let receipts = v.queue.receive(1, 1).await.unwrap();
			if !receipts.is_empty() {
				failures.push((
					command,
					value,
					output.status.code(),
					serde_json::json!("dispatched notification"),
					status["attemptCount"].clone(),
				));
			}
			for message in receipts {
				v.queue.delete(&message).await.unwrap();
			}
			v.done().await;
		}
	}
	// False is an actual work-admission control, not merely another failed
	// command against broken dependencies. Exercise both entry/pump paths.
	for (command, value) in [
		("--dispatch-videos", " false "),
		("--transcode-queue", "\u{0085}FaLsE\u{0085}"),
	] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.seed_job(0, 0).await;
		sqlx::query(r#"UPDATE video_jobs SET "LastEnqueuedAt"=NULL WHERE "DateiId"=$1"#)
			.bind(v.id)
			.execute(&v.app.pool)
			.await
			.unwrap();
		assert!(
			v.command(command, &[("Archive__MaintenanceMode", value.into())])
				.output()
				.await
				.unwrap()
				.status
				.success()
		);
		let status = v.status().await;
		assert!(status["lastEnqueuedAt"].is_string());
		assert_eq!(
			status["status"],
			if command == "--dispatch-videos" {
				"queued"
			} else {
				"completed"
			}
		);
		v.done().await;
	}
	assert!(
		failures.is_empty(),
		"invalid maintenance admitted video work: {failures:?}"
	);
}

#[tokio::test]
#[ignore = "explicit real PG/private Queue padded maintenance preserves state and unopened receipts"]
async fn padded_maintenance_preserves_jobs_bytes_and_unread_notifications() {
	for command in ["--dispatch-videos", "--transcode-queue"] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.seed_job(0, 0).await;
		sqlx::query(r#"UPDATE video_jobs SET "LastEnqueuedAt"=NULL WHERE "DateiId"=$1"#)
			.bind(v.id)
			.execute(&v.app.pool)
			.await
			.unwrap();
		v.send().await;
		let before = v.status().await;
		assert!(
			v.command(
				command,
				&[("Archive__MaintenanceMode", " \tTrUe\r\n".into())]
			)
			.output()
			.await
			.unwrap()
			.status
			.success()
		);
		assert_eq!(v.status().await, before);
		let messages = v.queue.receive(2, 1).await.unwrap();
		assert_eq!(messages.len(), 1);
		assert_eq!(
			messages[0].dequeue_count, 1,
			"paused command must not read a receipt"
		);
		v.queue.delete(&messages[0]).await.unwrap();
		assert_eq!(
			v.storage
				.read_header(&v.blob, CLIP.len())
				.await
				.unwrap()
				.unwrap(),
			CLIP
		);
		v.done().await;
	}
}

#[tokio::test]
#[ignore = "explicit real tools/current PG/private storage CLI gate"]
async fn corrupt_material_fails_with_the_exact_german_reason() {
	let v = Video::new(b"not video bytes", "aufnahme", "video/mp4", false).await;
	v.send().await;
	v.run(&[]).await;
	let s = v.status().await;
	assert_eq!(s["status"], "failed");
	assert_eq!(
		s["failureReason"],
		"Die Aufnahme konnte nicht gelesen werden."
	);
	assert_eq!(s["attemptCount"], 1);
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit actual missing tools/current PG/private storage CLI gate"]
async fn missing_ffmpeg_or_ffprobe_is_terminal_finite_and_retryable_after_repair() {
	for tool in ["Archive__Video__FfmpegPath", "Archive__Video__FfprobePath"] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.send().await;
		v.run(&[(tool, "/no/such/233-tool".into())]).await;
		let s = v.status().await;
		assert_eq!(s["status"], "failed");
		assert_eq!(
			s["failureReason"],
			"Die Konvertierung benötigt ffmpeg, das auf dem Server nicht verfügbar ist. Nach dem Beheben der Einrichtung erneut versuchen."
		);
		assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
		let mut editor = v.app.admin().await;
		assert_eq!(
			editor
				.post(
					&format!("/api/dateien/{}/video/retry", v.id),
					serde_json::json!({})
				)
				.await
				.0,
			200
		);
		v.run(&[]).await;
		assert_eq!(v.status().await["status"], "completed");
		v.done().await;
	}
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn duplicate_messages_never_rework_a_finished_row() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	v.run(&[]).await;
	let before = v.status().await;
	v.send().await;
	v.run(&[]).await;
	assert_eq!(v.status().await, before);
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn nonvideo_material_only_deletes_its_messages() {
	for (kind, content) in [("uebe", "video/quicktime"), ("aufnahme", "audio/mpeg")] {
		let v = Video::new(CLIP, kind, content, false).await;
		v.seed_job(0, 0).await;
		let before = v.status().await;
		v.send().await;
		v.run(&[("Archive__Video__FfprobePath", "/no/such/tool".into())])
			.await;
		assert_eq!(v.status().await, before);
		assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
		v.done().await;
	}
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn file_and_parent_trash_are_not_transcoded() {
	for parent in [false, true] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.seed_job(0, 0).await;
		if parent {
			sqlx::query(r#"UPDATE lieder SET "DeletedAt"=now() WHERE "Id"=$1"#)
				.bind(v.owner)
				.execute(&v.app.pool)
				.await
				.unwrap();
		} else {
			sqlx::query(r#"UPDATE dateien SET "DeletedAt"=now() WHERE "Id"=$1"#)
				.bind(v.id)
				.execute(&v.app.pool)
				.await
				.unwrap();
		}
		v.send().await;
		v.run(&[]).await;
		let count: i32 =
			sqlx::query_scalar(r#"SELECT "AttemptCount" FROM video_jobs WHERE "DateiId"=$1"#)
				.bind(v.id)
				.fetch_one(&v.app.pool)
				.await
				.unwrap();
		assert_eq!(count, 0);
		assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
		v.done().await;
	}
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn oversize_inputs_fail_without_opening_tools_or_counting_attempts() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	v.run(&[
		("Archive__Video__MaxInputBytes", "1".into()),
		("Archive__Video__FfprobePath", "/no/such/tool".into()),
	])
	.await;
	let s = v.status().await;
	assert_eq!(s["status"], "failed");
	assert_eq!(s["attemptCount"], 0);
	assert_eq!(
		s["failureReason"],
		"Die Aufnahme ist zu groß für die automatische Konvertierung."
	);
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn missing_job_row_self_heals_with_the_file_creator() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	v.run(&[]).await;
	assert_eq!(v.status().await["status"], "completed");
	let actor: Uuid =
		sqlx::query_scalar(r#"SELECT "TriggeredByAccountId" FROM video_jobs WHERE "DateiId"=$1"#)
			.bind(v.id)
			.fetch_one(&v.app.pool)
			.await
			.unwrap();
	assert_eq!(actor, Uuid::nil());
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn missing_file_is_deleted_without_row_work() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.queue
		.send(&archive_server::queue::envelope(
			"transcode",
			Uuid::new_v4(),
		))
		.await
		.unwrap();
	v.run(&[]).await;
	assert!(v.status().await.is_null());
	assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn malformed_and_foreign_envelopes_are_deleted_as_poison() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.seed_job(0, 0).await;
	let before = v.status().await;
	for body in [
		"not json".into(),
		archive_server::queue::envelope("extraction", v.id),
		r#"{"type":"transcode"}"#.into(),
		archive_server::queue::envelope("transcode", Uuid::nil()),
	] {
		v.queue.send(&body).await.unwrap();
	}
	for _ in 0..4 {
		v.run(&[]).await;
	}
	assert_eq!(v.status().await, before);
	assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit actual ffmpeg/current PG/private storage CLI gate"]
async fn an_already_playable_original_stays_one_object() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", true).await;
	v.send().await;
	v.run(&[]).await;
	let s = v.status().await;
	let copy = Uuid::parse_str(s["playbackDateiId"].as_str().unwrap()).unwrap();
	let blob: String = sqlx::query_scalar(r#"SELECT "BlobName" FROM dateien WHERE "Id"=$1"#)
		.bind(copy)
		.fetch_one(&v.app.pool)
		.await
		.unwrap();
	let bytes = v
		.storage
		.service_client()
		.unwrap()
		.blob_container_client(&v.container)
		.blob_client(&blob)
		.download(None)
		.await
		.unwrap()
		.body
		.collect()
		.await
		.unwrap();
	let playable = Video::new(&bytes, "aufnahme", "video/mp4", true).await;
	// The frozen EF-InMemory test puts the same Datei in both slots, which
	// the CURRENT CK_recordings_dateien rejects. Keep the playable original
	// in its original slot; assert the same one-object/no-slot-write behavior.
	let r = recording(&playable.app, playable.owner, playable.id, None, "video").await;
	sqlx::query(r#"UPDATE recordings SET "DurationSeconds"=3450 WHERE "Id"=$1"#)
		.bind(r)
		.execute(&playable.app.pool)
		.await
		.unwrap();
	playable.send().await;
	playable.run(&[]).await;
	let s = playable.status().await;
	assert_eq!(s["status"], "notNeeded");
	assert_eq!(s["attemptCount"], 1);
	assert!(s["failureReason"].is_null());
	assert!(s["completedAt"].is_string());
	assert_eq!(s["completedAt"], s["updatedAt"]);
	assert!(s["playbackDateiId"].is_null());
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM dateien")
		.fetch_one(&playable.app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	let row: (Option<Uuid>, Option<f64>, i64) = sqlx::query_as(
		r#"SELECT "PlaybackDateiId","DurationSeconds","RowVersion" FROM recordings WHERE "Id"=$1"#,
	)
	.bind(r)
	.fetch_one(&playable.app.pool)
	.await
	.unwrap();
	assert_eq!(row, (None, Some(3450.0), 1));
	playable.done().await;
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn transient_storage_failure_requeues_with_backoff_then_exhausts() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	let connection = std::env::var("ARCHIVE_TEST_AZURITE").unwrap();
	let bad = connection
		.split(';')
		.map(|p| {
			if p.starts_with("AccountKey=") {
				format!(
					"AccountKey={}",
					"QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE="
				)
			} else {
				p.into()
			}
		})
		.collect::<Vec<String>>()
		.join(";");
	v.send().await;
	let output = v
		.run(&[
			("ConnectionStrings__archive-blobs", bad.clone()),
			("Archive__Video__MaxAttempts", "2".into()),
		])
		.await;
	let s = v.status().await;
	assert_eq!(s["status"], "queued");
	assert_eq!(s["attemptCount"], 1);
	assert!(s["lastAttemptAt"].is_string());
	assert!(s["completedAt"].is_null());
	assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
	assert!(!String::from_utf8_lossy(&output.stdout).contains("QUFBQUFB"));
	v.send().await;
	v.run(&[
		("ConnectionStrings__archive-blobs", bad),
		("Archive__Video__MaxAttempts", "2".into()),
	])
	.await;
	let s = v.status().await;
	assert_eq!(s["status"], "failed");
	assert_eq!(s["attemptCount"], 2);
	assert_eq!(
		s["failureReason"],
		"Die Konvertierung ist nach mehreren Versuchen fehlgeschlagen."
	);
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit CLI/current PG/private Queue high dequeue versus low row attempts and bounded backoff"]
async fn dequeue_budget_exhausts_independently_of_row_attempts_and_backoff_is_bounded() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.seed_job(0, 0).await;
	// This is a same-receipt test; keep the legitimate pre-drain outbox sweep
	// from adding a second envelope while the one-second backoff elapses.
	sqlx::query(
		r#"UPDATE video_jobs SET "LastEnqueuedAt"=now()+interval '1 minute' WHERE "DateiId"=$1"#,
	)
	.bind(v.id)
	.execute(&v.app.pool)
	.await
	.unwrap();
	v.send().await;
	let message = v.queue.receive(1, 30).await.unwrap().pop().unwrap();
	assert_eq!(message.dequeue_count, 1);
	v.queue.abandon(&message, 0).await.unwrap();
	let bad = std::env::var("ARCHIVE_TEST_AZURITE")
		.unwrap()
		.split(';')
		.map(|field| {
			if field.starts_with("AccountKey=") {
				"AccountKey=QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE="
			} else {
				field
			}
		})
		.collect::<Vec<_>>()
		.join(";");
	let extra = [
		("ConnectionStrings__archive-blobs", bad),
		("Archive__Video__MaxAttempts", "4".into()),
		("Archive__Video__MaxMessagesPerRun", "1".into()),
		("Archive__Video__VisibilityBackoff", "00:00:05".into()),
		("Archive__Video__RedisplayAfter", "00:00:01".into()),
	];
	v.run(&extra).await;
	let status = v.status().await;
	assert_eq!(status["status"], "queued");
	assert_eq!(status["attemptCount"], 1);
	assert!(status["lastAttemptAt"].is_string());
	assert!(status["completedAt"].is_null());
	assert!(
		v.queue.receive(1, 30).await.unwrap().is_empty(),
		"receipt backoff must be positive"
	);
	// Five seconds times dequeue2 must be capped by redisplay1s, not10s.
	let message = tokio::time::timeout(std::time::Duration::from_secs(3), async {
		loop {
			if let Some(message) = v.queue.receive(1, 30).await.unwrap().pop() {
				break message;
			}
			tokio::time::sleep(std::time::Duration::from_millis(25)).await;
		}
	})
	.await
	.unwrap();
	assert_eq!(message.dequeue_count, 3);
	v.queue.abandon(&message, 0).await.unwrap();
	v.run(&extra).await; // product dequeue4; durable attempt2 still below4
	let status = v.status().await;
	assert_eq!(status["status"], "failed");
	assert_eq!(
		status["attemptCount"], 2,
		"row budget must not explain message exhaustion"
	);
	assert_eq!(
		status["failureReason"],
		"Die Konvertierung ist nach mehreren Versuchen fehlgeschlagen."
	);
	assert!(
		v.queue.receive(1, 1).await.unwrap().is_empty(),
		"exhausted receipt is deleted"
	);
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI gate"]
async fn pump_drains_until_empty_and_applies_bounded_dispositions() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.queue.send("not json").await.unwrap();
	v.send().await;
	v.run(&[("Archive__Video__MaxMessagesPerRun", "2".into())])
		.await;
	assert_eq!(v.status().await["status"], "completed");
	assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
	let s = v.status().await;
	v.run(&[]).await;
	assert_eq!(v.status().await, s);
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue/Blob CLI configuration gate"]
async fn lease_and_batch_options_stay_defensively_bounded_through_cli() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	for _ in 0..34 {
		v.queue.send("poison").await.unwrap();
	}
	v.run(&[
		("Archive__Video__BatchSize", "999".into()),
		("Archive__Video__MaxMessagesPerRun", "3".into()),
		("Archive__Video__TimeBudget", "00:00:01".into()),
		("Archive__Video__ReceiveVisibility", "00:01:00".into()),
	])
	.await;
	let mut count = 0;
	loop {
		let messages = v.queue.receive(32, 1).await.unwrap();
		if messages.is_empty() {
			break;
		}
		count += messages.len();
		for m in messages {
			v.queue.delete(&m).await.unwrap();
		}
	}
	assert_eq!(count, 31);
	v.send().await;
	v.run(&[
		("Archive__Video__MaxInputBytes", " 0x1 ".into()),
		("Archive__Video__FfprobePath", "   ".into()),
	])
	.await;
	let s = v.status().await;
	assert_eq!(s["attemptCount"], 0);
	assert_eq!(
		s["failureReason"],
		"Die Aufnahme ist zu groß für die automatische Konvertierung."
	);
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit product CLI/forwarded private Queue HTTP default and invalid option controls"]
async fn default_and_invalid_video_queue_options_are_observed_on_real_requests() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	let queue = AzureQueue::select(
		Some(&std::env::var("ARCHIVE_TEST_QUEUE").unwrap()),
		None,
		"archive-videos",
		true,
		Arc::new(SystemClock),
	)
	.unwrap();
	queue.ensure().await.unwrap();
	assert!(
		queue.receive(1, 1).await.unwrap().is_empty(),
		"exclusive233 default queue must start empty"
	);
	for variant in ["default", "invalid", "visibility-fit"] {
		for _ in 0..11 {
			queue.send("poison").await.unwrap();
		}
		let observer = support::videos::QueueObserver::new(&v.directory).await;
		let mut command = v.command(
			"--transcode-queue",
			&[(
				"ConnectionStrings__archive-queues",
				observer.connection.clone(),
			)],
		);
		for key in [
			"QueueName",
			"TimeBudget",
			"BatchSize",
			"ReceiveVisibility",
			"MaxMessagesPerRun",
			"DrainBudget",
		] {
			command.env_remove(format!("Archive__Video__{key}"));
		}
		if variant == "invalid" {
			for key in ["TimeBudget", "ReceiveVisibility", "DrainBudget"] {
				command.env(format!("Archive__Video__{key}"), "00:00:00");
			}
			for key in ["BatchSize", "MaxMessagesPerRun"] {
				command.env(format!("Archive__Video__{key}"), "0");
			}
			command.env("Archive__Video__QueueName", "  ");
		}
		if variant == "visibility-fit" {
			command.env("Archive__Video__BatchSize", "2");
		}
		assert!(command.output().await.unwrap().status.success());
		let requests = observer.finish().await;
		let receives: Vec<_> = requests
			.iter()
			.filter(|r| {
				r["method"] == "GET"
					&& r["path"]
						.as_str()
						.is_some_and(|p| p.ends_with("/archive-videos/messages"))
			})
			.collect();
		assert_eq!(receives.len(), 10, "default message cap10: {variant}");
		for receive in receives {
			assert_eq!(
				receive["query"]["numofmessages"],
				serde_json::json!(["1"]),
				"default/visibility-fit batch1; exact message budget is pinned separately: {variant}"
			);
			assert_eq!(
				receive["query"]["visibilitytimeout"],
				serde_json::json!(["28800"]),
				"frozen8h visibility/budget: {variant}"
			);
		}
		let remaining = queue.receive(32, 30).await.unwrap();
		assert_eq!(remaining.len(), 1);
		queue.delete(&remaining[0]).await.unwrap();
	}
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit product CLI/current PG/private providers stale defaults and reachable executable fallbacks"]
async fn stale_input_attempt_and_tool_defaults_survive_invalid_setters() {
	for minutes in [59, 61] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.seed_job(1, 0).await;
		sqlx::query(r#"UPDATE video_jobs SET "LastAttemptAt"=now()-make_interval(mins=>$2),"LastEnqueuedAt"=now()+interval '1 minute' WHERE "DateiId"=$1"#).bind(v.id).bind(minutes).execute(&v.app.pool).await.unwrap();
		let before = v.status().await;
		v.send().await;
		let observer = support::videos::QueueObserver::new(&v.directory).await;
		v.run(&[
			(
				"ConnectionStrings__archive-queues",
				observer.connection.clone(),
			),
			("Archive__Video__StaleRunningAfter", "00:00:00".into()),
			("Archive__Video__TimeBudget", "00:00:00".into()),
			("Archive__Video__FfprobePath", "   ".into()),
			("Archive__Video__FfmpegPath", "\t ".into()),
		])
		.await;
		let requests = observer.finish().await;
		let status = v.status().await;
		if minutes == 59 {
			assert_eq!(status, before, "invalid stale setter falls back to1h");
			assert!(
				requests.iter().any(|r| r["method"] == "PUT"
					&& r["query"]["visibilitytimeout"] == serde_json::json!(["60"])),
				"default fresh-lease backoff is60s"
			);
		} else {
			assert_eq!(
				status["status"], "completed",
				"blank ffprobe AND ffmpeg paths must reach real defaults past all prechecks"
			);
			assert_eq!(status["attemptCount"], 1);
		}
		v.done().await;
	}
	for (attempts, size, expected_attempts, expected_status, reason) in [
		(
			3,
			CLIP.len() as i64,
			3,
			"failed",
			"Die Konvertierung ist nach mehreren Versuchen fehlgeschlagen.",
		),
		(2, CLIP.len() as i64, 3, "completed", ""),
		(
			0,
			10 * 1024 * 1024 * 1024,
			1,
			"failed",
			"Die Aufnahme konnte nicht gelesen werden.",
		),
		(
			0,
			10 * 1024 * 1024 * 1024 + 1,
			0,
			"failed",
			"Die Aufnahme ist zu groß für die automatische Konvertierung.",
		),
	] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.seed_job(0, attempts).await;
		sqlx::query(r#"UPDATE dateien SET "SizeBytes"=$2 WHERE "Id"=$1"#)
			.bind(v.id)
			.bind(size)
			.execute(&v.app.pool)
			.await
			.unwrap();
		v.send().await;
		v.run(&[
			("Archive__Video__MaxAttempts", "0".into()),
			("Archive__Video__MaxInputBytes", "0".into()),
			("Archive__Video__FfprobePath", "  ".into()),
		])
		.await;
		let status = v.status().await;
		assert_eq!(status["status"], expected_status);
		assert_eq!(status["attemptCount"], expected_attempts);
		if reason.is_empty() {
			assert!(status["failureReason"].is_null());
		} else {
			assert_eq!(status["failureReason"], reason);
		}
		v.done().await;
	}
}
#[tokio::test]
#[ignore = "explicit current PG/private Queue CLI dispatcher gate"]
async fn dispatcher_sweeps_only_unstamped_and_strictly_stale_queued_rows_in_order() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.seed_job(0, 0).await;
	let stale = datei(&v.app, v.owner, false, "aufnahme", "video/mp4").await;
	let fresh = datei(&v.app, v.owner, false, "aufnahme", "video/mp4").await;
	let running = datei(&v.app, v.owner, false, "aufnahme", "video/mp4").await;
	sqlx::query(r#"UPDATE video_jobs SET "LastEnqueuedAt"=NULL,"CreatedAt"=now()-interval '30 minutes' WHERE "DateiId"=$1"#).bind(v.id).execute(&v.app.pool).await.unwrap();
	for (id, status, created, enqueued) in [
		(stale, 0, 120, 61),
		(fresh, 0, 10, 59),
		(running, 1, 5, 120),
	] {
		sqlx::query(r#"INSERT INTO video_jobs ("DateiId","Status","AttemptCount","TriggeredByAccountId","CreatedAt","UpdatedAt","LastEnqueuedAt","RowVersion") VALUES ($1,$2,0,$3,now()-make_interval(mins=>$4),now(),now()-make_interval(mins=>$5),7)"#).bind(id).bind(status).bind(Uuid::nil()).bind(created).bind(enqueued).execute(&v.app.pool).await.unwrap();
	}
	let output = v
		.command(
			"--dispatch-videos",
			&[("Archive__Video__MaxDispatchPerRun", "1".into())],
		)
		.output()
		.await
		.unwrap();
	assert!(output.status.success());
	let messages = v.queue.receive(32, 30).await.unwrap();
	assert_eq!(messages.len(), 1);
	assert_eq!(
		serde_json::from_str::<serde_json::Value>(&messages[0].body).unwrap()["dateiId"],
		stale.to_string()
	);
	v.queue.delete(&messages[0]).await.unwrap();
	assert!(
		v.command("--dispatch-videos", &[])
			.output()
			.await
			.unwrap()
			.status
			.success()
	);
	let messages = v.queue.receive(32, 30).await.unwrap();
	assert_eq!(messages.len(), 1);
	assert_eq!(
		serde_json::from_str::<serde_json::Value>(&messages[0].body).unwrap()["dateiId"],
		v.id.to_string()
	);
	v.queue.delete(&messages[0]).await.unwrap();
	let versions: Vec<i64> =
		sqlx::query_scalar(r#"SELECT "RowVersion" FROM video_jobs ORDER BY "RowVersion""#)
			.fetch_all(&v.app.pool)
			.await
			.unwrap();
	assert_eq!(versions, vec![0, 7, 7, 7]);
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL/private Queue/Blob/native ffmpeg CLI gate"]
async fn original_becomes_a_real_h264_copy_and_fills_only_empty_slot() {
	let app = App::new().await;
	let event = event(&app).await;
	let id = datei(&app, event, true, "aufnahme", "video/mp4").await;
	let recording = recording(&app, event, id, None, "video").await;
	sqlx::query(r#"UPDATE recordings SET "DurationSeconds"=3450 WHERE "Id"=$1"#)
		.bind(recording)
		.execute(&app.pool)
		.await
		.unwrap();
	let connection = std::env::var("ARCHIVE_TEST_AZURITE").unwrap();
	let queues = std::env::var("ARCHIVE_TEST_QUEUE").unwrap();
	let container = format!("video233-{}", Uuid::new_v4().simple());
	let queue_name = format!("video233-{}", Uuid::new_v4().simple());
	let storage = BlobStorage::new(
		StorageConfig::connection_string(&connection, &container).unwrap(),
		Arc::new(SystemClock),
	)
	.unwrap();
	let blobs = storage
		.service_client()
		.unwrap()
		.blob_container_client(&container);
	blobs.create(None).await.unwrap();
	let bytes = include_bytes!("../../../../../../tests/archive/apphost/TestAssets/kurzfilm.mp4");
	let blob = format!("dateien/{id}/x");
	blobs
		.blob_client(&blob)
		.upload(azure_core::http::RequestContent::from(bytes.to_vec()), None)
		.await
		.unwrap();
	sqlx::query(
		r#"UPDATE dateien SET "SizeBytes"=$2,"OriginalFileName"='Konzert.mp4' WHERE "Id"=$1"#,
	)
	.bind(id)
	.bind(bytes.len() as i64)
	.execute(&app.pool)
	.await
	.unwrap();
	let queue = AzureQueue::select(
		Some(&queues),
		None,
		&queue_name,
		true,
		Arc::new(SystemClock),
	)
	.unwrap();
	queue.ensure().await.unwrap();
	queue
		.send(&archive_server::queue::envelope("transcode", id))
		.await
		.unwrap();
	let mut url = url::Url::parse(&std::env::var("ARCHIVE_TEST_POSTGRES").unwrap()).unwrap();
	let db: String = sqlx::query_scalar("SELECT current_database()")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	url.set_path(&db);
	let started = chrono::Utc::now();
	let output = tokio::time::timeout(
		std::time::Duration::from_secs(30),
		tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
			.arg("--transcode-queue")
			.env("ARCHIVE_ENVIRONMENT", "Production")
			.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
			.env("ConnectionStrings__archive-db", url.as_str())
			.env("ConnectionStrings__archive-blobs", &connection)
			.env("ConnectionStrings__archive-queues", &queues)
			.env("Archive__Assets__ContainerName", &container)
			.env("Archive__Video__QueueName", &queue_name)
			.env("Archive__Video__TimeBudget", "00:00:10")
			.kill_on_drop(true)
			.output(),
	)
	.await
	.unwrap()
	.unwrap();
	assert!(
		output.status.success(),
		"normal product CLI must finish, got {:?}",
		output.status
	);
	let mut editor = app.admin().await;
	let (status, payload) = editor.get(&format!("/api/dateien/videos?ids={id}")).await;
	assert_eq!(status, 200, "{payload}");
	assert_eq!(payload["results"][0]["status"], "completed", "{payload}");
	let job = &payload["results"][0];
	assert!(job["failureReason"].is_null());
	assert_eq!(job["attemptCount"], 1);
	let completed = chrono::DateTime::parse_from_rfc3339(job["completedAt"].as_str().unwrap())
		.unwrap()
		.with_timezone(&chrono::Utc);
	assert!(completed >= started && completed <= chrono::Utc::now());
	assert_eq!(job["completedAt"], job["updatedAt"]);
	let copy = Uuid::parse_str(payload["results"][0]["playbackDateiId"].as_str().unwrap()).unwrap();
	assert_ne!(copy, id);
	let stored: (Option<Uuid>, Option<f64>, i64) = sqlx::query_as(
		r#"SELECT "PlaybackDateiId","DurationSeconds","RowVersion" FROM recordings WHERE "Id"=$1"#,
	)
	.bind(recording)
	.fetch_one(&app.pool)
	.await
	.unwrap();
	assert_eq!(stored, (Some(copy), None, 2));
	let copy_blob: String = sqlx::query_scalar(r#"SELECT "BlobName" FROM dateien WHERE "Id"=$1"#)
		.bind(copy)
		.fetch_one(&app.pool)
		.await
		.unwrap();
	let metadata: serde_json::Value =
		sqlx::query_scalar("SELECT to_jsonb(d) FROM dateien d WHERE \"Id\"=$1")
			.bind(copy)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	let original: serde_json::Value =
		sqlx::query_scalar("SELECT to_jsonb(d) FROM dateien d WHERE \"Id\"=$1")
			.bind(id)
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(metadata["Kind"], "aufnahme");
	assert_eq!(metadata["ContentType"], "video/mp4");
	assert_eq!(metadata["EventId"], event.to_string());
	assert!(metadata["LiedId"].is_null());
	assert_eq!(
		metadata["CreatedByAccountId"],
		original["CreatedByAccountId"]
	);
	assert_eq!(metadata["OriginalFileName"], "Konzert - Abspielkopie.mp4");
	assert!(copy_blob.starts_with(&format!("dateien/{copy}/")));
	assert!(copy_blob.ends_with(".mp4"));
	assert_ne!(copy_blob, original["BlobName"].as_str().unwrap());
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM dateien")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(count, 2, "one persistent original and exactly one copy");
	let downloaded = blobs
		.blob_client(&copy_blob)
		.download(None)
		.await
		.unwrap()
		.body
		.collect()
		.await
		.unwrap();
	assert_eq!(metadata["SizeBytes"], downloaded.len());
	assert!(!downloaded.is_empty(), "the exact committed blob exists");
	let path = std::env::temp_dir().join(format!("video233-{}.mp4", Uuid::new_v4()));
	tokio::fs::write(&path, downloaded).await.unwrap();
	let probe = tokio::process::Command::new("ffprobe")
		.args(["-v", "error", "-show_streams", "-print_format", "json"])
		.arg(&path)
		.output()
		.await
		.unwrap();
	assert!(probe.status.success());
	let probe: serde_json::Value = serde_json::from_slice(&probe.stdout).unwrap();
	assert_eq!(probe["streams"][0]["codec_name"], "h264");
	assert_eq!(probe["streams"][1]["codec_name"], "aac");
	assert!(queue.receive(1, 30).await.unwrap().is_empty());
	tokio::fs::remove_file(path).await.unwrap();
	blobs.delete(None).await.unwrap();
	app.close().await;
}
