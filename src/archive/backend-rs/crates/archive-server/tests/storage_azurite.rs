//! Private Azurite 3.35.0, normal signatures, no public access/OAuth/loose modes.
//! These are provider + seeded read/tier contracts, NOT #224 upload lifecycle.
// Local provider fixture construction must fail loudly on setup errors.
#![allow(clippy::unwrap_used, clippy::expect_used)]
mod support;
use archive_server::{
	auth::SystemClock,
	storage::{BlobStorage, Storage, StorageConfig, StorageError},
};
use azure_storage_blob::models::BlobClientGetPropertiesResultHeaders;
use chrono::{Duration, Utc};
use futures::StreamExt;
use serde_json::json;
use std::sync::{Arc, Mutex};
use support::{App, catalogue::*};
use uuid::Uuid;
async fn storage() -> (Arc<BlobStorage>, String) {
	let connection = std::env::var("ARCHIVE_TEST_AZURITE")
		.expect("explicit private local Azurite connection required");
	assert!(
		connection.contains("BlobEndpoint=http://127.0.0.1:"),
		"refuse hosted storage"
	);
	let container = format!("contract-{}", Uuid::new_v4().simple());
	let storage = Arc::new(
		BlobStorage::new(
			StorageConfig::connection_string(&connection, &container).unwrap(),
			Arc::new(SystemClock),
		)
		.unwrap(),
	);
	storage
		.service_client()
		.unwrap()
		.blob_container_client(&container)
		.create(None)
		.await
		.unwrap();
	(storage, container)
}
#[tokio::test]
#[ignore = "explicit private Azurite 3.35.0 + disposable PostgreSQL upload lifecycle"]
async fn authenticated_large_block_interrupt_renew_discover_commit_promote_restore_remove() {
	use base64::{Engine, engine::general_purpose::STANDARD};
	let (storage, container) = storage().await;
	let http = reqwest::Client::new();
	let mut app = App::new().await;
	app.with_storage(storage.clone());
	let mut editor = app.admin().await;
	let mut member = app.member().await;
	let lied = support::upload::create(&mut editor, "Große Partitur").await;
	let open = support::upload::open(
		&mut editor,
		lied,
		false,
		json!({"isPartitur":true,"fileName":"groß.pdf","sizeBytes":20*1024*1024+31}),
	)
	.await;
	let mut bytes = support::upload::pdf(20 * 1024 * 1024 + 31);
	bytes[64..].fill(b'x');
	let ids = (0..3)
		.map(|i| STANDARD.encode(format!("{i:06}")))
		.collect::<Vec<_>>();
	let original = url::Url::parse(open["uploadUrl"].as_str().unwrap()).unwrap();
	let mut put = original.clone();
	put.query_pairs_mut()
		.append_pair("comp", "block")
		.append_pair("blockid", &ids[0]);
	assert_eq!(
		http.put(put)
			.header("content-type", "application/pdf")
			.body(bytes[..8 * 1024 * 1024].to_vec())
			.send()
			.await
			.unwrap()
			.status(),
		201
	);
	let renew = editor
		.post(
			&format!(
				"/api/upload-sessions/{}/renew",
				open["uploadSessionId"].as_str().unwrap()
			),
			json!({}),
		)
		.await;
	assert_eq!(renew.0, 200, "{}", renew.1);
	let ticket = url::Url::parse(renew.1["uploadUrl"].as_str().unwrap()).unwrap();
	let mut discover = ticket.clone();
	discover
		.query_pairs_mut()
		.append_pair("comp", "blocklist")
		.append_pair("blocklisttype", "all");
	let response = http.get(discover).send().await.unwrap();
	assert_eq!(response.status(), 200);
	let xml = response.text().await.unwrap();
	assert!(xml.contains(&format!("<Name>{}</Name>", ids[0])));
	assert!(!xml.contains(&ids[1]));
	for i in 1..3 {
		let mut url = ticket.clone();
		url.query_pairs_mut()
			.append_pair("comp", "block")
			.append_pair("blockid", &ids[i]);
		assert_eq!(
			http.put(url)
				.header("content-type", "application/pdf")
				.body(
					bytes[i * 8 * 1024 * 1024..((i + 1) * 8 * 1024 * 1024).min(bytes.len())]
						.to_vec()
				)
				.send()
				.await
				.unwrap()
				.status(),
			201
		);
	}
	let mut commit = ticket.clone();
	commit.query_pairs_mut().append_pair("comp", "blocklist");
	let xml = format!(
		"<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>{}</BlockList>",
		ids.iter()
			.map(|id| format!("<Latest>{id}</Latest>"))
			.collect::<String>()
	);
	assert_eq!(
		http.put(commit)
			.header("content-type", "application/xml")
			.body(xml)
			.send()
			.await
			.unwrap()
			.status(),
		201
	);
	assert!(
		Storage::probe(storage.as_ref(), renew.1["blobName"].as_str().unwrap())
			.await
			.unwrap()
			.is_some()
	);
	assert_eq!(
		Storage::read_header(storage.as_ref(), renew.1["blobName"].as_str().unwrap(), 5)
			.await
			.unwrap()
			.unwrap(),
		b"%PDF-"
	);
	let (status, finished) = support::upload::finish(&mut editor, &open).await;
	assert_eq!(status, 200, "{finished}");
	let file = finished["datei"]["id"].as_str().unwrap();
	let final_blob = support::upload::blob(&app, Uuid::parse_str(file).unwrap()).await;
	assert!(
		storage
			.probe(renew.1["blobName"].as_str().unwrap())
			.await
			.unwrap()
			.is_none()
	);
	assert_eq!(
		storage
			.probe(&final_blob)
			.await
			.unwrap()
			.unwrap()
			.size_bytes,
		bytes.len() as u64
	);
	let access = member.get(&format!("/api/dateien/{file}/access")).await.1;
	let response = http
		.get(access["viewUrl"].as_str().unwrap())
		.header("range", "bytes=0-3")
		.send()
		.await
		.unwrap();
	assert_eq!(response.status(), 206);
	assert_eq!(response.headers()["content-type"], "application/pdf");
	assert_eq!(response.bytes().await.unwrap().as_ref(), b"%PDF");
	assert_eq!(
		editor
			.post(
				&format!("/api/dateien/{file}/delete"),
				json!({"rowVersion":1})
			)
			.await
			.0,
		204
	);
	assert_eq!(
		member.get(&format!("/api/dateien/{file}/access")).await.0,
		404
	);
	assert!(storage.probe(&final_blob).await.unwrap().is_some());
	assert_eq!(
		editor
			.post(&format!("/api/dateien/{file}/restore"), json!({}))
			.await
			.0,
		204
	);
	assert_eq!(
		editor
			.post(&format!("/api/papierkorb/dateien/{file}/remove"), json!({}))
			.await
			.0,
		204
	);
	assert!(storage.probe(&final_blob).await.unwrap().is_none());
	Storage::delete(storage.as_ref(), &final_blob)
		.await
		.unwrap();
	app.close().await;
	storage
		.service_client()
		.unwrap()
		.blob_container_client(&container)
		.delete(None)
		.await
		.unwrap();
}
#[tokio::test]
#[ignore = "explicit private Azurite 3.35.0 + disposable PostgreSQL"]
async fn signed_api_read_get_head_range_overrides_cold_hot_and_invalid_signatures() {
	let (storage, container) = storage().await;
	let http = reqwest::Client::new();
	let mut app = App::new().await;
	app.with_storage(storage.clone());
	let mut member = app.member().await;
	let lied = lied(&app, "Große Aufnahme", json!({})).await;
	let original = datei(&app, lied, false, "aufnahme", "video/quicktime").await;
	let copy = datei(&app, lied, false, "aufnahme", "video/mp4").await;
	video_copy(&app, original, copy, 2).await;
	let original_blob = format!("dateien/{original}/x");
	let copy_blob = format!("dateien/{copy}/x");
	let bytes = vec![0x5au8; 20 * 1024 * 1024 + 31];
	for (blob, data) in [
		(&original_blob, bytes.clone()),
		(&copy_blob, bytes[..4096].to_vec()),
	] {
		let ticket = storage
			.upload_ticket(blob, Duration::minutes(30))
			.await
			.unwrap();
		assert_eq!(
			http.put(&ticket)
				.header("x-ms-blob-type", "BlockBlob")
				.header("content-type", "application/xml")
				.body(data)
				.send()
				.await
				.unwrap()
				.status(),
			201
		);
	}
	let (status, access) = member.get(&format!("/api/dateien/{original}/access")).await;
	assert_eq!(status, 200);
	let view = access["viewUrl"].as_str().unwrap();
	let download = access["downloadUrl"].as_str().unwrap();
	let response = http.get(view).send().await.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["content-type"], "video/mp4");
	assert_eq!(response.headers()["content-disposition"], "inline");
	assert_eq!(response.bytes().await.unwrap().len(), 4096);
	let response = http.head(download).send().await.unwrap();
	assert_eq!(response.status(), 200);
	assert_eq!(response.headers()["content-type"], "video/quicktime");
	assert_eq!(response.headers()["content-disposition"], "attachment");
	assert_eq!(
		response.headers()["content-length"],
		bytes.len().to_string()
	);
	// Full large original, consumed as network chunks rather than one buffered
	// response. The browser uses this same attachment URL, never an API proxy.
	let response = http.get(download).send().await.unwrap();
	assert_eq!(response.status(), 200);
	let mut stream = response.bytes_stream();
	let mut received = 0usize;
	while let Some(chunk) = stream.next().await {
		let chunk = chunk.unwrap();
		assert!(chunk.iter().all(|b| *b == 0x5a));
		received += chunk.len();
	}
	assert_eq!(received, 20 * 1024 * 1024 + 31);
	let mut stream = storage.open_read(&original_blob).await.unwrap().unwrap();
	let mut received = 0usize;
	while let Some(chunk) = stream.next().await {
		let chunk = chunk.unwrap();
		assert!(chunk.iter().all(|b| *b == 0x5a));
		received += chunk.len();
	}
	assert_eq!(
		received,
		20 * 1024 * 1024 + 31,
		"official SDK sequential ETag-pinned ranges span multiple partitions"
	);
	let response = http
		.get(download)
		.header("range", "bytes=11-42")
		.send()
		.await
		.unwrap();
	assert_eq!(response.status(), 206);
	assert_eq!(
		response.headers()["content-range"],
		format!("bytes 11-42/{}", bytes.len())
	);
	assert_eq!(response.bytes().await.unwrap().as_ref(), &bytes[11..43]);
	storage.set_tier(&original_blob, "cold").await.unwrap();
	let props = storage
		.service_client()
		.unwrap()
		.blob_client(&container, &original_blob)
		.get_properties(None)
		.await
		.unwrap();
	assert_eq!(props.access_tier().unwrap().as_deref(), Some("Cold"));
	assert_eq!(
		http.get(download)
			.header("range", "bytes=0-3")
			.send()
			.await
			.unwrap()
			.status(),
		206
	);
	storage.set_tier(&original_blob, "hot").await.unwrap();
	assert_eq!(
		storage
			.service_client()
			.unwrap()
			.blob_client(&container, &original_blob)
			.get_properties(None)
			.await
			.unwrap()
			.access_tier()
			.unwrap()
			.as_deref(),
		Some("Hot")
	);
	assert_eq!(
		storage
			.set_tier("dateien/missing", "cold")
			.await
			.unwrap_err(),
		StorageError::Unavailable
	);
	assert!(storage.set_tier(&original_blob, "archive").await.is_err());
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
	let mut no_sas = url::Url::parse(download).unwrap();
	no_sas.set_query(None);
	assert_eq!(http.get(no_sas).send().await.unwrap().status(), 403);
	for (name, value) in [
		("rsct", "text/html"),
		("rscd", "inline"),
		("sp", "rw"),
		("sig", "tampered"),
	] {
		let mut url = url::Url::parse(download).unwrap();
		let pairs: Vec<_> = url
			.query_pairs()
			.into_owned()
			.map(|(k, v)| if k == name { (k, value.into()) } else { (k, v) })
			.collect();
		url.query_pairs_mut().clear().extend_pairs(pairs);
		assert_eq!(
			http.get(url).send().await.unwrap().status(),
			403,
			"tamper {name}"
		);
	}
	let mut wrong_blob = url::Url::parse(download).unwrap();
	wrong_blob.set_path(&format!("{}/other", wrong_blob.path()));
	assert_eq!(http.get(wrong_blob).send().await.unwrap().status(), 403);
	let connection = std::env::var("ARCHIVE_TEST_AZURITE").unwrap();
	let expired = BlobStorage::new(
		StorageConfig::connection_string(&connection, &container).unwrap(),
		Arc::new(support::Time(Mutex::new(Utc::now() - Duration::hours(1)))),
	)
	.unwrap();
	let ticket = expired
		.read_ticket(&original_blob, Duration::minutes(15), false, None)
		.await
		.unwrap();
	assert_eq!(http.get(ticket).send().await.unwrap().status(), 403);
	let wrong = connection.replace(
		"Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
		"cHVibGljLWJ1dC13cm9uZy1rZXk=",
	);
	let wrong = BlobStorage::new(
		StorageConfig::connection_string(&wrong, &container).unwrap(),
		Arc::new(SystemClock),
	)
	.unwrap();
	assert!(matches!(
		wrong.probe(&original_blob).await,
		Err(StorageError::Unavailable)
	));
	assert!(wrong.set_tier(&original_blob, "cold").await.is_err());
	let ticket = wrong
		.read_ticket(&original_blob, Duration::minutes(15), false, None)
		.await
		.unwrap();
	assert_eq!(http.get(ticket).send().await.unwrap().status(), 403);
	app.close().await;
	storage
		.service_client()
		.unwrap()
		.blob_container_client(&container)
		.delete(None)
		.await
		.unwrap();
}

#[tokio::test]
#[ignore = "explicit private Azurite 3.35.0"]
async fn official_sdk_probe_bounded_header_forward_stream_and_browser_block_sas_contract() {
	let (storage, container) = storage().await;
	let http = reqwest::Client::new();
	let blob = "pending/über + %.pdf";
	let ticket = storage
		.upload_ticket(blob, Duration::minutes(30))
		.await
		.unwrap();
	// Exact unchanged browser transfer: fixed-width IDs, discover, XML commit;
	// rcw is not restricted to one method/comp/header value.
	use base64::{Engine, engine::general_purpose::STANDARD};
	let ids = [STANDARD.encode("000000"), STANDARD.encode("000001")];
	for (id, data) in ids.iter().zip([b"%PDF-1.7".as_slice(), b"rest".as_slice()]) {
		let mut url = url::Url::parse(&ticket).unwrap();
		url.query_pairs_mut()
			.append_pair("comp", "block")
			.append_pair("blockid", id);
		assert_eq!(
			http.put(url)
				.header("content-type", "application/pdf")
				.body(data.to_vec())
				.send()
				.await
				.unwrap()
				.status(),
			201
		);
	}
	let mut blocks = url::Url::parse(&ticket).unwrap();
	blocks
		.query_pairs_mut()
		.append_pair("comp", "blocklist")
		.append_pair("blocklisttype", "all");
	let response = http.get(blocks).send().await.unwrap();
	assert_eq!(response.status(), 200);
	let xml = response.text().await.unwrap();
	assert!(xml.contains(&ids[0]) && xml.contains(&ids[1]));
	let mut commit = url::Url::parse(&ticket).unwrap();
	commit.query_pairs_mut().append_pair("comp", "blocklist");
	let xml = format!(
		"<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList><Latest>{}</Latest><Latest>{}</Latest></BlockList>",
		ids[0], ids[1]
	);
	assert_eq!(
		http.put(commit)
			.header("content-type", "application/xml")
			.body(xml)
			.send()
			.await
			.unwrap()
			.status(),
		201
	);
	let info = storage.probe(blob).await.unwrap().unwrap();
	assert_eq!(info.size_bytes, 12);
	assert_eq!(
		storage.read_header(blob, 4).await.unwrap().unwrap(),
		b"%PDF"
	);
	assert_eq!(
		storage.read_header(blob, 64).await.unwrap().unwrap(),
		b"%PDF-1.7rest"
	);
	assert_eq!(storage.read_header(blob, 0).await.unwrap().unwrap(), b"");
	let mut stream = storage.open_read(blob).await.unwrap().unwrap();
	let mut bytes = vec![];
	while let Some(chunk) = stream.next().await {
		bytes.extend_from_slice(&chunk.unwrap());
	}
	assert_eq!(bytes, b"%PDF-1.7rest");
	assert!(storage.probe("missing").await.unwrap().is_none());
	assert!(storage.read_header("missing", 4).await.unwrap().is_none());
	assert!(storage.open_read("missing").await.unwrap().is_none());
	let empty = "pending/empty";
	let ticket = storage
		.upload_ticket(empty, Duration::minutes(30))
		.await
		.unwrap();
	assert_eq!(
		http.put(ticket)
			.header("x-ms-blob-type", "BlockBlob")
			.header("content-length", "0")
			.body("")
			.send()
			.await
			.unwrap()
			.status(),
		201
	);
	assert!(
		storage
			.read_header(empty, 4)
			.await
			.unwrap()
			.unwrap()
			.is_empty()
	);
	let ticket = storage
		.read_ticket(blob, Duration::minutes(15), false, Some("application/pdf"))
		.await
		.unwrap();
	let response = http.get(ticket).send().await.unwrap();
	assert_eq!(response.headers()["content-type"], "application/pdf");
	assert_eq!(response.headers()["content-disposition"], "inline");
	storage
		.service_client()
		.unwrap()
		.blob_container_client(&container)
		.delete(None)
		.await
		.unwrap();
}

#[tokio::test]
#[ignore = "explicit private Azurite 3.35.0"]
async fn shared_key_policy_signs_official_sdk_nonempty_xml_bodies_and_service_queries() {
	use azure_storage_blob::models::{BlobServiceProperties, CorsRule};
	let (storage, container) = storage().await;
	let service = storage.service_client().unwrap();
	let properties = BlobServiceProperties {
		cors: Some(vec![CorsRule {
			allowed_origins: Some("*".into()),
			allowed_methods: Some("GET,HEAD,PUT,OPTIONS".into()),
			allowed_headers: Some("*".into()),
			exposed_headers: Some("*".into()),
			max_age_in_seconds: Some(3600),
		}]),
		..Default::default()
	};
	service
		.set_properties(
			azure_core::http::RequestContent::try_from(properties).unwrap(),
			None,
		)
		.await
		.unwrap();
	let properties = service
		.get_properties(None)
		.await
		.unwrap()
		.into_model()
		.unwrap();
	assert_eq!(
		properties.cors.unwrap()[0].allowed_methods.as_deref(),
		Some("GET,HEAD,PUT,OPTIONS")
	);
	service
		.blob_container_client(&container)
		.delete(None)
		.await
		.unwrap();
}
