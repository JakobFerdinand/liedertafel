//! Real filesystem/native-reader handoff; only upstream delivery/blocking-I/O
//! scheduling is controlled. A completed spool must expose all accepted bytes.
#![allow(clippy::unwrap_used, clippy::expect_used)] // Bounded synthetic I/O assertions.
mod support;
use archive_server::{
	extraction::pdf::Spool,
	storage::{BlobRead, Storage, StorageError},
};
use futures::{StreamExt, future::BoxFuture};
use std::sync::{Arc, Condvar, Mutex};
struct Gate {
	released: Mutex<bool>,
	release: Condvar,
	entered: tokio::sync::Notify,
}
struct Controlled {
	gate: Arc<Gate>,
}
impl Storage for Controlled {
	fn open_read_bounded<'a>(
		&'a self,
		_: &'a str,
		_: u64,
	) -> BoxFuture<'a, Result<Option<BlobRead>, StorageError>> {
		let gate = self.gate.clone();
		Box::pin(async move {
			Ok(Some(
				futures::stream::once(async move {
					// First stream poll is after the spool File has opened. Occupy the sole
					// blocking worker before write_all can queue its actual filesystem write.
					let (started, ready) = tokio::sync::oneshot::channel();
					let wait = gate.clone();
					tokio::task::spawn_blocking(move || {
						let _ = started.send(());
						let mut released = wait.released.lock().unwrap();
						while !*released {
							released = wait.release.wait(released).unwrap();
						}
					});
					ready.await.map_err(|_| StorageError::Unavailable)?;
					gate.entered.notify_one();
					Ok(azure_core::Bytes::from_static(
						b"%PDF-synthetic-complete-spool",
					))
				})
				.boxed(),
			))
		})
	}
	fn read_ticket<'a>(
		&'a self,
		_: &'a str,
		_: chrono::Duration,
		_: bool,
		_: Option<&'a str>,
	) -> BoxFuture<'a, Result<String, StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
	fn set_tier<'a>(&'a self, _: &'a str, _: &'a str) -> BoxFuture<'a, Result<(), StorageError>> {
		Box::pin(async { Err(StorageError::Unavailable) })
	}
}
#[test]
fn spool_waits_for_queued_file_writes_before_handing_the_path_to_native_readers() {
	let runtime = tokio::runtime::Builder::new_current_thread()
		.enable_all()
		.max_blocking_threads(1)
		.build()
		.unwrap();
	runtime.block_on(async{
  let directory=std::path::PathBuf::from(format!("/tmp/opencode/229-spool-barrier-{}",uuid::Uuid::new_v4()));use std::os::unix::fs::DirBuilderExt;std::fs::DirBuilder::new().mode(0o700).create(&directory).unwrap();
  let gate=Arc::new(Gate{released:Mutex::new(false),release:Condvar::new(),entered:tokio::sync::Notify::new()});let input=Controlled{gate:gate.clone()};let target=directory.clone();let mut task=tokio::spawn(async move{Spool::load(&input,"synthetic",1000,&target,||false).await});gate.entered.notified().await;
  let early=tokio::time::timeout(std::time::Duration::from_millis(25),&mut task).await;let premature=early.is_ok();
  let visible_before_release=std::fs::read_dir(&directory).unwrap().map(|e|e.unwrap().metadata().unwrap().len()).sum::<u64>();
  *gate.released.lock().unwrap()=true;gate.release.notify_all();
  let spool=match early{Ok(result)=>result.unwrap().unwrap(),Err(_)=>task.await.unwrap().unwrap()};
  // Always release the blocking worker before assertions, including red runs.
  let content=std::fs::read(&spool.0).unwrap();drop(spool);std::fs::remove_dir(directory).unwrap();
  assert!(!premature,"spool returned while its accepted write was still queued ({visible_before_release} visible bytes)");assert_eq!(content,b"%PDF-synthetic-complete-spool");
 });
}
