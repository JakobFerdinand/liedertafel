//! Own native processes beyond dropped SQL/HTTP futures; never export tool output.
use super::worker::{Error, MISSING_TOOL, UNREADABLE};
use std::{ffi::OsString, process::Stdio};
use tokio::io::AsyncReadExt;
use tokio_util::{sync::CancellationToken, task::TaskTracker};
use tracing::Instrument;

pub(super) async fn run(
	tool: &str,
	args: Vec<OsString>,
	cancel: &CancellationToken,
	tasks: &TaskTracker,
) -> Result<Vec<u8>, Error> {
	let cancel = cancel.child_token();
	let _guard = cancel.clone().drop_guard();
	let tool = tool.to_owned();
	tasks
		.spawn(
			async move {
				if cancel.is_cancelled() {
					return Err(Error::Cancelled);
				}
				let mut child = tokio::process::Command::new(tool)
					.args(args)
					.process_group(0)
					.stdin(Stdio::null())
					.stdout(Stdio::piped())
					.stderr(Stdio::piped())
					.kill_on_drop(true)
					.spawn()
					.map_err(|e| {
						if e.kind() == std::io::ErrorKind::NotFound {
							Error::Material(MISSING_TOOL)
						} else {
							Error::Infrastructure
						}
					})?;
				let pid = child.id().ok_or(Error::Infrastructure)?;
				let mut stdout = child.stdout.take().ok_or(Error::Infrastructure)?;
				let mut stderr = child.stderr.take().ok_or(Error::Infrastructure)?;
				let result = {
					let work = async {
						let mut out = Vec::new();
						let mut sink = tokio::io::sink();
						// Both pipes are drained concurrently even if a process fills stderr
						// before writing stdout. Contents are never logs/errors/telemetry.
						let (status, outcome, errors) = tokio::join!(
							child.wait(),
							stdout.read_to_end(&mut out),
							tokio::io::copy(&mut stderr, &mut sink)
						);
						let status = status.map_err(|_| Error::Infrastructure)?;
						outcome.map_err(|_| Error::Infrastructure)?;
						errors.map_err(|_| Error::Infrastructure)?;
						if status.success() {
							Ok(out)
						} else {
							Err(Error::Material(UNREADABLE))
						}
					};
					tokio::pin!(work);
					tokio::select! {
						biased;
						_ = cancel.cancelled()=>None,
						result = &mut work=>Some(result),
					}
				};
				if let Some(result) = result {
					return result;
				}
				// No shell; fixed utility + native argv. The dedicated process group
				// covers ffmpeg threads/helpers and inherited-pipe descendants. Snapshot
				// descendants also covers a helper that created another process group.
				let descendants = descendants(pid);
				for descendant in descendants.iter().rev() {
					kill(&descendant.to_string()).await;
				}
				kill(&format!("-{pid}")).await;
				let _ = child.kill().await;
				let _ = child.wait().await;
				Err(Error::Cancelled)
			}
			.in_current_span(),
		)
		.await
		.map_err(|_| Error::Infrastructure)?
}
async fn kill(target: &str) {
	let _ = tokio::process::Command::new("/bin/kill")
		.args(["-KILL", "--", target])
		.stdin(Stdio::null())
		.stdout(Stdio::null())
		.stderr(Stdio::null())
		.kill_on_drop(true)
		.status()
		.await;
}
fn descendants(parent: u32) -> Vec<u32> {
	let mut rows = Vec::new();
	if let Ok(entries) = std::fs::read_dir("/proc") {
		for entry in entries.flatten() {
			if let Some(pid) = entry
				.file_name()
				.to_str()
				.and_then(|s| s.parse::<u32>().ok())
				&& let Ok(stat) = std::fs::read_to_string(entry.path().join("stat"))
				&& let Some(tail) = stat.rsplit_once(')').map(|(_, s)| s)
				&& let Some(ppid) = tail
					.split_whitespace()
					.nth(1)
					.and_then(|s| s.parse::<u32>().ok())
			{
				rows.push((pid, ppid));
			}
		}
	}
	let mut result = Vec::new();
	let mut frontier = vec![parent];
	while let Some(parent) = frontier.pop() {
		for &(pid, ppid) in &rows {
			if ppid == parent && pid != parent && !result.contains(&pid) {
				result.push(pid);
				frontier.push(pid);
			}
		}
	}
	result
}
