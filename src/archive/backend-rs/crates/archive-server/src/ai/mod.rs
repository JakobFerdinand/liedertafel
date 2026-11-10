//! The sole provider boundary. Money and attribution never cross into the wire.
mod azure;
mod gateway;
mod ledger;
mod live_gate;
mod model;
mod notice;
mod options;
mod script;
pub use azure::AzureConfig;
pub use gateway::{Call, Gateway};
pub use live_gate::LiveEvaluation;
pub use model::*;
pub use notice::{CostState, Notices, routes};
pub use script::{Script, ScriptStep};

#[derive(Clone)]
pub struct Services {
	pub ledger: Ledger,
	pub gateway: Gateway,
}
impl Services {
	/// Every future model-using finite host uses this same registration and must
	/// await gateway.drain before its checked telemetry flush.
	pub fn from_env(
		pool: sqlx::PgPool,
		clock: std::sync::Arc<dyn crate::auth::Clock>,
		development: bool,
	) -> Result<Self, Error> {
		Self::from_env_with_script(pool, clock, development, Script::new(Vec::new()))
	}
	/// Domain-specific delivery stays below this exact gateway/SQL registration.
	pub fn from_env_with_script(
		pool: sqlx::PgPool,
		clock: std::sync::Arc<dyn crate::auth::Clock>,
		development: bool,
		script: Script,
	) -> Result<Self, Error> {
		let (_, options) = tokio::sync::watch::channel(Options::from_env(development)?);
		let ledger = Ledger::new(
			std::sync::Arc::new(PostgresStore::new(pool)),
			options,
			clock,
		);
		let gateway = if let Some(config) = AzureConfig::selected_from_env() {
			Gateway::azure(ledger.clone(), config)?
		} else {
			Gateway::scripted(ledger.clone(), script)
		};
		Ok(Self { ledger, gateway })
	}
}
pub use ledger::{
	CallSpec, Decision, Ledger, LedgerStore, MonthEntry, PostgresStore, Reservation, Settlement,
	Status, StoreReservation, Usage,
};
pub use options::{Options, Price, cost_micro_eur, month_key};

/// Errors never retain provider bodies, URLs, prompts or credential errors.
#[derive(Debug, thiserror::Error)]
pub enum Error {
	#[error("Ungültige KI-Konfiguration.")]
	Configuration,
	#[error("KI-Preis außerhalb des Zahlenbereichs.")]
	MoneyOverflow,
	#[error("Das KI-Verbrauchsbuch ist nicht verfügbar.")]
	Unavailable,
	#[error("Monatsbudget erreicht ({0}).")]
	BudgetExceeded(String),
	#[error("KI-Eingabe zu groß ({estimate}, Grenze {bound}).")]
	InputTooLarge { estimate: i64, bound: i64 },
	#[error("KI-Aufruf abgebrochen.")]
	Cancelled,
	#[error("KI-Anbieter nicht verfügbar.")]
	Provider,
	#[error("Ungültige KI-Anbieterantwort.")]
	Protocol,
	#[error("KI-Aufruf durch Inhaltsfilter abgelehnt.")]
	ContentFilter,
	#[error("KI-Gateway wird beendet.")]
	Closed,
}
