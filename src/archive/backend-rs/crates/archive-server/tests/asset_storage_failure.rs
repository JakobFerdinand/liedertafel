// Public emulator-key fixture setup, never a production path.
#![allow(clippy::unwrap_used)]
mod support;
use archive_server::storage::{BlobStorage, Storage, StorageConfig, StorageError};
use chrono::{Duration, TimeZone, Utc};
use std::sync::Arc;
use std::sync::Mutex;
use support::Time;
pub const KEY: &str =
	"Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
fn config() -> StorageConfig {
	StorageConfig::connection_string(&format!("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={KEY};BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"),"archive-assets").unwrap()
}
#[tokio::test]
async fn adapter_without_configuration_fails_with_config_error() {
	let storage = BlobStorage::new(
		StorageConfig::default(),
		Arc::new(archive_server::auth::SystemClock),
	)
	.unwrap();
	assert_eq!(
		storage
			.upload_ticket("pending/x", Duration::minutes(30))
			.await
			.unwrap_err(),
		StorageError::Configuration
	);
	assert_eq!(
		StorageError::Configuration.to_string(),
		"Kein Blob-Verbindungsstring konfiguriert."
	);
}
#[tokio::test]
async fn shared_key_ticket_is_local_blob_scoped_rcw_and_read_overrides_are_signed() {
	let now = Utc.with_ymd_and_hms(2026, 11, 9, 12, 0, 0).unwrap();
	let storage = BlobStorage::new(config(), Arc::new(Time(Mutex::new(now)))).unwrap();
	let url = storage
		.upload_ticket("pending/x", Duration::minutes(30))
		.await
		.unwrap();
	let url = url::Url::parse(&url).unwrap();
	let pairs: std::collections::BTreeMap<_, _> = url.query_pairs().into_owned().collect();
	assert_eq!(url.path(), "/devstoreaccount1/archive-assets/pending%2Fx");
	assert_eq!(pairs["sp"], "rcw");
	assert_eq!(pairs["sr"], "b");
	assert_eq!(pairs["sv"], "2025-11-05");
	assert_eq!(pairs["se"], "2026-11-09T12:30:00Z");
	assert!(!pairs.contains_key("st"));
	for (download, disposition) in [(false, "inline"), (true, "attachment")] {
		let value = storage
			.read_ticket(
				"revisions/über + %.pdf",
				Duration::minutes(15),
				download,
				Some("application/pdf"),
			)
			.await
			.unwrap();
		let url = url::Url::parse(&value).unwrap();
		let p: std::collections::BTreeMap<_, _> = url.query_pairs().into_owned().collect();
		assert_eq!(p["sp"], "r");
		assert_eq!(p["rsct"], "application/pdf");
		assert_eq!(p["rscd"], disposition);
		// Independent REST service-SAS vector using ring, not the adapter HMAC.
		let canonical = "/blob/devstoreaccount1/archive-assets/revisions/über + %.pdf";
		let sts = format!(
			"r\n\n2026-11-09T12:15:00Z\n{canonical}\n\n\n\n2025-11-05\nb\n\n\n\n{disposition}\n\n\napplication/pdf"
		);
		use base64::{Engine, engine::general_purpose::STANDARD};
		let key = ring::hmac::Key::new(ring::hmac::HMAC_SHA256, &STANDARD.decode(KEY).unwrap());
		assert_eq!(
			p["sig"],
			STANDARD.encode(ring::hmac::sign(&key, sts.as_bytes()).as_ref())
		);
	}
	let value = storage
		.read_ticket("x", Duration::minutes(15), true, None)
		.await
		.unwrap();
	assert!(!value.contains("rscd="));
	assert!(!value.contains("rsct="));
}

#[test]
fn connection_precedence_defaults_and_user_assigned_identity_are_explicit() {
	let connection = format!(
		"AccountName=devstoreaccount1;AccountKey={KEY};BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
	);
	let config =
		StorageConfig::select(Some(&connection), Some("invalid-uri"), None, false).unwrap();
	assert_eq!(config.container, "archive-assets");
	assert_eq!(config.read_lifetime, Duration::minutes(15));
	assert!(
		StorageConfig::select(
			None,
			Some("http://host.blob.core.windows.net/"),
			None,
			false
		)
		.is_err()
	);
	assert!(
		StorageConfig::select(
			None,
			Some("https://host.blob.core.windows.net/?sig=not-allowed"),
			None,
			false
		)
		.is_err()
	);
	let options = archive_server::storage::managed_identity_options(Some("explicit-client-id"));
	assert!(
		matches!(options.user_assigned_id,Some(azure_identity::UserAssignedId::ClientId(id)) if id=="explicit-client-id")
	);
	assert!(
		archive_server::storage::managed_identity_options(None)
			.user_assigned_id
			.is_none()
	);
}

#[tokio::test]
async fn connection_endpoint_trailing_slash_does_not_change_blob_scope() {
	let connection = format!(
		"AccountName=devstoreaccount1;AccountKey={KEY};BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1/;"
	);
	let storage = BlobStorage::new(
		StorageConfig::connection_string(&connection, "archive-assets").unwrap(),
		Arc::new(archive_server::auth::SystemClock),
	)
	.unwrap();
	let ticket = storage
		.upload_ticket("pending/x", Duration::minutes(30))
		.await
		.unwrap();
	assert_eq!(
		url::Url::parse(&ticket).unwrap().path(),
		"/devstoreaccount1/archive-assets/pending%2Fx"
	);
}
