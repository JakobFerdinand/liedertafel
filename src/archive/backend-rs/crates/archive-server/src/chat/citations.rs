use crate::catalogue::search::fold;
use serde_json::{Value, json};

#[derive(Default)]
pub(super) struct Candidates(Vec<(String, String)>);
impl Candidates {
	pub fn collect(&mut self, result: &str) {
		let Ok(value) = serde_json::from_str::<Value>(result) else {
			return;
		};
		if let Some(lieder) = value["lieder"].as_array() {
			for lied in lieder {
				if let (Some(id), Some(title)) = (
					lied["id"].as_str().filter(|s| !s.is_empty()),
					lied["title"].as_str().filter(|s| !s.is_empty()),
				) {
					self.0.push((id.into(), title.into()));
				}
			}
		}
	}
	fn permits(&self, title: &str) -> bool {
		let title = fold(Some(title));
		self.0.iter().any(|(_, t)| fold(Some(t)) == title)
	}
	pub fn chips(&self, answer: &str) -> Vec<Value> {
		let answer = fold(Some(answer));
		let mut ids = std::collections::HashSet::new();
		self.0
			.iter()
			.filter(|(id, title)| {
				!ids.contains(id) && answer.contains(&fold(Some(&format!("[Quelle: {title}]"))))
			})
			.cloned()
			.collect::<Vec<_>>()
			.into_iter()
			.filter_map(|(id, title)| {
				ids.insert(id.clone())
					.then(|| json!({"id":id,"label":title}))
			})
			.collect()
	}
}
#[derive(Default)]
pub(super) struct Filter {
	pending: String,
}
const PREFIX: &str = "[Quelle:";
impl Filter {
	pub fn append(&mut self, text: &str, candidates: &Candidates) -> String {
		self.pending.push_str(text);
		let mut out = String::new();
		while !self.pending.is_empty() {
			let Some(start) = self.pending.to_ascii_lowercase().find("[quelle:") else {
				let held = (1..PREFIX.len())
					.rev()
					.find(|&n| {
						self.pending.len() >= n
							&& self.pending.is_char_boundary(self.pending.len() - n)
							&& PREFIX[..n]
								.eq_ignore_ascii_case(&self.pending[self.pending.len() - n..])
					})
					.unwrap_or(0);
				out.push_str(&self.pending[..self.pending.len() - held]);
				self.pending = self.pending[self.pending.len() - held..].into();
				break;
			};
			out.push_str(&self.pending[..start]);
			self.pending = self.pending[start..].into();
			let mut depth = 1;
			let mut end = None;
			for (index, c) in self.pending.char_indices().skip(PREFIX.len()) {
				if c == '[' {
					depth += 1;
				} else if c == ']' {
					depth -= 1;
					if depth == 0 {
						end = Some(index);
						break;
					}
				}
			}
			let Some(end) = end else {
				break;
			};
			let title = self.pending[PREFIX.len()..end].trim();
			if candidates.permits(title) {
				out.push_str("[Quelle: ");
				out.push_str(title);
				out.push(']');
			}
			self.pending = self.pending[end + 1..].into();
		}
		out
	}
	pub fn complete(self) -> String {
		if self.pending.to_ascii_lowercase().starts_with("[quelle:") {
			String::new()
		} else {
			self.pending
		}
	}
}
