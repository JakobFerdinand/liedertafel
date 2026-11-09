use archive_server::storage::{Storage, StorageError};
use chrono::Duration;
use futures::future::BoxFuture;
use std::sync::{
	Mutex,
	atomic::{AtomicBool, Ordering},
};
#[derive(Default)]
pub struct FakeStorage {
	pub calls: Mutex<Vec<(String, String)>>,
	pub fail: AtomicBool,
}
impl Storage for FakeStorage {
	fn read_ticket<'a>(
		&'a self,
		blob: &'a str,
		_lifetime: Duration,
		download: bool,
		_content_type: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async move {
			self.calls.lock().unwrap().push((
				if download { "download" } else { "view" }.into(),
				blob.into(),
			));
			if self.fail.load(Ordering::SeqCst) {
				return Err(StorageError::Unavailable);
			}
			Ok(format!(
				"https://private.storage.test/{blob}?ticket={}",
				if download { "attachment" } else { "inline" }
			))
		})
	}
	fn set_tier<'a>(
		&'a self,
		blob: &'a str,
		tier: &'a str,
	) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async move {
			self.calls.lock().unwrap().push((tier.into(), blob.into()));
			if self.fail.load(Ordering::SeqCst) {
				Err(StorageError::Unavailable)
			} else {
				Ok(())
			}
		})
	}
}
