use super::{Auth, AuthError};
impl Auth {
	pub async fn seed(&self) -> Result<serde_json::Value, AuthError> {
		if !self.config.development {
			return Err(AuthError::Configuration);
		}
		super::operator::seed(&self.pool)
			.await
			.map_err(|error| match error {
				super::operator::OperatorError::Auth(error) => error,
				_ => AuthError::Database,
			})
	}
}
