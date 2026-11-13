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
	pub tier_fail: AtomicBool,
	pub objects: Mutex<std::collections::HashMap<String, (Vec<u8>, String)>>,
	pub promote_barrier: Mutex<Option<std::sync::Arc<tokio::sync::Barrier>>>,
}
impl FakeStorage {
	pub fn store(&self, blob: &str, bytes: &[u8], content: &str) {
		self.objects
			.lock()
			.unwrap()
			.insert(blob.into(), (bytes.into(), content.into()));
	}
}
impl Storage for FakeStorage {
	fn probe<'a>(
		&'a self,
		blob: &'a str,
	) -> BoxFuture<'a, Result<Option<archive_server::storage::ObjectMetadata>, StorageError>> {
		Box::pin(async move {
			self.calls
				.lock()
				.unwrap()
				.push(("probe".into(), blob.into()));
			if self.fail.load(Ordering::SeqCst) {
				return Err(StorageError::Unavailable);
			}
			Ok(self
				.objects
				.lock()
				.unwrap()
				.get(blob)
				.map(|(bytes, content)| archive_server::storage::ObjectMetadata {
					size_bytes: bytes.len() as u64,
					content_type: Some(content.clone()),
				}))
		})
	}
	fn read_header<'a>(
		&'a self,
		blob: &'a str,
		length: usize,
	) -> BoxFuture<'a, Result<Option<Vec<u8>>, StorageError>> {
		Box::pin(async move {
			if self.fail.load(Ordering::SeqCst) {
				return Err(StorageError::Unavailable);
			}
			Ok(self
				.objects
				.lock()
				.unwrap()
				.get(blob)
				.map(|(bytes, _)| bytes[..length.min(bytes.len())].into()))
		})
	}
	fn promote<'a>(
		&'a self,
		source: &'a str,
		target: &'a str,
	) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async move {
			self.calls
				.lock()
				.unwrap()
				.push(("promote".into(), target.into()));
			if self.fail.load(Ordering::SeqCst) {
				return Err(StorageError::Unavailable);
			}
			{
				let mut objects = self.objects.lock().unwrap();
				let value = objects
					.get(source)
					.cloned()
					.ok_or(StorageError::Unavailable)?;
				objects.insert(target.into(), value);
			}
			let barrier = self.promote_barrier.lock().unwrap().clone();
			if let Some(b) = barrier {
				b.wait().await;
			}
			Ok(())
		})
	}
	fn upload_ticket<'a>(
		&'a self,
		blob: &'a str,
		_lifetime: Duration,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async move {
			self.calls
				.lock()
				.unwrap()
				.push(("upload".into(), blob.into()));
			if self.fail.load(Ordering::SeqCst) {
				Err(StorageError::Unavailable)
			} else {
				Ok(format!("https://private.storage.test/{blob}?ticket=upload"))
			}
		})
	}
	fn delete<'a>(&'a self, blob: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async move {
			self.calls
				.lock()
				.unwrap()
				.push(("delete".into(), blob.into()));
			if self.fail.load(Ordering::SeqCst) {
				Err(StorageError::Unavailable)
			} else {
				self.objects.lock().unwrap().remove(blob);
				Ok(())
			}
		})
	}
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
			if self.fail.load(Ordering::SeqCst) || self.tier_fail.load(Ordering::SeqCst) {
				Err(StorageError::Unavailable)
			} else {
				Ok(())
			}
		})
	}
}
