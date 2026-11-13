//! Literal frozen PostgreSQL ledger. Month rows are locks/watermarks, not balances.
use super::{Error, Options, cost_micro_eur, month_key};
use chrono::{DateTime, Utc};
use futures::future::BoxFuture;
use opentelemetry::{KeyValue, global};
use sqlx::{PgPool, Row};
use std::{sync::Arc, time::Duration};
use tokio::sync::watch;
use uuid::Uuid;

#[derive(Clone, Debug)]
pub struct CallSpec {
	pub feature: String,
	pub operation_id: Uuid,
	pub account_id: Option<Uuid>,
	pub model: String,
	pub estimated_input: i64,
	pub max_output: i32,
}
/// Move-only: one settlement per admitted call. SQL retries themselves are NOT
/// idempotent after an unknown commit acknowledgement (same as frozen source).
#[derive(Debug)]
pub struct Reservation {
	pub entry_id: Uuid,
	pub month: String,
	pub call: CallSpec,
	pub reserved_micro_eur: i64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Usage {
	pub reported: bool,
	pub input: i64,
	pub output: i64,
}
impl Usage {
	pub fn reported(input: i64, output: i64) -> Self {
		Self {
			reported: true,
			input: input.max(0),
			output: output.max(0),
		}
	}
	pub fn observe(&mut self, other: Self) {
		self.reported |= other.reported;
		self.input = self.input.max(other.input);
		self.output = self.output.max(other.output);
	}
}
#[derive(Clone, Debug, sqlx::FromRow)]
pub struct MonthEntry {
	pub feature: String,
	pub model: String,
	pub calls: i64,
	pub input_tokens: i64,
	pub output_tokens: i64,
	pub cost_micro_eur: i64,
	pub reserved_micro_eur: i64,
}
#[derive(Debug)]
pub struct Status {
	pub month: String,
	pub spent_micro_eur: i64,
	pub cap_micro_eur: i64,
}

pub struct StoreReservation {
	pub month: String,
	pub call: CallSpec,
	pub reserve: i64,
	pub cap: i64,
	pub now: DateTime<Utc>,
}
pub struct Decision {
	pub entry_id: Option<Uuid>,
	pub spent: i64,
}
pub struct Settlement {
	pub entry_id: Uuid,
	pub release: i64,
	pub input: i64,
	pub output: i64,
	pub cost: i64,
	pub now: DateTime<Utc>,
}
/// External storage seam retained for offline pure ledger siblings; production
/// always selects PostgresStore. SQL errors are deliberately redacted here.
pub trait LedgerStore: Send + Sync {
	fn reserve(&self, request: StoreReservation) -> BoxFuture<'_, Result<Decision, Error>>;
	fn settle(&self, settlement: &Settlement) -> BoxFuture<'_, Result<bool, Error>>;
	fn spent<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<i64, Error>>;
	fn entries<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<Vec<MonthEntry>, Error>>;
	fn notified<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<i64, Error>>;
	fn notify<'a>(&'a self, month: &'a str, level: i64) -> BoxFuture<'a, Result<(), Error>>;
}
#[derive(Clone)]
pub struct PostgresStore {
	pool: PgPool,
}
impl PostgresStore {
	pub fn new(pool: PgPool) -> Self {
		Self { pool }
	}
}
impl From<sqlx::Error> for Error {
	fn from(_: sqlx::Error) -> Self {
		Self::Unavailable
	}
}
const SPENT: &str = r#"SELECT COALESCE(SUM("CostMicroEur" + "ReservedMicroEur"), 0)::bigint FROM ai_usage_entries WHERE "YearMonth" = $1"#;
impl LedgerStore for PostgresStore {
	fn reserve(&self, r: StoreReservation) -> BoxFuture<'_, Result<Decision, Error>> {
		Box::pin(async move {
			let mut tx = self.pool.begin().await?;
			sqlx::query("SET LOCAL lock_timeout = '5s'")
				.execute(&mut *tx)
				.await?;
			sqlx::query(
				r#"INSERT INTO ai_budget_months ("YearMonth") VALUES ($1) ON CONFLICT DO NOTHING"#,
			)
			.bind(&r.month)
			.execute(&mut *tx)
			.await?;
			sqlx::query(r#"SELECT 1 FROM ai_budget_months WHERE "YearMonth" = $1 FOR UPDATE"#)
				.bind(&r.month)
				.execute(&mut *tx)
				.await?;
			let spent: i64 = sqlx::query_scalar(SPENT)
				.bind(&r.month)
				.fetch_one(&mut *tx)
				.await?;
			let total = spent.checked_add(r.reserve).ok_or(Error::MoneyOverflow)?;
			if total > r.cap {
				tx.rollback().await?;
				return Ok(Decision {
					entry_id: None,
					spent,
				});
			}
			let id = sqlx::query_scalar(
				r#"
INSERT INTO ai_usage_entries
 ("Id", "YearMonth", "Feature", "Model", "OperationId", "AccountId", "Calls",
  "InputTokens", "OutputTokens", "CostMicroEur", "ReservedMicroEur", "CreatedAt", "UpdatedAt")
VALUES ($1,$2,$3,$4,$5,$6,1,0,0,0,$7,$8,$8)
ON CONFLICT ("OperationId", "Model", "YearMonth") DO UPDATE SET
 "Calls" = ai_usage_entries."Calls" + 1,
 "ReservedMicroEur" = ai_usage_entries."ReservedMicroEur" + EXCLUDED."ReservedMicroEur",
 "UpdatedAt" = EXCLUDED."UpdatedAt"
RETURNING "Id""#,
			)
			.bind(Uuid::now_v7())
			.bind(&r.month)
			.bind(&r.call.feature)
			.bind(&r.call.model)
			.bind(r.call.operation_id)
			.bind(r.call.account_id)
			.bind(r.reserve)
			.bind(r.now)
			.fetch_one(&mut *tx)
			.await?;
			tx.commit().await?;
			Ok(Decision {
				entry_id: Some(id),
				spent: total,
			})
		})
	}
	fn settle(&self, s: &Settlement) -> BoxFuture<'_, Result<bool, Error>> {
		// Own the arguments; the returned future is tied only to the store.
		let (id, release, input, output, cost, now) =
			(s.entry_id, s.release, s.input, s.output, s.cost, s.now);
		Box::pin(async move {
			Ok(sqlx::query(
				r#"UPDATE ai_usage_entries SET
 "ReservedMicroEur" = GREATEST(0, "ReservedMicroEur" - $1),
 "InputTokens" = "InputTokens" + $2, "OutputTokens" = "OutputTokens" + $3,
 "CostMicroEur" = "CostMicroEur" + $4, "UpdatedAt" = $5 WHERE "Id" = $6"#,
			)
			.bind(release)
			.bind(input)
			.bind(output)
			.bind(cost)
			.bind(now)
			.bind(id)
			.execute(&self.pool)
			.await?
			.rows_affected()
				== 1)
		})
	}
	fn spent<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<i64, Error>> {
		Box::pin(async move {
			Ok(sqlx::query_scalar(SPENT)
				.bind(month)
				.fetch_one(&self.pool)
				.await?)
		})
	}
	fn entries<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<Vec<MonthEntry>, Error>> {
		Box::pin(async move {
			let rows = sqlx::query(
				r#"SELECT "Feature", "Model", SUM("Calls")::bigint AS calls,
 SUM("InputTokens")::bigint AS input, SUM("OutputTokens")::bigint AS output,
 SUM("CostMicroEur")::bigint AS cost, SUM("ReservedMicroEur")::bigint AS reserved
 FROM ai_usage_entries WHERE "YearMonth" = $1 GROUP BY "Feature", "Model""#,
			)
			.bind(month)
			.fetch_all(&self.pool)
			.await?;
			let mut result: Vec<_> = rows
				.into_iter()
				.map(|r| {
					Ok(MonthEntry {
						feature: r.try_get("Feature")?,
						model: r.try_get("Model")?,
						calls: r.try_get("calls")?,
						input_tokens: r.try_get("input")?,
						output_tokens: r.try_get("output")?,
						cost_micro_eur: r.try_get("cost")?,
						reserved_micro_eur: r.try_get("reserved")?,
					})
				})
				.collect::<Result<_, sqlx::Error>>()?;
			result.sort_by(|a, b| (&a.feature, &a.model).cmp(&(&b.feature, &b.model)));
			Ok(result)
		})
	}
	fn notified<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<i64, Error>> {
		Box::pin(async move {
			Ok(sqlx::query_scalar(
				r#"SELECT "NotifiedMicroEur" FROM ai_budget_months WHERE "YearMonth" = $1"#,
			)
			.bind(month)
			.fetch_optional(&self.pool)
			.await?
			.unwrap_or(0))
		})
	}
	fn notify<'a>(&'a self, month: &'a str, level: i64) -> BoxFuture<'a, Result<(), Error>> {
		Box::pin(async move {
			sqlx::query(r#"INSERT INTO ai_budget_months ("YearMonth", "NotifiedMicroEur") VALUES ($1,$2)
 ON CONFLICT ("YearMonth") DO UPDATE SET "NotifiedMicroEur" = GREATEST(ai_budget_months."NotifiedMicroEur", EXCLUDED."NotifiedMicroEur")
 WHERE ai_budget_months."NotifiedMicroEur" < EXCLUDED."NotifiedMicroEur""#).bind(month).bind(level).execute(&self.pool).await?;
			Ok(())
		})
	}
}

#[derive(Clone)]
pub struct Ledger {
	pub(super) store: Arc<dyn LedgerStore>,
	pub(super) options: watch::Receiver<Options>,
	pub(super) clock: Arc<dyn crate::auth::Clock>,
}
impl Ledger {
	pub fn new(
		store: Arc<dyn LedgerStore>,
		options: watch::Receiver<Options>,
		clock: Arc<dyn crate::auth::Clock>,
	) -> Self {
		Self {
			store,
			options,
			clock,
		}
	}
	pub async fn reserve(&self, call: CallSpec) -> Result<Reservation, Error> {
		let options = self.options.borrow().clone();
		let meter = global::meter(archive_telemetry::AI_SOURCE);
		let result = async {
			let price = options.find_price(&call.model).ok_or(Error::Unavailable)?;
			let reserve = cost_micro_eur(price, call.estimated_input, i64::from(call.max_output))?;
			let cap = options.cap_micro_eur()?;
			let month = month_key(self.clock.now());
			let decision = self
				.store
				.reserve(StoreReservation {
					month: month.clone(),
					call: call.clone(),
					reserve,
					cap,
					now: self.clock.now(),
				})
				.await?;
			meter
				.f64_gauge("archive.ai.budget.month_spend")
				.with_unit("EUR")
				.build()
				.record(
					super::options::eur(decision.spent)
						.to_string()
						.parse()
						.unwrap_or(0.0),
					&[],
				);
			meter
				.f64_gauge("archive.ai.budget.month_cap")
				.with_unit("EUR")
				.build()
				.record(
					super::options::eur(cap).to_string().parse().unwrap_or(0.0),
					&[],
				);
			let entry_id = decision
				.entry_id
				.ok_or_else(|| Error::BudgetExceeded(month.clone()))?;
			Ok(Reservation {
				entry_id,
				month,
				call: call.clone(),
				reserved_micro_eur: reserve,
			})
		}
		.await;
		if let Err(error) = &result {
			let reason = if matches!(error, Error::BudgetExceeded(_)) {
				"cap"
			} else if options.find_price(&call.model).is_none() {
				"unpriced_model"
			} else {
				"ledger_unavailable"
			};
			meter
				.u64_counter("archive.ai.budget.refusals")
				.with_unit("{call}")
				.build()
				.add(
					1,
					&[
						KeyValue::new("archive.ai.feature", call.feature),
						KeyValue::new("gen_ai.request.model", call.model),
						KeyValue::new("archive.ai.refusal.reason", reason),
					],
				);
			tracing::warn!(reason, "KI-Budget: Aufruf abgelehnt.");
		}
		result
	}
	/// No request cancellation is accepted here. Four finite attempts; a failed
	/// write leaves the durable conservative reservation, never a TTL refund.
	pub async fn settle(&self, r: Reservation, usage: Usage) -> bool {
		let price = self.options.borrow().find_price(&r.call.model).cloned();
		let amount = if usage.reported {
			price.map(|p| cost_micro_eur(&p, usage.input, usage.output))
		} else {
			None
		};
		let (input, output, cost) = match amount {
			Some(Ok(cost)) => (usage.input.max(0), usage.output.max(0), cost),
			Some(Err(_)) => {
				self.settlement_failure(&r, "MoneyOverflow");
				return false;
			}
			None => (
				r.call.estimated_input.max(0),
				i64::from(r.call.max_output.max(0)),
				r.reserved_micro_eur,
			),
		};
		let settlement = Settlement {
			entry_id: r.entry_id,
			release: r.reserved_micro_eur,
			input,
			output,
			cost,
			now: self.clock.now(),
		};
		for delay in [Some(100), Some(400), Some(1600), None] {
			// Keep a finite host inside its existing 30s shutdown budget even
			// when the connection/row stalls. Timeout is an unavailable write,
			// never a refund; acknowledgement ambiguity remains documented.
			match tokio::time::timeout(Duration::from_secs(5), self.store.settle(&settlement))
				.await
				.unwrap_or(Err(Error::Unavailable))
			{
				Ok(true) => {
					let meter = global::meter(archive_telemetry::AI_SOURCE);
					let tags = [
						KeyValue::new("archive.ai.feature", r.call.feature.clone()),
						KeyValue::new("gen_ai.request.model", r.call.model.clone()),
					];
					for (kind, value) in [("input", input), ("output", output)] {
						meter
							.u64_counter("archive.ai.tokens")
							.with_unit("{token}")
							.build()
							.add(
								value as u64,
								&[
									tags[0].clone(),
									tags[1].clone(),
									KeyValue::new("gen_ai.token.type", kind),
								],
							);
					}
					meter
						.f64_counter("archive.ai.cost")
						.with_unit("EUR")
						.build()
						.add(
							super::options::eur(cost).to_string().parse().unwrap_or(0.0),
							&tags,
						);
					tracing::info!(feature = %r.call.feature, model = %r.call.model, input, output, cost_micro_eur = cost, month = %r.month, reported = usage.reported, "KI-Budget: Verbrauch verbucht.");
					return true;
				}
				Ok(false) => {
					self.settlement_failure(&r, "RowMissing");
					return false;
				}
				Err(_) => {
					if let Some(delay) = delay {
						tokio::time::sleep(Duration::from_millis(delay)).await;
					}
				}
			}
		}
		self.settlement_failure(&r, "Unavailable");
		false
	}
	fn settlement_failure(&self, r: &Reservation, failure: &str) {
		global::meter(archive_telemetry::AI_SOURCE)
			.u64_counter("archive.ai.budget.settlement_failures")
			.with_unit("{call}")
			.build()
			.add(
				1,
				&[
					KeyValue::new("archive.ai.feature", r.call.feature.clone()),
					KeyValue::new("gen_ai.request.model", r.call.model.clone()),
				],
			);
		tracing::error!(feature = %r.call.feature, failure, reserved_micro_eur = r.reserved_micro_eur, "KI-Budget: Verbrauch konnte nicht verbucht werden; die Reservierung bleibt bestehen.");
	}
	pub async fn status(&self) -> Result<Status, Error> {
		let month = month_key(self.clock.now());
		Ok(Status {
			spent_micro_eur: self.store.spent(&month).await?,
			cap_micro_eur: self.options.borrow().cap_micro_eur()?,
			month,
		})
	}
	pub async fn would_admit(&self, call: &CallSpec) -> bool {
		let options = self.options.borrow().clone();
		let Some(price) = options.find_price(&call.model) else {
			return false;
		};
		let Ok(reserve) = cost_micro_eur(price, call.estimated_input, i64::from(call.max_output))
		else {
			return false;
		};
		let Ok(cap) = options.cap_micro_eur() else {
			return false;
		};
		self.store
			.spent(&month_key(self.clock.now()))
			.await
			.ok()
			.and_then(|v| v.checked_add(reserve))
			.is_some_and(|v| v <= cap)
	}
	pub async fn month_entries(&self, month: &str) -> Result<Vec<MonthEntry>, Error> {
		self.store.entries(month).await
	}
}
