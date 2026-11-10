//! R3 independent BCL string grammar at existing HTTP seams, not typed JSON.
// Synthetic database/process fixtures must fail loudly on setup or assertion errors.
#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
mod support;
use serde_json::{Value, json};
use std::collections::HashSet;
use support::App;
use uuid::Uuid;
fn fixture() -> Vec<(String, Option<String>)> {
	serde_json::from_str(include_str!("../../../tests/fixtures/guid-bcl.json")).unwrap()
}
fn segment(raw: &str) -> String {
	url::form_urlencoded::byte_serialize(raw.as_bytes())
		.collect::<String>()
		.replace('+', "%20")
}
#[tokio::test]
#[ignore = "explicit disposable PostgreSQL BCL Guid path and member-string matrix"]
async fn bcl_matrix_selects_real_records_and_keeps_constrained_fallback_and_nil_domain_guards() {
	let app = App::new().await;
	let mut admin = app.admin().await;
	let mut anonymous = app.client();
	let owner = Uuid::parse_str(
		admin.get("/api/auth/me").await.1["accountId"]
			.as_str()
			.unwrap(),
	)
	.unwrap();
	let cases = fixture();
	let ids: HashSet<_> = cases.iter().filter_map(|(_, v)| v.clone()).collect();
	assert_eq!(cases.len(), 139);
	for text in ids {
		let id = Uuid::parse_str(&text).unwrap();
		sqlx::query(r#"INSERT INTO lieder ("Id","Title","CreatedAt","UpdatedAt","CreatedByAccountId","UpdatedByAccountId","RowVersion") VALUES ($1,$2,now(),now(),$3,$3,1)"#).bind(id).bind(&text).bind(owner).execute(&app.pool).await.unwrap();
		sqlx::query(r#"INSERT INTO lied_entwuerfe ("Id","OwnerAccountId","Title","Composer","Arranger","Lyricist","VoiceConfiguration","MusicalKey","Language","Lyrics","EditedFields","Suggestions","CreatedAt","UpdatedAt") VALUES ($1,$2,'A','','','','','','','','','{}',now(),now())"#).bind(id).bind(owner).execute(&app.pool).await.unwrap();
		sqlx::query(r#"INSERT INTO auftritt_entwuerfe ("Id","OwnerAccountId","Title","DateiCount","Form","CreatedAt","UpdatedAt") VALUES ($1,$2,'A',0,'{"version":1}',now(),now())"#).bind(id).bind(owner).execute(&app.pool).await.unwrap();
		if !id.is_nil() {
			let email = format!("guid-{id}@liedertafel.test");
			sqlx::query(r#"INSERT INTO "AspNetUsers" ("Id","Email","NormalizedEmail","UserName","NormalizedUserName","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount") VALUES ($1,$2,upper($2),$2,upper($2),true,false,false,true,0)"#).bind(id).bind(email).execute(&app.pool).await.unwrap();
			sqlx::query(r#"INSERT INTO "AspNetUserRoles" ("UserId","RoleId") SELECT $1,"Id" FROM "AspNetRoles" WHERE "Name"='Member'"#).bind(id).execute(&app.pool).await.unwrap();
		}
	}
	for (raw, expected) in cases {
		// Empty path is the collection route, not a malformed path capture.
		let path = if raw.is_empty() {
			"%00".to_owned()
		} else {
			segment(&raw)
		};
		let (status, _, bytes) = admin.wire("GET", &format!("/api/lieder/{path}"), "").await;
		assert_eq!(
			status.as_u16(),
			if expected.is_some() { 200 } else { 400 },
			"Lied path {raw:?}: {}",
			String::from_utf8_lossy(&bytes)
		);
		if let Some(id) = &expected {
			let b: Value = serde_json::from_slice(&bytes).unwrap();
			assert_eq!(b["lied"]["id"], *id);
		}
		for prefix in ["lied-entwuerfe", "auftritt-entwuerfe"] {
			let (status, _, bytes) = admin
				.wire("GET", &format!("/api/{prefix}/{path}"), "")
				.await;
			assert_eq!(
				status.as_u16(),
				if expected.is_some() { 200 } else { 404 },
				"{prefix} path {raw:?}: {}",
				String::from_utf8_lossy(&bytes)
			);
			if expected.is_none() {
				let b: Value = serde_json::from_slice(&bytes).unwrap();
				assert_eq!(b["title"], "API-Endpunkt nicht gefunden.");
				assert_eq!(
					anonymous.get(&format!("/api/{prefix}/{path}")).await.1["title"],
					"API-Endpunkt nicht gefunden."
				);
			}
		}
		let valid_domain = expected
			.as_deref()
			.is_some_and(|id| id != "00000000-0000-0000-0000-000000000000");
		let (status, body) = admin
			.post(
				"/api/admin/members/role",
				json!({"accountId":raw,"role":"Member"}),
			)
			.await;
		assert_eq!(
			status.as_u16(),
			if valid_domain { 200 } else { 400 },
			"AccountId {raw:?}: {body}"
		);
		if !valid_domain {
			for route in ["deactivate", "reactivate", "email/request", "email/confirm"] {
				let before = admin.get("/api/admin/members").await.1;
				let mails = app.mail.0.lock().unwrap().len();
				let (s, b) = admin
					.post(
						&format!("/api/admin/members/{route}"),
						json!({"accountId":raw,"newEmail":"never@liedertafel.test","code":"123456"}),
					)
					.await;
				assert_eq!(s, 400, "{raw:?} {route}: {b}");
				assert_eq!(b["title"], "Die Kennung ist ungültig.");
				assert_eq!(before, admin.get("/api/admin/members").await.1);
				assert_eq!(app.mail.0.lock().unwrap().len(), mails);
			}
		}
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL existing routes and strict JSON separation"]
async fn existing_unconstrained_routes_share_string_grammar_but_json_guid_remains_d_only() {
	let mut app = App::new().await;
	app.with_storage(std::sync::Arc::new(support::storage::FakeStorage::default()));
	let mut admin = app.admin().await;
	let mut anon = app.client();
	let d = "12345678-abcd-4321-89ab-cdef01234567";
	let x = "{0x12345678,0xabcd,0x4321,{0x89,0xab,0xcd,0xef,0x01,0x23,0x45,0x67}}";
	for (raw, valid) in [
		(d, true),
		(&format!("({d})"), true),
		(&format!(" {d} "), true),
		(x, true),
		(&format!("({{{d}}})"), false),
		(&format!("({})", d.replace('-', "")), false),
		(&format!("urn:uuid:{d}"), false),
	] {
		for (method, template) in [
			("GET", "/api/events/{id}"),
			("PATCH", "/api/events/{id}"),
			("GET", "/api/lieder/{id}/historie"),
			("GET", "/api/dateien/{id}/access"),
			("POST", "/api/dateien/{id}/storage-tier"),
			("PATCH", "/api/dateien/{id}"),
			("PUT", "/api/events/{id}/programme/items"),
			("POST", "/api/lieder/{id}/dateien/upload-session"),
			("POST", "/api/events/{id}/dateien/upload-session"),
			("POST", "/api/upload-sessions/{id}/renew"),
			("POST", "/api/upload-sessions/{id}/finalize"),
			("DELETE", "/api/upload-sessions/{id}"),
			("PATCH", "/api/lieder/{id}"),
			("POST", "/api/lieder/{id}/delete"),
			("POST", "/api/events/{id}/delete"),
			("POST", "/api/dateien/{id}/delete"),
			("POST", "/api/lieder/{id}/restore"),
			("POST", "/api/events/{id}/restore"),
			("POST", "/api/dateien/{id}/restore"),
			("POST", "/api/papierkorb/lieder/{id}/remove"),
			("POST", "/api/papierkorb/auftritte/{id}/remove"),
			("POST", "/api/papierkorb/dateien/{id}/remove"),
		] {
			let path = template.replace("{id}", &segment(raw));
			let (s, b) = admin
				.request(method, &path, json!({"tier":"cold","rowVersion":1}))
				.await;
			assert_eq!(
				s.as_u16(),
				if valid { 404 } else { 400 },
				"{method} {template} {raw:?}: {b}"
			);
			if !valid {
				assert_eq!(b["title"], "Die Anfrage ist ungültig.");
				let (s, _, _) = anon.wire(method, &path, "{}").await;
				assert_eq!(s, 400, "unconstrained binding before auth");
			}
		}
	}
	let event = support::catalogue::event(&app).await;
	for raw in [d, &format!("({d})"), x, &d.replace('-', "")] {
		let (s, b) = admin
			.request(
				"PUT",
				&format!("/api/events/{event}/programme/items"),
				json!({"items":[{"liedId":raw}]}),
			)
			.await;
		assert_eq!(s, 400);
		assert_eq!(
			b["title"],
			if raw == d {
				"Ein Programmpunkt verweist auf ein nicht vorhandenes Lied."
			} else {
				"Die Anfrage ist ungültig."
			}
		);
	}
	app.close().await;
}

#[tokio::test]
#[ignore = "explicit disposable PostgreSQL all existing member operations with live CLR string IDs"]
async fn valid_p_x_and_whitespace_account_ids_drive_all_five_existing_member_operations() {
	let app = App::new().await;
	let mut admin = app.admin().await;
	let _member = app.member().await;
	let d = "12345678-abcd-4321-89ab-cdef01234567";
	let id = Uuid::parse_str(d).unwrap();
	sqlx::query(r#"INSERT INTO "AspNetUsers" ("Id","Email","NormalizedEmail","UserName","NormalizedUserName","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount") VALUES ($1,'guid-live@liedertafel.test','GUID-LIVE@LIEDERTAFEL.TEST','guid-live@liedertafel.test','GUID-LIVE@LIEDERTAFEL.TEST',true,false,false,true,0)"#).bind(id).execute(&app.pool).await.unwrap();
	sqlx::query(r#"INSERT INTO "AspNetUserRoles" ("UserId","RoleId") SELECT $1,"Id" FROM "AspNetRoles" WHERE "Name"='Member'"#).bind(id).execute(&app.pool).await.unwrap();
	for (route, raw, extra) in [
		("deactivate", format!("({d})"), json!({})),
		("reactivate", format!(" {d} "), json!({})),
		(
			"role",
			"{0x12345678,0xabcd,0x4321,{0x89,0xab,0xcd,0xef,0x01,0x23,0x45,0x67}}".into(),
			json!({"role":"Editor"}),
		),
		(
			"email/request",
			format!("({d})"),
			json!({"newEmail":"guid-moved@liedertafel.test"}),
		),
	] {
		let mut body = extra;
		body["accountId"] = json!(raw);
		let (s, b) = admin
			.post(&format!("/api/admin/members/{route}"), body)
			.await;
		assert_eq!(s, 200, "{route}: {b}");
	}
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
	let (s,b)=admin.post("/api/admin/members/email/confirm",json!({"accountId":format!("\u{00a0}{d}\u{00a0}"),"newEmail":"guid-moved@liedertafel.test","code":code})).await;
	assert_eq!(s, 200, "{b}");
	let members = admin.get("/api/admin/members").await.1;
	let target = members["members"]
		.as_array()
		.unwrap()
		.iter()
		.find(|m| m["accountId"] == d)
		.unwrap();
	assert_eq!(target["email"], "guid-moved@liedertafel.test");
	assert_eq!(target["status"], "active");
	assert!(
		target["roles"]
			.as_array()
			.unwrap()
			.contains(&json!("Editor"))
	);
	app.close().await;
}
