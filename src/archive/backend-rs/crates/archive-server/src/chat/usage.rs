//! AGUI.Server1.0.0 TokenUsageTracker sibling; never the billing ledger.
use crate::ai::{Error, WireUsage};
#[derive(Default)]
pub(super) struct Tracker(Vec<WireUsage>);
impl Tracker {
	pub fn observe(&mut self, mut report: WireUsage) -> Result<(), Error> {
		report.provider = report.provider.filter(|s| !s.trim().is_empty());
		report.model = report.model.filter(|s| !s.trim().is_empty());
		let index = self
			.0
			.iter()
			.position(|u| u.provider == report.provider && u.model == report.model)
			.unwrap_or_else(|| {
				self.0.push(WireUsage {
					provider: report.provider.clone(),
					model: report.model.clone(),
					..Default::default()
				});
				self.0.len() - 1
			});
		let entry = &mut self.0[index];
		for (target, reported) in [
			(&mut entry.input_tokens, report.input_tokens),
			(&mut entry.output_tokens, report.output_tokens),
			(&mut entry.total_tokens, report.total_tokens),
			(&mut entry.reasoning_tokens, report.reasoning_tokens),
			(&mut entry.cached_input_tokens, report.cached_input_tokens),
			(
				&mut entry.cache_write_input_tokens,
				report.cache_write_input_tokens,
			),
		] {
			if let Some(count) = reported {
				let total = target
					.unwrap_or(0)
					.checked_add(count)
					.ok_or(Error::Protocol)?;
				// Locked client requires nonnegative JS-safe integers. Invalid
				// metadata fails the run; never clamp/fabricate valid wire counts.
				if count < 0 || total > 9_007_199_254_740_991 {
					return Err(Error::Protocol);
				}
				*target = Some(total);
			}
		}
		Ok(())
	}
	pub fn json(&self) -> Option<serde_json::Value> {
		(!self.0.is_empty()).then(|| serde_json::json!(self.0))
	}
}
