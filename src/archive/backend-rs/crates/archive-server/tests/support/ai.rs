// Delivery/storage stand-ins for the frozen pure ledger/gateway siblings only.
#![allow(dead_code, clippy::unwrap_used, clippy::expect_used, clippy::panic)]
use archive_server::{ai::*, auth::Clock};
use chrono::{DateTime, TimeZone, Utc};
use futures::future::BoxFuture;
use std::{
	collections::BTreeMap,
	sync::{
		Arc, Mutex,
		atomic::{AtomicBool, AtomicUsize, Ordering},
	},
};
use uuid::Uuid;

type MemoryRow = (String, Uuid, Uuid, Option<Uuid>, MonthEntry);
#[derive(Default)]
pub struct MemoryStore {
	pub rows: tokio::sync::Mutex<Vec<MemoryRow>>,
	watermarks: tokio::sync::Mutex<BTreeMap<String, i64>>,
	pub broken: AtomicBool,
	pub fail_settlements: AtomicUsize,
	pub attempts: AtomicUsize,
	pub missing: AtomicBool,
	pub hanging_settlement: AtomicBool,
}
impl LedgerStore for MemoryStore {
	fn reserve(&self, r: StoreReservation) -> BoxFuture<'_, Result<Decision, Error>> {
		Box::pin(async move {
			if self.broken.load(Ordering::SeqCst) {
				return Err(Error::Unavailable);
			}
			let mut rows = self.rows.lock().await;
			let spent: i64 = rows
				.iter()
				.filter(|(m, ..)| *m == r.month)
				.map(|(_, _, _, _, e)| e.cost_micro_eur + e.reserved_micro_eur)
				.sum();
			if spent + r.reserve > r.cap {
				return Ok(Decision {
					entry_id: None,
					spent,
				});
			}
			let id = if let Some((_, _, id, _, e)) = rows.iter_mut().find(|(m, op, _, _, e)| {
				*m == r.month && *op == r.call.operation_id && e.model == r.call.model
			}) {
				e.calls += 1;
				e.reserved_micro_eur += r.reserve;
				*id
			} else {
				let id = Uuid::now_v7();
				rows.push((
					r.month,
					r.call.operation_id,
					id,
					r.call.account_id,
					MonthEntry {
						feature: r.call.feature,
						model: r.call.model,
						calls: 1,
						input_tokens: 0,
						output_tokens: 0,
						cost_micro_eur: 0,
						reserved_micro_eur: r.reserve,
					},
				));
				id
			};
			Ok(Decision {
				entry_id: Some(id),
				spent: spent + r.reserve,
			})
		})
	}
	fn settle(&self, s: &Settlement) -> BoxFuture<'_, Result<bool, Error>> {
		let (id, release, input, output, cost) = (s.entry_id, s.release, s.input, s.output, s.cost);
		Box::pin(async move {
			let attempt = self.attempts.fetch_add(1, Ordering::SeqCst);
			if self.hanging_settlement.load(Ordering::SeqCst) {
				std::future::pending::<()>().await;
			}
			if attempt < self.fail_settlements.load(Ordering::SeqCst) {
				return Err(Error::Unavailable);
			}
			if self.missing.load(Ordering::SeqCst) {
				return Ok(false);
			}
			let mut rows = self.rows.lock().await;
			let Some((_, _, _, _, e)) = rows.iter_mut().find(|(_, _, entry, ..)| *entry == id)
			else {
				return Ok(false);
			};
			e.reserved_micro_eur = (e.reserved_micro_eur - release).max(0);
			e.input_tokens += input;
			e.output_tokens += output;
			e.cost_micro_eur += cost;
			Ok(true)
		})
	}
	fn spent<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<i64, Error>> {
		Box::pin(async move {
			if self.broken.load(Ordering::SeqCst) {
				return Err(Error::Unavailable);
			}
			Ok(self
				.rows
				.lock()
				.await
				.iter()
				.filter(|(m, ..)| m == month)
				.map(|(_, _, _, _, e)| e.cost_micro_eur + e.reserved_micro_eur)
				.sum())
		})
	}
	fn entries<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<Vec<MonthEntry>, Error>> {
		Box::pin(async move {
			Ok(self
				.rows
				.lock()
				.await
				.iter()
				.filter(|(m, ..)| m == month)
				.map(|(_, _, _, _, e)| e.clone())
				.collect())
		})
	}
	fn notified<'a>(&'a self, month: &'a str) -> BoxFuture<'a, Result<i64, Error>> {
		Box::pin(async move { Ok(*self.watermarks.lock().await.get(month).unwrap_or(&0)) })
	}
	fn notify<'a>(&'a self, month: &'a str, level: i64) -> BoxFuture<'a, Result<(), Error>> {
		Box::pin(async move {
			let mut w = self.watermarks.lock().await;
			let entry = w.entry(month.into()).or_default();
			*entry = (*entry).max(level);
			Ok(())
		})
	}
}
pub struct Time(pub Mutex<DateTime<Utc>>);
impl Clock for Time {
	fn now(&self) -> DateTime<Utc> {
		*self.0.lock().unwrap()
	}
}
pub struct Harness {
	pub store: Arc<MemoryStore>,
	pub ledger: Ledger,
	pub options: tokio::sync::watch::Sender<Options>,
	pub clock: Arc<Time>,
}
impl Harness {
	pub fn new() -> Self {
		let mut options = Options::default();
		options
			.models
			.insert("scripted".into(), Price::new("1", "4").unwrap());
		let (options, rx) = tokio::sync::watch::channel(options);
		let store = Arc::new(MemoryStore::default());
		let clock = Arc::new(Time(Mutex::new(
			Utc.with_ymd_and_hms(2026, 10, 6, 12, 0, 0).unwrap(),
		)));
		let ledger = Ledger::new(store.clone(), rx, clock.clone());
		Self {
			store,
			ledger,
			options,
			clock,
		}
	}
	pub fn request(&self) -> Request {
		let mut r = Request::new(vec![Message::text("user", "Frage")]);
		r.operation = Some(Operation::start("chat", None));
		r
	}
	pub fn call(&self) -> CallSpec {
		CallSpec {
			feature: "chat".into(),
			operation_id: Uuid::now_v7(),
			account_id: None,
			model: "scripted".into(),
			estimated_input: 100,
			max_output: 200,
		}
	}
	pub async fn row(&self) -> MonthEntry {
		self.store.rows.lock().await[0].4.clone()
	}
}
