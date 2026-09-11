# Choir archive

ARC-001 establishes the local walking skeleton. The frontend uses Next.js App
Router with static export (replacing the plan's Vite choice at implementation
request). ASP.NET Core serves the exported assets and `/api/*` in production;
Next.js/Turbopack supplies hot reload and a same-origin API proxy in development.
Server-only Next.js features are outside this hosting contract.

The archive targets .NET 10 LTS. The root SDK uses `10.0.100` with
`latestFeature` roll-forward; existing Functions applications retain their
runtime targets and their CI also installs the pinned SDK.
