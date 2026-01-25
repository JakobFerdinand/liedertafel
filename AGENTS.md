# AGENTS.md

This repository is an Astro site for Liedertafel Mining 1906.
Use this guide when editing or extending the codebase.

## Repository Layout
- src/layouts/Layout.astro: global HTML shell and global CSS.
- src/pages/*.astro: top-level pages.
- src/components/*.astro: reusable page sections.
- src/assets: static assets referenced by components/pages.
- public: public static files served as-is.

## Commands (Build / Lint / Test)
- Install deps: `pnpm install`
- Dev server: `pnpm run dev`
- Build: `pnpm run build`
- Preview build: `pnpm run preview`

### Linting
- No lint script is configured in `package.json`.
- Do not invent lint commands; add one only if explicitly requested.

### Tests
- No test runner is configured in `package.json`.
- Single-test command: not applicable.
- If tests are added later, update this file with a single-test example.

## Astro Conventions
- Use `.astro` components for pages and UI sections.
- Keep page composition in `src/pages` and reuse UI in `src/components`.
- Use `Layout.astro` as the global shell and for global CSS.
- Global design tokens live in `:root` within `Layout.astro`.
- Prefer scoped component styles in `<style>` blocks.

## Formatting and Style
- Indentation uses tabs in `.astro` and CSS blocks.
- Use double quotes for JavaScript/TypeScript strings in frontmatter.
- Use single quotes inside HTML attributes only when necessary.
- Keep HTML and CSS aligned with existing component patterns.

## CSS and Design System
- Global typography variables live in `Layout.astro`.
  - `--font-heading` defines the heading font.
- Heading styles are centralized in `Layout.astro`.
  - Do not reintroduce per-component heading styles unless required.
- Keep component styles small and focused on layout and component-specific rules.
- Prefer existing color palette and gradients; avoid introducing new colors.
- Avoid adding global CSS unless the rule is truly shared.

## Naming Conventions
- CSS classes are descriptive and lowercase with hyphenation.
- Component file names use PascalCase (e.g., `TeamIntro.astro`).
- Page routes map to file names under `src/pages`.

## Imports and Assets
- Use relative imports for local components and assets.
- Keep imports at the top of the frontmatter block.
- Use `src/assets` for images, PDFs, and SVGs referenced in code.

## Data and Content
- Inline data structures (e.g., team list) live in frontmatter.
- Content is German; keep copy consistent with existing tone.
- When adding content, keep line length reasonable for readability.

## Error Handling and Safety
- This site is mostly static; avoid unnecessary runtime logic.
- Validate external links and include `rel="noreferrer"` when using `target="_blank"`.
- Prefer explicit `alt` text for images.

## Accessibility
- Use semantic elements (`section`, `article`, `nav`, `footer`).
- Add `aria-label` where icons or decorative elements are used.
- Ensure text remains readable over gradients and overlays.

## Performance
- Favor SVGs and optimized images where possible.
- Avoid large inline assets in components.

## Build Artifacts
- `dist/` is a build output directory.
- Avoid editing `dist/` directly unless explicitly asked.

## Cursor / Copilot Rules
- No Cursor rules found in `.cursor/rules/` or `.cursorrules`.
- No Copilot rules found in `.github/copilot-instructions.md`.

## Recommended Editing Workflow
1. Inspect related component/page styles before changing global rules.
2. Keep design consistent with existing sections.
3. Prefer small, focused edits to minimize layout regressions.
4. Run `npm run build` to verify when making larger changes.

## Notes for Agentic Tools
- Follow existing style and structure; do not introduce a new system.
- Centralize shared CSS in `Layout.astro` and use `:global()` where needed.
- Avoid adding dependencies unless requested.
- If you must add a new script or tool, update this file.
