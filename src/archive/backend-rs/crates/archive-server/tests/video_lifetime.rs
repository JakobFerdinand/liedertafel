#![allow(clippy::unwrap_used)] // Actual descendant/process/SQL assertions.
mod support;
use archive_server::queue::Queue;
use std::{
	path::{Path, PathBuf},
	time::Duration,
};
use support::videos::{CLIP, Video};
use uuid::Uuid;

#[tokio::test]
#[ignore = "explicit product CLI/private Queue/real PG pre-claim deadline disposition gate"]
async fn preclaim_deadline_abandons_even_when_a_concurrent_writer_finishes_the_row() {
	let mut failures = Vec::new();
	for (name, missing, concurrent_terminal) in [
		("terminal", false, true),
		("queued", false, false),
		("missing", true, false),
	] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		if !missing {
			v.seed_job(0, 0).await;
		}
		v.send().await;
		let database: String = sqlx::query_scalar("SELECT current_database()")
			.fetch_one(&v.app.pool)
			.await
			.unwrap();
		let mut lock = v.app.pool.begin().await.unwrap();
		sqlx::raw_sql("LOCK TABLE dateien IN ACCESS EXCLUSIVE MODE")
			.execute(&mut *lock)
			.await
			.unwrap();
		let child = v
			.command(
				"--transcode-queue",
				&[
					("Archive__Video__TimeBudget", "00:00:02".into()),
					("Archive__Video__VisibilityBackoff", "00:00:01".into()),
					("Archive__Video__MaxMessagesPerRun", "1".into()),
				],
			)
			.stdout(std::process::Stdio::piped())
			.stderr(std::process::Stdio::piped())
			.spawn()
			.unwrap();
		// Observe the real product process blocked on its first Datei read,
		// before it can see/claim any job. No worker/private-function seam.
		tokio::time::timeout(Duration::from_secs(5), async {
			loop {
				let blocked: bool = sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=$1 AND application_name='archive-server' AND wait_event_type='Lock' AND query LIKE '%FROM dateien%')").bind(&database).fetch_one(&v.app.pool).await.unwrap();
				if blocked { break; }
				tokio::time::sleep(Duration::from_millis(10)).await;
			}
		}).await.unwrap();
		if concurrent_terminal {
			sqlx::query(r#"UPDATE video_jobs SET "Status"=3,"CompletedAt"=now(),"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1"#).bind(v.id).execute(&v.app.pool).await.unwrap();
		}
		// The positive backoff has elapsed while the pre-claim read stays
		// locked. An abandoned receipt is visible again; a deleted one is not.
		tokio::time::sleep(Duration::from_millis(3300)).await;
		let receipts = v.queue.receive(1, 30).await.unwrap();
		let observed = receipts.len();
		for message in receipts {
			assert_eq!(
				message.body,
				archive_server::queue::envelope("transcode", v.id)
			);
			assert_eq!(message.dequeue_count, 2);
			v.queue.delete(&message).await.unwrap();
		}
		lock.rollback().await.unwrap();
		let output = tokio::time::timeout(Duration::from_secs(5), child.wait_with_output())
			.await
			.unwrap()
			.unwrap();
		assert!(output.status.success(), "{name}: finite exit");
		let row: Option<(i32, i32, i64)> = sqlx::query_as(
			r#"SELECT "Status","AttemptCount","RowVersion" FROM video_jobs WHERE "DateiId"=$1"#,
		)
		.bind(v.id)
		.fetch_optional(&v.app.pool)
		.await
		.unwrap();
		assert_eq!(
			row,
			if missing {
				None
			} else if concurrent_terminal {
				Some((3, 0, 1))
			} else {
				Some((0, 0, 0))
			},
			"{name}: only the fixture's concurrent writer may change the row"
		);
		if observed != 1 {
			failures.push((name, observed));
		}
		v.done().await;
	}
	// Actual delete control: with a normally readable terminal row, the same
	// envelope really is acknowledged and does not reappear after backoff.
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.seed_job(3, 0).await;
	v.send().await;
	v.run(&[]).await;
	assert!(v.queue.receive(1, 1).await.unwrap().is_empty());
	assert_eq!(v.status().await["attemptCount"], 0);
	v.done().await;
	assert!(
		failures.is_empty(),
		"pre-claim deadline lost queue receipt instead of abandoning: {failures:?}"
	);
}

#[tokio::test]
#[ignore = "explicit product CLI/private Queue/real PG no-lease no-reload dependency gate"]
async fn preclaim_deadline_releases_receipt_without_waiting_for_job_recovery_sql() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.seed_job(0, 0).await;
	v.send().await;
	let database: String = sqlx::query_scalar("SELECT current_database()")
		.fetch_one(&v.app.pool)
		.await
		.unwrap();
	let mut lock = v.app.pool.begin().await.unwrap();
	sqlx::raw_sql("LOCK TABLE dateien IN ACCESS EXCLUSIVE MODE")
		.execute(&mut *lock)
		.await
		.unwrap();
	let child = v
		.command(
			"--transcode-queue",
			&[
				("Archive__Video__TimeBudget", "00:00:02".into()),
				("Archive__Video__VisibilityBackoff", "00:00:01".into()),
				("Archive__Video__MaxMessagesPerRun", "1".into()),
			],
		)
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.spawn()
		.unwrap();
	tokio::time::timeout(Duration::from_secs(5), async {
		loop {
			let blocked: bool = sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=$1 AND application_name='archive-server' AND wait_event_type='Lock' AND query LIKE '%FROM dateien%')").bind(&database).fetch_one(&v.app.pool).await.unwrap();
			if blocked { break; }
			tokio::time::sleep(Duration::from_millis(10)).await;
		}
	}).await.unwrap();
	// A recovery read would now wait here too. The expired, unclaimed message
	// must be abandoned while BOTH relations remain unavailable to the worker.
	sqlx::raw_sql("LOCK TABLE video_jobs IN ACCESS EXCLUSIVE MODE")
		.execute(&mut *lock)
		.await
		.unwrap();
	tokio::time::sleep(Duration::from_millis(3300)).await;
	let recovery_query: bool = sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=$1 AND application_name='archive-server' AND wait_event_type='Lock' AND query LIKE '%FROM video_jobs%')").bind(&database).fetch_one(&v.app.pool).await.unwrap();
	let messages = v.queue.receive(1, 30).await.unwrap();
	let count = messages.len();
	for message in messages {
		assert_eq!(message.dequeue_count, 2);
		v.queue.delete(&message).await.unwrap();
	}
	lock.rollback().await.unwrap();
	let output = tokio::time::timeout(Duration::from_secs(5), child.wait_with_output())
		.await
		.unwrap()
		.unwrap();
	assert!(output.status.success());
	assert!(
		!recovery_query,
		"unclaimed deadline must not start a job recovery query"
	);
	assert_eq!(
		count, 1,
		"receipt must be released without recovery SQL availability"
	);
	let status = v.status().await;
	assert_eq!(status["status"], "queued");
	assert_eq!(status["attemptCount"], 0);
	assert_eq!(status["rowVersion"], 0);
	v.done().await;
}
async fn wait_file(path: &Path) {
	tokio::time::timeout(Duration::from_secs(5), async {
		while !path.exists() {
			tokio::time::sleep(Duration::from_millis(10)).await;
		}
	})
	.await
	.unwrap();
}
fn tree_tool() -> (PathBuf, PathBuf) {
	use std::io::Write;
	use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
	let path = std::env::temp_dir().join(format!("video233 tree {}.py", Uuid::new_v4()));
	let witness = path.with_extension("pids");
	let script = format!(
		"#!/usr/bin/python3\nimport os,sys,subprocess\nchild=subprocess.Popen([sys.executable,'-c','import os,time; os.write(1,b\"x\"*1048576); os.write(2,b\"y\"*1048576); time.sleep(60)'])\nwith open({:?},'w') as f: f.write(str(os.getpid())+' '+str(child.pid))\nchild.wait()\n",
		witness.to_str().unwrap()
	);
	let mut f = std::fs::OpenOptions::new()
		.create_new(true)
		.write(true)
		.mode(0o700)
		.open(&path)
		.unwrap();
	f.write_all(script.as_bytes()).unwrap();
	std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o700)).unwrap();
	(path, witness)
}
#[tokio::test]
#[ignore = "explicit actual process-tree/deadline/current PostgreSQL/Queue/Blob gate"]
async fn deadline_kills_actual_pipe_holding_descendant_and_reaps_before_recovery() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	let (tool, witness) = tree_tool();
	let mut command = v.command(
		"--transcode-queue",
		&[
			(
				"Archive__Video__FfprobePath",
				tool.to_string_lossy().into_owned(),
			),
			("Archive__Video__TimeBudget", "00:00:00.5".into()),
		],
	);
	let child = command
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.spawn()
		.unwrap();
	wait_file(&witness).await;
	let pids = std::fs::read_to_string(&witness).unwrap();
	let output = tokio::time::timeout(Duration::from_secs(10), child.wait_with_output())
		.await
		.unwrap()
		.unwrap();
	assert!(output.status.success());
	for pid in pids.split_whitespace() {
		assert!(
			!Path::new("/proc").join(pid).exists(),
			"actual tool and descendant must be gone/reaped: {pid}"
		);
	}
	let s = v.status().await;
	assert_eq!(s["status"], "queued");
	assert_eq!(s["attemptCount"], 1);
	assert!(s["failureReason"].is_null());
	std::fs::remove_file(tool).unwrap();
	std::fs::remove_file(witness).unwrap();
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit actual process-tree/SIGTERM/current PostgreSQL/Queue/Blob gate"]
async fn parent_sigterm_kills_actual_descendant_and_leaves_recoverable_running_lease() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	let (tool, witness) = tree_tool();
	let mut child = v
		.command(
			"--transcode-queue",
			&[(
				"Archive__Video__FfprobePath",
				tool.to_string_lossy().into_owned(),
			)],
		)
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.spawn()
		.unwrap();
	wait_file(&witness).await;
	let pids = std::fs::read_to_string(&witness).unwrap();
	let pid = child.id().unwrap().to_string();
	assert!(
		tokio::process::Command::new("/bin/kill")
			.args(["-TERM", "--", &pid])
			.status()
			.await
			.unwrap()
			.success()
	);
	let status = tokio::time::timeout(Duration::from_secs(10), child.wait())
		.await
		.unwrap()
		.unwrap();
	assert!(!status.success());
	for pid in pids.split_whitespace() {
		assert!(
			!Path::new("/proc").join(pid).exists(),
			"tool/descendant must be reaped"
		);
	}
	let s = v.status().await;
	assert_eq!(s["status"], "running");
	assert_eq!(s["attemptCount"], 1);
	std::fs::remove_file(tool).unwrap();
	std::fs::remove_file(witness).unwrap();
	v.done().await;
}

fn held_probe() -> (PathBuf, PathBuf, PathBuf) {
	use std::io::Write;
	use std::os::unix::fs::OpenOptionsExt;
	let path = std::env::temp_dir().join(format!("video233 probe {}.py", Uuid::new_v4()));
	let ready = path.with_extension("ready");
	let release = path.with_extension("release");
	let real = std::env::var("Archive__Video__FfprobePath").unwrap();
	let script = format!(
		"#!/usr/bin/python3\nimport os,sys,time\nopen({:?},'w').close()\nwhile not os.path.exists({:?}): time.sleep(.01)\nos.execv({:?},[{:?}]+sys.argv[1:])\n",
		ready.to_str().unwrap(),
		release.to_str().unwrap(),
		real,
		real
	);
	let mut f = std::fs::OpenOptions::new()
		.create_new(true)
		.write(true)
		.mode(0o700)
		.open(&path)
		.unwrap();
	f.write_all(script.as_bytes()).unwrap();
	(path, ready, release)
}
#[tokio::test]
#[ignore = "explicit real SQL/actual held native probe late-winner CAS gate"]
async fn late_worker_cannot_publish_copy_over_a_newer_terminal_winner() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	let (tool, ready, release) = held_probe();
	let mut child = v
		.command(
			"--transcode-queue",
			&[(
				"Archive__Video__FfprobePath",
				tool.to_string_lossy().into_owned(),
			)],
		)
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.spawn()
		.unwrap();
	wait_file(&ready).await;
	assert_eq!(v.status().await["status"], "running");
	sqlx::query(r#"UPDATE video_jobs SET "Status"=3,"CompletedAt"=now(),"RowVersion"="RowVersion"+1 WHERE "DateiId"=$1"#).bind(v.id).execute(&v.app.pool).await.unwrap();
	let winner = v.status().await;
	std::fs::write(&release, []).unwrap();
	assert!(
		tokio::time::timeout(Duration::from_secs(15), child.wait())
			.await
			.unwrap()
			.unwrap()
			.success()
	);
	assert_eq!(v.status().await, winner);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM dateien")
		.fetch_one(&v.app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	for p in [tool, ready, release] {
		std::fs::remove_file(p).unwrap();
	}
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit real SQL/actual held native probe recording slot race gate"]
async fn late_worker_never_overwrites_an_editor_playback_slot() {
	use support::catalogue::{datei, recording};
	let v = Video::new(CLIP, "aufnahme", "video/mp4", true).await;
	let recording = recording(&v.app, v.owner, v.id, None, "video").await;
	v.send().await;
	let (tool, ready, release) = held_probe();
	let mut child = v
		.command(
			"--transcode-queue",
			&[(
				"Archive__Video__FfprobePath",
				tool.to_string_lossy().into_owned(),
			)],
		)
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.spawn()
		.unwrap();
	wait_file(&ready).await;
	let editor_copy = datei(&v.app, v.owner, true, "aufnahme", "video/mp4").await;
	sqlx::query(r#"UPDATE recordings SET "PlaybackDateiId"=$2,"DurationSeconds"=42,"RowVersion"="RowVersion"+1 WHERE "Id"=$1"#).bind(recording).bind(editor_copy).execute(&v.app.pool).await.unwrap();
	std::fs::write(&release, []).unwrap();
	assert!(
		tokio::time::timeout(Duration::from_secs(15), child.wait())
			.await
			.unwrap()
			.unwrap()
			.success()
	);
	assert_eq!(v.status().await["status"], "completed");
	let row: (Option<Uuid>, Option<f64>, i64) = sqlx::query_as(
		r#"SELECT "PlaybackDateiId","DurationSeconds","RowVersion" FROM recordings WHERE "Id"=$1"#,
	)
	.bind(recording)
	.fetch_one(&v.app.pool)
	.await
	.unwrap();
	assert_eq!(row, (Some(editor_copy), Some(42.0), 2));
	for p in [tool, ready, release] {
		std::fs::remove_file(p).unwrap();
	}
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit real SQL/competing product CLI lease gate"]
async fn a_competing_worker_cannot_retake_a_fresh_running_lease() {
	let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
	v.send().await;
	let (tool, ready, release) = held_probe();
	let mut child = v
		.command(
			"--transcode-queue",
			&[(
				"Archive__Video__FfprobePath",
				tool.to_string_lossy().into_owned(),
			)],
		)
		.stdout(std::process::Stdio::piped())
		.stderr(std::process::Stdio::piped())
		.spawn()
		.unwrap();
	wait_file(&ready).await;
	let before = v.status().await;
	assert_eq!(before["status"], "running");
	v.send().await;
	v.run(&[]).await;
	assert_eq!(v.status().await, before);
	std::fs::write(&release, []).unwrap();
	assert!(
		tokio::time::timeout(Duration::from_secs(15), child.wait())
			.await
			.unwrap()
			.unwrap()
			.success()
	);
	let s = v.status().await;
	assert_eq!(s["attemptCount"], 1);
	assert_eq!(s["status"], "completed");
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM dateien")
		.fetch_one(&v.app.pool)
		.await
		.unwrap();
	assert_eq!(count, 2);
	for p in [tool, ready, release] {
		std::fs::remove_file(p).unwrap();
	}
	v.done().await;
}

#[tokio::test]
#[ignore = "explicit real PostgreSQL/private Queue stale/exhaustion CLI gate"]
async fn stale_running_retake_and_exhausted_rows_spend_no_extra_attempt() {
	for attempts in [1, 3] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		v.seed_job(1, attempts).await;
		v.send().await;
		v.run(&[]).await;
		let s = v.status().await;
		if attempts == 1 {
			assert_eq!(s["status"], "completed");
			assert_eq!(s["attemptCount"], 2);
		} else {
			assert_eq!(s["status"], "failed");
			assert_eq!(s["attemptCount"], 3);
			assert_eq!(
				s["failureReason"],
				"Die Konvertierung ist nach mehreren Versuchen fehlgeschlagen."
			);
		}
		v.done().await;
	}
}

#[tokio::test]
#[ignore = "explicit current PG/real upload-before-save rollback/retry CLI gate"]
async fn copy_persistence_failure_requeues_without_publishing_copy_or_recording_slots() {
	use support::catalogue::recording;
	let v = Video::new(CLIP, "aufnahme", "video/mp4", true).await;
	let recording = recording(&v.app, v.owner, v.id, None, "video").await;
	sqlx::raw_sql("CREATE FUNCTION video233_refuse_copy() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"OriginalFileName\" LIKE '%Abspielkopie.mp4' THEN RAISE EXCEPTION 'synthetic'; END IF; RETURN NEW; END $$; CREATE TRIGGER video233_refuse_copy BEFORE INSERT ON dateien FOR EACH ROW EXECUTE FUNCTION video233_refuse_copy()").execute(&v.app.pool).await.unwrap();
	v.send().await;
	v.run(&[]).await;
	let s = v.status().await;
	assert_eq!(s["status"], "queued");
	assert_eq!(s["attemptCount"], 1);
	assert!(s["playbackDateiId"].is_null());
	let copy: Option<Uuid> =
		sqlx::query_scalar(r#"SELECT "PlaybackDateiId" FROM recordings WHERE "Id"=$1"#)
			.bind(recording)
			.fetch_one(&v.app.pool)
			.await
			.unwrap();
	assert_eq!(copy, None);
	let count: i64 = sqlx::query_scalar("SELECT count(*) FROM dateien")
		.fetch_one(&v.app.pool)
		.await
		.unwrap();
	assert_eq!(count, 1);
	sqlx::raw_sql(
		"DROP TRIGGER video233_refuse_copy ON dateien; DROP FUNCTION video233_refuse_copy()",
	)
	.execute(&v.app.pool)
	.await
	.unwrap();
	v.send().await;
	v.run(&[]).await;
	let s = v.status().await;
	assert_eq!(s["status"], "completed");
	assert_eq!(s["attemptCount"], 2);
	v.done().await;
}
