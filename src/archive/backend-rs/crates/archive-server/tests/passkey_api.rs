//! Real ES256 signatures over fresh HTTP challenges, using a synthetic credential
//! actually enrolled by the frozen .NET host (not an invented Rust JSON row).
// Synthetic authenticator/fixture helpers assert loudly at the integration seam.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use base64::{
	Engine,
	engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD},
};
use ring::{
	rand::SystemRandom,
	signature::{ECDSA_P256_SHA256_ASN1_SIGNING, EcdsaKeyPair},
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use uuid::Uuid;

fn fixture() -> Value {
	serde_json::from_str(include_str!("../../../tests/fixtures/dotnet-es256.json")).unwrap()
}
async fn import(app: &support::App) -> Value {
	let fixture = fixture();
	let user = &fixture["user"];
	let id: Uuid = user["Id"].as_str().unwrap().parse().unwrap();
	assert_eq!(fixture["syntheticOnly"], true);
	sqlx::query("INSERT INTO \"AspNetUsers\"(\"Id\",\"Email\",\"NormalizedEmail\",\"DisplayName\",\"EmailConfirmed\",\"PhoneNumberConfirmed\",\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\",\"SecurityStamp\") VALUES($1,$2,$3,$4,true,false,false,true,0,'synthetic')").bind(id).bind(user["Email"].as_str().unwrap()).bind(user["NormalizedEmail"].as_str().unwrap()).bind(user["DisplayName"].as_str().unwrap()).execute(&app.pool).await.unwrap();
	let role = Uuid::new_v4();
	sqlx::query(
		"INSERT INTO \"AspNetRoles\"(\"Id\",\"Name\",\"NormalizedName\") VALUES($1,'Member','MEMBER')",
	)
	.bind(role)
	.execute(&app.pool)
	.await
	.unwrap();
	sqlx::query("INSERT INTO \"AspNetUserRoles\" VALUES($1,$2)")
		.bind(id)
		.bind(role)
		.execute(&app.pool)
		.await
		.unwrap();
	sqlx::query(
		"INSERT INTO \"AspNetUserPasskeys\"(\"CredentialId\",\"UserId\",\"Data\") VALUES($1,$2,$3)",
	)
	.bind(
		STANDARD
			.decode(fixture["credential"]["credentialId"].as_str().unwrap())
			.unwrap(),
	)
	.bind(id)
	.bind(&fixture["data"])
	.execute(&app.pool)
	.await
	.unwrap();
	fixture
}
fn assertion(
	fixture: &Value,
	challenge: &str,
	origin: &str,
	rp: &str,
	flags: u8,
	counter: u32,
	handle: Option<&[u8]>,
) -> Value {
	// Test authenticator only. The server must verify this via mature WebAuthn core.
	let random = SystemRandom::new();
	let der = STANDARD
		.decode(fixture["credential"]["privateKey"].as_str().unwrap())
		.unwrap();
	let key = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &der, &random).unwrap();
	let client = serde_json::to_vec(
		&json!({"type":"webauthn.get","challenge":challenge,"origin":origin,"crossOrigin":false}),
	)
	.unwrap();
	let mut auth = Sha256::digest(rp.as_bytes()).to_vec();
	auth.push(flags);
	auth.extend(counter.to_be_bytes());
	let mut signed = auth.clone();
	signed.extend(Sha256::digest(&client));
	let signature = key.sign(&random, &signed).unwrap();
	let id = URL_SAFE_NO_PAD.encode(
		STANDARD
			.decode(fixture["credential"]["credentialId"].as_str().unwrap())
			.unwrap(),
	);
	json!({"id":id,"rawId":id,"type":"public-key","response":{"authenticatorData":URL_SAFE_NO_PAD.encode(auth),"clientDataJSON":URL_SAFE_NO_PAD.encode(client),"signature":URL_SAFE_NO_PAD.encode(signature.as_ref()),"userHandle":handle.map(|h|URL_SAFE_NO_PAD.encode(h))},"clientExtensionResults":{}})
}
async fn obtain_challenge(client: &mut support::Client) -> String {
	let response = client
		.post("/api/auth/passkeys/login/options", json!({}))
		.await;
	assert_eq!(response.0, 200);
	let options: Value =
		serde_json::from_str(response.1["requestOptions"].as_str().unwrap()).unwrap();
	options["challenge"].as_str().unwrap().into()
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn dotnet_old_row_new_rust_challenge_real_signature_replay_and_counter_persistence() {
	let app = support::App::new().await;
	let fixture = import(&app).await;
	let mut client = app.client();
	client.csrf().await;
	let handle = fixture["user"]["Id"].as_str().unwrap();
	assert_eq!(handle.len(), 36);
	let challenge = obtain_challenge(&mut client).await;
	let credential = assertion(
		&fixture,
		&challenge,
		"http://localhost:58121",
		"localhost",
		5,
		2,
		Some(handle.as_bytes()),
	);
	let body = json!({"credential":credential.to_string()});
	let response = client
		.post("/api/auth/passkeys/login/verify", body.clone())
		.await;
	assert_eq!(response.0, 200, "{}", response.1);
	let me = client.get("/api/auth/me").await.1;
	assert_eq!(me["authMethod"], "passkey");
	assert_eq!(me["accountId"], handle);
	client.csrf().await;
	assert_eq!(
		client.post("/api/auth/passkeys/login/verify", body).await.0,
		400
	);
	let saved: Value = sqlx::query_scalar("SELECT \"Data\" FROM \"AspNetUserPasskeys\"")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(saved["SignCount"], 2);
	for field in [
		"PublicKey",
		"Name",
		"CreatedAt",
		"AttestationObject",
		"ClientDataJson",
	] {
		assert_eq!(saved[field], fixture["data"][field]);
	}
	// Independent router/service simulates replica/restart: no local ceremony/session state.
	client.router = support::App::router(
		app.pool.clone(),
		app.mail.clone(),
		app.time.clone(),
		archive_server::auth::AuthConfig::development(),
	);
	assert_eq!(
		client.get("/api/auth/me").await.1["verifiedAt"],
		me["verifiedAt"]
	);
	let challenge = obtain_challenge(&mut client).await;
	let credential = assertion(
		&fixture,
		&challenge,
		"http://localhost",
		"localhost",
		5,
		3,
		Some(handle.as_bytes()),
	);
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":credential.to_string()})
			)
			.await
			.0,
		200
	);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn signed_negative_ceremonies_reject_rp_origin_uv_handle_counter_and_backup_changes() {
	let app = support::App::new().await;
	let fixture = import(&app).await;
	let handle = fixture["user"]["Id"].as_str().unwrap();
	// Independently valid signatures. Each failed condition must be enforced by
	// the real ceremony/ownership verifier, not merely a malformed-payload parser.
	for (origin, rp, flags, handle) in [
		(
			"http://evil.example",
			"localhost",
			5,
			Some(handle.as_bytes()),
		),
		(
			"http://localhost",
			"evil.example",
			5,
			Some(handle.as_bytes()),
		),
		("http://localhost", "localhost", 1, Some(handle.as_bytes())),
		("http://localhost", "localhost", 4, Some(handle.as_bytes())),
		("http://localhost", "localhost", 13, Some(handle.as_bytes())),
		("http://localhost", "localhost", 5, None),
		(
			"http://localhost",
			"localhost",
			5,
			Some(b"00000000-0000-0000-0000-000000000999".as_slice()),
		),
		(
			"http://localhost",
			"localhost",
			5,
			Some(b"sixteen-byte-uid".as_slice()),
		),
	] {
		let mut client = app.client();
		client.csrf().await;
		let challenge = obtain_challenge(&mut client).await;
		let credential = assertion(&fixture, &challenge, origin, rp, flags, 2, handle);
		let result = client
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":credential.to_string()}),
			)
			.await;
		assert_eq!(result.0, 400, "{origin} {rp} {flags}: {}", result.1);
		assert_eq!(result.1["title"], "Die Passkey-Anmeldung ist ungültig.");
		assert_eq!(client.get("/api/auth/me").await.1["authenticated"], false);
	}
	// Counter 0/0 is allowed, but once either is nonzero, <= is rejected.
	let mut client = app.client();
	client.csrf().await;
	let challenge = obtain_challenge(&mut client).await;
	let mut forged = assertion(
		&fixture,
		&challenge,
		"http://localhost",
		"localhost",
		5,
		2,
		Some(handle.as_bytes()),
	);
	let mut signature = URL_SAFE_NO_PAD
		.decode(forged["response"]["signature"].as_str().unwrap())
		.unwrap();
	let last = signature.len() - 1;
	signature[last] ^= 1;
	forged["response"]["signature"] = json!(URL_SAFE_NO_PAD.encode(signature));
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":forged.to_string()})
			)
			.await
			.0,
		400,
		"valid policy/challenge with an invalid signature must fail cryptographic verification"
	);
	sqlx::query("UPDATE \"AspNetUserPasskeys\" SET \"Data\"=jsonb_set(\"Data\",'{SignCount}','0')")
		.execute(&app.pool)
		.await
		.unwrap();
	for counter in [0u32, 0, 4, 4, 3, 0, 5] {
		let mut client = app.client();
		client.csrf().await;
		let challenge = obtain_challenge(&mut client).await;
		let credential = assertion(
			&fixture,
			&challenge,
			"http://localhost",
			"localhost",
			5,
			counter,
			Some(handle.as_bytes()),
		);
		let expected = if matches!(counter, 0 | 4 | 5) {
			200
		} else {
			400
		};
		let prior: u64 =
			sqlx::query_scalar::<_, Value>("SELECT \"Data\" FROM \"AspNetUserPasskeys\"")
				.fetch_one(&app.pool)
				.await
				.unwrap()["SignCount"]
				.as_u64()
				.unwrap();
		let expected = if (counter == 0 && prior == 0) || u64::from(counter) > prior {
			expected
		} else {
			400
		};
		assert_eq!(
			client
				.post(
					"/api/auth/passkeys/login/verify",
					json!({"credential":credential.to_string()})
				)
				.await
				.0,
			expected
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn valid_crypto_never_confirms_unconfirmed_locked_or_roleless_account() {
	let app = support::App::new().await;
	let fixture = import(&app).await;
	let handle = fixture["user"]["Id"].as_str().unwrap();
	for update in [
		"UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=false",
		"UPDATE \"AspNetUsers\" SET \"EmailConfirmed\"=true, \"LockoutEnd\"='2099-01-01'",
		"UPDATE \"AspNetUsers\" SET \"LockoutEnd\"=NULL; DELETE FROM \"AspNetUserRoles\"",
	] {
		sqlx::raw_sql(update).execute(&app.pool).await.unwrap();
		let mut client = app.client();
		client.csrf().await;
		let challenge = obtain_challenge(&mut client).await;
		let credential = assertion(
			&fixture,
			&challenge,
			"http://localhost",
			"localhost",
			5,
			2,
			Some(handle.as_bytes()),
		);
		assert_eq!(
			client
				.post(
					"/api/auth/passkeys/login/verify",
					json!({"credential":credential.to_string()})
				)
				.await
				.0,
			400
		);
		assert_eq!(client.get("/api/auth/me").await.1["authenticated"], false);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn passkey_management_uses_standard_base64_owner_binding_names_cap_and_audits() {
	let app = support::App::new().await;
	let fixture = import(&app).await;
	let handle = fixture["user"]["Id"].as_str().unwrap();
	let id = fixture["credential"]["credentialId"].as_str().unwrap();
	let mut client = app.client();
	client.csrf().await;
	let challenge = obtain_challenge(&mut client).await;
	let credential = assertion(
		&fixture,
		&challenge,
		"http://localhost",
		"localhost",
		5,
		2,
		Some(handle.as_bytes()),
	);
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":credential.to_string()})
			)
			.await
			.0,
		200
	);
	client.csrf().await;
	let foreign = Uuid::new_v4();
	sqlx::query("INSERT INTO \"AspNetUsers\"(\"Id\",\"EmailConfirmed\",\"PhoneNumberConfirmed\",\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\") VALUES($1,true,false,false,true,0)").bind(foreign).execute(&app.pool).await.unwrap();
	sqlx::query(
		"INSERT INTO \"AspNetUserPasskeys\"(\"CredentialId\",\"UserId\",\"Data\") VALUES($1,$2,$3)",
	)
	.bind(b"foreign-key".as_slice())
	.bind(foreign)
	.bind(&fixture["data"])
	.execute(&app.pool)
	.await
	.unwrap();
	for route in ["rename", "remove"] {
		assert_eq!(
			client
				.post(
					&format!("/api/auth/passkeys/{route}"),
					json!({"credentialId":STANDARD.encode(b"foreign-key"),"name":"Laptop"})
				)
				.await
				.0,
			404
		);
	}
	assert_eq!(
		client.get("/api/auth/passkeys").await.1["passkeys"][0]["credentialId"],
		id
	);
	for (bad, status) in [("not%base64", 400), ("AQIDBA==", 404)] {
		assert_eq!(
			client
				.post(
					"/api/auth/passkeys/rename",
					json!({"credentialId":bad,"name":"Laptop"})
				)
				.await
				.0,
			status
		);
		assert_eq!(
			client
				.post("/api/auth/passkeys/remove", json!({"credentialId":bad}))
				.await
				.0,
			status
		);
	}
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/rename",
				json!({"credentialId":id,"name":"  "})
			)
			.await
			.0,
		400
	);
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/rename",
				json!({"credentialId":id,"name":"Laptop"})
			)
			.await
			.0,
		200
	);
	assert_eq!(
		client.get("/api/auth/passkeys").await.1["passkeys"][0]["name"],
		"Laptop"
	);
	let mut config = archive_server::auth::AuthConfig::development();
	config.max_passkeys = 1;
	client.router =
		support::App::router(app.pool.clone(), app.mail.clone(), app.time.clone(), config);
	assert_eq!(
		client
			.post("/api/auth/passkeys/register/options", json!({}))
			.await
			.0,
		409
	);
	assert_eq!(
		client
			.post(
				"/api/auth/passkeys/register/verify",
				json!({"credential":"{}"})
			)
			.await
			.0,
		409
	);
	assert_eq!(
		client
			.post("/api/auth/passkeys/remove", json!({"credentialId":id}))
			.await
			.0,
		200
	);
	assert_eq!(
		client.get("/api/auth/passkeys").await.1["passkeys"],
		json!([])
	);
	let actions: Vec<i32> =
		sqlx::query_scalar("SELECT \"Action\" FROM member_admin_actions ORDER BY \"Action\"")
			.fetch_all(&app.pool)
			.await
			.unwrap();
	assert_eq!(actions, vec![6, 7]);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn challenges_are_expiring_browser_operation_bound_and_counter_updates_are_atomic() {
	let app = support::App::new().await;
	let fixture = import(&app).await;
	let handle = fixture["user"]["Id"].as_str().unwrap();
	let mut original = app.client();
	original.csrf().await;
	let challenge = obtain_challenge(&mut original).await;
	let credential = assertion(
		&fixture,
		&challenge,
		"http://localhost",
		"localhost",
		5,
		2,
		Some(handle.as_bytes()),
	);
	let mut other = app.client();
	other.csrf().await;
	obtain_challenge(&mut other).await;
	assert_eq!(
		other
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":credential.to_string()})
			)
			.await
			.0,
		400
	);
	*app.time.0.lock().unwrap() += chrono::Duration::minutes(6);
	assert_eq!(
		original
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":credential.to_string()})
			)
			.await
			.0,
		400
	);
	let mut tasks = tokio::task::JoinSet::new();
	for _ in 0..5 {
		let mut client = app.client();
		client.csrf().await;
		let challenge = obtain_challenge(&mut client).await;
		let credential = assertion(
			&fixture,
			&challenge,
			"http://localhost",
			"localhost",
			5,
			2,
			Some(handle.as_bytes()),
		);
		tasks.spawn(async move {
			client
				.post(
					"/api/auth/passkeys/login/verify",
					json!({"credential":credential.to_string()}),
				)
				.await
				.0
		});
	}
	let mut successes = 0;
	while let Some(result) = tasks.join_next().await {
		let status = result.unwrap();
		assert!(status == 200 || status == 400);
		successes += usize::from(status == 200);
	}
	assert_eq!(
		successes, 1,
		"distinct valid challenges cannot race the stored nonzero counter"
	);
	app.close().await;
}

fn registration(challenge: &str, id: &[u8], origin: &str, rp: &str, flags: u8) -> Value {
	// Synthetic authenticator wire format, not a server verifier. "none" is the
	// actual RP attestation policy; upstream still verifies origin/challenge/UV.
	use serde_cbor_2::Value as C;
	let fixture = fixture();
	let mut auth = Sha256::digest(rp.as_bytes()).to_vec();
	auth.push(flags);
	auth.extend(0u32.to_be_bytes());
	auth.extend([0; 16]);
	auth.extend((id.len() as u16).to_be_bytes());
	auth.extend(id);
	auth.extend(
		STANDARD
			.decode(fixture["data"]["PublicKey"].as_str().unwrap())
			.unwrap(),
	);
	let attestation = C::Map(
		[
			(C::Text("fmt".into()), C::Text("none".into())),
			(C::Text("attStmt".into()), C::Map(Default::default())),
			(C::Text("authData".into()), C::Bytes(auth)),
		]
		.into_iter()
		.collect(),
	);
	let client = serde_json::to_vec(
		&json!({"type":"webauthn.create","challenge":challenge,"origin":origin,"crossOrigin":false}),
	)
	.unwrap();
	let id = URL_SAFE_NO_PAD.encode(id);
	json!({"id":id,"rawId":id,"type":"public-key","response":{"attestationObject":URL_SAFE_NO_PAD.encode(serde_cbor_2::to_vec(&attestation).unwrap()),"clientDataJSON":URL_SAFE_NO_PAD.encode(client),"transports":["internal"]},"clientExtensionResults":{}})
}
async fn creation(client: &mut support::Client) -> String {
	let response = client
		.post("/api/auth/passkeys/register/options", json!({}))
		.await;
	assert_eq!(response.0, 200, "{}", response.1);
	let options: Value =
		serde_json::from_str(response.1["creationOptions"].as_str().unwrap()).unwrap();
	assert_eq!(
		URL_SAFE_NO_PAD
			.decode(options["user"]["id"].as_str().unwrap())
			.unwrap()
			.len(),
		36
	);
	options["challenge"].as_str().unwrap().into()
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn enrollment_exact_policy_session_binding_atomic_cap_and_no_cross_operation_state() {
	let app = support::App::new().await;
	let mut member = app.member().await;
	let fixture = fixture();
	let mut config = archive_server::auth::AuthConfig::development();
	config.max_passkeys = 1;
	member.router =
		support::App::router(app.pool.clone(), app.mail.clone(), app.time.clone(), config);
	for (origin, rp, flags) in [
		("http://evil.example", "localhost", 0x45),
		("http://localhost", "evil.example", 0x45),
		("http://localhost", "localhost", 0x41),
		("http://localhost", "localhost", 0x44),
		("http://localhost", "localhost", 0x55),
	] {
		let challenge = creation(&mut member).await;
		let credential = registration(&challenge, b"synthetic-enrollment", origin, rp, flags);
		let rejected = member
			.post(
				"/api/auth/passkeys/register/verify",
				json!({"credential":credential.to_string()}),
			)
			.await;
		assert_eq!(rejected.0, 400);
		assert_eq!(
			rejected.1["title"],
			"Die Passkey-Registrierung ist ungültig."
		);
	}
	// A valid assertion for a *registration* challenge has no login state.
	let challenge = creation(&mut member).await;
	let signed = assertion(
		&fixture,
		&challenge,
		"http://localhost",
		"localhost",
		5,
		2,
		Some(fixture["user"]["Id"].as_str().unwrap().as_bytes()),
	);
	assert_eq!(
		member
			.post(
				"/api/auth/passkeys/login/verify",
				json!({"credential":signed.to_string()})
			)
			.await
			.0,
		400
	);
	// Same account but a different authenticated session cannot claim enrollment
	// state copied from the first browser/session, even with its own valid CSRF.
	let mut other = app.client();
	other.router = member.router.clone();
	other.csrf().await;
	other
		.post(
			"/api/auth/code/request",
			json!({"email":"mitglied@liedertafel.test"}),
		)
		.await;
	assert_eq!(
		other
			.post(
				"/api/auth/code/verify",
				json!({"email":"mitglied@liedertafel.test","code":app.mail.code()})
			)
			.await
			.0,
		200
	);
	other.csrf().await;
	other.cookies.insert(
		"archive.ceremony".into(),
		member.cookies["archive.ceremony"].clone(),
	);
	let credential = registration(
		&challenge,
		b"session-bound-key",
		"http://localhost",
		"localhost",
		0x45,
	);
	assert_eq!(
		other
			.post(
				"/api/auth/passkeys/register/verify",
				json!({"credential":credential.to_string()})
			)
			.await
			.0,
		400
	);
	let mut sessions = vec![];
	for id in [
		b"synthetic-key-one".as_slice(),
		b"synthetic-key-two".as_slice(),
	] {
		let mut client = app.client();
		client.router = member.router.clone();
		client.cookies.insert(
			"archive.auth".into(),
			member.cookies["archive.auth"].clone(),
		);
		client.csrf().await;
		let challenge = creation(&mut client).await;
		let credential = registration(&challenge, id, "http://localhost", "localhost", 0x45);
		sessions.push((client, credential));
	}
	let mut tasks = tokio::task::JoinSet::new();
	for (mut client, credential) in sessions {
		tasks.spawn(async move {
			client
				.post(
					"/api/auth/passkeys/register/verify",
					json!({"credential":credential.to_string(),"name":"Laptop"}),
				)
				.await
				.0
		});
	}
	let mut statuses = vec![];
	while let Some(result) = tasks.join_next().await {
		statuses.push(result.unwrap());
	}
	statuses.sort();
	assert_eq!(statuses, vec![200, 409]);
	let keys = member.get("/api/auth/passkeys").await.1;
	assert_eq!(keys["passkeys"].as_array().unwrap().len(), 1);
	let data: Value = sqlx::query_scalar("SELECT \"Data\" FROM \"AspNetUserPasskeys\"")
		.fetch_one(&app.pool)
		.await
		.unwrap();
	assert_eq!(data["IsUserVerified"], true);
	assert_eq!(data["SignCount"], 0);
	assert_eq!(data["Name"], "Laptop");
	let audit: i64 =
		sqlx::query_scalar("SELECT count(*) FROM member_admin_actions WHERE \"Action\"=5")
			.fetch_one(&app.pool)
			.await
			.unwrap();
	assert_eq!(audit, 1);
	app.close().await;
}

#[tokio::test]
#[ignore = "requires dedicated throwaway ARCHIVE_TEST_POSTGRES"]
async fn explicit_origins_never_allow_other_ports_subdomains_or_backup_eligibility_downgrades() {
	let app = support::App::new().await;
	let fixture = import(&app).await;
	let handle = fixture["user"]["Id"].as_str().unwrap();
	let mut config = archive_server::auth::AuthConfig::development();
	config.origins = vec!["http://localhost:58121".parse().unwrap()];
	let router = support::App::router(app.pool.clone(), app.mail.clone(), app.time.clone(), config);
	for origin in [
		"http://localhost:58122",
		"http://sub.localhost:58121",
		"https://localhost:58121",
	] {
		let mut client = app.client();
		client.router = router.clone();
		client.csrf().await;
		let challenge = obtain_challenge(&mut client).await;
		let signed = assertion(
			&fixture,
			&challenge,
			origin,
			"localhost",
			5,
			2,
			Some(handle.as_bytes()),
		);
		assert_eq!(
			client
				.post(
					"/api/auth/passkeys/login/verify",
					json!({"credential":signed.to_string()})
				)
				.await
				.0,
			400
		);
	}
	sqlx::query(
		"UPDATE \"AspNetUserPasskeys\" SET \"Data\"=jsonb_set(\"Data\",'{IsBackupEligible}','true')",
	)
	.execute(&app.pool)
	.await
	.unwrap();
	for (flags, counter, status, backed_up) in [
		(5, 2, 400, false),
		(13, 2, 200, false),
		(29, 3, 200, true),
		(13, 4, 200, false),
	] {
		let mut client = app.client();
		client.csrf().await;
		let challenge = obtain_challenge(&mut client).await;
		let signed = assertion(
			&fixture,
			&challenge,
			"http://localhost",
			"localhost",
			flags,
			counter,
			Some(handle.as_bytes()),
		);
		assert_eq!(
			client
				.post(
					"/api/auth/passkeys/login/verify",
					json!({"credential":signed.to_string()})
				)
				.await
				.0,
			status
		);
		let data: Value = sqlx::query_scalar("SELECT \"Data\" FROM \"AspNetUserPasskeys\"")
			.fetch_one(&app.pool)
			.await
			.unwrap();
		assert_eq!(data["IsBackedUp"], backed_up);
		assert_eq!(data["IsBackupEligible"], true);
	}
	app.close().await;
}
