use chrono::{DateTime, Duration, Utc};

/// Real time is replaced only at this external boundary in HTTP tests.
pub trait Clock: Send + Sync {
	fn now(&self) -> DateTime<Utc>;
}
pub struct SystemClock;
impl Clock for SystemClock {
	fn now(&self) -> DateTime<Utc> {
		Utc::now()
	}
}

#[derive(Clone, Debug)]
pub struct AuthConfig {
	pub development: bool,
	pub rp_id: String,
	pub origins: Vec<url::Url>,
	pub fresh_window: Duration,
	pub code_lifetime: Duration,
	pub resend_cooldown: Duration,
	pub max_code_attempts: i32,
	pub max_email_requests: i64,
	pub max_ip_requests: i64,
	pub max_verify_failures: i64,
	pub max_passkeys: i64,
	pub max_passkey_name: usize,
}
impl AuthConfig {
	pub fn development() -> Self {
		Self {
			development: true,
			rp_id: "localhost".into(),
			origins: vec![],
			fresh_window: Duration::minutes(10),
			code_lifetime: Duration::minutes(10),
			resend_cooldown: Duration::seconds(60),
			max_code_attempts: 5,
			max_email_requests: 5,
			max_ip_requests: 30,
			max_verify_failures: 10,
			max_passkeys: 10,
			max_passkey_name: 100,
		}
	}
	pub fn from_env(development: bool) -> Result<Self, super::AuthError> {
		let mut config = Self::development();
		config.development = development;
		config.rp_id = std::env::var("Authentication__PasskeyRelyingPartyId")
			.ok()
			.filter(|s| !s.trim().is_empty())
			.map(|s| s.trim().to_lowercase())
			.or_else(|| development.then(|| "localhost".into()))
			.ok_or(super::AuthError::Configuration)?;
		for i in 0..32 {
			if let Ok(origin) = std::env::var(format!("Authentication__PasskeyOrigins__{i}")) {
				let url = url::Url::parse(origin.trim().trim_end_matches('/'))
					.map_err(|_| super::AuthError::Configuration)?;
				config.origins.push(url);
			}
		}
		// No production wildcard/request-host fallback. Empty allowlist fails closed.
		if !development && config.origins.is_empty() {
			return Err(super::AuthError::Configuration);
		}
		for (name, target) in [
			("CodeLifetime", &mut config.code_lifetime),
			("ResendCooldown", &mut config.resend_cooldown),
			("FreshVerificationWindow", &mut config.fresh_window),
		] {
			if let Ok(value) = std::env::var(format!("Authentication__{name}")) {
				*target = parse_duration(&value)?;
			}
		}
		for (name, target) in [
			("MaxRequestsPerEmailPerHour", &mut config.max_email_requests),
			("MaxRequestsPerIpPerHour", &mut config.max_ip_requests),
			(
				"MaxVerifyFailuresPerEmailPerTenMinutes",
				&mut config.max_verify_failures,
			),
			("PasskeyMaxPerAccount", &mut config.max_passkeys),
		] {
			if let Ok(value) = std::env::var(format!("Authentication__{name}")) {
				*target = value.parse().map_err(|_| super::AuthError::Configuration)?;
			}
		}
		if let Ok(value) = std::env::var("Authentication__MaxVerifyAttemptsPerCode") {
			config.max_code_attempts =
				value.parse().map_err(|_| super::AuthError::Configuration)?;
		}
		if let Ok(value) = std::env::var("Authentication__PasskeyNameMaxLength") {
			config.max_passkey_name = value.parse().map_err(|_| super::AuthError::Configuration)?;
		}
		if std::env::var("Authentication__CodeLength").is_ok_and(|s| s != "6") {
			return Err(super::AuthError::Configuration);
		}
		config.validate()?;
		Ok(config)
	}
	pub(super) fn validate(&self) -> Result<(), super::AuthError> {
		if self.rp_id.is_empty()
			|| self.rp_id.contains(['/', ':', ' '])
			|| (!self.development && self.origins.is_empty())
			|| self.origins.iter().any(|o| {
				o.scheme() != "https" && (!self.development || o.scheme() != "http")
					|| o.path() != "/"
					|| o.query().is_some()
					|| o.fragment().is_some()
					|| !o.username().is_empty()
					|| o.password().is_some()
			}) || self.max_passkeys < 1
			|| self.max_passkeys > 100
			|| self.max_passkey_name == 0
			|| self.max_passkey_name > 100
			|| self.max_code_attempts < 1
			|| self.max_email_requests < 1
			|| self.max_ip_requests < 1
			|| self.max_verify_failures < 1
			|| self.code_lifetime <= Duration::zero()
			|| self.code_lifetime > Duration::hours(1)
			|| self.fresh_window < Duration::zero()
			|| self.resend_cooldown < Duration::zero()
		{
			return Err(super::AuthError::Configuration);
		}
		Ok(())
	}
}

/// The existing environment uses .NET TimeSpan (`[days.]hh:mm:ss[.fraction]`).
pub(super) fn parse_duration(value: &str) -> Result<Duration, super::AuthError> {
	let bad = || super::AuthError::Configuration;
	let parts: Vec<_> = value.trim().split(':').collect();
	if parts.len() != 3 {
		return Err(bad());
	}
	let (days, hours) = parts[0]
		.split_once('.')
		.map_or(("0", parts[0]), |(d, h)| (d, h));
	let days: i64 = days.parse().map_err(|_| bad())?;
	let hours: i64 = hours.parse().map_err(|_| bad())?;
	let minutes: i64 = parts[1].parse().map_err(|_| bad())?;
	let seconds: f64 = parts[2].parse().map_err(|_| bad())?;
	if !(0..=365).contains(&days)
		|| !(0..24).contains(&hours)
		|| !(0..60).contains(&minutes)
		|| !seconds.is_finite()
		|| !(0.0..60.0).contains(&seconds)
	{
		return Err(bad());
	}
	Ok(Duration::milliseconds(
		((days * 86400 + hours * 3600 + minutes * 60) as f64 * 1000.0 + seconds * 1000.0) as i64,
	))
}
