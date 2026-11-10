//! Deterministic ScoreTextAnalyzer sibling. Never writes catalogue fields.
use regex::Regex;
use serde::Serialize;
use std::sync::OnceLock;
#[derive(Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Facts {
	pub voice: Option<String>,
	pub musical_key: Option<String>,
	pub time_signature: Option<String>,
	pub tempo: Option<String>,
	pub composer: Option<String>,
	pub lyricist: Option<String>,
	pub arranger: Option<String>,
	pub copyright: Option<String>,
}
impl Facts {
	pub fn is_empty(&self) -> bool {
		[
			&self.voice,
			&self.musical_key,
			&self.time_signature,
			&self.tempo,
			&self.composer,
			&self.lyricist,
			&self.arranger,
			&self.copyright,
		]
		.iter()
		.all(|v| v.is_none())
	}
}
pub struct Analysis {
	pub clean_text: String,
	pub facts: Facts,
}
fn regex(pattern: &str) -> Option<Regex> {
	Regex::new(pattern).ok()
}
fn bound(s: &str, n: usize) -> String {
	let mut units = 0;
	s.chars()
		.take_while(|c| {
			units += c.len_utf16();
			units <= n
		})
		.collect::<String>()
		.trim_end()
		.into()
}
fn hyphen(c: char) -> bool {
	matches!(c, '-' | '‐' | '‑' | '–')
}
fn plain(c: char) -> bool {
	c.is_ascii_alphabetic() || "ÄÖÜäöüß".contains(c)
}
// Frozen Regex predicates inspect UTF-16 code units, not Unicode word chars.
// A supplementary scalar's adjacent surrogate is neither Letter nor Nd.
fn category(c: char, digit: bool) -> bool {
	static LETTER: OnceLock<Option<Regex>> = OnceLock::new();
	static DIGIT: OnceLock<Option<Regex>> = OnceLock::new();
	let re = if digit {
		DIGIT.get_or_init(|| regex(r"^\p{Nd}$"))
	} else {
		LETTER.get_or_init(|| regex(r"^\p{L}$"))
	};
	c.len_utf16() == 1
		&& re
			.as_ref()
			.is_some_and(|r| r.is_match(c.encode_utf8(&mut [0; 4])))
}
fn letter(c: char) -> bool {
	category(c, false)
}
fn letter_or_digit(c: char) -> bool {
	letter(c) || category(c, true)
}
fn before(s: &str, at: usize, predicate: fn(char) -> bool) -> bool {
	s[..at].chars().next_back().is_some_and(predicate)
}
fn after(s: &str, at: usize, predicate: fn(char) -> bool) -> bool {
	s[at..].chars().next().is_some_and(predicate)
}
fn voice_matches<'a>(re: &Regex, text: &'a str) -> Vec<(usize, usize, &'a str, Option<&'a str>)> {
	let mut found = Vec::new();
	let mut at = 0;
	while let Some(m) = re.captures_at(text, at) {
		let (Some(whole), Some(name)) = (m.get(0), m.name("name")) else {
			break;
		};
		let post = m
			.name("post")
			.filter(|p| !after(text, p.end(), letter_or_digit));
		let end = post.map_or(name.end(), |p| p.end());
		if !before(text, whole.start(), letter_or_digit) && !after(text, end, letter) {
			found.push((
				whole.start(),
				end,
				name.as_str(),
				post.or_else(|| m.name("pre")).map(|n| n.as_str()),
			));
			at = end;
		} else {
			at = whole.start()
				+ text[whole.start()..]
					.chars()
					.next()
					.map_or(1, char::len_utf8);
		}
	}
	found
}
const SIGNATURES: [&str; 12] = [
	"2/2", "3/2", "4/2", "2/4", "3/4", "4/4", "5/4", "6/4", "3/8", "6/8", "9/8", "12/8",
];
pub fn analyze(text: Option<&str>) -> Analysis {
	let text = text.unwrap_or_default();
	let mut facts = Facts::default();
	let lines: Vec<Vec<String>> = text
		.split('\n')
		.map(|l| {
			l.split_whitespace()
				.map(|t| {
					t.chars()
						.filter(|c| {
							!c.is_control() && !('\u{e000}'..='\u{f8ff}').contains(c) && *c != '�'
						})
						.collect::<String>()
				})
				.filter(|t| {
					!t.is_empty()
						&& !t.chars().all(|c| "_‿⁀~".contains(c))
						&& (!t.chars().any(char::is_alphabetic) || t.chars().any(plain))
				})
				.collect()
		})
		.filter(|l: &Vec<String>| !l.is_empty())
		.collect();
	if lines.is_empty() {
		return Analysis {
			clean_text: String::new(),
			facts,
		};
	}
	let tokens: Vec<&str> = lines.iter().flatten().map(String::as_str).collect();
	let head = tokens
		.iter()
		.take(40)
		.copied()
		.collect::<Vec<_>>()
		.join(" ");
	let mut clean = String::new();
	let mut join = false;
	let punctuation = regex(r"^\p{P}+$");
	for token in &tokens {
		if token.chars().all(hyphen) {
			join = !clean.is_empty();
			continue;
		}
		let word = token.trim_matches(hyphen);
		if word.is_empty() {
			continue;
		}
		let joins = !clean.is_empty() && (join || token.starts_with(hyphen));
		if joins && word.starts_with(char::is_lowercase) {
			clean.push_str(word);
		} else if joins {
			clean.push_str(" - ");
			clean.push_str(word);
		} else {
			if !clean.is_empty() && !punctuation.as_ref().is_some_and(|r| r.is_match(word)) {
				clean.push(' ');
			}
			clean.push_str(word);
		}
		join = token.ends_with(hyphen);
	}
	if let Some(re) = regex(
		r"(?:(?<pre>[12]|II?)\.?\s*)?(?<name>Mezzosopran|MEZZOSOPRAN|Soprano?|SOPRAN|Alto?|ALT|Tenore?|TENOR|Bariton|BARITON|Bass|BASS|Baß)(?:\s*(?<post>[12]|II?))?",
	) {
		let voice = |(_, _, name, number): (usize, usize, &str, Option<&str>)| {
			let name = match Some(name.to_lowercase()).as_deref() {
				Some("mezzosopran") => "Mezzosopran",
				Some("sopran" | "soprano") => "Sopran",
				Some("alt" | "alto") => "Alt",
				Some("tenor" | "tenore") => "Tenor",
				Some("bariton") => "Bariton",
				_ => "Bass",
			};
			let number = number.map(|n| {
				match n {
					"I" => "1",
					"II" => "2",
					n => n,
				}
				.to_owned()
			});
			(name.to_owned(), number)
		};
		let mut found: Vec<_> = voice_matches(&re, &head).into_iter().map(&voice).collect();
		for line in lines.iter().filter(|l| l.len() <= 3) {
			let value = line.join(" ");
			if let Some(m) = voice_matches(&re, &value).first().copied()
				&& m.0 == 0 && m.1 == value.trim_end_matches(['.', ':']).len()
			{
				let v = voice(m);
				if v.0 != "Alt" || v.1.is_some() {
					found.push(v);
				}
			}
		}
		found.sort();
		found.dedup();
		let voices: Vec<_> = found
			.iter()
			.filter(|(name, n)| n.is_some() || !found.iter().any(|(o, m)| o == name && m.is_some()))
			.collect();
		facts.voice = match voices.as_slice() {
			[] => None,
			[v] => Some(format!(
				"{}{}",
				v.0,
				v.1.as_ref().map(|n| format!(" {n}")).unwrap_or_default()
			)),
			_ if voices.iter().any(|v| v.0 != voices[0].0) => Some("Partitur".into()),
			_ => Some(format!(
				"{} {}",
				voices[0].0,
				voices
					.iter()
					.filter_map(|v| v.1.as_deref())
					.collect::<Vec<_>>()
					.join("+")
			)),
		};
	}
	if let Some(re) =
		regex(r"([A-Ha-h](?:is|es|s)?|[A-Ga-g][♭♯b#])[- ]?(Dur|dur|Moll|moll|[Mm]ajor|[Mm]inor)")
		&& let Some(m) = re.captures_iter(&head).find(|m| {
			m.get(0).is_some_and(|m| {
				!before(&head, m.start(), letter) && !after(&head, m.end(), letter)
			})
		}) {
		let minor = matches!(&m[2], "Moll" | "moll" | "Minor" | "minor");
		let mut note = m[1].to_owned();
		if note.len() == 2 {
			if note.ends_with('b') {
				note.replace_range(1.., "♭");
			} else if note.ends_with('#') {
				note.replace_range(1.., "♯");
			}
		}
		let mut chars = note.chars();
		if let Some(first) = chars.next() {
			facts.musical_key = Some(format!(
				"{}{}-{}",
				if minor {
					first.to_lowercase().to_string()
				} else {
					first.to_uppercase().to_string()
				},
				chars.collect::<String>(),
				if minor { "Moll" } else { "Dur" }
			));
		}
	}
	if facts.musical_key.is_none() {
		facts.musical_key = tokens
			.iter()
			.take(12)
			.find(|t| {
				let c: Vec<_> = t.chars().collect();
				c.len() == 2 && ('A'..='G').contains(&c[0]) && "♭♯#".contains(c[1])
			})
			.map(|s| s.replace('#', "♯"));
	}
	facts.time_signature = tokens
		.iter()
		.take(40)
		.find(|t| SIGNATURES.contains(t))
		.map(|s| s.to_string());
	if facts.time_signature.is_none() {
		for (i, t) in tokens.iter().take(12).enumerate() {
			if t.bytes().all(|b| b.is_ascii_digit()) {
				let stacked = if matches!(t.len(), 2 | 3) {
					Some(format!("{}/{}", &t[..t.len() - 1], &t[t.len() - 1..]))
				} else {
					None
				};
				let pair = tokens
					.get(..tokens.len().min(12))
					.and_then(|t| t.get(i + 1))
					.filter(|n| n.bytes().all(|b| b.is_ascii_digit()))
					.map(|n| format!("{t}/{n}"));
				facts.time_signature = stacked
					.into_iter()
					.chain(pair)
					.find(|s| SIGNATURES.contains(&s.as_str()));
				if facts.time_signature.is_some() {
					break;
				}
			}
		}
	}
	let tempos = [
		"Adagio",
		"Andante",
		"Andantino",
		"Allegro",
		"Allegretto",
		"Moderato",
		"Largo",
		"Larghetto",
		"Lento",
		"Presto",
		"Vivace",
		"Maestoso",
		"Grave",
		"Sostenuto",
		"Tranquillo",
	];
	let word = tokens
		.iter()
		.take(40)
		.map(|s| s.trim_matches([',', '.', ';', ':', '(', ')']))
		.find(|s| tempos.contains(s));
	let prefix = bound(text, 600);
	let beats = regex(r"=\s*(?:ca\.?\s*)?(\d{2,3})")
		.and_then(|r| {
			r.captures_iter(&prefix)
				.find(|m| {
					m.get(0)
						.is_some_and(|m| !after(&prefix, m.end(), |c| category(c, true)))
				})
				.map(|m| m[1].to_owned())
		})
		.filter(|s| s.parse::<u32>().is_ok_and(|n| (30..=240).contains(&n)))
		.map(|s| format!("{s} Schläge/min"));
	facts.tempo = match (word, beats) {
		(Some(w), Some(b)) => Some(format!("{w}, {b}")),
		(Some(w), None) => Some(w.into()),
		(None, b) => b,
	};
	if let Some(re) = regex(
		r"(?i)(Musik und Text|Text und Musik|Worte und Weise|Text und Melodie|Musik|Melodie|Weise|Komposition|Komponist|Music|Text|Worte|Words|Lyrics|Dichtung|Satz|Chorsatz|Arrangement|Arr\.|Bearbeitung|Bearb\.|Einrichtung)\s*:\s*",
	) {
		for (index, line) in lines.iter().enumerate() {
			let line = line.join(" ");
			if facts.copyright.is_none()
				&& (line.contains('©') || line.to_lowercase().starts_with("copyright"))
			{
				facts.copyright = Some(bound(&line, 160));
			}
			let matches: Vec<_> = re
				.captures_iter(&line)
				.filter(|m| m.get(0).is_some_and(|m| !before(&line, m.start(), letter)))
				.collect();
			for (i, m) in matches.iter().enumerate() {
				let Some(whole) = m.get(0) else {
					continue;
				};
				let end = matches
					.get(i + 1)
					.and_then(|m| m.get(0))
					.map(|m| m.start())
					.unwrap_or(line.len());
				let mut value = line[whole.end()..end]
					.trim_matches([' ', ',', ';', '·', '-', '–', '/'])
					.to_owned();
				if value.is_empty() && i + 1 == matches.len() {
					value = lines
						.get(index + 1)
						.map(|l| l.join(" "))
						.unwrap_or_default();
				}
				if value.is_empty()
					|| re
						.find_iter(&value)
						.any(|m| !before(&value, m.start(), letter))
				{
					continue;
				}
				let value = bound(&value, 120);
				let label = m[1].to_lowercase();
				let both = label.contains(" und ");
				let arranger = matches!(
					label.as_str(),
					"satz"
						| "chorsatz" | "arrangement"
						| "arr." | "bearbeitung"
						| "bearb." | "einrichtung"
				);
				let lyricist = matches!(
					label.as_str(),
					"text" | "worte" | "words" | "lyrics" | "dichtung"
				);
				if arranger {
					facts.arranger.get_or_insert(value.clone());
				} else if both || lyricist {
					facts.lyricist.get_or_insert(value.clone());
				}
				if !arranger && (both || !lyricist) {
					facts.composer.get_or_insert(value);
				}
			}
		}
	}
	Analysis {
		clean_text: clean,
		facts,
	}
}
