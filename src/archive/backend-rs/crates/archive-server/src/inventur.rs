//! Offline Quellen-Inventur. Reads local files; a model name selects prices only.
use crate::{
	ai,
	catalogue::kinds,
	extraction,
	extraction::pdf::{Outcome, PdfEngine},
	reading,
};
use rust_decimal::Decimal;
use serde::Serialize;
use serde_json::{Value, json};
use std::{
	borrow::Cow,
	collections::BTreeMap,
	fs::{self, File},
	io::{self, Read, Write},
	os::unix::fs::MetadataExt,
	path::{Component, Path, PathBuf},
};

const USAGE: &str =
	"Usage: --inventur <verzeichnis> [--out <bericht.json>] [--model <modellschlüssel>]";

#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error("Inventur konnte nicht gelesen oder geschrieben werden: {0}")]
	Io(#[from] io::Error),
	#[error("Inventur-Bericht konnte nicht serialisiert werden.")]
	Json(#[from] serde_json::Error),
	#[error("PDFium-Konfiguration nicht verfügbar.")]
	Pdfium,
	#[error("Namenskonflikt: verschachtelter Kopiersuffix wird nicht unterstützt.")]
	CopySuffix,
	#[error("Preiskonfiguration der Inventur ist ungültig.")]
	Price,
}

pub fn run(args: &[String], mut out: impl Write) -> Result<u8, Error> {
	let option = |name| args.windows(2).find(|a| a[0] == name).map(|a| &a[1]);
	let mut positional = args.iter();
	let root = loop {
		match positional.next().map(String::as_str) {
			Some("--model" | "--out") => {
				positional.next();
			}
			Some(value) if value.starts_with('-') => {}
			value => break value,
		}
	};
	let Some(root) = root.filter(|root| Path::new(root).is_dir()) else {
		if let Some(root) = root {
			writeln!(out, "Verzeichnis nicht gefunden: {root}")?;
		} else {
			writeln!(out, "Verzeichnis nicht angegeben. {USAGE}")?;
		}
		writeln!(out, "{USAGE}")?;
		return Ok(2);
	};
	let root = full_path(Path::new(root))?;
	let configuration = inventory_configuration()?;
	let model = option("--model")
		.map(|s| s.trim().to_owned())
		.unwrap_or_else(|| {
			configuration
				.get("archive:chat:deploymentname")
				.map(|v| v.trim().to_owned())
				.filter(|v| !v.is_empty())
				.unwrap_or_else(|| "scripted".into())
		});
	let mut walk = Walk::default();
	walk.visit(
		&root,
		&root,
		&mut vec![directory_identity(&fs::metadata(&root)?)],
	)?;
	let report = walk.report(&root, &model, configuration)?;
	let encoded = serde_json::to_string_pretty(&report)?;
	if let Some(path) = option("--out") {
		let path = full_path(Path::new(path))?;
		if let Some(dir) = path.parent() {
			fs::create_dir_all(dir)?;
		}
		fs::write(&path, encoded)?;
		writeln!(out, "Bericht geschrieben: {}", path.display())?;
	} else {
		writeln!(out, "{encoded}")?;
	}
	writeln!(out, "\nInventur von {}", root.display())?;
	let (t, p, c) = (&report["totals"], &report["pdf"], &report["readingCost"]);
	writeln!(
		out,
		"{} Dateien, {} Byte, {} Ordner ({} leer)",
		t["fileCount"],
		t["bytes"],
		t["folderCount"],
		array_len(&t["emptyFolders"])
	)?;
	if let Some(first) = report["largest"].get(0) {
		writeln!(
			out,
			"Größe der größten Datei: {} Byte ({})",
			first["bytes"],
			first["path"].as_str().unwrap_or_default()
		)?;
	}
	writeln!(
		out,
		"PDFs: {}, mit eingebettetem Text {}, Scans {} ({} Seiten)",
		p["files"], p["embeddedText"], p["scanned"], p["scannedPages"]
	)?;
	writeln!(
		out,
		"Midis: {}\nNamenskonflikte: {}",
		report["media"]["midiFiles"],
		array_len(&report["duplicates"])
	)?;
	if let Some(eur) = c["estimatedEur"].as_f64() {
		writeln!(
			out,
			"Lesekosten-Schätzung ({model}, analytisch): {eur:.6} EUR für {} Anfragen",
			c["calls"]
		)?;
	} else {
		writeln!(
			out,
			"Lesekosten-Schätzung ({model}): kein Preis unter Archive:Ai:Models — analytisch, ohne Modellanfrage."
		)?;
	}
	Ok(0)
}

fn inventory_configuration() -> Result<BTreeMap<String, String>, Error> {
	fn collect(path: &str, value: Value, settings: &mut BTreeMap<String, String>) {
		match value {
			Value::Object(children) => {
				for (key, value) in children {
					let key = key.to_ascii_lowercase();
					let key = if path.is_empty() {
						key
					} else {
						format!("{path}:{key}")
					};
					collect(&key, value, settings);
				}
			}
			Value::String(s) => {
				settings.insert(path.to_owned(), s);
			}
			Value::Number(n) => {
				settings.insert(path.to_owned(), n.to_string());
			}
			_ => {}
		}
	}
	let mut result = BTreeMap::new();
	let exe = std::env::current_exe()?;
	if let Some(dir) = exe.parent() {
		for name in ["appsettings.json", "appsettings.Development.json"] {
			match fs::read(dir.join(name)) {
				Ok(raw) => collect("", serde_json::from_slice(&raw)?, &mut result),
				Err(e) if e.kind() == io::ErrorKind::NotFound => {}
				Err(e) => return Err(e.into()),
			}
		}
	}
	result.extend(std::env::vars_os().map(|(key, value)| {
		(
			key.to_string_lossy()
				.replace("__", ":")
				.to_ascii_lowercase(),
			value.to_string_lossy().into_owned(),
		)
	}));
	Ok(result)
}
fn array_len(v: &Value) -> usize {
	v.as_array().map_or(0, Vec::len)
}
fn full_path(path: &Path) -> io::Result<PathBuf> {
	// Match GetFullPath's lexical normalization without dereferencing symlinks.
	let mut full = PathBuf::new();
	for component in std::path::absolute(path)?.components() {
		match component {
			Component::ParentDir => {
				full.pop();
			}
			Component::CurDir => {}
			part => full.push(part.as_os_str()),
		}
	}
	Ok(full)
}

#[derive(Default)]
struct Walk {
	folders: usize,
	empty: Vec<String>,
	files: Vec<Entry>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Entry {
	path: String,
	bytes: u64,
	content_type: Option<&'static str>,
	playable: Option<bool>,
	link_target: Option<String>,
	flags: Vec<&'static str>,
	pdf: Option<Pdf>,
}
#[derive(Serialize)]
struct Pdf {
	pages: Option<i32>,
	text: &'static str,
	reason: Option<&'static str>,
}

impl Walk {
	fn visit(
		&mut self,
		root: &Path,
		dir: &Path,
		ancestors: &mut Vec<(u64, u64)>,
	) -> Result<(), Error> {
		let mut empty = true;
		for item in fs::read_dir(dir)? {
			let item = item?;
			let path = item.path();
			empty &= item.file_name() == ".gitkeep";
			// Follow links, but keep the enumerated path, not the target path.
			// A dangling link fails the walk just like an unreadable entry.
			let metadata = fs::metadata(&path)?;
			if metadata.is_dir() {
				self.folders += 1;
				let identity = directory_identity(&metadata);
				if !ancestors.contains(&identity) {
					ancestors.push(identity);
					self.visit(root, &path, ancestors)?;
					ancestors.pop();
				}
			} else if metadata.is_file() {
				self.files.push(Entry::read(relative(root, &path), &path)?);
			}
		}
		if dir != root && empty {
			self.empty.push(relative(root, dir));
		}
		Ok(())
	}

	fn report(
		mut self,
		root: &Path,
		model: &str,
		config: BTreeMap<String, String>,
	) -> Result<Value, Error> {
		self.files.sort_by(|a, b| ordinal(&a.path, &b.path));
		self.empty.sort_by(|a, b| ordinal(a, b));
		let duplicates = duplicates(&mut self.files)?;
		let mut by_type = BTreeMap::<_, (usize, u64)>::new();
		let mut largest: Vec<_> = self.files.iter().collect();
		largest.sort_by(|a, b| {
			b.bytes
				.cmp(&a.bytes)
				.then_with(|| ordinal(&a.path, &b.path))
		});
		let (
			mut pdf_files,
			mut embedded,
			mut scanned,
			mut scanned_pages,
			mut pages_read,
			mut calls,
		) = (0, 0, 0, 0, 0, 0);
		let (mut midi, mut ready, mut conversion) = (0, 0, 0);
		let (mut unreadable, mut shortcuts) = (Vec::new(), Vec::new());
		let reading = reading::Options::from_configuration(true, |key| {
			config
				.get(&format!("archive:reading:{}", key.to_ascii_lowercase()))
				.cloned()
		});
		for f in &self.files {
			let group = by_type.entry(f.content_type).or_default();
			group.0 += 1;
			group.1 += f.bytes;
			if let Some(p) = &f.pdf {
				pdf_files += 1;
				match p.text {
					"embeddedText" => embedded += 1,
					"scan" => {
						scanned += 1;
						let pages = i64::from(p.pages.unwrap_or(0));
						scanned_pages += pages;
						let bounded = pages.min(i64::from(reading.max_pages_per_request));
						pages_read += bounded;
						let images = i64::from(reading.images_per_call);
						calls += (bounded + images - 1) / images;
					}
					_ => unreadable.push(json!({"path":f.path,"reason":p.reason})),
				}
			}
			midi += usize::from(f.flags.contains(&"midi"));
			if f.flags.contains(&"shortcut") {
				shortcuts.push(json!({"path":f.path,"linkTarget":f.link_target}));
			}
			ready += usize::from(f.playable == Some(true));
			conversion += usize::from(f.playable == Some(false));
		}
		let input = calls
			* (i64::from(reading.images_per_call) * reading.estimated_tokens_per_image + 1000);
		let output = calls * i64::from(reading.transcription_max_output_tokens);
		let prices = ai::Options::inventory_prices(config).map_err(|_| Error::Price)?;
		let price = prices.find_price(model);
		let eur = price
			.map(|p| {
				let million = Decimal::from(1_000_000);
				Decimal::from(input)
					.checked_mul(p.input)?
					.checked_div(million)?
					.checked_add(
						Decimal::from(output)
							.checked_mul(p.output)?
							.checked_div(million)?,
					)
			})
			.map(|estimate| estimate.ok_or(Error::Price))
			.transpose()?;
		let number = |v: Option<Decimal>| {
			v.map(|d| d.to_string().parse::<serde_json::Number>())
				.transpose()
		};
		Ok(json!({
			"root":root,
			"totals":{"fileCount":self.files.len(),"bytes":self.files.iter().map(|f| f.bytes).sum::<u64>(),"folderCount":self.folders,"emptyFolders":self.empty},
			"byType":by_type.into_iter().map(|(t,(n,b))| json!({"contentType":t,"fileCount":n,"bytes":b})).collect::<Vec<_>>(),
			"largest":largest.into_iter().take(20).map(|f| json!({"path":f.path,"bytes":f.bytes})).collect::<Vec<_>>(),
			"duplicates":duplicates,"shortcuts":shortcuts,
			"pdf":{"files":pdf_files,"embeddedText":embedded,"scanned":scanned,"scannedPages":scanned_pages,"unreadable":unreadable},
			"media":{"midiFiles":midi,"mediaFiles":ready+conversion,"playbackReady":ready,"needingConversion":conversion},
			"readingCost":{"model":model, "priceInputEurPerMillion":number(price.map(|p| p.input))?, "priceOutputEurPerMillion":number(price.map(|p| p.output))?,
				"scannedDocuments":scanned,"scannedPagesRead":pages_read,"calls":calls,"inputTokens":input,"outputTokens":output,"estimatedEur":number(eur)?,
				"note":price.is_none().then(|| format!("Kein Preis für das Modell '{model}' unter Archive:Ai:Models konfiguriert; die Schätzung bleibt ohne EUR-Betrag.")),
				"assumptions":{"maxPagesPerRequest":reading.max_pages_per_request,"imagesPerCall":reading.images_per_call,
					"tokensPerImage":reading.estimated_tokens_per_image,"promptTokensPerCall":1000,"transcriptionMaxOutputTokens":reading.transcription_max_output_tokens}},
			"files":self.files,
		}))
	}
}

fn directory_identity(metadata: &fs::Metadata) -> (u64, u64) {
	(metadata.dev(), metadata.ino())
}

impl Entry {
	fn read(relative: String, path: &Path) -> Result<Self, Error> {
		let mut file = File::open(path)?;
		let bytes = file.metadata()?.len();
		let mut header = [0; 64];
		let header = &mut header[..bytes.min(64) as usize];
		file.read_exact(header)?;
		let (_, ext) = stem_ext(&relative);
		let ext = ext.to_ascii_lowercase();
		let mut entry = Self {
			path: relative,
			bytes,
			content_type: None,
			playable: None,
			link_target: None,
			flags: Vec::new(),
			pdf: None,
		};
		if header.starts_with(b"%PDF-") {
			entry.content_type = Some("application/pdf");
			entry.pdf = Some(pdf(path)?);
		} else if header.starts_with(b"MThd") {
			entry.content_type = Some("audio/midi");
			entry.flags.push("midi");
		} else if ext == ".url" || (ext == ".lnk" && header.starts_with(b"L\0\0\0")) {
			entry.flags.push("shortcut");
			if ext == ".url"
				&& let Ok(contents) = fs::read(path)
			{
				entry.link_target = shortcut_text(&contents)
					.split('\n')
					.map(str::trim)
					.find_map(|line| {
						line.get(..4)
							.filter(|v| v.eq_ignore_ascii_case("URL="))
							.map(|_| line[4..].trim().to_owned())
					});
			}
		} else {
			let video = [
				".mp4", ".mov", ".mkv", ".avi", ".webm", ".mpd", ".3gp", ".m4v",
			]
			.contains(&ext.as_str());
			let media = kinds::detect(header, video);
			if media != "application/octet-stream" {
				entry.content_type = Some(media);
				entry.playable = Some(if video {
					matches!(media, "video/mp4" | "video/webm")
				} else {
					matches!(
						media,
						"audio/mp4" | "audio/webm" | "audio/mpeg" | "audio/wav"
					)
				});
			} else {
				entry
					.flags
					.push(if header.starts_with(b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1") {
						"oleCompoundFile"
					} else if header.starts_with(b"PK\x03\x04") {
						"zipContainer"
					} else if !header.is_empty() && !header.contains(&0) {
						"textLike"
					} else {
						"unknown"
					});
			}
		}
		Ok(entry)
	}
}

fn shortcut_text(bytes: &[u8]) -> Cow<'_, str> {
	// File.ReadAllText detects one BOM. Check UTF-32 first: its LE marker
	// starts with the UTF-16 LE marker. Broken units use replacement text.
	if let Some((bytes, big_endian)) = bytes
		.strip_prefix(b"\0\0\xfe\xff")
		.map(|b| (b, true))
		.or_else(|| bytes.strip_prefix(b"\xff\xfe\0\0").map(|b| (b, false)))
	{
		let (chunks, remainder) = bytes.as_chunks::<4>();
		let mut text: String = chunks
			.iter()
			.map(|&word| {
				let code = if big_endian {
					u32::from_be_bytes(word)
				} else {
					u32::from_le_bytes(word)
				};
				char::from_u32(code).unwrap_or(char::REPLACEMENT_CHARACTER)
			})
			.collect();
		if !remainder.is_empty() {
			text.push(char::REPLACEMENT_CHARACTER);
		}
		Cow::Owned(text)
	} else if let Some((bytes, big_endian)) = bytes
		.strip_prefix(b"\xfe\xff")
		.map(|b| (b, true))
		.or_else(|| bytes.strip_prefix(b"\xff\xfe").map(|b| (b, false)))
	{
		let (chunks, remainder) = bytes.as_chunks::<2>();
		let units = chunks.iter().map(|&word| {
			if big_endian {
				u16::from_be_bytes(word)
			} else {
				u16::from_le_bytes(word)
			}
		});
		let mut text: String = char::decode_utf16(units)
			.map(|c| c.unwrap_or(char::REPLACEMENT_CHARACTER))
			.collect();
		if !remainder.is_empty() {
			text.push(char::REPLACEMENT_CHARACTER);
		}
		Cow::Owned(text)
	} else {
		String::from_utf8_lossy(bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(bytes))
	}
}

fn pdf(path: &Path) -> Result<Pdf, Error> {
	let engine = PdfEngine::from_env().map_err(|_| Error::Pdfium)?;
	let pages = engine.page_count(path, || false).ok();
	let (text, reason) = match engine.extract(path, &extraction::Options::default(), || false) {
		Ok(Outcome::Text(_)) => ("embeddedText", None),
		Ok(Outcome::NoText) => ("scan", None),
		Ok(Outcome::Failure(reason)) => ("failed", Some(reason)),
		Err(_) => ("failed", Some("Die Datei konnte nicht gelesen werden.")),
	};
	Ok(Pdf {
		pages,
		text,
		reason: reason.or(if pages.is_none() {
			Some(extraction::pdf::CORRUPT)
		} else {
			None
		}),
	})
}

fn relative(root: &Path, path: &Path) -> String {
	path.strip_prefix(root)
		.unwrap_or(path)
		.to_string_lossy()
		.into_owned()
}
// StringComparer.Ordinal compares UTF-16 code units, not UTF-8 bytes.
fn ordinal(a: &str, b: &str) -> std::cmp::Ordering {
	a.encode_utf16().cmp(b.encode_utf16())
}
fn stem_ext(path: &str) -> (&str, &str) {
	let file = path.rsplit('/').next().unwrap_or(path);
	match file.rfind('.') {
		Some(i) if i + 1 < file.len() => file.split_at(i),
		Some(i) => (&file[..i], ""),
		None => (file, ""),
	}
}
fn duplicate_stem(stem: &str) -> Result<&str, Error> {
	if let Some((left, digits)) = stem.strip_suffix(')').and_then(|s| s.rsplit_once('('))
		&& !digits.is_empty()
		&& digits.bytes().all(|b| b.is_ascii_digit())
	{
		let trimmed = left.trim_end();
		if trimmed.ends_with(')') {
			// The frozen walker fails here: it reuses the original '(' index.
			return Err(Error::CopySuffix);
		}
		return Ok(trimmed);
	}
	Ok(stem)
}
fn duplicates(files: &mut [Entry]) -> Result<Vec<Value>, Error> {
	let mut groups = BTreeMap::<_, Vec<_>>::new();
	for (i, file) in files.iter().enumerate() {
		let folder = file.path.rsplit_once('/').map_or("", |(folder, _)| folder);
		let (stem, ext) = stem_ext(&file.path);
		groups
			.entry((
				folder.to_owned(),
				reading::programme::invariant_lower(duplicate_stem(stem)?),
				reading::programme::invariant_lower(ext),
			))
			.or_default()
			.push(i);
	}
	let mut duplicates = Vec::new();
	for ((folder, _, _), group) in groups.into_iter().filter(|(_, g)| g.len() > 1) {
		let (stem, ext) = stem_ext(&files[group[0]].path);
		let mut sizes = group.iter().map(|&i| files[i].bytes).collect::<Vec<_>>();
		sizes.sort_unstable();
		let flag = if sizes.first() == sizes.last() {
			"sameSizeDuplicateCandidate"
		} else {
			"unresolvedDuplicateCandidate"
		};
		duplicates.push(
			json!({"folder":folder,"stem":duplicate_stem(stem)?,"extension":ext,
			"paths":group.iter().map(|&i| &files[i].path).collect::<Vec<_>>(),"sizes":sizes}),
		);
		for i in group {
			files[i].flags.push(flag);
		}
	}
	duplicates.sort_by(|a, b| {
		["folder", "stem", "extension"]
			.into_iter()
			.map(|k| {
				ordinal(
					a[k].as_str().unwrap_or_default(),
					b[k].as_str().unwrap_or_default(),
				)
			})
			.find(|o| !o.is_eq())
			.unwrap_or(std::cmp::Ordering::Equal)
	});
	Ok(duplicates)
}
