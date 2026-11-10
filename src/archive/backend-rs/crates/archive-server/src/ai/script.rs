//! Delivery-only scripted harness. It cannot reserve, settle or call a provider.
use super::{Error, ProviderRequest, Update};
use futures::{
	StreamExt,
	stream::{self, BoxStream},
};
use std::{
	collections::VecDeque,
	sync::{Arc, Mutex},
	time::Duration,
};

#[derive(Clone, Debug)]
pub enum ScriptStep {
	Update(Update),
	Delay(Duration),
	Hold,
	Fail,
}
type Handler = dyn Fn(&ProviderRequest) -> Vec<ScriptStep> + Send + Sync;
#[derive(Clone)]
pub struct Script {
	state: Arc<Mutex<State>>,
	handler: Option<Arc<Handler>>,
}
struct State {
	scripts: VecDeque<Vec<ScriptStep>>,
	requests: Vec<ProviderRequest>,
}
impl Script {
	pub fn new(scripts: Vec<Vec<ScriptStep>>) -> Self {
		Self {
			state: Arc::new(Mutex::new(State {
				scripts: scripts.into(),
				requests: Vec::new(),
			})),
			handler: None,
		}
	}
	pub fn handler(
		handler: impl Fn(&ProviderRequest) -> Vec<ScriptStep> + Send + Sync + 'static,
	) -> Self {
		Self {
			handler: Some(Arc::new(handler)),
			..Self::new(Vec::new())
		}
	}
	pub fn requests(&self) -> Vec<ProviderRequest> {
		self.state
			.lock()
			.map(|s| s.requests.clone())
			.unwrap_or_default()
	}
	pub(super) fn stream(
		&self,
		request: ProviderRequest,
	) -> BoxStream<'static, Result<Update, Error>> {
		let actions = self
			.state
			.lock()
			.map(|mut s| {
				s.requests.push(request.clone());
				s.scripts.pop_front()
			})
			.ok()
			.flatten();
		let actions = self
			.handler
			.as_ref()
			.map(|f| f(&request))
			.or(actions)
			.unwrap_or_else(|| vec![ScriptStep::Fail]);
		stream::unfold(VecDeque::from(actions), |mut actions| async move {
			loop {
				match actions.pop_front()? {
					ScriptStep::Update(update) => return Some((Ok(update), actions)),
					ScriptStep::Delay(duration) => tokio::time::sleep(duration).await,
					ScriptStep::Hold => std::future::pending::<()>().await,
					ScriptStep::Fail => return Some((Err(Error::Provider), VecDeque::new())),
				}
			}
		})
		.boxed()
	}
}
