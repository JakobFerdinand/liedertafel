// Actual ENV -> reader -> native renderer -> real gateway/SQL accounting.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Isolated-child/SQL assertions.
mod support;
use archive_server::{
	ai::{Script, ScriptStep, Update},
	queue::Disposition,
	reading::{Job, Options},
};
#[tokio::test]
#[ignore = "explicit PostgreSQL/native PDFium: padded options reach actual worker model/render bounds"]
async fn padded_options_bound_real_page_images_text_outputs_and_token_reservation() {
	if std::env::var_os("ARCHIVE_229_READING_WORKER_CHILD").is_none() {
		let mut child = std::process::Command::new(std::env::current_exe().unwrap());
		child.args([
			"--exact",
			"padded_options_bound_real_page_images_text_outputs_and_token_reservation",
			"--ignored",
			"--nocapture",
		]);
		for (key, _) in std::env::vars() {
			if key
				.replace("__", ":")
				.to_ascii_lowercase()
				.starts_with("archive:reading:")
			{
				child.env_remove(key);
			}
		}
		for (name, value) in [
			("MaxPdfBytes", " 1048576 "),
			("MaxPagesPerRequest", " 1 "),
			("ImagesPerCall", " 1 "),
			("MaxImageLongSide", " 80 "),
			("JpegQuality", " 1 "),
			("EstimatedTokensPerImage", " 100 "),
			("TranscriptionMaxOutputTokens", " 7 "),
			("FieldsMaxOutputTokens", " 9 "),
			("MaxFieldTextCharacters", " 8 "),
		] {
			child.env(format!("Archive__Reading__{name}"), value);
		}
		let output = child
			.env("ARCHIVE_229_READING_WORKER_CHILD", "true")
			.output()
			.unwrap();
		assert!(
			output.status.success(),
			"{}\n{}",
			String::from_utf8_lossy(&output.stdout),
			String::from_utf8_lossy(&output.stderr)
		);
		return;
	}
	let app = support::App::new_scoped("reading229").await;
	let options = Options::from_env(false);
	let script = Script::handler(|r| {
		vec![ScriptStep::Update(Update {
			text: if r.options.response_format.is_some() {
				"{\"felder\":[{\"feld\":\"composer\",\"wert\":\"Οσ\",\"sicherheit\":\"sicher\",\"zitat\":\"Von ΟΣ\"}]}".into()
			} else {
				"Von ΟΣ und weitere Zeilen".into()
			},
			..Default::default()
		})]
	});
	let limit = options.max_pages_per_request;
	let h = support::reading::Harness::new(&app, support::reading::scan(3), script, options);
	let (owner, id) = h.seed(&app, limit).await;
	assert_eq!(h.run(id, 1).await, Disposition::Delete);
	let job = Job::load(&app.pool, id).await.unwrap().unwrap();
	assert_eq!(
		(job.status, job.pages_read, job.pages_total, job.partial()),
		(3, 1, Some(3), true)
	);
	let requests = h.script.requests();
	assert_eq!(requests.len(), 2);
	assert_eq!(requests[0].options.max_output_tokens, Some(7));
	assert_eq!(requests[1].options.max_output_tokens, Some(9));
	let images = requests[0].messages[0]
		.contents
		.iter()
		.filter_map(|c| {
			if let archive_server::ai::Content::ImageUrl(url) = c {
				Some(url)
			} else {
				None
			}
		})
		.collect::<Vec<_>>();
	assert_eq!(images.len(), 1);
	use base64::Engine;
	let jpeg = base64::engine::general_purpose::STANDARD
		.decode(images[0].split_once(',').unwrap().1)
		.unwrap();
	let image = image::load_from_memory(&jpeg).unwrap();
	assert_eq!(image.width().max(image.height()), 80);
	// At JPEG quality1 every coefficient in the first quantization table is
	// saturated255. The default80 encoding would not satisfy this wire check.
	let dqt = jpeg
		.windows(2)
		.position(|marker| marker == [0xff, 0xdb])
		.unwrap();
	assert!(
		jpeg[dqt + 5..dqt + 69]
			.iter()
			.all(|coefficient| *coefficient == 255)
	);
	let prompt = match &requests[1].messages[0].contents[0] {
		archive_server::ai::Content::Text(text) => text,
		_ => unreachable!(),
	};
	assert!(prompt.contains("NOTENTEXT-ANFANG\nVon ΟΣ u\nNOTENTEXT-ENDE"));
	assert!(!prompt.contains("weitere"));
	let song = archive_server::catalogue::queries::lied(&app.pool, owner)
		.await
		.unwrap()
		.unwrap();
	assert_eq!(song.composer.as_deref(), Some("Οσ"));
	assert!(h.gateway.drain().await);
	let rows:Vec<(String,i64,i64,i64)>=sqlx::query_as(r#"SELECT "Feature","InputTokens","OutputTokens","ReservedMicroEur" FROM ai_usage_entries ORDER BY "Feature""#).fetch_all(&app.pool).await.unwrap();
	let (transcription_text, _) =
		archive_server::ai::estimate_text(&requests[0].messages, &requests[0].options).unwrap();
	assert_eq!(
		rows[1],
		("scan-transcription".into(), transcription_text + 100, 7, 0)
	);
	assert_eq!(rows[0].0, "scan-fields");
	assert_eq!((rows[0].2, rows[0].3), (9, 0));
	app.close().await;
}
