//! Static serving of the exported Next.js frontend, byte-parity with the
//! frozen C# host: `index.html` for directories, `404.html` as the body of
//! missing GET routes (status 404), and no file ever served from under
//! `/api/*` — the API namespace is reserved by the route layer first.

use std::future::Future;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::task::{Context, Poll};

use axum::body::Body;
use axum::http::{HeaderValue, Method, Request, StatusCode, header};
use axum::response::{IntoResponse, Response};
use tower::Service;
use tower_http::services::ServeDir;
use tower_http::trace::MakeSpan;

/// The static export as the C# `UseDefaultFiles` + `UseStaticFiles` pair
/// serves it: file requests come from the disk, misses only through
/// [`NotFound`], which keeps the C# main-fallback behavior (plain 404;
/// `404.html` as a body for GETs).
///
/// Files are never shadowed under `/api/*`: those routes are registered in
/// the router before this fallback runs.
pub fn static_site(root: &Path) -> StaticSite {
	StaticSite {
		inner: ServeDir::new(root)
			.call_fallback_on_method_not_allowed(true)
			.fallback(NotFound {
				pages_root: root.to_path_buf(),
			}),
	}
}

/// A wrapper so the bytes that reach the browser carry the same media types
/// the ASP.NET static middleware sets: text answers always include the
/// `charset=utf-8` the `UseStaticFiles` default writes.
#[derive(Debug, Clone)]
pub struct StaticSite {
	inner: ServeDir<NotFound>,
}

impl Service<Request<Body>> for StaticSite {
	type Response = Response;
	type Error = std::convert::Infallible;
	type Future = Pin<Box<dyn Future<Output = Result<Response, Self::Error>> + Send>>;

	fn poll_ready(&mut self, _cx: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
		Poll::Ready(Ok(()))
	}

	fn call(&mut self, request: Request<Body>) -> Self::Future {
		let mut inner = self.inner.clone();
		Box::pin(async move {
			let response = inner.call(request).await;
			Ok(message(match response {
				Ok(response) => response,
				Err(infallible) => match infallible {},
			}))
		})
	}
}

/// Copies the ServeDir response into the axum body type and adds the
/// `charset=utf-8` attribute to text media types, like the C# static
/// middleware does for every text file.
fn message<B>(response: axum::http::Response<B>) -> Response
where
	B: axum::body::HttpBody<Data = axum::body::Bytes> + Send + 'static,
	B::Error: Into<Box<dyn std::error::Error + Send + Sync>>,
{
	let (mut parts, serve_body) = response.into_parts();
	let media_type = parts
		.headers
		.get(header::CONTENT_TYPE)
		.and_then(|value| value.to_str().ok())
		.map(|value| value.to_string())
		.filter(|media_type| media_type.starts_with("text/") && !media_type.contains("charset="));
	if let Some(media_type) = media_type
		&& let Ok(value) = HeaderValue::from_str(&format!("{media_type}; charset=utf-8"))
	{
		parts.headers.insert(header::CONTENT_TYPE, value);
	}
	Response::from_parts(parts, Body::new(serve_body))
}

/// The C# main fallback: 404 status, `404.html` as the body when the export
/// carries one and the request is a browser GET, nothing otherwise.
#[derive(Debug, Clone)]
pub struct NotFound {
	pages_root: PathBuf,
}

impl Service<Request<Body>> for NotFound {
	type Response = Response;
	type Error = std::convert::Infallible;
	type Future = Pin<Box<dyn Future<Output = Result<Response, Self::Error>> + Send>>;

	fn poll_ready(&mut self, _cx: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
		Poll::Ready(Ok(()))
	}

	fn call(&mut self, request: Request<Body>) -> Self::Future {
		// The C# main fallback: body only for GET requests whose `404.html`
		// exists; every other shape answers the bare status code.
		let method = request.method().clone();
		let page = self.pages_root.join("404.html");
		Box::pin(async move {
			if method != Method::GET {
				return Ok(StatusCode::NOT_FOUND.into_response());
			}
			let Ok(contents) = tokio::fs::read(&page).await else {
				return Ok(StatusCode::NOT_FOUND.into_response());
			};
			Ok((
				StatusCode::NOT_FOUND,
				[(
					header::CONTENT_TYPE,
					HeaderValue::from_static("text/html; charset=utf-8"),
				)],
				contents,
			)
				.into_response())
		})
	}
}

/// Health probes (`/alive`, `/health`) are excluded from traces: the C# ASP.NET
/// instrumentation filter in the service defaults does the same, so the
/// dashboard shows request spans for real traffic only.
#[derive(Debug, Clone, Default)]
pub struct ProbeSkipper;

impl ProbeSkipper {
	/// Exact paths only: the skip logic must not silence a real route whose
	/// path merely starts with a probe name.
	pub fn is_probe(path: &str) -> bool {
		matches!(path, "/alive" | "/health")
	}
}

impl<B> MakeSpan<B> for ProbeSkipper {
	fn make_span(&mut self, request: &Request<B>) -> tracing::Span {
		if ProbeSkipper::is_probe(request.uri().path()) {
			return tracing::Span::none();
		}
		// Never export query strings, cookies or headers. Auth and signed URL
		// callers may carry secrets there even on rejected requests.
		tracing::debug_span!("request", method=%request.method(), uri=%request.uri().path(), version=?request.version())
	}
}
