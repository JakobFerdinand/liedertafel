// Official-SDK delivery/credential fixtures are assertion-only test code.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use archive_server::{
	auth::Clock,
	storage::{BlobStorage, Storage, StorageConfig, StorageError},
};
use azure_core::{
	credentials::{AccessToken, TokenCredential, TokenRequestOptions},
	http::{AsyncRawResponse, Body, HttpClient, Request, Transport, headers::Headers},
};
use chrono::{Duration, TimeZone, Utc};
use futures::future::join_all;
use std::sync::{
	Arc, Mutex,
	atomic::{AtomicU16, Ordering},
};
use support::Time;

#[derive(Debug, Default)]
struct Credential(Mutex<Vec<Vec<String>>>);

#[derive(Debug, Default)]
struct CopyHttp {
	delegation: Http,
	requests: Mutex<Vec<Request>>,
	statuses: Mutex<std::collections::VecDeque<&'static str>>,
	delete_status: AtomicU16,
}
#[async_trait::async_trait] // Official SDK's dyn HTTP transport trait requires its macro.
impl HttpClient for CopyHttp {
	async fn execute_request(&self, request: &Request) -> azure_core::Result<AsyncRawResponse> {
		if request
			.url()
			.query()
			.is_some_and(|q| q.contains("userdelegationkey"))
		{
			return self.delegation.execute_request(request).await;
		}
		self.requests.lock().unwrap().push(request.clone());
		let mut headers = Headers::new();
		if request.method() == azure_core::http::Method::Delete {
			let status = self.delete_status.load(Ordering::SeqCst);
			return Ok(AsyncRawResponse::from_bytes(
				if status == 0 { 202 } else { status }.into(),
				headers,
				"",
			));
		}
		if request.method() == azure_core::http::Method::Put {
			headers.insert("x-ms-copy-id", "synthetic-copy");
			headers.insert("x-ms-copy-status", "pending");
			return Ok(AsyncRawResponse::from_bytes(202.into(), headers, ""));
		}
		let status = self
			.statuses
			.lock()
			.unwrap()
			.pop_front()
			.unwrap_or("pending");
		if status == "404" {
			return Ok(AsyncRawResponse::from_bytes(404.into(), headers, ""));
		}
		headers.insert("x-ms-copy-status", status);
		Ok(AsyncRawResponse::from_bytes(200.into(), headers, ""))
	}
}
fn copying(http: Arc<CopyHttp>) -> BlobStorage {
	BlobStorage::with_credential(
		StorageConfig::service_uri("https://private.blob.core.windows.net/", "archive-assets")
			.unwrap(),
		Arc::new(archive_server::auth::SystemClock),
		Arc::new(Credential::default()),
		Some(Transport::new(http)),
	)
	.unwrap()
}
#[tokio::test(start_paused = true)]
async fn promotion_polls_pending_and_temporary_404_until_success_without_deleting_source() {
	let http = Arc::new(CopyHttp::default());
	http.statuses
		.lock()
		.unwrap()
		.extend(["pending", "404", "success"]);
	let storage = copying(http.clone());
	storage
		.promote("pending/source", "dateien/target")
		.await
		.unwrap();
	let requests = http.requests.lock().unwrap();
	assert_eq!(requests.len(), 4);
	assert!(
		requests
			.iter()
			.all(|r| r.method() != azure_core::http::Method::Delete)
	);
	let source = url::Url::parse(
		requests[0]
			.headers()
			.get_str(&"x-ms-copy-source".into())
			.unwrap(),
	)
	.unwrap();
	let p: std::collections::HashMap<_, _> = source.query_pairs().into_owned().collect();
	assert_eq!(p["sp"], "r");
	assert!(!p.contains_key("rscd"));
	let expires = chrono::DateTime::parse_from_rfc3339(&p["se"]).unwrap();
	assert!((expires.with_timezone(&Utc) - Utc::now()).num_seconds() <= 60);
}
#[tokio::test(start_paused = true)]
async fn failed_aborted_and_deadline_copies_fail_closed_and_cancellation_stops_polls() {
	for status in ["failed", "aborted", "pending"] {
		let http = Arc::new(CopyHttp::default());
		http.statuses.lock().unwrap().push_back(status);
		let storage = copying(http);
		assert_eq!(
			storage
				.promote("pending/source", "dateien/attempt")
				.await
				.unwrap_err(),
			StorageError::Unavailable
		);
	}
	let http = Arc::new(CopyHttp::default());
	let storage = copying(http.clone());
	assert!(
		tokio::time::timeout(
			std::time::Duration::from_millis(100),
			storage.promote("pending/source", "dateien/attempt")
		)
		.await
		.is_err()
	);
	let count = http.requests.lock().unwrap().len();
	tokio::time::advance(std::time::Duration::from_secs(60)).await;
	assert_eq!(http.requests.lock().unwrap().len(), count);
}
#[tokio::test]
async fn delete_is_idempotent_for_missing_only_not_forbidden() {
	let http = Arc::new(CopyHttp::default());
	let storage = copying(http.clone());
	http.delete_status.store(404, Ordering::SeqCst);
	storage.delete("dateien/missing").await.unwrap();
	http.delete_status.store(403, Ordering::SeqCst);
	assert_eq!(
		storage.delete("dateien/forbidden").await.unwrap_err(),
		StorageError::Unavailable
	);
}
#[async_trait::async_trait] // Official SDK's dyn credential trait requires its macro.
impl TokenCredential for Credential {
	async fn get_token(
		&self,
		scopes: &[&str],
		_: Option<TokenRequestOptions<'_>>,
	) -> azure_core::Result<AccessToken> {
		self.0
			.lock()
			.unwrap()
			.push(scopes.iter().map(|s| s.to_string()).collect());
		Ok(AccessToken::new(
			"secret-bearer-marker",
			time::OffsetDateTime::now_utc() + time::Duration::days(1),
		))
	}
}
#[derive(Debug, Default)]
struct Http {
	requests: Mutex<Vec<Request>>,
	status: AtomicU16,
}
#[async_trait::async_trait] // Official SDK's dyn HTTP transport trait requires its macro.
impl HttpClient for Http {
	async fn execute_request(&self, request: &Request) -> azure_core::Result<AsyncRawResponse> {
		self.requests.lock().unwrap().push(request.clone());
		let status = self.status.load(Ordering::SeqCst);
		if status == u16::MAX {
			return futures::future::pending().await;
		}
		if status != 0 {
			return Ok(AsyncRawResponse::from_bytes(
				status.into(),
				Headers::new(),
				"secret-provider-payload?sig=secret-signature-marker",
			));
		}
		let Body::Bytes(bytes) = request.body() else {
			panic!("control-plane request must be small XML")
		};
		let body = String::from_utf8(bytes.to_vec()).unwrap();
		fn field<'a>(xml: &'a str, name: &str) -> &'a str {
			xml.split(&format!("<{name}>"))
				.nth(1)
				.unwrap()
				.split(&format!("</{name}>"))
				.next()
				.unwrap()
		}
		let response = format!(
			"<UserDelegationKey><SignedOid>11111111-1111-1111-1111-111111111111</SignedOid><SignedTid>22222222-2222-2222-2222-222222222222</SignedTid><SignedStart>{}</SignedStart><SignedExpiry>{}</SignedExpiry><SignedService>b</SignedService><SignedVersion>2026-04-06</SignedVersion><Value>cHVibGljLXRlc3Qta2V5LW5vdC1hLXJlYWwtc2VjcmV0</Value></UserDelegationKey>",
			field(&body, "Start"),
			field(&body, "Expiry")
		);
		Ok(AsyncRawResponse::from_bytes(
			200.into(),
			Headers::new(),
			response,
		))
	}
}
#[tokio::test]
async fn official_sdk_delegation_cache_serializes_refresh_uses_scope_and_signs_overrides_rcw() {
	let clock = Arc::new(Time(Mutex::new(
		Utc.with_ymd_and_hms(2026, 11, 9, 12, 0, 0).unwrap() + Duration::milliseconds(123),
	)));
	let credential = Arc::new(Credential::default());
	let http = Arc::new(Http::default());
	let config =
		StorageConfig::service_uri("https://private.blob.core.windows.net/", "archive-assets")
			.unwrap();
	let storage = BlobStorage::with_credential(
		config,
		clock.clone(),
		credential.clone(),
		Some(Transport::new(http.clone())),
	)
	.unwrap();
	let tickets = join_all((0..20).map(|_| {
		storage.read_ticket(
			"dateien/über + %.pdf",
			Duration::minutes(15),
			false,
			Some("application/pdf"),
		)
	}))
	.await;
	assert!(tickets.iter().all(Result::is_ok));
	assert_eq!(http.requests.lock().unwrap().len(), 1);
	let url = url::Url::parse(tickets[0].as_ref().unwrap()).unwrap();
	let p: std::collections::BTreeMap<_, _> = url.query_pairs().into_owned().collect();
	assert_eq!(p["sp"], "r");
	assert_eq!(p["sr"], "b");
	assert_eq!(p["sv"], "2026-04-06");
	assert_eq!(p["rsct"], "application/pdf");
	assert_eq!(p["rscd"], "inline");
	assert_eq!(p["se"], "2026-11-09T12:15:00Z");
	assert!(!p.contains_key("st"));
	// Independent Microsoft REST 2025-07-05+ delegation vector, signed with
	// ring rather than calling the SDK builder again as its own oracle.
	let sts = [
		"r",
		"",
		"2026-11-09T12:15:00Z",
		"/blob/private/archive-assets/dateien/über + %.pdf",
		"11111111-1111-1111-1111-111111111111",
		"22222222-2222-2222-2222-222222222222",
		"2026-11-09T11:55:00Z",
		"2026-11-09T12:15:00Z",
		"b",
		"2026-04-06",
		"",
		"",
		"",
		"",
		"",
		"",
		"",
		"2026-04-06",
		"b",
		"",
		"",
		"",
		"",
		"",
		"inline",
		"",
		"",
		"application/pdf",
	]
	.join("\n");
	use base64::{Engine, engine::general_purpose::STANDARD};
	let key = ring::hmac::Key::new(
		ring::hmac::HMAC_SHA256,
		b"public-test-key-not-a-real-secret",
	);
	assert_eq!(
		p["sig"],
		STANDARD.encode(ring::hmac::sign(&key, sts.as_bytes()).as_ref())
	);
	assert_eq!(
		credential.0.lock().unwrap()[0],
		["https://storage.azure.com/.default"]
	);
	{
		let requests = http.requests.lock().unwrap();
		let request = &requests[0];
		assert_eq!(request.method().to_string(), "POST");
		assert_eq!(
			request.headers().get_str(&"authorization".into()).unwrap(),
			"Bearer secret-bearer-marker"
		);
		assert!(
			request
				.url()
				.query()
				.unwrap()
				.contains("comp=userdelegationkey")
		);
		let Body::Bytes(body) = request.body() else {
			panic!()
		};
		let body = String::from_utf8(body.to_vec()).unwrap();
		assert!(body.contains("2026-11-09T11:55:00Z"));
		assert!(body.contains("2026-11-09T12:15:00Z"));
	}
	let upload = storage
		.upload_ticket("pending/x", Duration::minutes(30))
		.await
		.unwrap();
	let p: std::collections::BTreeMap<_, _> = url::Url::parse(&upload)
		.unwrap()
		.query_pairs()
		.into_owned()
		.collect();
	assert_eq!(p["sp"], "rcw");
	assert_eq!(http.requests.lock().unwrap().len(), 2);
	let download = storage
		.read_ticket("x", Duration::minutes(15), true, Some("video/mp4"))
		.await
		.unwrap();
	assert!(download.contains("rscd=attachment"));
	assert_eq!(http.requests.lock().unwrap().len(), 2);
	*clock.0.lock().unwrap() = clock.now() + Duration::minutes(31);
	storage
		.read_ticket("x", Duration::minutes(15), false, None)
		.await
		.unwrap();
	assert_eq!(http.requests.lock().unwrap().len(), 3);
}
#[tokio::test]
async fn delegation_failure_retries_only_twice_redacts_and_does_not_poison_cache() {
	let clock = Arc::new(Time(Mutex::new(
		Utc.with_ymd_and_hms(2026, 11, 9, 12, 0, 0).unwrap(),
	)));
	let http = Arc::new(Http::default());
	http.status.store(500, Ordering::SeqCst);
	let storage = BlobStorage::with_credential(
		StorageConfig::service_uri("https://private.blob.core.windows.net/", "archive-assets")
			.unwrap(),
		clock,
		Arc::new(Credential::default()),
		Some(Transport::new(http.clone())),
	)
	.unwrap();
	let error = storage
		.upload_ticket("secret-blob-marker", Duration::minutes(30))
		.await
		.unwrap_err();
	assert_eq!(error, StorageError::Unavailable);
	assert_eq!(
		format!("{error:?}: {error}"),
		"Unavailable: Speicherdienst nicht erreichbar."
	);
	assert_eq!(http.requests.lock().unwrap().len(), 3);
	http.status.store(0, Ordering::SeqCst);
	storage
		.upload_ticket("pending/x", Duration::minutes(30))
		.await
		.unwrap();
	assert_eq!(http.requests.lock().unwrap().len(), 4);
}

#[tokio::test(start_paused = true)]
async fn network_deadline_is_five_seconds_per_try_and_cancelled_refresh_releases_cache_lock() {
	let http = Arc::new(Http::default());
	http.status.store(u16::MAX, Ordering::SeqCst);
	let storage = Arc::new(
		BlobStorage::with_credential(
			StorageConfig::service_uri("https://private.blob.core.windows.net/", "archive-assets")
				.unwrap(),
			Arc::new(archive_server::auth::SystemClock),
			Arc::new(Credential::default()),
			Some(Transport::new(http.clone())),
		)
		.unwrap(),
	);
	let start = tokio::time::Instant::now();
	let error = tokio::time::timeout(
		std::time::Duration::from_secs(20),
		storage.upload_ticket("pending/x", Duration::minutes(30)),
	)
	.await
	.expect("three five-second network attempts must finish")
	.unwrap_err();
	assert_eq!(error, StorageError::Unavailable);
	assert_eq!(http.requests.lock().unwrap().len(), 3);
	assert!(start.elapsed() >= std::time::Duration::from_secs(15));
	assert!(start.elapsed() < std::time::Duration::from_secs(20));
	let task = {
		let storage = storage.clone();
		tokio::spawn(async move {
			storage
				.upload_ticket("pending/cancel", Duration::minutes(30))
				.await
		})
	};
	tokio::task::yield_now().await;
	task.abort();
	assert!(task.await.unwrap_err().is_cancelled());
	http.status.store(0, Ordering::SeqCst);
	storage
		.upload_ticket("pending/retry", Duration::minutes(30))
		.await
		.unwrap();
}

#[derive(Debug, Default)]
struct StreamingHttp {
	consumed: Arc<std::sync::atomic::AtomicUsize>,
	requests: Mutex<Vec<Request>>,
}
#[async_trait::async_trait] // Official SDK's dyn transport trait.
impl HttpClient for StreamingHttp {
	async fn execute_request(&self, request: &Request) -> azure_core::Result<AsyncRawResponse> {
		self.requests.lock().unwrap().push(request.clone());
		let total = 1024 * 1024 * 1024u64;
		let mut headers = Headers::new();
		headers.insert("etag", "\"v1\"");
		headers.insert("content-type", "application/octet-stream");
		if request.method().to_string() == "HEAD" {
			headers.insert("content-length", total.to_string());
			return Ok(AsyncRawResponse::from_bytes(200.into(), headers, ""));
		}
		let range = request.headers().get_str(&"range".into()).unwrap();
		let (start, end) = range
			.strip_prefix("bytes=")
			.unwrap()
			.split_once('-')
			.unwrap();
		let start = start.parse::<u64>().unwrap();
		let end = end.parse::<u64>().unwrap().min(total - 1);
		let len = end - start + 1;
		headers.insert("content-length", len.to_string());
		headers.insert("content-range", format!("bytes {start}-{end}/{total}"));
		let consumed = self.consumed.clone();
		let stream = futures::stream::unfold(len, move |left| {
			let consumed = consumed.clone();
			async move {
				if left == 0 {
					None
				} else {
					let n = left.min(65536) as usize;
					consumed.fetch_add(n, Ordering::SeqCst);
					Some((Ok(azure_core::Bytes::from(vec![42; n])), left - n as u64))
				}
			}
		});
		Ok(AsyncRawResponse::new(206.into(), headers, Box::pin(stream)))
	}
}
#[tokio::test]
async fn gigabyte_read_is_bounded_lazy_etag_pinned_and_drop_stops_network_work() {
	let http = Arc::new(StreamingHttp::default());
	let storage = BlobStorage::with_credential(
		StorageConfig::service_uri("https://private.blob.core.windows.net/", "archive-assets")
			.unwrap(),
		Arc::new(archive_server::auth::SystemClock),
		Arc::new(Credential::default()),
		Some(Transport::new(http.clone())),
	)
	.unwrap();
	let stream = storage.open_read("large").await.unwrap().unwrap();
	drop(stream);
	tokio::task::yield_now().await;
	assert_eq!(
		http.consumed.load(Ordering::SeqCst),
		0,
		"dropping an unopened forward stream must not leave a detached partition download"
	);
	use futures::StreamExt;
	let mut stream = storage.open_read("large").await.unwrap().unwrap();
	let chunk = stream.next().await.unwrap().unwrap();
	assert!(!chunk.is_empty());
	assert!(http.consumed.load(Ordering::SeqCst) <= 8 * 1024 * 1024);
	drop(stream);
	let count = http.consumed.load(Ordering::SeqCst);
	tokio::task::yield_now().await;
	assert_eq!(http.consumed.load(Ordering::SeqCst), count);
	let requests = http.requests.lock().unwrap();
	let get = requests
		.iter()
		.find(|r| r.method().to_string() == "GET")
		.unwrap();
	assert_eq!(get.headers().get_str(&"if-match".into()).unwrap(), "\"v1\"");
	assert_eq!(
		get.headers().get_str(&"range".into()).unwrap(),
		"bytes=0-8388607"
	);
}
