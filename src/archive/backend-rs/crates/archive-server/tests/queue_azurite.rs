#![allow(clippy::unwrap_used, clippy::expect_used)] // Provider assertions.
use archive_server::{
	auth::SystemClock,
	queue::{AzureQueue, Queue},
};
use std::sync::Arc;

#[tokio::test]
#[ignore = "explicit private Azurite Queue gate"]
async fn real_receipts_utf8_visibility_and_stale_delete() {
	let connection = std::env::var("ARCHIVE_TEST_QUEUE").unwrap();
	let name = format!("test226-{}", uuid::Uuid::new_v4().simple());
	let queue =
		AzureQueue::select(Some(&connection), None, &name, true, Arc::new(SystemClock)).unwrap();
	queue.ensure().await.unwrap();
	queue.ensure().await.unwrap();
	let body = "{\"type\":\"extraction\",\"text\":\"Grüße + / =\"}";
	assert!(queue.send(body).await.unwrap());
	let first = queue.receive(1, 30).await.unwrap().remove(0);
	assert_eq!(first.body, body);
	assert_eq!(first.dequeue_count, 1);
	assert!(queue.receive(1, 30).await.unwrap().is_empty());
	queue.abandon(&first, 0).await.unwrap();
	let next = queue.receive(1, 30).await.unwrap().remove(0);
	assert_eq!(next.body, body);
	assert_eq!(next.dequeue_count, 2);
	assert_ne!(next.receipt, first.receipt);
	assert!(queue.delete(&first).await.is_err());
	queue.delete(&next).await.unwrap();
	assert!(queue.receive(1, 30).await.unwrap().is_empty());
}
