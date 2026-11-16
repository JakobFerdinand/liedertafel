//! Instance-local cancellation only. PostgreSQL, not this registry, arbitrates
//! ownership and terminal outcomes across replicas.
use std::{
	collections::HashMap,
	sync::{Arc, Mutex},
};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;
type Key = (Uuid, Uuid);
struct Entry {
	number: i32,
	identity: Uuid,
	cancel: CancellationToken,
}
#[derive(Default)]
pub struct DraftReadingRegistry {
	running: Mutex<HashMap<Key, Entry>>,
}
impl DraftReadingRegistry {
	pub(crate) fn start(
		self: &Arc<Self>,
		draft: Uuid,
		file: Uuid,
		number: i32,
		parent: &CancellationToken,
	) -> Handle {
		let cancel = parent.child_token();
		let identity = Uuid::new_v4();
		if let Ok(mut running) = self.running.lock() {
			if running
				.get(&(draft, file))
				.is_some_and(|old| old.number >= number)
			{
				cancel.cancel();
			} else {
				if let Some(old) = running.insert(
					(draft, file),
					Entry {
						number,
						identity,
						cancel: cancel.clone(),
					},
				) {
					old.cancel.cancel();
				}
			}
		} else {
			cancel.cancel();
		}
		Handle {
			owner: self.clone(),
			key: (draft, file),
			identity,
			cancel,
		}
	}
	pub fn cancel_datei(&self, draft: Uuid, file: Uuid) {
		if let Ok(mut running) = self.running.lock()
			&& let Some(old) = running.remove(&(draft, file))
		{
			old.cancel.cancel();
		}
	}
	pub fn cancel_entwurf(&self, draft: Uuid) {
		if let Ok(mut running) = self.running.lock() {
			running.retain(|(id, _), entry| {
				if *id == draft {
					entry.cancel.cancel();
					false
				} else {
					true
				}
			});
		}
	}
}
impl crate::catalogue::drafts::DraftLifecycle for DraftReadingRegistry {
	fn removed(&self, draft: Uuid, file: Uuid) {
		self.cancel_datei(draft, file);
	}
	fn discarded(&self, draft: Uuid) {
		self.cancel_entwurf(draft);
	}
}
pub(crate) struct Handle {
	owner: Arc<DraftReadingRegistry>,
	key: Key,
	identity: Uuid,
	pub cancel: CancellationToken,
}
impl Drop for Handle {
	fn drop(&mut self) {
		self.cancel.cancel();
		if let Ok(mut running) = self.owner.running.lock()
			&& running
				.get(&self.key)
				.is_some_and(|e| e.identity == self.identity)
		{
			running.remove(&self.key);
		}
	}
}
