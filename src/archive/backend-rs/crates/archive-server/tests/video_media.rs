#![allow(clippy::unwrap_used)] // Generated public media/real native CLI assertions.
mod support;
use support::videos::{CLIP, Video};
use uuid::Uuid;

#[tokio::test]
#[ignore = "explicit real ffmpeg/ffprobe/current PG/private storage square bound gate"]
async fn actual_square_video_scales_to_1920_square_not_a_literal_1080_rectangle() {
	let path = std::env::temp_dir().join(format!("square233-{}.mp4", Uuid::new_v4()));
	let output = tokio::process::Command::new("ffmpeg")
		.args([
			"-v",
			"error",
			"-f",
			"lavfi",
			"-i",
			"color=c=black:s=2048x2048:r=1",
			"-frames:v",
			"1",
			"-c:v",
			"mpeg4",
			"-y",
		])
		.arg(&path)
		.output()
		.await
		.unwrap();
	assert!(output.status.success());
	let bytes = tokio::fs::read(&path).await.unwrap();
	let v = Video::new(&bytes, "aufnahme", "video/mp4", false).await;
	v.send().await;
	v.run(&[]).await;
	let s = v.status().await;
	assert_eq!(s["status"], "completed");
	let id = Uuid::parse_str(s["playbackDateiId"].as_str().unwrap()).unwrap();
	let blob: String = sqlx::query_scalar(r#"SELECT "BlobName" FROM dateien WHERE "Id"=$1"#)
		.bind(id)
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
	tokio::fs::write(&path, bytes).await.unwrap();
	let output = tokio::process::Command::new("ffprobe")
		.args(["-v", "error", "-show_streams", "-print_format", "json"])
		.arg(&path)
		.output()
		.await
		.unwrap();
	assert!(output.status.success());
	let facts: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
	assert_eq!(facts["streams"][0]["width"], 1920);
	assert_eq!(facts["streams"][0]["height"], 1920);
	assert_eq!(facts["streams"][0]["codec_name"], "h264");
	assert_eq!(facts["streams"][0]["pix_fmt"], "yuv420p");
	let decoded = tokio::process::Command::new("ffmpeg")
		.args(["-v", "error", "-i"])
		.arg(&path)
		.args(["-frames:v", "1", "-f", "null", "-"])
		.output()
		.await
		.unwrap();
	assert!(decoded.status.success());
	tokio::fs::remove_file(path).await.unwrap();
	v.done().await;
}
#[tokio::test]
#[ignore = "explicit current PG/private storage exact declared input length gate"]
async fn missing_blob_and_declared_length_mismatch_fail_before_tools() {
	use archive_server::storage::Storage;
	for missing in [false, true] {
		let v = Video::new(CLIP, "aufnahme", "video/mp4", false).await;
		if missing {
			v.storage.delete(&v.blob).await.unwrap();
		} else {
			sqlx::query(r#"UPDATE dateien SET "SizeBytes"="SizeBytes"+1 WHERE "Id"=$1"#)
				.bind(v.id)
				.execute(&v.app.pool)
				.await
				.unwrap();
		}
		v.send().await;
		v.run(&[("Archive__Video__FfprobePath", "/no/such/tool".into())])
			.await;
		let s = v.status().await;
		assert_eq!(s["status"], "failed");
		assert_eq!(s["attemptCount"], 1);
		assert_eq!(
			s["failureReason"],
			"Die Aufnahme konnte nicht gelesen werden."
		);
		v.done().await;
	}
}

#[tokio::test]
#[ignore = "explicit real rotation metadata/native ffmpeg/current PG/private storage gate"]
async fn actual_rotation_is_applied_before_even_scale() {
	let input = std::env::temp_dir().join(format!("rotation233-{}.mp4", Uuid::new_v4()));
	let rotated = input.with_extension("rotated.mp4");
	tokio::fs::write(&input, CLIP).await.unwrap();
	let output = tokio::process::Command::new("ffmpeg")
		.args(["-v", "error", "-display_rotation", "90", "-i"])
		.arg(&input)
		.args(["-c", "copy", "-y"])
		.arg(&rotated)
		.output()
		.await
		.unwrap();
	assert!(output.status.success());
	let output = tokio::process::Command::new("ffprobe")
		.args(["-v", "error", "-show_streams", "-print_format", "json"])
		.arg(&rotated)
		.output()
		.await
		.unwrap();
	let source: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
	assert!(
		source["streams"][0]["side_data_list"]
			.as_array()
			.unwrap()
			.iter()
			.any(|d| d["rotation"].as_i64().is_some_and(|n| n.abs() == 90))
	);
	let bytes = tokio::fs::read(&rotated).await.unwrap();
	let v = Video::new(&bytes, "aufnahme", "video/mp4", false).await;
	v.send().await;
	v.run(&[]).await;
	let s = v.status().await;
	assert_eq!(s["status"], "completed");
	let id = Uuid::parse_str(s["playbackDateiId"].as_str().unwrap()).unwrap();
	let blob: String = sqlx::query_scalar(r#"SELECT "BlobName" FROM dateien WHERE "Id"=$1"#)
		.bind(id)
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
	tokio::fs::write(&input, bytes).await.unwrap();
	let output = tokio::process::Command::new("ffprobe")
		.args(["-v", "error", "-show_streams", "-print_format", "json"])
		.arg(&input)
		.output()
		.await
		.unwrap();
	let copy: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
	assert_eq!(copy["streams"][0]["width"], 36);
	assert_eq!(copy["streams"][0]["height"], 64);
	assert_eq!(copy["streams"][0]["codec_name"], "h264");
	for p in [input, rotated] {
		tokio::fs::remove_file(p).await.unwrap();
	}
	v.done().await;
}
