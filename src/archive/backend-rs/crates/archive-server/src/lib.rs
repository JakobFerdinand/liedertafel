//! Rust archive origin: the statically exported Next.js frontend, auth/passkeys,
//! `/api/build`, readiness and the stable 404/501 miss for every
//! `/api/*` route no Rust implementation answers yet.
//!
//! The C# backend (`src/archive/backend`) stays the parity oracle for the
//! whole port — behavior here mirrors it at the seams the browser and the
//! apphost can observe.

pub mod ai;
pub mod api;
pub mod auth;
mod binding;
pub mod catalogue;
mod clr_guid;
pub mod events;
pub mod extraction;
pub mod frontend;
pub mod origin;
pub mod recordings;
pub mod settings;
pub mod storage;
pub use storage::queue;

pub use origin::{Origin, build_router};
pub use settings::Settings;
