// Fixture failures should identify the exact assertion.
#![allow(clippy::unwrap_used)]
use serde_json::{Value, json};
use std::{fs, path::Path, process::Command};

fn archive() -> Command {
	let mut command = Command::new(env!("CARGO_BIN_EXE_archive-server"));
	command.env_clear();
	command
}

fn native_report(mut command: Command, root: &Path, output: &Path) -> Value {
	let result = command
		.arg("--inventur")
		.arg(root)
		.args(["--model", "unpriced-test-fixture", "--out"])
		.arg(output)
		.output()
		.unwrap();
	assert!(
		result.status.success(),
		"{}",
		String::from_utf8_lossy(&result.stderr)
	);
	serde_json::from_slice(&fs::read(output).unwrap()).unwrap()
}

fn frozen() -> Option<Command> {
	let oracle = std::env::var_os("ARCHIVE_234_FROZEN_DLL")?;
	let exe = std::env::var_os("ARCHIVE_234_DOTNET").unwrap_or_else(|| "dotnet".into());
	let mut cmd = Command::new(exe);
	cmd.env_clear().arg(oracle);
	Some(cmd)
}

#[test]
fn inventur_reads_shortcut_boms_and_utf8_fallback_without_fetching() {
	let fixture = Fixture::new();
	let root = fixture.0.join("source");
	fs::create_dir(&root).unwrap();
	let url = "http://127.0.0.1:1/ä";
	let text = format!("[InternetShortcut]\r\n uRl= {url} \r\nURL=second");
	let utf16 = |be| {
		text.encode_utf16()
			.flat_map(|unit| {
				if be {
					unit.to_be_bytes()
				} else {
					unit.to_le_bytes()
				}
			})
			.collect::<Vec<_>>()
	};
	let utf32 = |be| {
		text.chars()
			.flat_map(|c| {
				if be {
					u32::from(c).to_be_bytes()
				} else {
					u32::from(c).to_le_bytes()
				}
			})
			.collect::<Vec<_>>()
	};
	type ShortcutCase<'a> = (&'a str, &'a [u8], &'a [u8], Option<&'a str>);
	let cases: &[ShortcutCase<'_>] = &[
		("utf16le", b"\xff\xfe", &utf16(false), Some(url)),
		("utf16be", b"\xfe\xff", &utf16(true), Some(url)),
		("utf32le", b"\xff\xfe\0\0", &utf32(false), Some(url)),
		("utf32be", b"\0\0\xfe\xff", &utf32(true), Some(url)),
		("utf8", b"\xef\xbb\xbf", text.as_bytes(), Some(url)),
		("ordinary", b"", text.as_bytes(), Some(url)),
		(
			"fallback",
			b"",
			b"\xff\nURL=urn:synthetic\xff",
			Some("urn:synthetic�"),
		),
		(
			"double-bom",
			b"\xef\xbb\xbf",
			b"\xef\xbb\xbfURL=urn:synthetic",
			None,
		),
		(
			"bare-cr",
			b"",
			b"[InternetShortcut]\rURL=urn:synthetic",
			None,
		),
		("missing-url", b"", b"[InternetShortcut]\n", None),
		("empty-url", b"", b"URL=   \nURL=second", Some("")),
	];
	for (name, bom, body, _) in cases {
		fs::write(root.join(format!("{name}.url")), [*bom, *body].concat()).unwrap();
	}
	let rust = native_report(archive(), &root, &fixture.0.join("rust.json"));
	for (name, _, _, expected) in cases {
		let path = format!("{name}.url");
		let shortcut = rust["shortcuts"]
			.as_array()
			.unwrap()
			.iter()
			.find(|s| s["path"] == path)
			.unwrap();
		assert_eq!(shortcut["linkTarget"].as_str(), *expected, "{name}");
	}
	if let Some(oracle) = frozen() {
		assert_eq!(
			rust,
			native_report(oracle, &root, &fixture.0.join("frozen.json"))
		);
	}
}

#[cfg(unix)]
#[test]
fn inventur_counts_linked_directories_per_path_and_does_not_recurse_into_ancestors() {
	use std::os::unix::fs::symlink;
	let fixture = Fixture::new();
	let root = fixture.0.join("source");
	fs::create_dir_all(root.join("real/empty")).unwrap();
	fs::write(root.join("real/score.txt"), b"hello").unwrap();
	symlink("real", root.join("alias-a")).unwrap();
	symlink("real", root.join("alias-b")).unwrap();
	let out = fixture.0.join("rust.json");
	let report = native_report(archive(), &root, &out);
	let paths = |report: &Value| {
		report["files"]
			.as_array()
			.unwrap()
			.iter()
			.map(|f| f["path"].clone())
			.collect::<Vec<_>>()
	};
	assert_eq!(
		paths(&report),
		json!(["alias-a/score.txt", "alias-b/score.txt", "real/score.txt"])
			.as_array()
			.unwrap()
			.clone()
	);
	assert_eq!(
		report["totals"],
		json!({"fileCount":3,"bytes":15,"folderCount":6,
		"emptyFolders":["alias-a/empty","alias-b/empty","real/empty"]})
	);
	if let Some(oracle) = frozen() {
		assert_eq!(
			report,
			native_report(oracle, &root, &fixture.0.join("frozen.json"))
		);
	}
	// The frozen walk has no cycle policy. Only run this part against Rust.
	symlink("..", root.join("real/back")).unwrap();
	let mut cmd = Command::new("timeout");
	cmd.args(["5s", env!("CARGO_BIN_EXE_archive-server")]);
	let loop_report = native_report(cmd, &root, &out);
	assert_eq!(paths(&loop_report), paths(&report));
	assert_eq!(loop_report["totals"]["folderCount"], 9);
	assert_eq!(
		loop_report["totals"]["emptyFolders"],
		report["totals"]["emptyFolders"]
	);

	fs::remove_file(root.join("real/back")).unwrap();
	let external = fixture.0.join("synthetic-sibling");
	fs::create_dir(&external).unwrap();
	fs::write(external.join("example.txt"), b"synthetic").unwrap();
	symlink("../synthetic-sibling", root.join("external")).unwrap();
	let report = native_report(archive(), &root, &out);
	assert!(paths(&report).contains(&json!("external/example.txt")));
	if let Some(oracle) = frozen() {
		assert_eq!(
			report,
			native_report(oracle, &root, &fixture.0.join("frozen.json"))
		);
	}
	symlink("does-not-exist", root.join("dangling")).unwrap();
	let dangling = archive().arg("--inventur").arg(&root).output().unwrap();
	assert_eq!(dangling.status.code(), Some(1));
	assert!(dangling.stdout.is_empty());
}

fn fixture() -> &'static Path {
	Path::new(concat!(
		env!("CARGO_MANIFEST_DIR"),
		"/../../../../../tests/archive/fixtures/drive-inventur"
	))
}

fn copy_corpus(source: &Path, dest: &Path) {
	fs::create_dir_all(dest).unwrap();
	for entry in fs::read_dir(source).unwrap() {
		let entry = entry.unwrap();
		let name = entry.file_name();
		let target = dest.join(&name);
		if entry.file_type().unwrap().is_dir() {
			copy_corpus(&entry.path(), &target);
		} else if name != "mapping.json" && name != "README.md" {
			fs::copy(entry.path(), target).unwrap();
		}
	}
}

struct Fixture(std::path::PathBuf);
impl Fixture {
	fn new() -> Self {
		let p = std::env::temp_dir().join(format!("archive-234-inventur-{}", uuid::Uuid::new_v4()));
		fs::create_dir(&p).unwrap();
		Self(p)
	}
}
impl Drop for Fixture {
	fn drop(&mut self) {
		let _ = fs::remove_dir_all(&self.0);
	}
}

#[test]
#[ignore = "requires Archive__Pdfium__LibraryPath pointing to the pinned local PDFium"]
fn inventur_matches_sanitized_corpus_totals_and_mapping_without_services() {
	let dir = Fixture::new();
	let root = dir.0.join("source");
	copy_corpus(fixture(), &root);
	let out = dir.0.join("output/report.json");
	let execute = |args: &[&str]| {
		let output = archive()
			.arg("--inventur")
			.arg(&root)
			.args(args)
			.env(
				"Archive__Pdfium__LibraryPath",
				std::env::var_os("Archive__Pdfium__LibraryPath").unwrap(),
			)
			.env("ARCHIVE_ENVIRONMENT", "Development")
			.env("PORT", "not-a-port")
			.env("Archive__Chat__Endpoint", "not-an-azure-uri")
			.env("Archive__Chat__DeploymentName", "   GPT-6-LUNA   ")
			.env("Archive__Ai__MonthlyCapEur", "not-used-by-inventory")
			.output()
			.unwrap();
		assert!(
			output.status.success(),
			"{}",
			String::from_utf8_lossy(&output.stderr)
		);
		String::from_utf8(output.stdout).unwrap()
	};
	let stdout = execute(&["--out", out.to_str().unwrap()]);
	assert!(stdout.starts_with("Bericht geschrieben: "));
	assert!(stdout.contains("23 Dateien, 29632 Byte, 7 Ordner (2 leer)"));
	let report: Value = serde_json::from_slice(&fs::read(&out).unwrap()).unwrap();
	assert_eq!(report["root"], json!(root));
	assert_eq!(
		report["totals"],
		json!({"fileCount":23,"bytes":29632,"folderCount":7,
		"emptyFolders":["Sample B/2. Media","Sample B/3. Notierprogrammdaten"]})
	);
	assert_eq!(report["largest"].as_array().unwrap().len(), 20);
	assert_eq!(
		report["largest"][0],
		json!({"path":"Sample B/1. Noten/PT - Sample B.pdf","bytes":14828})
	);
	assert_eq!(report["largest"][1]["bytes"], 2027);
	assert_eq!(report["largest"][2]["bytes"], 2026);
	assert_eq!(
		report["pdf"],
		json!({"files":12,"embeddedText":11,"scanned":1,"scannedPages":3,"unreadable":[]})
	);
	assert_eq!(
		report["media"],
		json!({"midiFiles":1,"mediaFiles":5,"playbackReady":5,"needingConversion":0})
	);
	assert_eq!(
		report["shortcuts"],
		json!([{"path":"Verknüpfung - Sample A.url","linkTarget":"PLACEHOLDER-SOURCE-FOLDER-LINK"}])
	);
	assert_eq!(report["duplicates"].as_array().unwrap().len(), 1);
	assert_eq!(
		report["duplicates"][0]["paths"],
		json!(["Sample C/Sample C B1 (1).pdf", "Sample C/Sample C B1.pdf"])
	);
	let files = report["files"].as_array().unwrap();
	let lookup = |path: &str| files.iter().find(|f| f["path"] == path).unwrap();
	assert_eq!(lookup("Sample C/Sample C B1.pdf")["pdf"]["pages"], 1);
	assert_eq!(lookup("Sample C/Sample C B1 (1).pdf")["pdf"]["pages"], 4);
	assert_eq!(
		report["readingCost"],
		json!({"model":"GPT-6-LUNA","priceInputEurPerMillion":0.1056,
		"priceOutputEurPerMillion":0.528,"scannedDocuments":1,"scannedPagesRead":3,"calls":1,
		"inputTokens":26000,"outputTokens":5000,"estimatedEur":0.0053856,"note":null,
		"assumptions":{"maxPagesPerRequest":20,"imagesPerCall":10,"tokensPerImage":2500,
			"promptTokensPerCall":1000,"transcriptionMaxOutputTokens":5000}})
	);
	let mapping: Value =
		serde_json::from_slice(&fs::read(fixture().join("mapping.json")).unwrap()).unwrap();
	let mut declared = std::collections::HashSet::new();
	for entry in mapping["lieder"]
		.as_array()
		.unwrap()
		.iter()
		.flat_map(|l| l["dateien"].as_array().unwrap())
		.chain(mapping["ohneLiedzuordnung"].as_array().unwrap())
	{
		let path = entry["path"].as_str().unwrap();
		assert!(declared.insert(path));
		let file = lookup(path);
		if entry["contentType"].is_string() {
			assert_eq!(file["contentType"], entry["contentType"], "{path}");
		}
		let flags = |v: &Value| {
			v["flags"]
				.as_array()
				.into_iter()
				.flatten()
				.map(|s| s.as_str().unwrap().to_owned())
				.collect::<std::collections::BTreeSet<_>>()
		};
		assert_eq!(flags(file), flags(entry), "{path}");
	}
	assert_eq!(declared.len(), files.len());
	// Optional fresh frozen executable differential: compare the entire JSON
	// object, not implementation-owned snapshots or serializer key order.
	if let Some(oracle) = std::env::var_os("ARCHIVE_234_FROZEN_DLL") {
		let oracle_out = dir.0.join("oracle.json");
		let exe = std::env::var_os("ARCHIVE_234_DOTNET").unwrap_or_else(|| "dotnet".into());
		let output = Command::new(exe)
			.env_clear()
			.arg(oracle)
			.arg("--inventur")
			.arg(&root)
			.args(["--model", "GPT-6-LUNA", "--out"])
			.arg(&oracle_out)
			.output()
			.unwrap();
		assert!(
			output.status.success(),
			"{}",
			String::from_utf8_lossy(&output.stderr)
		);
		assert_eq!(
			report,
			serde_json::from_slice::<Value>(&fs::read(oracle_out).unwrap()).unwrap()
		);
	}
	let stdout = execute(&["--model", " fehlt-im-preisverzeichnis "]);
	let (report, summary) = stdout.split_once("\n\nInventur von ").unwrap();
	let report: Value = serde_json::from_str(report).unwrap();
	assert_eq!(report["readingCost"]["model"], "fehlt-im-preisverzeichnis");
	assert!(report["readingCost"]["estimatedEur"].is_null());
	assert!(summary.contains("analytisch, ohne Modellanfrage"));
}

fn blank_pdf(pages: usize) -> String {
	use std::fmt::Write as _;
	let mut body = "%PDF-1.4\n".to_owned();
	let mut offset = vec![0];
	let kids = (3..pages + 3)
		.map(|i| format!("{i} 0 R"))
		.collect::<Vec<_>>()
		.join(" ");
	let page =
		"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>".to_string();
	let objects = [
		"<< /Type /Catalog /Pages 2 0 R >>".to_owned(),
		format!("<< /Type /Pages /Count {pages} /Kids [{kids}] >>"),
	]
	.into_iter()
	.chain(std::iter::repeat_n(page, pages));
	for (i, object) in objects.enumerate() {
		offset.push(body.len());
		writeln!(body, "{} 0 obj\n{object}\nendobj", i + 1).unwrap();
	}
	let start = body.len();
	writeln!(body, "xref\n0 {}\n0000000000 65535 f ", offset.len()).unwrap();
	for o in offset.iter().skip(1) {
		writeln!(body, "{o:010} 00000 n ").unwrap();
	}
	writeln!(
		body,
		"trailer << /Size {} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF",
		offset.len()
	)
	.unwrap();
	body
}

#[test]
#[ignore = "requires Archive__Pdfium__LibraryPath pointing to the pinned local PDFium"]
fn inventur_caps_each_scan_independently_and_keeps_malformed_pdf_a_file_fact() {
	let dir = Fixture::new();
	fs::write(dir.0.join("Lang mit 23 Seiten.pdf"), blank_pdf(23)).unwrap();
	fs::write(dir.0.join("Noch ein Scan.pdf"), blank_pdf(1)).unwrap();
	fs::write(dir.0.join("kaputt.pdf"), b"%PDF-1.4\ninvalid").unwrap();
	let output = archive()
		.arg("--inventur")
		.arg(&dir.0)
		.args(["--model", "gpt-6-luna"])
		.env(
			"Archive__Pdfium__LibraryPath",
			std::env::var_os("Archive__Pdfium__LibraryPath").unwrap(),
		)
		.output()
		.unwrap();
	assert!(output.status.success());
	let stdout = String::from_utf8(output.stdout).unwrap();
	let (report, _) = stdout.split_once("\n\nInventur von ").unwrap();
	let report: Value = serde_json::from_str(report).unwrap();
	assert_eq!(report["pdf"]["scannedPages"], 24);
	assert_eq!(
		report["pdf"]["unreadable"],
		json!([{"path":"kaputt.pdf","reason":"Das Dokument konnte nicht gelesen werden."}])
	);
	assert_eq!(report["readingCost"]["scannedPagesRead"], 21);
	assert_eq!(report["readingCost"]["scannedDocuments"], 2);
	assert_eq!(report["readingCost"]["calls"], 3);
	assert_eq!(report["readingCost"]["inputTokens"], 78000);
	assert_eq!(report["readingCost"]["outputTokens"], 15000);
	assert_eq!(report["readingCost"]["estimatedEur"], 0.0161568);
}

#[test]
fn inventur_native_configuration_failure_does_not_report_a_zero_page_scan() {
	let fixture = Fixture::new();
	fs::write(fixture.0.join("scan.pdf"), blank_pdf(1)).unwrap();
	let output = archive()
		.arg("--inventur")
		.arg(&fixture.0)
		.output()
		.unwrap();
	assert_eq!(output.status.code(), Some(1));
	assert!(output.stdout.is_empty());
	assert!(
		String::from_utf8(output.stderr)
			.unwrap()
			.contains("PDFium-Konfiguration nicht verfügbar")
	);
}

#[test]
fn inventur_missing_root_prints_usage_without_booting_the_host() {
	let output = archive()
		.args(["--inventur", "--model", "unconfigured"])
		.env("ARCHIVE_ENVIRONMENT", "Development")
		.env("PORT", "not-a-port")
		.output()
		.unwrap();
	assert_eq!(output.status.code(), Some(2));
	assert_eq!(
		String::from_utf8(output.stdout).unwrap(),
		concat!(
			"Verzeichnis nicht angegeben. Usage: --inventur <verzeichnis> [--out <bericht.json>] [--model <modellschlüssel>]\n",
			"Usage: --inventur <verzeichnis> [--out <bericht.json>] [--model <modellschlüssel>]\n",
		)
	);
}

#[test]
fn inventur_merges_base_development_and_environment_price_configuration_offline() {
	let fixture = Fixture::new();
	let root = fixture.0.join("source");
	fs::create_dir(&root).unwrap();
	let exe = fixture.0.join("archive-server");
	if fs::hard_link(env!("CARGO_BIN_EXE_archive-server"), &exe).is_err() {
		fs::copy(env!("CARGO_BIN_EXE_archive-server"), &exe).unwrap();
	}
	fs::write(fixture.0.join("appsettings.json"),r#"{"Archive":{"Chat":{"DeploymentName":" base "},"Ai":{"Models":{"base":{"InputPricePerMillionEur":1,"OutputPricePerMillionEur":2}}}}}"#).unwrap();
	fs::write(fixture.0.join("appsettings.Development.json"),r#"{"Archive":{"Reading":{"ImagesPerCall":4},"Ai":{"Models":{"base":{"OutputPricePerMillionEur":3.500000000000000000000000001}}}}}"#).unwrap();
	let output = Command::new(exe)
		.env_clear()
		.env("ARCHIVE_ENVIRONMENT", "Production")
		.env("archive__ai__models__base__inputpricepermillioneur", "5.5")
		.arg("--inventur")
		.arg(root)
		.output()
		.unwrap();
	assert!(output.status.success());
	let stdout = String::from_utf8(output.stdout).unwrap();
	let (report, _) = stdout.split_once("\n\nInventur von ").unwrap();
	let costs = &serde_json::from_str::<Value>(report).unwrap()["readingCost"];
	assert_eq!(costs["model"], "base");
	assert_eq!(costs["priceInputEurPerMillion"], 5.5);
	assert!(costs["priceOutputEurPerMillion"].is_number());
	assert_eq!(
		costs["priceOutputEurPerMillion"].to_string(),
		"3.500000000000000000000000001"
	);
	assert_eq!(costs["assumptions"]["imagesPerCall"], 4);
}

#[test]
#[ignore = "requires the pinned local PDFium; optionally compares the actual frozen executable"]
fn inventur_preserves_exact_prices_estimate_and_summary() {
	let dir = Fixture::new();
	let root = dir.0.join("source");
	fs::create_dir(&root).unwrap();
	fs::copy(
		fixture().join("Sample B/1. Noten/PT - Sample B.pdf"),
		root.join("scan.pdf"),
	)
	.unwrap();
	let run = |mut cmd: Command, name: &str, input: &str, output_price: &str| {
		let path = dir.0.join(name);
		let result = cmd
			.args([
				"--inventur",
				root.to_str().unwrap(),
				"--model",
				"precise",
				"--out",
			])
			.arg(&path)
			.env(
				"Archive__Ai__Models__precise__InputPricePerMillionEur",
				input,
			)
			.env(
				"Archive__Ai__Models__precise__OutputPricePerMillionEur",
				output_price,
			)
			.output()
			.unwrap();
		assert!(result.status.success(), "{:?}", result.status);
		let report = serde_json::from_slice::<Value>(&fs::read(path).unwrap()).unwrap();
		let stdout = String::from_utf8(result.stdout).unwrap();
		(report, stdout.lines().last().unwrap().to_owned())
	};
	for (input, output, estimate, summary) in [
		(
			"123456789012345678.1234567890",
			"3.500000000000000000000000001",
			"3209876514320987.648709876514",
			"3209876514320987.648710",
		),
		("0.00001", "0.000048", "0.0000005", "0.000001"),
	] {
		let mut rust = archive();
		rust.env(
			"Archive__Pdfium__LibraryPath",
			std::env::var_os("Archive__Pdfium__LibraryPath").unwrap(),
		);
		let assert_costs = |report: &Value| {
			for (field, expected) in [
				("priceInputEurPerMillion", input),
				("priceOutputEurPerMillion", output),
				("estimatedEur", estimate),
			] {
				let number = &report["readingCost"][field];
				assert!(number.is_number(), "{field}: {number}");
				let decimal = |s: &str| s.parse::<rust_decimal::Decimal>().unwrap();
				assert_eq!(decimal(&number.to_string()), decimal(expected), "{field}");
			}
		};
		let (mut report, line) = run(rust, "rust.json", input, output);
		assert_costs(&report);
		assert_eq!(
			line,
			format!("Lesekosten-Schätzung (precise, analytisch): {summary} EUR für 1 Anfragen")
		);
		if let Some(oracle) = frozen() {
			let (mut expected, frozen_line) = run(oracle, "frozen.json", input, output);
			assert_costs(&expected);
			assert_eq!(line, frozen_line);
			// JSON numbers may have different trailing zeroes, but the exact
			// decimal values were checked above on both emitted reports.
			for field in [
				"priceInputEurPerMillion",
				"priceOutputEurPerMillion",
				"estimatedEur",
			] {
				report["readingCost"][field] = Value::Null;
				expected["readingCost"][field] = Value::Null;
			}
			assert_eq!(report, expected);
		}
	}
}
