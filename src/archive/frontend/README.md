# Liedertafel archive frontend

Next.js App Router, TypeScript, Tailwind and Biome, generated with
`create-next-app`. The interface is German.

Start through the [archive AppHost](../README.md), which injects `PORT` and
`ARCHIVE_API_URL`. Browser requests use the `/api/*` development proxy.

```bash
pnpm run check
pnpm run build
pnpm exec playwright install chromium
ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser
```

The build statically exports `out/` for the ASP.NET container. `next start`, SSR
and Server Actions are not part of this hosting contract. Read the parent README
before adding routes, authentication or runtime data fetching.
