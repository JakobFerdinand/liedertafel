use super::{
	Error, Ledger, MonthEntry, month_key,
	options::{eur, micro_eur},
};
use axum::{
	Json,
	extract::State,
	http::{HeaderMap, StatusCode},
	response::{IntoResponse, Response},
	routing::{MethodRouter, get},
};
use rust_decimal::{Decimal, RoundingStrategy};
use serde::Serialize;
use std::sync::Arc;

/// Frozen decimal.ToString("0.00", InvariantCulture), including midpoint-away
/// rounding and unsigned formatted zero. This affects notices, not ledger money.
fn format_eur(amount: Decimal) -> String {
	let rounded = amount.round_dp_with_strategy(2, RoundingStrategy::MidpointAwayFromZero);
	if rounded.is_zero() {
		"0.00".into()
	} else {
		format!("{rounded:.2}")
	}
}

pub struct CostState {
	pub month: String,
	pub cap_eur: Decimal,
	pub thresholds_eur: Vec<Decimal>,
	pub spent_micro_eur: i64,
	pub notified_micro_eur: i64,
	pub entries: Vec<MonthEntry>,
}
#[derive(Clone)]
pub struct Notices {
	ledger: Ledger,
}
impl Notices {
	pub fn new(ledger: Ledger) -> Self {
		Self { ledger }
	}
	pub async fn state(&self) -> Result<CostState, Error> {
		let month = month_key(self.ledger.clock.now());
		let entries = self.ledger.store.entries(&month).await?;
		let spent = entries
			.iter()
			.try_fold(0_i64, |s, e| {
				s.checked_add(e.cost_micro_eur)?
					.checked_add(e.reserved_micro_eur)
			})
			.ok_or(Error::MoneyOverflow)?;
		let notified = self.ledger.store.notified(&month).await?;
		let options = self.ledger.options.borrow().clone();
		Ok(CostState {
			month,
			cap_eur: options.monthly_cap_eur,
			thresholds_eur: options.levels(),
			spent_micro_eur: spent,
			notified_micro_eur: notified,
			entries,
		})
	}
	/// Sequential watermark, NOT a cross-replica exactly-once log claim.
	pub async fn review(&self) -> Result<CostState, Error> {
		let mut state = self.state().await?;
		let mut target = None;
		for level in &state.thresholds_eur {
			let micro = micro_eur(*level)?;
			if state.spent_micro_eur >= micro && state.notified_micro_eur < micro {
				target = Some((*level, micro));
			}
		}
		if let Some((level, micro)) = target {
			if level == state.cap_eur {
				tracing::warn!(
					"KI-Budget: Monatsbudget erreicht ({}: {} von {} EUR)",
					state.month,
					format_eur(eur(state.spent_micro_eur)),
					format_eur(state.cap_eur)
				);
			} else {
				tracing::warn!(
					"KI-Budget: Schwellwert {} EUR im Monat {} erreicht ({} von {} EUR): {}",
					format_eur(level),
					state.month,
					format_eur(eur(state.spent_micro_eur)),
					format_eur(state.cap_eur),
					Self::breakdown(&state.entries)
				);
			}
			self.ledger.store.notify(&state.month, micro).await?;
			state.notified_micro_eur = state.notified_micro_eur.max(micro);
		}
		Ok(state)
	}
	pub fn breakdown(entries: &[MonthEntry]) -> String {
		entries
			.iter()
			.map(|e| {
				format!(
					"{} {} EUR",
					e.feature,
					format_eur(eur(e.cost_micro_eur) + eur(e.reserved_micro_eur))
				)
			})
			.collect::<Vec<_>>()
			.join("; ")
	}
}
/// Serialize final EUR decimals as JSON numbers without an f64 money conversion.
struct DecimalNumber(Decimal);
impl Serialize for DecimalNumber {
	fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
		serde_json::value::RawValue::from_string(self.0.to_string())
			.map_err(serde::ser::Error::custom)?
			.serialize(serializer)
	}
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct BudgetResponse {
	month: String,
	cap_eur: DecimalNumber,
	thresholds_eur: Vec<DecimalNumber>,
	spent_eur: DecimalNumber,
	notified_eur: DecimalNumber,
	features: Vec<FeatureResponse>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FeatureResponse {
	feature: String,
	model: String,
	calls: i64,
	input_tokens: i64,
	output_tokens: i64,
	spent_eur: DecimalNumber,
}
impl CostState {
	fn response(self) -> Response {
		Json(BudgetResponse {
			month: self.month,
			cap_eur: DecimalNumber(self.cap_eur),
			thresholds_eur: self.thresholds_eur.into_iter().map(DecimalNumber).collect(),
			spent_eur: DecimalNumber(
				eur(self.spent_micro_eur)
					.round_dp_with_strategy(4, RoundingStrategy::MidpointAwayFromZero),
			),
			notified_eur: DecimalNumber(eur(self.notified_micro_eur)),
			features: self
				.entries
				.into_iter()
				.map(|e| FeatureResponse {
					spent_eur: DecimalNumber(eur(e.cost_micro_eur) + eur(e.reserved_micro_eur)),
					feature: e.feature,
					model: e.model,
					calls: e.calls,
					input_tokens: e.input_tokens,
					output_tokens: e.output_tokens,
				})
				.collect(),
		})
		.into_response()
	}
}
pub fn routes() -> Vec<(&'static str, MethodRouter<Arc<crate::Origin>>)> {
	vec![(
		"/api/maintenance/ai-budget",
		get(budget).fallback(crate::api::api_miss),
	)]
}
async fn budget(State(origin): State<Arc<crate::Origin>>, headers: HeaderMap) -> Response {
	let Some(auth) = &origin.auth else {
		return crate::api::problem(StatusCode::UNAUTHORIZED, crate::auth::UNAUTHORIZED);
	};
	let access = match auth
		.authorize(&headers, crate::auth::AccessPolicy::Editor, false)
		.await
	{
		Ok(access) => access,
		Err(e) => return e.into_response(),
	};
	let response = if let Some(ai) = &origin.ai {
		match Notices::new(ai.ledger.clone()).state().await {
			Ok(state) => state.response(),
			Err(_) => crate::api::problem(
				StatusCode::INTERNAL_SERVER_ERROR,
				"Das KI-Verbrauchsbuch ist nicht verfügbar.",
			),
		}
	} else {
		crate::api::problem(
			StatusCode::SERVICE_UNAVAILABLE,
			"Das KI-Verbrauchsbuch ist nicht verfügbar.",
		)
	};
	access.respond(response)
}
