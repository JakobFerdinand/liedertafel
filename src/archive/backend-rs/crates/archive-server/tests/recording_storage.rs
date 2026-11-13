//! Normal private Azurite signing at the actual recording authorization seam.
// Provider-fixture construction must fail loudly, like storage_azurite.rs.
#![allow(clippy::unwrap_used, clippy::expect_used)]
mod support;
use archive_server::{
	auth::SystemClock,
	storage::{BlobStorage, StorageConfig},
};
use serde_json::{Value, json};
use std::sync::Arc;
use support::{App, Client, recordings::*};
use uuid::Uuid;
async fn upload(c: &mut Client, event: Uuid, bytes: &[u8], content: &str) -> Value {
	let session=support::upload::open(c,event,true,json!({"kind":"aufnahme","contentType":content,"fileName":"synthetic-audio.wav","sizeBytes":bytes.len()})).await;
	let response = reqwest::Client::new()
		.put(session["uploadUrl"].as_str().unwrap())
		.header("x-ms-blob-type", "BlockBlob")
		.header("content-type", content)
		.body(bytes.to_vec())
		.send()
		.await
		.unwrap();
	assert_eq!(response.status(), 201);
	let (status, b) = support::upload::finish(c, &session).await;
	assert_eq!(status, 200, "{b}");
	b["datei"].clone()
}
#[tokio::test]
#[ignore = "explicit private Azurite 3.35.0 + disposable PostgreSQL recording SAS/GET/HEAD/range/tamper/policy"]
async fn recording_upload_attach_publish_direct_signed_get_head_ranges_and_policy_renewal() {
	let connection = std::env::var("ARCHIVE_TEST_AZURITE").unwrap();
	assert!(connection.contains("BlobEndpoint=http://127.0.0.1:"));
	let container = format!("recording-{}", Uuid::new_v4().simple());
	let storage = Arc::new(
		BlobStorage::new(
			StorageConfig::connection_string(&connection, &container).unwrap(),
			Arc::new(SystemClock),
		)
		.unwrap(),
	);
	let service = storage.service_client().unwrap();
	service
		.blob_container_client(&container)
		.create(None)
		.await
		.unwrap();
	let mut app = App::new().await;
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let event = event(&mut editor).await;
	let rec = create(&mut editor, event, "Synthetischer Ton", "audio").await;
	let path = format!("/api/recordings/{rec}");
	let http = reqwest::Client::new();
	// Generated PCM WAV only: actual browser-decodable synthetic media, no private fixtures.
	let mut bytes = Vec::new();
	let data = 16000u32;
	bytes.extend_from_slice(b"RIFF");
	bytes.extend_from_slice(&(36 + data).to_le_bytes());
	bytes.extend_from_slice(b"WAVEfmt ");
	bytes.extend_from_slice(&16u32.to_le_bytes());
	bytes.extend_from_slice(&1u16.to_le_bytes());
	bytes.extend_from_slice(&1u16.to_le_bytes());
	bytes.extend_from_slice(&8000u32.to_le_bytes());
	bytes.extend_from_slice(&16000u32.to_le_bytes());
	bytes.extend_from_slice(&2u16.to_le_bytes());
	bytes.extend_from_slice(&16u16.to_le_bytes());
	bytes.extend_from_slice(b"data");
	bytes.extend_from_slice(&data.to_le_bytes());
	bytes.resize(44 + data as usize, 0);
	let file = upload(&mut editor, event, &bytes, "audio/wav").await;
	let (status, b) = editor
		.request(
			"PUT",
			&format!("{path}/dateien/original"),
			json!({"dateiId":file["id"]}),
		)
		.await;
	assert_eq!(status, 200, "{b}");
	assert_eq!(member.get(&format!("{path}/access")).await.0, 404);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"isPublished":true}))
			.await
			.0,
		200
	);
	let (status, b) = member.get(&format!("{path}/access")).await;
	assert_eq!(status, 200);
	assert!(b["downloadUrl"].is_null());
	let view = b["viewUrl"].as_str().unwrap().to_owned();
	let response = http.get(&view).send().await.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["content-type"], "audio/wav");
	assert_eq!(response.headers()["content-disposition"], "inline");
	assert_eq!(response.bytes().await.unwrap().as_ref(), bytes);
	let response = http.head(&view).send().await.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(
		response.headers()["content-length"],
		bytes.len().to_string()
	);
	let response = http
		.get(&view)
		.header("range", "bytes=0-11")
		.send()
		.await
		.unwrap();
	assert_eq!(response.status(), 206);
	assert_eq!(
		response.headers()["content-range"],
		format!("bytes 0-11/{}", bytes.len())
	);
	assert_eq!(response.bytes().await.unwrap().as_ref(), &bytes[..12]);
	assert_eq!(
		member
			.get(&format!(
				"/api/dateien/{}/access",
				file["id"].as_str().unwrap()
			))
			.await
			.0,
		404
	);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"downloadEnabled":true}))
			.await
			.0,
		200
	);
	let b = member.get(&format!("{path}/access")).await.1;
	let download = b["downloadUrl"].as_str().unwrap();
	let response = http.head(download).send().await.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["content-disposition"], "attachment");
	assert_eq!(response.headers()["content-type"], "audio/wav");
	for (key, value) in [
		("sp", "rw"),
		("sig", "tampered"),
		("rsct", "text/html"),
		("rscd", "inline"),
	] {
		let mut url = url::Url::parse(download).unwrap();
		let pairs = url
			.query_pairs()
			.into_owned()
			.map(|(k, v)| if k == key { (k, value.into()) } else { (k, v) })
			.collect::<Vec<_>>();
		url.query_pairs_mut().clear().extend_pairs(pairs);
		assert_eq!(http.get(url).send().await.unwrap().status(), 403);
	}
	let mut unsigned = url::Url::parse(download).unwrap();
	unsigned.set_query(None);
	assert_eq!(http.get(unsigned).send().await.unwrap().status(), 403);
	assert_eq!(
		http.put(download)
			.header("x-ms-blob-type", "BlockBlob")
			.body("bad")
			.send()
			.await
			.unwrap()
			.status(),
		403
	);
	let mut wrong = url::Url::parse(download).unwrap();
	wrong.set_path(&format!("{}/other", wrong.path()));
	assert_eq!(http.get(wrong).send().await.unwrap().status(), 403);
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"downloadEnabled":false}))
			.await
			.0,
		200
	);
	assert!(member.get(&format!("{path}/access")).await.1["downloadUrl"].is_null());
	assert_eq!(
		editor
			.request("PATCH", &path, json!({"isPublished":false}))
			.await
			.0,
		200
	);
	assert_eq!(member.get(&format!("{path}/access")).await.0, 404);
	// Previously issued bounded SAS is not revoked by withdrawal; new tickets are.
	assert_eq!(http.get(&view).send().await.unwrap().status(), 200);
	// Video uses this same recording ticket boundary, not the generic Datei
	// route. Synthetic container headers test metadata/signatures, not decoding
	// or the deliberately later #233 transcode worker.
	let video = create(&mut editor, event, "Synthetische Kamera", "video").await;
	let video_path = format!("/api/recordings/{video}");
	let mut mov = mp4();
	mov[8..12].copy_from_slice(b"qt  ");
	let original = upload(&mut editor, event, &mov, "video/quicktime").await;
	assert_eq!(
		editor
			.request(
				"PUT",
				&format!("{video_path}/dateien/original"),
				json!({"dateiId":original["id"]})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		editor
			.request(
				"PATCH",
				&video_path,
				json!({"isPublished":true,"downloadEnabled":true})
			)
			.await
			.0,
		200
	);
	let b = member.get(&format!("{video_path}/access")).await.1;
	assert_eq!(b["kind"], "video");
	assert_eq!(b["playbackState"], "needsPlaybackCopy");
	assert!(b["viewUrl"].is_null());
	let response = http
		.head(b["downloadUrl"].as_str().unwrap())
		.send()
		.await
		.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["content-type"], "video/quicktime");
	assert_eq!(response.headers()["content-disposition"], "attachment");
	let mp4 = mp4();
	let copy = upload(&mut editor, event, &mp4, "video/mp4").await;
	assert_eq!(
		editor
			.request(
				"PUT",
				&format!("{video_path}/dateien/playback"),
				json!({"dateiId":copy["id"]})
			)
			.await
			.0,
		200
	);
	let b = member.get(&format!("{video_path}/access")).await.1;
	assert_eq!(b["dateiId"], copy["id"]);
	assert_eq!(b["source"], "playbackCopy");
	for (key, disposition) in [("viewUrl", "inline"), ("downloadUrl", "attachment")] {
		let response = http
			.get(b[key].as_str().unwrap())
			.header("range", "bytes=4-11")
			.send()
			.await
			.unwrap();
		assert_eq!(response.status(), 206);
		assert_eq!(response.headers()["content-type"], "video/mp4");
		assert_eq!(response.headers()["content-disposition"], disposition);
		assert_eq!(response.bytes().await.unwrap().as_ref(), &mp4[4..12]);
	}
	assert_eq!(
		member
			.get(&format!(
				"/api/dateien/{}/access",
				copy["id"].as_str().unwrap()
			))
			.await
			.0,
		404
	);
	app.close().await;
	service
		.blob_container_client(&container)
		.delete(None)
		.await
		.unwrap();
}
