#![allow(clippy::unwrap_used)] // Scripted provider-boundary assertions.
use archive_server::queue::*;
use futures::future::BoxFuture;
use std::sync::{Arc, Mutex};
struct Source {
	messages: Mutex<Vec<Message>>,
	actions: Mutex<Vec<(String, i32)>>,
}
impl Queue for Source {
	fn send<'a>(&'a self, _: &'a str) -> BoxFuture<'a, Result<bool, QueueError>> {
		Box::pin(async { Ok(false) })
	}
	fn receive(&self, n: usize, _: i32) -> BoxFuture<'_, Result<Vec<Message>, QueueError>> {
		Box::pin(async move {
			let mut messages = self.messages.lock().unwrap();
			let len = n.min(messages.len());
			Ok(messages.drain(..len).collect())
		})
	}
	fn delete<'a>(&'a self, m: &'a Message) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async move {
			self.actions.lock().unwrap().push((m.id.clone(), -1));
			if m.id == "0" { Err(QueueError) } else { Ok(()) }
		})
	}
	fn abandon<'a>(&'a self, m: &'a Message, n: i32) -> BoxFuture<'a, Result<(), QueueError>> {
		Box::pin(async move {
			self.actions.lock().unwrap().push((m.id.clone(), n));
			Ok(())
		})
	}
}
#[tokio::test]
async fn bounded_finite_drain_continues_receipt_failure_and_releases_remainder() {
	let source = Source {
		messages: Mutex::new(
			(0..4)
				.map(|i| Message {
					id: i.to_string(),
					receipt: "receipt".into(),
					body: "body".into(),
					dequeue_count: 1,
				})
				.collect(),
		),
		actions: Mutex::new(Vec::new()),
	};
	let paused = Arc::new(std::sync::atomic::AtomicBool::new(false));
	let pause = paused.clone();
	let cancel = tokio_util::sync::CancellationToken::new();
	let settings = DrainSettings {
		batch_size: 4,
		receive_visibility: chrono::Duration::seconds(60),
		time_budget: chrono::Duration::seconds(5),
		max_messages: 4,
		drain_budget: chrono::Duration::seconds(60),
		backoff: chrono::Duration::seconds(1),
	};
	let count = drain(
		&source,
		&settings,
		&archive_server::auth::SystemClock,
		|| paused.load(std::sync::atomic::Ordering::Relaxed),
		&cancel,
		|m, _| {
			if m.id == "1" {
				pause.store(true, std::sync::atomic::Ordering::Relaxed);
			}
			Box::pin(async { Ok(Disposition::Delete) })
		},
	)
	.await
	.unwrap();
	assert_eq!(count, 2);
	assert_eq!(
		*source.actions.lock().unwrap(),
		vec![
			("0".into(), -1),
			("1".into(), -1),
			("2".into(), 0),
			("3".into(), 0)
		]
	);
}

#[test]
fn envelopes_keep_typed_duplicates_case_sensitive_names_clr_json_guid_and_invalid_trace() {
	let id = uuid::Uuid::new_v4();
	let valid = format!(
		r#"{{"type":"extraction","dateiId":"{id}","traceParent":"invalid","traceState":"also invalid"}}"#
	);
	assert!(parse_envelope(&valid, "extraction").is_some());
	for body in [
		valid.replace("dateiId", "DateiId"),
		valid.replace(&id.to_string(), &id.simple().to_string()),
		format!(r#"{{"type":"extraction","dateiId":null,"dateiId":"{id}"}}"#),
		format!(r#"{{"type":5,"type":"extraction","dateiId":"{id}"}}"#),
	] {
		assert!(parse_envelope(&body, "extraction").is_none());
	}
	assert!(
		parse_envelope(
			&format!(
				r#"{{"type":"reading","type":"extraction","dateiId":"{id}","ignored":{{"any":true}}}}"#
			),
			"extraction"
		)
		.is_some()
	);
	assert_eq!(
		archive_server::extraction::Options::default().visibility_fit_batch_size(),
		4
	);
	let options = archive_server::extraction::Options {
		receive_visibility: chrono::Duration::seconds(30) + chrono::Duration::nanoseconds(300),
		time_budget: chrono::Duration::nanoseconds(100),
		..Default::default()
	};
	assert_eq!(options.visibility_fit_batch_size(), 3);
}

struct Time(Mutex<chrono::DateTime<chrono::Utc>>);
impl archive_server::auth::Clock for Time {
	fn now(&self) -> chrono::DateTime<chrono::Utc> {
		*self.0.lock().unwrap()
	}
}
#[tokio::test]
async fn receive_deadline_clamps_serial_messages_and_releases_expired_batch() {
	use archive_server::auth::Clock;
	let source = Source {
		messages: Mutex::new(
			(0..4)
				.map(|i| Message {
					id: i.to_string(),
					receipt: "receipt".into(),
					body: "body".into(),
					dequeue_count: 1,
				})
				.collect(),
		),
		actions: Mutex::new(vec![]),
	};
	let now = chrono::Utc::now();
	let clock = Arc::new(Time(Mutex::new(now)));
	let observed = Arc::new(Mutex::new(vec![]));
	let time = clock.clone();
	let observations = observed.clone();
	let settings = DrainSettings {
		batch_size: 4,
		receive_visibility: chrono::Duration::seconds(60),
		time_budget: chrono::Duration::seconds(30),
		max_messages: 4,
		drain_budget: chrono::Duration::seconds(120),
		backoff: chrono::Duration::seconds(1),
	};
	let count = drain(
		&source,
		&settings,
		clock.as_ref(),
		|| false,
		&tokio_util::sync::CancellationToken::new(),
		move |m, deadline| {
			observations
				.lock()
				.unwrap()
				.push((deadline - now).num_seconds());
			*time.0.lock().unwrap() = now
				+ chrono::Duration::seconds(match m.id.as_str() {
					"0" => 20,
					"1" => 55,
					_ => 61,
				});
			Box::pin(async { Ok(Disposition::Delete) })
		},
	)
	.await
	.unwrap();
	assert_eq!(count, 3);
	assert_eq!(*observed.lock().unwrap(), vec![30, 50, 60]);
	assert_eq!(clock.now(), now + chrono::Duration::seconds(61));
	assert_eq!(
		source.actions.lock().unwrap().last(),
		Some(&("3".into(), 0))
	);
}

#[tokio::test]
async fn message_cap_bounds_receive_and_first_empty_ends_without_sleep() {
	let source = Source {
		messages: Mutex::new(
			(0..4)
				.map(|i| Message {
					id: i.to_string(),
					receipt: "receipt".into(),
					body: "body".into(),
					dequeue_count: 1,
				})
				.collect(),
		),
		actions: Mutex::new(vec![]),
	};
	let settings = DrainSettings {
		batch_size: 4,
		receive_visibility: chrono::Duration::seconds(60),
		time_budget: chrono::Duration::seconds(5),
		max_messages: 2,
		drain_budget: chrono::Duration::seconds(60),
		backoff: chrono::Duration::seconds(1),
	};
	let cancel = tokio_util::sync::CancellationToken::new();
	let count = drain(
		&source,
		&settings,
		&archive_server::auth::SystemClock,
		|| false,
		&cancel,
		|_, _| Box::pin(async { Ok(Disposition::Delete) }),
	)
	.await
	.unwrap();
	assert_eq!(count, 2);
	assert_eq!(source.messages.lock().unwrap().len(), 2);
	let empty = Source {
		messages: Mutex::new(vec![]),
		actions: Mutex::new(vec![]),
	};
	assert_eq!(
		drain(
			&empty,
			&settings,
			&archive_server::auth::SystemClock,
			|| false,
			&cancel,
			|_, _| Box::pin(async { Ok(Disposition::Delete) })
		)
		.await
		.unwrap(),
		0
	);
}
