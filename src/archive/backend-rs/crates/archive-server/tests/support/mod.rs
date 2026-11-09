// Shared harness is independently compiled by multiple suites using subsets.
#![allow(dead_code)]
// Integration-only assertion/fixture helpers, matching the existing HTTP suites.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
use archive_server::{
	Origin, Settings,
	auth::{Auth, AuthConfig, Clock, Mail, MailError, MailMessage},
	build_router,
};
use axum::{
	Router,
	body::Body,
	http::{Request, StatusCode},
};
use chrono::{DateTime, Utc};
use http_body_util::BodyExt;
use serde_json::Value;
use sqlx::{Connection, PgConnection, PgPool};
use std::{
	collections::BTreeMap,
	future::Future,
	path::PathBuf,
	pin::Pin,
	sync::{Arc, Mutex},
};
use tower::ServiceExt;
use uuid::Uuid;

#[derive(Default)]
pub struct CaptureMail(
	pub Mutex<Vec<MailMessage>>,
	pub std::sync::atomic::AtomicBool,
);
impl CaptureMail {
	pub fn code(&self) -> String {
		let messages = self.0.lock().unwrap();
		messages
			.last()
			.unwrap()
			.text
			.split("Ihr Anmeldecode lautet: ")
			.nth(1)
			.unwrap()[..6]
			.to_owned()
	}
}
impl Mail for CaptureMail {
	fn send<'a>(
		&'a self,
		message: MailMessage,
	) -> Pin<Box<dyn Future<Output = Result<(), MailError>> + Send + 'a>> {
		Box::pin(async move {
			if self.1.load(std::sync::atomic::Ordering::Relaxed) {
				return Err(MailError::Delivery);
			}
			self.0.lock().unwrap().push(message);
			Ok(())
		})
	}
}
pub struct Time(pub Mutex<DateTime<Utc>>);
impl Clock for Time {
	fn now(&self) -> DateTime<Utc> {
		*self.0.lock().unwrap()
	}
}

pub struct App {
	pub router: Router,
	pub pool: PgPool,
	pub mail: Arc<CaptureMail>,
	pub time: Arc<Time>,
	url: String,
	database: String,
}
impl App {
	pub async fn new() -> Self {
		let url = std::env::var("ARCHIVE_TEST_POSTGRES").expect("explicit throwaway PG gate");
		let mut db_url = url::Url::parse(&url).unwrap();
		assert!(
			matches!(
				db_url.host_str(),
				Some("localhost" | "127.0.0.1" | "[::1]" | "::1")
			),
			"auth fixtures refuse nonlocal servers"
		);
		let database = format!("auth_{}", Uuid::new_v4().simple());
		let mut admin = PgConnection::connect(&url).await.unwrap();
		sqlx::raw_sql(sqlx::AssertSqlSafe(format!("CREATE DATABASE {database}")))
			.execute(&mut admin)
			.await
			.unwrap();
		db_url.set_path(&database);
		let output = tokio::time::timeout(
			std::time::Duration::from_secs(30),
			tokio::process::Command::new(env!("CARGO_BIN_EXE_archive-server"))
				.arg("--migrate")
				.env("ConnectionStrings__archive-migrations", db_url.as_str())
				.env("ARCHIVE_ENVIRONMENT", "Production")
				.env_remove("OTEL_EXPORTER_OTLP_ENDPOINT")
				.kill_on_drop(true)
				.output(),
		)
		.await
		.unwrap()
		.unwrap();
		assert!(
			output.status.success(),
			"{}",
			String::from_utf8_lossy(&output.stderr)
		);
		let pool = PgPool::connect(db_url.as_str()).await.unwrap();
		let mail = Arc::new(CaptureMail::default());
		let time = Arc::new(Time(Mutex::new(Utc::now())));
		let config = AuthConfig::development();
		let router = Self::router(pool.clone(), mail.clone(), time.clone(), config.clone());
		Self {
			router,
			pool,
			mail,
			time,
			url,
			database,
		}
	}
	pub fn router(
		pool: PgPool,
		mail: Arc<CaptureMail>,
		time: Arc<Time>,
		config: AuthConfig,
	) -> Router {
		let auth = Auth::new(pool, config, mail, time).unwrap();
		build_router(Arc::new(
			Origin::new(Settings::new(true, PathBuf::new(), 0)).with_auth(auth),
		))
	}
	pub fn client(&self) -> Client {
		// Each virtual browser models a separate peer; a single Client keeps its
		// address for abuse-limit/race tests. Signature-vector suites create many
		// browsers and must reach the verifier, not an unrelated shared "unknown"
		// address quota. Anonymous allocation tests supply their own exact peers.
		static NEXT_PEER: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);
		let peer = NEXT_PEER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
		Client {
			router: self.router.clone(),
			cookies: BTreeMap::new(),
			token: String::new(),
			peer: (std::net::Ipv4Addr::from(0xc6120000 + peer % 131072), 54321).into(),
		}
	}
	pub async fn member(&self) -> Client {
		let mut client = self.client();
		client.csrf().await;
		assert_eq!(
			client
				.post("/api/dev/auth/seed", serde_json::json!({}))
				.await
				.0,
			200
		);
		assert_eq!(
			client
				.post(
					"/api/auth/code/request",
					serde_json::json!({"email":"mitglied@liedertafel.test"})
				)
				.await
				.0,
			202
		);
		assert_eq!(
			client
				.post(
					"/api/auth/code/verify",
					serde_json::json!({"email":"mitglied@liedertafel.test","code":self.mail.code()})
				)
				.await
				.0,
			200
		);
		client.csrf().await;
		client
	}
	pub async fn close(self) {
		self.pool.close().await;
		let mut admin = PgConnection::connect(&self.url).await.unwrap();
		sqlx::raw_sql(sqlx::AssertSqlSafe(format!(
			"DROP DATABASE {} WITH (FORCE)",
			self.database
		)))
		.execute(&mut admin)
		.await
		.unwrap();
	}
}

pub struct Client {
	pub router: Router,
	pub cookies: BTreeMap<String, String>,
	pub token: String,
	peer: std::net::SocketAddr,
}
impl Client {
	pub async fn csrf(&mut self) {
		let (status, body) = self.get("/api/antiforgery").await;
		assert_eq!(status, 200, "{body}");
		self.token = body["token"].as_str().unwrap().to_owned();
	}
	pub async fn get(&mut self, path: &str) -> (StatusCode, Value) {
		self.request("GET", path, Value::Null).await
	}
	pub async fn post(&mut self, path: &str, body: Value) -> (StatusCode, Value) {
		self.request("POST", path, body).await
	}
	pub async fn request(&mut self, method: &str, path: &str, body: Value) -> (StatusCode, Value) {
		let mut request = Request::builder()
			.method(method)
			.uri(path)
			.header(
				"cookie",
				self.cookies
					.iter()
					.map(|(k, v)| format!("{k}={v}"))
					.collect::<Vec<_>>()
					.join("; "),
			)
			.header("X-CSRF-TOKEN", &self.token)
			.header("content-type", "application/json")
			.body(Body::from(body.to_string()))
			.unwrap();
		request
			.extensions_mut()
			.insert(axum::extract::ConnectInfo(self.peer));
		let response = self.router.clone().oneshot(request).await.unwrap();
		let status = response.status();
		for header in response.headers().get_all("set-cookie") {
			let pair = header.to_str().unwrap().split(';').next().unwrap();
			let (key, value) = pair.split_once('=').unwrap();
			self.cookies.insert(key.into(), value.into());
		}
		let bytes = response.into_body().collect().await.unwrap().to_bytes();
		(status, serde_json::from_slice(&bytes).unwrap())
	}
}
