//! First Rust archive origin (#219): the statically exported Next.js
//! frontend, `/api/build`, readiness and the stable 404/501 miss for every
//! `/api/*` route no Rust implementation answers yet.
//!
//! The C# backend (`src/archive/backend`) stays the parity oracle for the
//! whole port — behavior here mirrors it at the seams the browser and the
//! apphost can observe.

pub mod api;
pub mod frontend;
pub mod origin;
pub mod settings;

pub use origin::{Origin, build_router};
pub use settings::Settings;
