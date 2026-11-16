//! Member-only read-only chat. Provider delivery stays below the real Gateway.
mod a2ui;
mod citations;
mod input;
mod options;
pub use options::Options;
pub(crate) mod script;
mod tools;
mod usage;
use crate::ai;
use crate::{
	Origin,
	api::problem,
	auth::{AccessPolicy, AuthError},
	clr_guid::GuidPath,
};
use axum::response::sse::{Event, Sse};
use axum::{
	extract::{Request, State},
	http::StatusCode,
	response::{IntoResponse, Response},
	routing::{MethodRouter, get, post},
};
use chrono::{DateTime, Utc};
use serde_json::{Value, json};
use std::sync::Arc;
use tokio_util::sync::CancellationToken;
use tracing::Instrument;
use uuid::Uuid;

pub const UNAVAILABLE: &str = "Der Archiv-Chat ist derzeit nicht verfügbar.";
pub const THREAD_NOT_FOUND: &str = "Chatverlauf nicht gefunden.";

pub(crate) fn routes() -> Vec<(&'static str, MethodRouter<Arc<Origin>>)> {
	vec![
		("/api/chat", post(run).fallback(crate::api::api_miss)),
		(
			"/api/chat/thread/{id}",
			get(thread)
				.fallback(crate::api::api_miss)
				.layer(axum::middleware::from_fn(crate::clr_guid::constrained)),
		),
	]
}

async fn run(State(origin): State<Arc<Origin>>, request: Request) -> Result<Response, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		crate::auth::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, true)
		.await?;
	if !origin.chat.enabled || origin.chat.disabled {
		return Ok(access.respond(problem(StatusCode::SERVICE_UNAVAILABLE, UNAVAILABLE)));
	}
	let bytes = axum::body::to_bytes(request.into_body(), 30_000_000)
		.await
		.map_err(|_| invalid())?;
	let mut input = input::parse(&bytes).map_err(|_| invalid())?;
	let question = std::mem::take(&mut input.question);
	if question.is_empty() {
		return Err(invalid());
	}
	if i64::try_from(question.encode_utf16().count()).unwrap_or(i64::MAX)
		> i64::from(origin.chat.max_question_chars)
	{
		return Err(AuthError::Problem(
			StatusCode::BAD_REQUEST,
			"Die Frage ist zu lang.",
		));
	}
	let raw = input.thread_id.trim();
	let id = if raw.is_empty() {
		Uuid::now_v7()
	} else {
		crate::clr_guid::try_parse(raw).ok_or_else(invalid)?
	};
	let account = access.current.account_id;
	let cancel = origin.shutdown.child_token();
	let guard = cancel.clone().drop_guard();
	let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
	let (ready, accepted) = tokio::sync::oneshot::channel();
	let owned = origin.clone();
	// Transfer the WHOLE producer before thread/history SQL. The response owns
	// disconnect cancellation; returning this handler must not cancel its body.
	origin.spawn_reading(
		async move {
			produce(owned, id, account, question, input, cancel, ready, tx).await;
		}
		.instrument(tracing::info_span!(
			"invoke_agent archive-chat",
			gen_ai.agent.name = "archive-chat",
			otel.status_code = tracing::field::Empty
		)),
	);
	accepted.await.map_err(|_| AuthError::Database)??;
	let stream = futures::stream::unfold((rx, guard), |(mut rx, guard)| async move {
		rx.recv().await.map(|value: Value| {
			(
				Ok::<_, std::convert::Infallible>(Event::default().data(value.to_string())),
				(rx, guard),
			)
		})
	});
	Ok(access.respond(Sse::new(stream).into_response()))
}

fn invalid() -> AuthError {
	AuthError::Problem(StatusCode::BAD_REQUEST, "Ungültige Anfrage.")
}

#[derive(sqlx::FromRow)]
struct Stored {
	#[sqlx(rename = "Role")]
	role: String,
	#[sqlx(rename = "Content")]
	content: String,
	#[sqlx(rename = "CreatedAt")]
	created_at: DateTime<Utc>,
	#[sqlx(rename = "A2uiJson")]
	a2ui: Option<Value>,
}
async fn recent(pool: &sqlx::PgPool, id: Uuid) -> Result<Vec<Stored>, sqlx::Error> {
	sqlx::query_as(r#"SELECT * FROM (SELECT * FROM chat_messages WHERE "ThreadId"=$1 ORDER BY "CreatedAt" DESC,"Id" DESC LIMIT 20) recent ORDER BY "CreatedAt","Id""#).bind(id).fetch_all(pool).await
}
async fn thread(
	State(origin): State<Arc<Origin>>,
	GuidPath(id): GuidPath,
	request: Request,
) -> Result<Response, AuthError> {
	let auth = origin.auth.as_deref().ok_or(AuthError::Problem(
		StatusCode::UNAUTHORIZED,
		crate::auth::UNAUTHORIZED,
	))?;
	let access = auth
		.authorize(request.headers(), AccessPolicy::Member, false)
		.await?;
	let exists: bool = sqlx::query_scalar(
		r#"SELECT EXISTS(SELECT 1 FROM chat_threads WHERE "Id"=$1 AND "AccountId"=$2)"#,
	)
	.bind(id)
	.bind(access.current.account_id)
	.fetch_one(&auth.pool)
	.await?;
	if !exists {
		return Ok(access.respond(problem(StatusCode::NOT_FOUND, THREAD_NOT_FOUND)));
	}
	let mut messages = Vec::new();
	for m in recent(&auth.pool, id).await? {
		let cards = if let Some(stored) = m.a2ui {
			a2ui::recheck(&auth.pool, stored).await?
		} else {
			None
		};
		if m.content.is_empty() && cards.is_none() {
			continue;
		}
		messages
			.push(json!({"role":m.role,"content":m.content,"createdAt":m.created_at,"a2ui":cards}));
	}
	Ok(access.respond(axum::Json(json!({"threadId":id,"messages":messages})).into_response()))
}

#[allow(clippy::too_many_arguments)] // One owned HTTP producer transfers its full run, not detached sub-futures.
async fn produce(
	origin: Arc<Origin>,
	id: Uuid,
	account: Uuid,
	question: String,
	input: input::Input,
	cancel: CancellationToken,
	ready: tokio::sync::oneshot::Sender<Result<(), AuthError>>,
	tx: tokio::sync::mpsc::UnboundedSender<Value>,
) {
	let Some(auth) = &origin.auth else {
		let _ = ready.send(Err(AuthError::Database));
		return;
	};
	let owner =
		sqlx::query_scalar::<_, Uuid>(r#"SELECT "AccountId" FROM chat_threads WHERE "Id"=$1"#)
			.bind(id)
			.fetch_optional(&auth.pool)
			.await;
	let owner = match owner {
		Ok(o) => o,
		Err(_) => {
			let _ = ready.send(Err(AuthError::Database));
			return;
		}
	};
	if owner.is_some_and(|a| a != account) {
		let _ = ready.send(Err(AuthError::Problem(
			StatusCode::FORBIDDEN,
			"Keine Berechtigung für diesen Chatverlauf.",
		)));
		return;
	}
	let history = match recent(&auth.pool, id).await {
		Ok(h) => h,
		Err(_) => {
			let _ = ready.send(Err(AuthError::Database));
			return;
		}
	};
	if ready.send(Ok(())).is_err() {
		cancel.cancel();
	}
	let run_id = &input.run_id;
	let mut started = json!({"type":"RUN_STARTED","threadId":id,"runId":run_id});
	if let Some(parent) = &input.parent_run_id {
		started["parentRunId"] = json!(parent);
	}
	let _ = tx.send(started);
	let mut messages: Vec<_> = history
		.into_iter()
		.filter(|m| matches!(m.role.as_str(), "user" | "assistant") && !m.content.is_empty())
		.map(|m| ai::Message::text(&m.role, &m.content))
		.collect();
	messages.push(ai::Message::text("user", &question));
	let mut operation = ai::Operation::start("chat", Some(account));
	operation.max_output_tokens = Some(2000);
	let mut answer = String::new();
	let mut usage = usage::Tracker::default();
	let mut candidates = citations::Candidates::default();
	let mut message = None;
	let mut surfaces = a2ui::Surfaces::default();
	let result=async {
		let services=origin.ai.as_ref().ok_or(ai::Error::Unavailable)?;
		let overall=deadline(origin.chat.overall_seconds)?;
		let bound=origin.chat.max_tool_calls.max(1);
		for step in 0..bound {
			let mut request=ai::Request::new(messages.clone());
			request.operation=Some(operation.clone());request.agent=Some("archive-chat".into());request.options.tools=tools::definitions()?;request.options.instructions=Some(include_str!("prompt.txt").trim_end_matches('\n').into());
			let mut first_deadline=deadline(origin.chat.no_token_seconds)?;
			let mut call=Some(services.gateway.start(request.clone(),true)?);
			let mut received=false;let mut retried=false;
			let mut tool_calls=ai::ToolCalls::default();
			let mut step_text=String::new();
			let mut filter=citations::Filter::default();
			let result=async {
				loop {
					let current=call.as_mut().ok_or(ai::Error::Closed)?;
					let update=tokio::select!{biased;_=cancel.cancelled()=>return Err(ai::Error::Cancelled),_=wait_deadline(overall)=>return Err(ai::Error::Cancelled),_=wait_deadline(first_deadline)=>return Err(ai::Error::Cancelled),update=current.next()=>update};
					let update=match update {
						Err(error) if step==0&&!received&&!retried&&matches!(error,ai::Error::Provider|ai::Error::Protocol|ai::Error::ContentFilter)=>{
							// One feature retry only, AFTER the prior paid producer has
							// joined its accounting. Logical step/operation stay put.
							if let Some(previous)=call.take(){let _=previous.finish().await;}retried=true;first_deadline=deadline(origin.chat.no_token_seconds)?;call=Some(services.gateway.start(request.clone(),true)?);continue;
						},
						result=>result?,
					};
					let Some(update)=update else {return Ok(());};
					received=true;
					first_deadline=None;
					tool_calls.observe(&update.tools)?;
					let text=filter.append(&update.text,&candidates);
					step_text.push_str(&text);answer.push_str(&text);emit_text(&tx,&mut message,&text);
					if let Some(report)=update.wire_usage {usage.observe(*report)?;}
				}
			}.await;
			// Per-call join is not request-cancellable, and never globally drains.
			let completion=if let Some(call)=call{call.finish().await}else{Ok(())};result.and(completion)?;
			let trailing=filter.complete();step_text.push_str(&trailing);answer.push_str(&trailing);emit_text(&tx,&mut message,&trailing);
			let calls=tool_calls.finish()?;
			if calls.is_empty(){return Ok(());}
			end_text(&tx,&mut message);
			let mut assistant=Vec::new();if !step_text.is_empty(){assistant.push(ai::Content::Text(step_text));}assistant.extend(calls.clone());
			messages.push(ai::Message{role:"assistant".into(),contents:assistant});
			let mut results=Vec::new();
			for call in calls {
				if let ai::Content::FunctionCall{id:call_id,name,arguments}=call {
					if name!="zeige_oberflaeche" {
						let _=tx.send(json!({"type":"TOOL_CALL_START","toolCallId":call_id,"toolCallName":name}));
						let _=tx.send(json!({"type":"TOOL_CALL_ARGS","toolCallId":call_id,"delta":arguments.raw()}));
						let _=tx.send(json!({"type":"TOOL_CALL_END","toolCallId":call_id}));
					}
					if step==bound-1 {return Err(ai::Error::Protocol);}
					if cancel.is_cancelled(){return Err(ai::Error::Cancelled);}
					let args:Value=serde_json::from_str(arguments.raw()).map_err(|_|ai::Error::Protocol)?;
					let invoke=async{if name=="zeige_oberflaeche"{surfaces.show(&auth.pool,&args).await.unwrap_or_else(|_|"{}".into())}else{tools::invoke(&auth.pool,&name,&args).await.unwrap_or_else(|_|"{}".into())}};
					// Unknown provider-supplied names/arguments are never telemetry.
					let span = match name.as_str() {
						"catalogue_search" => tracing::info_span!("execute_tool catalogue_search", gen_ai.tool.name = "catalogue_search"),
						"lied_details" => tracing::info_span!("execute_tool lied_details", gen_ai.tool.name = "lied_details"),
						"zeige_oberflaeche" => tracing::info_span!("execute_tool zeige_oberflaeche", gen_ai.tool.name = "zeige_oberflaeche"),
						_ => tracing::info_span!("execute_tool unknown"),
					};
					let result=tokio::select!{biased;_=cancel.cancelled()=>return Err(ai::Error::Cancelled),_=wait_deadline(overall)=>return Err(ai::Error::Cancelled),result=invoke.instrument(span)=>result};
					candidates.collect(&result);
					results.push(ai::Content::FunctionResult{id:call_id,result:ai::Payload::Text(result)});
				}
			}
			messages.push(ai::Message{role:"tool".into(),contents:results});
		}
		Err(ai::Error::Protocol)
	}.await;
	let success = result.is_ok();
	if !success {
		tracing::Span::current().record("otel.status_code", "ERROR");
		let error = if matches!(result, Err(ai::Error::BudgetExceeded(_))) {
			json!({"type":"RUN_ERROR","code":"monatsbudget_erreicht","message":"Monatsbudget erreicht. Der Archiv-Chat steht im nächsten Monat wieder zur Verfügung; Suche und Archiv funktionieren weiter."})
		} else {
			json!({"type":"RUN_ERROR","message":"Die Antwort konnte nicht fertig gestellt werden."})
		};
		let _ = tx.send(error);
	}
	// Final history always stays owned, even if HttpAgent stops at RUN_ERROR.
	let persisted_answer = if success {
		let Some(answer) = tools::persisted_prefix(
			&answer,
			usize::try_from(origin.chat.max_answer_chars).unwrap_or(0),
		) else {
			tracing::error!("Archiv-Chat: Verlauf konnte nicht gespeichert werden.");
			let _ = tx.send(
				json!({"type":"RUN_ERROR","message":"Die Antwort konnte nicht fertig gestellt werden."}),
			);
			return;
		};
		Some(answer)
	} else {
		None
	};
	if save(
		&auth.pool,
		id,
		account,
		owner.is_none(),
		&question,
		persisted_answer.as_deref(),
		if success { surfaces.json() } else { None },
		auth.clock.now(),
	)
	.await
	.is_err()
	{
		tracing::error!("Archiv-Chat: Verlauf konnte nicht gespeichert werden.");
		if success {
			let _ = tx.send(
				json!({"type":"RUN_ERROR","message":"Die Antwort konnte nicht fertig gestellt werden."}),
			);
		}
		return;
	}
	if success {
		if let Some(cards) = surfaces.json() {
			let _ = tx.send(json!({"type":"CUSTOM","name":"archive.a2ui","value":cards}));
		}
		let chips = candidates.chips(&answer);
		if !chips.is_empty() {
			let _ = tx.send(json!({"type":"CUSTOM","name":"archive.citations","value":chips}));
		}
		end_text(&tx, &mut message);
		let mut finished = json!({"type":"RUN_FINISHED","threadId":id,"runId":run_id,"outcome":{"type":"success"}});
		if let Some(usage) = usage.json() {
			finished["usage"] = usage;
		}
		let _ = tx.send(finished);
	}
}
fn emit_text(
	tx: &tokio::sync::mpsc::UnboundedSender<Value>,
	message: &mut Option<String>,
	text: &str,
) {
	if text.is_empty() {
		return;
	}
	let id = message.get_or_insert_with(|| {
		let id = Uuid::now_v7().to_string();
		let _ = tx.send(
			json!({"type":"TEXT_MESSAGE_START","messageId":id,"role":"assistant","name":"archive-chat"}),
		);
		id
	});
	let _ = tx.send(json!({"type":"TEXT_MESSAGE_CONTENT","messageId":id,"delta":text}));
}
fn end_text(tx: &tokio::sync::mpsc::UnboundedSender<Value>, message: &mut Option<String>) {
	if let Some(id) = message.take() {
		let _ = tx.send(json!({"type":"TEXT_MESSAGE_END","messageId":id}));
	}
}
fn deadline(seconds: i32) -> Result<Option<tokio::time::Instant>, ai::Error> {
	// Actual BCL CancelAfter(TimeSpan.FromSeconds(Int32)) capture: -1 SECOND
	// is not Timeout.InfiniteTimeSpan (-1 millisecond), and >UInt32 milliseconds
	// also throws. Keep these configuration errors rather than infinite waits.
	if !(0..=4_294_967).contains(&seconds) {
		Err(ai::Error::Configuration)
	} else {
		Ok(Some(
			tokio::time::Instant::now()
				+ std::time::Duration::from_secs(
					u64::try_from(seconds).map_err(|_| ai::Error::Configuration)?,
				),
		))
	}
}

async fn wait_deadline(deadline: Option<tokio::time::Instant>) {
	if let Some(deadline) = deadline {
		tokio::time::sleep_until(deadline).await;
	} else {
		std::future::pending::<()>().await;
	}
}
#[allow(clippy::too_many_arguments)] // One atomic thread/turn batch; surfaces have no independent commit.
async fn save(
	pool: &sqlx::PgPool,
	id: Uuid,
	account: Uuid,
	new: bool,
	question: &str,
	answer: Option<&str>,
	surfaces: Option<Value>,
	now: DateTime<Utc>,
) -> Result<(), sqlx::Error> {
	let mut tx = pool.begin().await?;
	if new {
		sqlx::query(
			r#"INSERT INTO chat_threads ("Id","AccountId","CreatedAt","UpdatedAt") VALUES ($1,$2,$3,$3)"#,
		)
		.bind(id)
		.bind(account)
		.bind(now)
		.execute(&mut *tx)
		.await?;
	} else {
		sqlx::query(r#"UPDATE chat_threads SET "UpdatedAt"=$2 WHERE "Id"=$1"#)
			.bind(id)
			.bind(now)
			.execute(&mut *tx)
			.await?;
	}
	for (role, content, at) in std::iter::once(("user", question, now)).chain(
		answer
			.filter(|a| !a.is_empty() || surfaces.is_some())
			.map(|a| ("assistant", a, now + chrono::Duration::nanoseconds(100))),
	) {
		sqlx::query(r#"INSERT INTO chat_messages ("Id","ThreadId","Role","Content","CreatedAt","A2uiJson") VALUES ($1,$2,$3,$4,$5,$6)"#).bind(Uuid::now_v7()).bind(id).bind(role).bind(content).bind(at).bind(if role=="assistant"{surfaces.as_ref()}else{None}).execute(&mut *tx).await?;
	}
	tx.commit().await?;
	// Trimming is a separate save, matching the frozen history provider.
	sqlx::query(r#"DELETE FROM chat_messages WHERE "ThreadId"=$1 AND "Id" NOT IN (SELECT "Id" FROM chat_messages WHERE "ThreadId"=$1 ORDER BY "CreatedAt" DESC,"Id" DESC LIMIT 20)"#).bind(id).execute(pool).await?;
	Ok(())
}

#[cfg(test)]
mod tests {
	#[tokio::test]
	async fn whole_second_deadline_domain_matches_actual_frozen_bcl_capture() {
		for seconds in [-2, -1, 4_294_968, i32::MAX] {
			assert!(super::deadline(seconds).is_err());
		}
		for seconds in [0, 1, 4_294_967] {
			assert!(super::deadline(seconds).unwrap().is_some());
		}
	}
}
