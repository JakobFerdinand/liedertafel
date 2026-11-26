# Liedertafel archive frontend

Next.js App Router, TypeScript, Tailwind and Biome, generated with
`create-next-app`. The interface is German.

Start by hand against the local Compose stack (see the
[archive README](../README.md)): pass `../dev.env` to `env` (its hyphenated
`ConnectionStrings__*` names cannot be sourced), then
`PORT=3000 pnpm run dev` — the dev-time `PORT` and the server-only
`ARCHIVE_API_URL` come from the environment. Browser requests use the
`/api/*` development proxy.

```bash
pnpm run check
pnpm run build
pnpm exec playwright install chromium
ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser
```

The build statically exports `out/` for the ASP.NET container. `next start`, SSR
and Server Actions are not part of this hosting contract. Read the parent README
before adding routes, authentication or runtime data fetching.
