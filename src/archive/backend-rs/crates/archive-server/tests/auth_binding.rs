//! The maintainer-requested raw HTTP/real-PG seam; only mail delivery/time are fake.
// Fixture/setup and captured effects are assertions, never production input paths.
#![allow(clippy::unwrap_used)]
mod support;
use serde_json::{Value, json};
use support::App;

fn oracle() -> Vec<Value> {
	serde_json::from_str(include_str!(
		"../../../tests/fixtures/auth-binding-oracle.json"
	))
	.unwrap()
}
async fn effects(app: &App) -> Value {
	let mut result = serde_json::Map::new();
	for table in [
		"AspNetUsers",
		"AspNetUserRoles",
		"AspNetUserPasskeys",
		"auth_sign_in_codes",
		"auth_request_log",
		"member_invitations",
		"member_email_changes",
		"member_admin_actions",
		"archive_ceremonies",
		"archive_sessions",
	] {
		let rows: Value = sqlx::query_scalar(sqlx::AssertSqlSafe(format!("SELECT coalesce(json_agg(v ORDER BY v::text),'[]'::json) FROM (SELECT row_to_json(t) v FROM \"{table}\" t) s")))
			.fetch_one(&app.pool).await.unwrap();
		result.insert(table.to_owned(), rows);
	}
	result.insert("mailCount".into(), json!(app.mail.0.lock().unwrap().len()));
	Value::Object(result)
}

#[tokio::test]
#[ignore = "explicit real PostgreSQL 13-DTO per-occurrence/no-effects differential"]
async fn every_known_typed_occurrence_rejects_before_mail_challenge_ceremony_and_member_effects() {
	let app = App::new().await;
	let mut admin = app.admin().await;
	let (_, me) = admin.get("/api/auth/me").await;
	let mut request = app.client();
	request.csrf().await;
	assert_eq!(
		request
			.post(
				"/api/auth/code/request",
				json!({"email":"mitglied@liedertafel.test"})
			)
			.await
			.0,
		202
	);
	let code = app.mail.code();
	assert_eq!(
		admin
			.post("/api/auth/passkeys/register/options", json!({}))
			.await
			.0,
		200
	);
	let mut login = app.client();
	login.csrf().await;
	assert_eq!(
		login
			.post("/api/auth/passkeys/login/options", json!({}))
			.await
			.0,
		200
	);
	let baseline = json!({"email":"mitglied@liedertafel.test","code":code,"accountId":me["accountId"],"newEmail":"changed@liedertafel.test","role":"Editor","displayName":"Visible","name":"Visible","credential":"{}","credentialId":"AQ=="});
	let before = effects(&app).await;
	let mut failures = Vec::new();
	let mut checked = 0;
	for case in oracle().into_iter().filter(|c| c["accepted"] == false) {
		let path = case["path"].as_str().unwrap();
		let raw = case["raw"].as_str().unwrap();
		let raw = if raw.starts_with('{') && raw.ends_with('}') {
			let fields = case["fields"].as_array().unwrap();
			let defaults: serde_json::Map<String, Value> = fields
				.iter()
				.map(|f| {
					let f = f.as_str().unwrap();
					(f.to_owned(), baseline[f].clone())
				})
				.collect();
			let prefix = serde_json::to_string(&defaults).unwrap();
			let value = case["label"]
				.as_str()
				.unwrap()
				.split_once('-')
				.and_then(|(_, s)| s.split_once('-'))
				.map(|(_, s)| &baseline[s]);
			let raw = if let Some(value) = value {
				raw.replace("\"last\"", &value.to_string())
					.replace("\"first\"", &value.to_string())
			} else {
				raw.to_owned()
			};
			format!("{},{}", &prefix[..prefix.len() - 1], &raw[1..])
		} else {
			raw.to_owned()
		};
		let (s, b) = if path == "/api/auth/passkeys/login/verify" {
			login.raw("POST", path, &raw).await
		} else {
			admin.raw("POST", path, &raw).await
		};
		checked += 1;
		if s != 400 || b["title"] != "Die Anfrage ist ungültig." || effects(&app).await != before {
			failures.push(json!({"path":path,"label":case["label"],"status":s.as_u16(),"body":b}));
		}
	}
	// A new, invite-eligible address (not an existing-account conflict) makes
	// optional-name rejection protect actual account/invitation/mail writes.
	for invalid in ["42", "true", "{}", "[]"] {
		let raw = format!(
			r#"{{"email":"must-not-create@liedertafel.test","role":"Member","DisplayName":{invalid}}}"#
		);
		let (s, b) = admin.raw("POST", "/api/admin/invitations", &raw).await;
		assert_eq!(s, 400);
		assert_eq!(b["title"], "Die Anfrage ist ungültig.");
		assert_eq!(effects(&app).await, before);
	}
	app.close().await;
	assert_eq!(checked, 593);
	assert!(
		failures.is_empty(),
		"typed occurrence differences: {failures:?}"
	);
}

#[tokio::test]
#[ignore = "explicit PostgreSQL nullable/unknown DTO and actual frozen HTTP empty-body policy"]
async fn nullable_strings_unknown_values_and_optional_empty_roots_reach_ordinary_domain_validation()
{
	let app = App::new().await;
	let mut admin = app.admin().await;
	let mut checked = 0;
	for case in oracle().into_iter().filter(|c| c["accepted"] == true) {
		let path = case["path"].as_str().unwrap();
		let canonical = case["bound"].to_string();
		let expected = admin.raw("POST", path, &canonical).await;
		let actual = admin.raw("POST", path, case["raw"].as_str().unwrap()).await;
		assert_eq!(actual, expected, "{case}");
		assert!(matches!(actual.0.as_u16(), 400 | 404), "{case}");
		assert_ne!(
			actual.1["title"], "Die Anfrage ist ungültig.",
			"nullable binding must not reject: {case}"
		);
		checked += 1;
	}
	assert_eq!(checked, 237);
	let http: Vec<Value> = serde_json::from_str(include_str!(
		"../../../tests/fixtures/http-binding-oracle.json"
	))
	.unwrap();
	let before = effects(&app).await;
	let mut optional = 0;
	for case in http.into_iter().filter(|c| {
		!c["path"].as_str().unwrap().contains("/upload-sessions/")
			&& c["status"] == 400
			&& ["", "null", "{}"].contains(&c["raw"].as_str().unwrap())
	}) {
		let (s, b) = admin
			.raw(
				"POST",
				case["path"].as_str().unwrap(),
				case["raw"].as_str().unwrap(),
			)
			.await;
		assert_eq!(s.as_u16(), case["status"].as_u64().unwrap() as u16);
		assert_eq!(b["title"], case["title"]);
		optional += 1;
	}
	assert_eq!(optional, 39);
	assert_eq!(effects(&app).await, before);
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL bodyless auth mutations ignore arbitrary JSON bytes"]
async fn bodyless_options_seed_and_logout_ignore_even_malformed_json_and_keep_their_effects() {
	let app = App::new().await;
	let mut admin = app.admin().await;
	for path in [
		"/api/auth/passkeys/login/options",
		"/api/auth/passkeys/register/options",
		"/api/dev/auth/seed",
	] {
		for raw in ["", "null", "{}", "{", "[]"] {
			let (s, b) = admin.raw("POST", path, raw).await;
			assert_eq!(s, 200, "{path} {raw}: {b}");
		}
	}
	for raw in ["", "null", "{}", "{", "[]"] {
		let mut member = app.member().await;
		let (s, b) = member.raw("POST", "/api/auth/logout", raw).await;
		assert_eq!(s, 200, "{raw}: {b}");
		assert_eq!(member.get("/api/auth/me").await.1["authenticated"], false);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit PostgreSQL arrival-order successful mutations and route-specific unknowns"]
async fn raw_last_valid_and_null_occurrences_drive_real_login_invitation_and_member_mutations() {
	let app = App::new().await;
	let mut admin = app.admin().await;
	for (i, (first, last)) in [
		("email", "Email"),
		("EMAIL", "email"),
		("email", "email"),
		("eMaIl", "EMAIL"),
	]
	.into_iter()
	.enumerate()
	{
		let email = format!("binding-{i}@liedertafel.test");
		let raw = format!(
			r#"{{"{first}":null,"{last}":"{email}","role":"invalid","Role":"Member","displayName":"first","DisplayName":{},"credential":42}}"#,
			if i == 3 { "null" } else { "\"Visible Name\"" }
		);
		let (s, b) = admin.raw("POST", "/api/admin/invitations", &raw).await;
		assert_eq!(s, 201, "{b}");
		let id = b["accountId"].as_str().unwrap();
		let (_, members) = admin.get("/api/admin/members").await;
		let entry = members["members"]
			.as_array()
			.unwrap()
			.iter()
			.find(|m| m["accountId"] == id)
			.unwrap();
		assert_eq!(
			entry["displayName"],
			if i == 3 {
				Value::Null
			} else {
				json!("Visible Name")
			}
		);
		assert_eq!(
			admin
				.raw(
					"POST",
					"/api/admin/invitations/resend",
					&format!(r#"{{"Email":"{email}","role":42}}"#)
				)
				.await
				.0,
			200
		);
		let mut member = app.client();
		member.csrf().await;
		let raw = format!(r#"{{"{first}":"bad","{last}":"{email}","role":42}}"#);
		assert_eq!(
			member.raw("POST", "/api/auth/code/request", &raw).await.0,
			202
		);
		let code = app.mail.code();
		assert_eq!(
			member
				.raw(
					"POST",
					"/api/auth/code/verify",
					&format!(r#"{{"EMAIL":"{email}","code":"000000","Code":"{code}"}}"#)
				)
				.await
				.0,
			200
		);
		for (path, extra) in [
			("role", ",\"Role\":\"Editor\""),
			("deactivate", ""),
			("reactivate", ""),
		] {
			let raw = format!(
				r#"{{"accountId":"invalid-domain-id","ACCOUNTID":"{id}"{extra},"newEmail":42}}"#
			);
			let (s, b) = admin
				.raw("POST", &format!("/api/admin/members/{path}"), &raw)
				.await;
			assert_eq!(s, 200, "{path}: {b}");
		}
		let new = format!("changed-{i}@liedertafel.test");
		assert_eq!(
			admin
				.raw(
					"POST",
					"/api/admin/members/email/request",
					&format!(
						r#"{{"AccountId":"{id}","newEmail":"bad","NEWEMAIL":"{new}","code":42}}"#
					)
				)
				.await
				.0,
			200
		);
		let code = app
			.mail
			.0
			.lock()
			.unwrap()
			.last()
			.unwrap()
			.text
			.split("Ihr Bestätigungscode lautet: ")
			.nth(1)
			.unwrap()[..6]
			.to_owned();
		assert_eq!(
			admin
				.raw(
					"POST",
					"/api/admin/members/email/confirm",
					&format!(
						r#"{{"ACCOUNTID":"{id}","NEWEMAIL":"{new}","code":null,"CODE":"{code}"}}"#
					)
				)
				.await
				.0,
			200
		);
	}
	let before = effects(&app).await;
	assert_eq!(
		admin
			.raw(
				"POST",
				"/api/admin/invitations",
				r#"{"email":"must-not-send@liedertafel.test","Email":null,"role":"Member"}"#
			)
			.await
			.0,
		400
	);
	assert_eq!(
		admin
			.raw(
				"POST",
				"/api/auth/code/request",
				r#"{"email":"mitglied@liedertafel.test","Email":null}"#
			)
			.await
			.0,
		400
	);
	assert_eq!(effects(&app).await, before);
	app.close().await;
}
