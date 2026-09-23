# Archive-Frontend Redesign Concept — extending the chat design language

Status: concept (no code changed). Source of truth for the design language is the
ARC-022 chat redesign: `src/archive/frontend/components/chat-bereich.tsx`,
`src/archive/frontend/app/fragen/chat.css`, `src/archive/frontend/app/arbeitsplatz.css`,
and the shared tokens in `src/archive/frontend/app/globals.css`.

---

## 1. Design language of the chat redesign

### 1.1 Color tokens

The palette lives in `:root` of `src/archive/frontend/app/globals.css` (lines 3–12):

| Token | Value | Role |
| --- | --- | --- |
| `--brand` | `#823c41` | Deep wine red — links, primary buttons, accents, speaker labels |
| `--ink` | `#402326` | Warm dark brown — body text, input borders |
| `--paper` | `#ffffff` | Background |
| `--wash` | `#f3ecec` | Warm blush tint — card backgrounds, user bubble, hover fills |
| `--font-heading` | `Georgia, "Times New Roman", serif` | All headings, wordmark |

The chat adds two **locally scoped** tokens in `.chat-seite`
(`app/fragen/chat.css` lines 1–6):

| Token | Value | Role |
| --- | --- | --- |
| `--chat-muted` | `#735e60` | Muted warm taupe — secondary text, hints, placeholder, meta labels |
| `--chat-rule` | `#ded0d1` | Hairline rule color — 1px separators, suggestion borders, scrollbar |

**Problem to fix first:** these two tokens are duplicated as raw hex values in
`app/arbeitsplatz.css` (`#ded0d1` line 10, `#735e60` line 22) while other
components read `var(--chat-rule)` from an ancestor scope that only exists on
`.chat-seite`. The redesign must promote them to `:root` in `globals.css`
(e.g. `--muted: #735e60; --rule: #ded0d1;`) and keep `--chat-muted`/`--chat-rule`
as aliases on `.chat-seite` during migration. No parallel palette may be invented.

Derived-alpha usage exists (`rgba(64, 35, 38, 0.2)` in `.passkey-verwaltung`,
globals.css line 211) — treat `--ink` at reduced alpha as the sanctioned way to
get softer rules; do not add new greys.

### 1.2 Typography

- Headings: `var(--font-heading)` (Georgia), **weight 400** — the serif at regular
  weight is the personality carrier. Globals set `h1 { font-size: clamp(2.5rem, 6vw, 4.8rem) }`
  for landing pages and `.compact h1 { clamp(2.5rem, 5vw, 3.5rem) }` for section pages.
- The chat introduces tighter page-heading scales inside its column:
  `h1 clamp(2rem, 4vw, 2.8rem)`, `h2 clamp(1.3rem, 3vw, 1.6rem)` (chat.css 12–15, 82–85).
  This is the sanctioned scale for content-page headings in narrower columns.
- Body: Arial/Helvetica, `line-height: 1.7` (globals) — chat answer text uses
  `1.75` with `max-width: 66ch` (chat.css 141–144); intro paragraphs cap at `57ch`.
- Small/meta text ladder: `0.875rem` toolbar text, `0.9rem` body-adjacent, `0.8rem`
  labels/status (`chat-sprecher`, `chat-status`), `0.75rem` fine print
  (`chat-speicher-hinweis`, keyboard hint). Labels carry `font-weight: 600`/`700`.
- No ALL-CAPS eyebrow labels, no letter-spacing tricks. `.section-label` is simply
  brand-colored text at inherited size.

### 1.3 Component vocabulary (cited patterns)

These are the reusable patterns the chat established. Restyled areas must
compose from this vocabulary, not invent new ones:

1. **Quiet brand text-button** — `background: transparent; color: var(--brand); font-weight: 600;`
   with hover underline (`.chat-neustart`, chat.css 34–45) or hover invert
   (`.passkey-liste button`, globals 242–252). For de-emphasized destructive/secondary
   actions; primary actions keep the solid `button` default (brand bg, paper text,
   `border-radius: 0.3rem`, `padding: 0.8rem 1.2rem`).
2. **Composer card** — bordered container (`1px solid var(--chat-muted)` — the
   strongest border in the system), `border-radius: 0.75rem`, `background: var(--paper)`,
   `:focus-within` gets `outline: 2px solid var(--brand); outline-offset: 2px`
   (`.chat-komponist`, chat.css 252–262). Inputs inside are borderless; the
   container owns the focus ring.
3. **Field group** — `label { display:block; font-weight:600; font-size:0.9rem }`,
   inputs `border: 2px solid var(--ink); border-radius: 0.3rem; padding: 0.8rem;
   font: inherit` (`.auth-karte input/select/textarea`, globals 153–207).
   Errors: `.feld-fehler` (brand color, 600) + `role="alert"`; hints:
   `.feld-hinweis` (0.9rem). This vocabulary is already shared by anmelde-formular,
   lied-formular, fassungs-formular — keep it as the single form system.
4. **Callout/state block** — `border-left: 3px solid var(--brand); background: var(--wash);
   padding: 1rem 1.2rem` (`.chat-zustand`, `.chat-fehler`, chat.css 319–325; same
   family as `.archive-note` and `.auth-erfolg` with 4px). One pattern for every
   loading/error/success state.
5. **Suggestion/tile button** — 1px `--chat-rule` border, paper background, brand
   text, `text-align: left`, `padding: 0.9rem`, hover: wash bg + brand border
   (`.chat-vorschlaege button`, chat.css 93–110). Grid `repeat(3, minmax(0,1fr))`,
   collapsing to `1fr` at ≤540px and in the sidebar.
6. **Message/bubble roles** — user content sits on `--wash` with asymmetric radius
   `0.85rem 0.85rem 0.2rem 0.85rem`, `max-width: 85%` (92% mobile), justified end;
   assistant content is plain full-width text (`.chat-frage`/`.chat-antwort`,
   chat.css 128–139). Use "tinted = user/system-generated, plain = content" as the
   underlying idea wherever list items need visual roles.
7. **Speaker/role label** — small bold brand text above content
   (`.chat-sprecher`, chat.css 121–126).
8. **Meta divider + label row** — flex row with `border-block: 1px solid var(--rule)`,
   muted 0.875rem text, space-between (`.chat-werkzeuge`, chat.css 23–32).
   Same pattern at `.archiv-chat-ansicht`.
9. **Empty state with mark** — inline SVG (`chat-archivzeichen`, stroke
   `currentColor`, brand color) beside a serif h2, followed by actionable tiles
   (chat-bereich.tsx 310–343). Empty states are invitations to act, never bare text.
10. **Status line** — `<output>` with reserved `min-height: 1.6em`, muted 0.8rem,
    collapses when empty via `:empty` (chat.css 218–229). Copy pattern:
    "Antwort wird geschrieben …" — ellipsis progress, never spinners.
11. **Hairline sections** — content separated by `1px solid var(--rule)` borders
    (`chat-quellen-bereich` top border, `lied-fassung-block`/`fassungs-gruppe`
    top borders in globals). `border-left: 4px solid` marks the *selected* item
    (`.fassungs-gruppe[data-gewaehlt]`).
12. **Scroll region** — `max-height: min(55dvh, 42rem); overflow-y: auto;
    overscroll-behavior-y: contain; scrollbar-width: thin;
    scrollbar-color: var(--rule) transparent;` + keyboard focusable
    (`tabIndex={0}`, `:focus-visible { outline: 2px solid var(--brand) }`)
    (chat.css 47–62).
13. **Touch targets** — interactive chat buttons enforce `min-height: 2.75rem`
    (chat.css 298–306).

### 1.4 Layout patterns

- **Arbeitsplatz shell** (`arbeitsplatz.css`, `archiv-arbeitsplatz.tsx`): the chat is
  a persistent panel in the root layout, not a page widget. Sticky toggle bar
  `.archiv-chat-leiste` (`position: sticky; top: 0; z-index: 5; border-bottom: 1px solid`)
  under the header; at `≥1100px` the open chat becomes a second grid column
  (`grid-template-columns: minmax(0,1fr) minmax(23rem,0.7fr)`, gap `clamp(2rem,4vw,4rem)`)
  with a sticky panel (`top: 5rem; height: calc(100dvh - 13rem)`); below 1100px the
  chat replaces page content as an overlay view; `/fragen` is the "Großansicht"
  (`max-width: 56rem` column). Content column `.archiv-seiteninhalt` caps at
  `max-width: 68rem; margin-inline: auto`.
- Page intro block `.introduction` (`padding-block: 4rem 2rem`, compact variant
  `padding-bottom: 1rem`) opens every page: `section-label` → `h1` → lead paragraph.
- Measures: 65ch default paragraphs, 66ch chat answers, 57ch intros; cards cap at
  `max-width: 32rem` (`.auth-karte`), the chat column at `48rem`.

### 1.5 Responsive behavior

- Two breakpoints: **1100px** (chat panel ↔ overlay) and **540px** (mobile stacking:
  header becomes column, grids collapse to 1fr, search field stacks, `dl` single
  column — globals 733–768, chat.css 337–377, arbeitsplatz.css 147–159).
- `dvh` units for viewport-anchored regions; `overflow-wrap: anywhere` on
  long-value containers; `min-width: 0` on grid/flex children.
- No horizontal overflow is enforced by test (`shell.spec.ts` asserts
  `document.documentElement.scrollWidth <= window.innerWidth`; chat.spec does the
  same for mobile).
- Mobile header collapses to `flex-direction: column` — the redesign may replace
  this with a proper collapsible nav, see area 2.

### 1.6 Motion

Deliberately nearly none. Only color/border hover transitions and text state
changes ("Antwort wird geschrieben …" with a static brand dot in `.chat-sucht`).
Keep it that way: motion only as direct feedback for user actions
(open/close chat, publish/unpublish), no entrance animations, no decorative
transitions. Respect `prefers-reduced-motion` if any animation is ever added.

### 1.7 Tech conventions to preserve

- **Tailwind v4 is imported (`@import "tailwindcss"`) but the codebase styles with
  plain semantic CSS**: German BEM-ish class names (`.chat-verlauf`,
  `.lieder-eintrag`, `.material-datei`) in `globals.css` + page-scoped CSS files.
  No utility classes in components. Redesign must continue this pattern — extend
  `globals.css` tokens/classes, do not start using Tailwind utilities.
- Next.js 16.3.5 App Router, static export (`output: "export"`), React 19.2.8.
  **Breaking changes vs. training data: implementers must read the relevant guides
  under `src/archive/frontend/node_modules/next/dist/docs/` before coding** (the
  generated `src/archive/frontend/AGENTS.md` block mandates this). No server-only
  features; client components fetch `/api/*` relative URLs.
- Biome 2.4.2 lint/format via `pnpm run check` (biome check + next typegen +
  tsc --noEmit) from `src/archive/frontend`.
- Static export build must keep working: `pnpm run build`.
- A11y floor already in place and must be kept: `skip-link`, semantic landmarks,
  `aria-live` for status/error output, `aria-pressed` toggles, visible
  `:focus-visible` outlines (3px currentColor globally), labelled controls,
  German `lang="de-AT"`.
- Copy: German (site is de-AT), du-form for members in the workspace, Sie-form on
  the public maintenance page (existing inconsistency to keep or unify deliberately).
  Status copy uses "…" ellipsis progress phrasing.
- Behavior contracts in `src/archive/README.md` (ARC-015/016/017/018/019/022) and
  the generated `src/archive/frontend/AGENTS.md` are binding. Styling changes must
  not alter markup semantics the tests rely on (see 2.x test notes).

---

## 2. Area map

Each area = one commit (section 3). Acceptance criteria common to all areas:

- `cd src/archive/frontend && pnpm install --frozen-lockfile && pnpm run check` passes.
- `pnpm run build` (static export) passes.
- `pnpm exec playwright install chromium` then
  `ARCHIVE_BASE_URL=http://localhost:<port> pnpm run test:browser` passes, including
  the listed spec files for the area; the shell-level no-horizontal-overflow
  assertion still holds.
- German copy preserved in meaning; no English UI text introduced.

---

### Area 1 — Design tokens & shared foundation

**Files:** `app/globals.css`, `app/fragen/chat.css`, `app/arbeitsplatz.css` (token parts only).

**Current state:** `--brand/--ink/--paper/--wash/--font-heading` in `:root`;
`--chat-muted`/`--chat-rule` scoped to `.chat-seite` with raw-hex duplicates in
`arbeitsplatz.css`; a long flat globals file (768 lines) mixing shell, forms,
catalogue, material, auth styles with a single 540px media block.

**Brief:**
- Promote `--muted: #735e60` and `--rule: #ded0d1` to `:root`; alias
  `--chat-muted`/`--chat-rule` to them on `.chat-seite`; replace the raw hex
  duplicates in `arbeitsplatz.css` with the variables.
- Codify the chat's scales as shared custom properties (values, not new colors):
  heading clamps, the small-text ladder (0.75/0.8/0.875/0.9rem), radius scale
  (0.2/0.3/0.45rem controls, 0.75rem composer, 0.85rem bubble), rule color, and
  the callout recipe (`.hinweis-block`-style: brand left border on wash).
- Consolidate the duplicated patterns in globals into the chat's vocabulary:
  transparent brand buttons (passkey, lied-anderer-titel, fassungs-liste) → one
  `.knopf-leise`-style class; the three state-block variants → one class family.
  Keep old class names working (tests select `.material-datei`, `.lieder-eintrag`,
  `.chat-antwort-text`, `data-testid="build-version"`, `data-status`) — rename
  only with matching test updates.
- Do not restructure the whole file in this commit; only tokens + the promoted
  shared classes. (File-splitting can ride area 2 if desired.)

**Acceptance:** check + build + full browser suite pass; visual behavior of the
chat unchanged (`chat.spec.ts` green); grep shows no remaining raw `#735e60`/`#ded0d1`
outside token definitions.

---

### Area 2 — App shell: header, navigation, status chrome, footer

**Files:** `app/layout.tsx`, `components/auth-status.tsx`, `components/maintenance-banner.tsx`,
shell rules in `app/globals.css` (`.site-header`, `nav`, `.wordmark`, `footer`,
`.skip-link`, `.maintenance-banner`) and `app/arbeitsplatz.css`.

**Current state:** Brand-colored header bar with serif wordmark ("Liedertafel /
Mining 1906" two-line) and a plain wrapping link row (6 links + AuthStatus) that
on mobile stacks into a column (globals 737–741, 751–754). AuthStatus is a bare
"Angemeldet" text + button. Maintenance banner is a full-width brand bar. Footer is
a single muted line.

**Brief:**
- Keep the brand bar + serif wordmark (it is the anchor of the identity); refine
  the nav into a **collapsible menu on mobile**: a real disclosure (`button`
  + `aria-expanded`/`aria-controls` panel) instead of a wrapped link column;
  desktop keeps the inline row at the established 0.95rem. Touch targets ≥ 2.75rem.
- Style the active route (underline/thicker decoration using the existing
  `text-underline-offset` link language or a subtle rule) — the chat toolbar's
  "meta divider" pattern (1px rule, muted meta text) fits a breadcrumb/section
  strip if one is wanted; keep chrome minimal.
- AuthStatus: present status as the chat's speaker-label pattern — small muted
  status text (`Anmeldung wird geprüft …`, brand-colored "Angemeldet") with the
  quiet brand "Abmelden" button; error copy unchanged.
- Maintenance banner: keep full-bleed brand bar (it must dominate), align
  typography with the callout recipe; it is a status line, not a card.
- Footer: keep short; set it in the muted token instead of bare inherited color.
- **Responsive requirement:** no horizontal overflow at 320–1100px; menu usable
  one-handed; chat toggle bar (`archiv-chat-leiste`) stays below the header and
  unaffected except color-token adoption.

**Playwright impact:** `shell.spec.ts` (deep link, nav clicks "Archiv"/"Systemstatus",
no-overflow, `lang="de-AT"`), `chat.spec.ts` (chat shell intact). Keep link
accessible names ("Archiv", "Mitgliederbereich", "Liederkatalog", "Archiv fragen",
"Verwaltung", "Systemstatus", "Anmelden") unchanged, or update tests in the same commit.

---

### Area 3 — Startseite (`/`)

**Files:** `app/page.tsx`, `components/suche-formular.tsx`, home styles in
`globals.css` (`.introduction`, `.archive-note`, `.lieder-suche`, `.system-status`
as embedded).

**Current state:** Classic hero: section-label, oversized serif h1 ("Was wir singen,
bleibt bei uns."), lead, search form; then an `.archive-note` left-border block and
the SystemStatus card. Search field is a 2px-border input + solid button row.

**Brief:**
- Keep the hero structure and copy; treat the oversized serif headline as the
  page's one bold element. Align the lead to the chat intro measure (57ch) and
  the chat's intro spacing rhythm.
- Restyle the search form as the chat's **composer card**: bordered container with
  `:focus-within` brand outline, borderless input inside, solid "Suchen" button
  right-aligned (same geometry as the chat send button, min-height 2.75rem).
  This makes the search echo "ask the archive" visually — start page and chat
  become siblings.
- `.archive-note` becomes the standard callout block (brand left border on wash)
  with its h2 in the compact serif scale; links inside keep brand color.
- SystemStatus embed on the home page inherits area 9 styling; no separate design.
- **Responsive:** search card full-width with stacked input/button at ≤540px
  (existing `.lieder-suche-felder` stacking, restyled); hero type already clamps.

**Playwright impact:** `suche.spec.ts` (home search navigates to `/lieder/?suche=…`
with the field labelled "Lieder suchen"), `shell.spec.ts` home heading assertion.

---

### Area 4 — Anmelden (`/anmelden`)

**Files:** `app/anmelden/page.tsx`, `components/anmelde-formular.tsx`, `.auth-karte`
form styles in `globals.css`.

**Current state:** Compact intro + `.auth-karte` (wash card, 32rem) with the two-step
email → code flow, passkey button, `.feld-hinweis`/`.feld-fehler` messages, and an
`.auth-erfolg` left-border success block. Functionally the best-styled form already.

**Brief:**
- Keep the `.auth-karte` card as the canonical form container; tune it to the
  composer-card language: same radius scale, same focus-within outline on the
  card (inputs keep their 2px ink borders — only the chat textarea is borderless;
  don't degrade form affordance), label ladder from 1.3.
- Give the two steps the chat's step/status treatment: the state between "Code
  gesendet" and verify uses the status-line pattern (muted, "…"-phrased copy),
  success/error use the callout block; cooldown countdown stays an `aria-live`
  output.
- The passkey path is a quiet brand text-button ("Mit Passkey anmelden") beneath
  the primary action — quiet/primary distinction per 1.3-1.
- **Responsive:** card already `max-width: 32rem` with fluid padding
  (`clamp(1.2rem, 4vw, 2.5rem)`); verify ≥44px targets; the `.auth-aktionen` row
  wraps (keep) and must stack cleanly at ≤540px.

**Playwright impact:** `auth.spec.ts` ("Anmelden validates…", "Anmelden requests a
code with a mocked API", "Archiv gate links to Anmelden"), `auth-flow.spec.ts`
(full code flow), `passkey.spec.ts` (mobile project). Keep `id="email"`,
`id="code"`, label texts, and button names stable.

---

### Area 5 — Mitgliederbereich (`/archiv`)

**Files:** `app/archiv/page.tsx`, `components/archiv-bereich.tsx`,
`components/passkey-verwaltung.tsx`, `.auth-karte`, `.passkey-*` styles in globals.

**Current state:** Intro ("Mitgliederbereich") + one `.auth-karte` holding welcome
text, role line, Abmelden button and PasskeyVerwaltung (list with rename/remove
inline forms, divider `.passkey-verwaltung`).

**Brief:**
- Split the single card into a small composition using the chat's meta-divider
  pattern: a header row ("Unser Archiv" + muted role line "Angemeldet als …"),
  then content, then a hairline-divided Passkeys section (`.passkey-verwaltung`
  already has the top rule — formalize it with `--rule`).
- The greeting is content, not chrome: plain text at body measure; the
  "Der Liederkatalog ist jetzt geöffnet" pointer becomes a tile/suggestion-style
  link row (1.3-5) rather than inline prose links.
- Passkey list: switch row buttons to quiet brand text-buttons (rename/remove),
  keep the inline rename input with the standard field vocabulary; the add-passkey
  row (input + button) mirrors the search composer row.
- Loading/error states ("Mitgliedschaft wird geprüft …") adopt the callout block.
- **Responsive:** passkey rows already wrap (`flex-wrap`); at ≤540px the name span
  takes full width (existing rule). Verify list rows keep ≥44px touch targets.

**Playwright impact:** `passkey.spec.ts` (enroll/rename/remove flows, mobile project),
`auth.spec.ts` "Archiv gate links to Anmelden when signed out". Keep button names
("Umbenennen", "Entfernen", "Passkey hinzufügen", "Abmelden") stable.

---

### Area 6 — Liederkatalog (`/lieder`)

**Files:** `app/lieder/page.tsx`, `components/lieder-katalog.tsx`,
`components/lied-formular.tsx` (shared with area 7), `.lieder-*`, `.lied-anlegen`,
`.lied-bearbeiten`, `.lieder-suche` styles in globals.

**Current state:** Compact intro; `.lieder-suche` rendered *inside* an `auth-karte`
class combo; editor-only "Neues Lied" card with the big LiedFormular; flat list of
`.lieder-eintrag` rows separated by wash hairlines (h3 title, "Auch bekannt als",
Urheber meta, fundstelle lines, editor status pill `.lieder-status`, action row);
pagination `.lieder-seiten` (Zurück/Stand/Weiter).

**Brief:**
- The search block stops borrowing `auth-karte` and becomes the same composer-card
  search as the start page (one shared class, one look, two placements).
- Catalogue rows: adopt the chat's content/list rhythm — generous row spacing
  (`1.8rem`-ish grid gap or the existing 1rem padding raised), title in the
  serif at the chat h2 scale (`clamp(1.3rem, 3vw, 1.6rem)`), meta (Urheber,
  andere Titel, Fundstellen) in the muted 0.9rem ladder. Fundstellen ("Getroffen
  im Liedtext.") are the catalogue's "sources": style them like chat source
  references (small, muted, brand markers) to create a cross-area echo.
- Editor controls: status pill `.lieder-status` keeps brand-outline but aligns to
  the small-label ladder; Bearbeiten/Veröffentlichen/Zurückziehen as primary
  (solid) for the state-changing action and quiet brand for Bearbeiten-toggle;
  busy copy ("Wird veröffentlicht …") unchanged.
- The inline `.lied-bearbeiten` form card keeps the form vocabulary; the "Neues
  Lied" editor card becomes a collapsible disclosure (`details` or button-toggle
  like Bearbeiten) so members don't scroll past a form wall — editors only.
- Pagination: quiet text buttons + muted "Seite x von y" stand, using the
  chat-werkzeuge row pattern; ≥44px targets.
- **Responsive:** rows stack naturally; action rows wrap; search composer stacks
  at ≤540px; no horizontal overflow (asserted suite-wide).

**Playwright impact:** `lieder.spec.ts` (member/editor catalogue views, publish
flow, status marks), `suche.spec.ts` (results, Fundstellen, pagination, empty
states, editor lyrics editing from results). Selectors use roles/names and
`.lieder-eintrag` — keep that class name.

---

### Area 7 — Lied-Detail (`/lied`)

**Files:** `app/lied/page.tsx`, `components/lied-detail.tsx`,
`components/fassungs-wahl.tsx`, `components/noten-bereich.tsx`,
`components/audio-spieler.tsx`, `components/midi-spieler.tsx`,
`components/fassungs-formular.tsx`, `.lied-ansicht`, `.fassungs-*`, `.noten-*`,
`.material-*`, `.audio-*` styles in globals.

**Current state:** Status pill + serif title + Urheber meta; FassungsWahl as
fieldset of arrangement groups with `aria-pressed` version buttons (selected group
gets 4px brand left border); NotenBereich lists published material grouped by
type (Noten/Audio/MIDI) with view/listen buttons, PDF iframe, download links,
audio/MIDI players; editor batch-upload fieldset with per-file rows, progress
states, resume/cancel/retry; editor "Fassungen pflegen" and "Lied bearbeiten"
cards.

**Brief:**
- This page is the deepest member surface — give it the chat's **document
  reading** treatment: title block like a chat answer header (serif title,
  muted meta line), generous reading measure, hairline-separated sections.
- FassungsWahl is the selection vocabulary source: version buttons become the
  suggestion-tile pattern (1.3-5); selected version gets the brand treatment it
  already has (`aria-pressed` → solid brand). The selected arrangement's 4px left
  border stays — it is the system's "selected" marker.
- Material entries: voice label as speaker-label pattern (small bold brand),
  meta line (Fassung · Größe · Typ) in the muted ladder — mirroring chat source
  rows. "Noten anzeigen"/"Anhören" primary; "Herunterladen" already `.noten-laden`
  (brand outline) — keep as quiet variant. The PDF iframe keeps its soft border
  but moves to `--rule`.
- Players: keep all behavior (single-active rule, ticket renewal, tempo, labels);
  restyle only — bordered card (`--rule`, 0.3rem radius), brand border when
  playing (`.audio-laeuft` exists), tabular time display (keep
  `font-variant-numeric: tabular-nums`), range inputs with ≥44px thumb targets on
  touch. Error copy unchanged.
- Editor batch upload: per-file rows adopt the message-row structure (name +
  meta + status in one head row, fields in the material-felder grid); status text
  ("wird übertragen … 42 %") keeps the status-line style; progress could add a
  thin brand progress bar (the only new decoration allowed, it answers a real
  state); resume/cancel/retry buttons quiet vs primary per 1.3-1.
- Editor cards "Fassungen pflegen"/"Lied bearbeiten" keep `.auth-karte` container
  styling with the area-4 form refinements; the fassung blocks keep dashed
  separators (`1px dashed` is already in the vocabulary for versioned sub-items).
- **Responsive:** PDF iframe full-width (already), players' `.audio-bereiche`
  sliders wrap with `min-width: 6rem` (keep), batch rows stack at ≤540px
  (`.material-felder` auto-fit grid already collapses); no overflow with long
  file names (`overflow-wrap: anywhere` present — keep).

**Playwright impact:** the largest suite — `noten.spec.ts`, `materialien.spec.ts`,
`audio-wiedergabe.spec.ts`, `midi-wiedergabe.spec.ts`, `lieder.spec.ts`
(fassung selection tests use role headings "Originalfassung"/"Satz für Männerchor"
and `.lied-fassung-block`, `.material-datei`, `data-status`). Keep all class names,
`aria-pressed` semantics, button names ("Noten anzeigen", "Anhören",
"Herunterladen", "Material hochladen", "Material übertragen", "Erneut versuchen")
and the `<iframe title="Noten (PDF) …">` intact.

---

### Area 8 — Verwaltung (`/verwaltung`)

**Files:** `app/verwaltung/page.tsx`, `components/mitglieder-verwaltung.tsx`,
`.mitglieder-liste`, `.mitglied-aktionen`, `.adress-wechsel*` styles in globals.

**Current state:** Long intro paragraph; invite form in `.auth-karte`; members
table (E-Mail/Name/Rollen/Status/Einladung/Aktion) with per-row select + buttons
and an embedded `<details>` address-change form; table scrolls horizontally via
`.mitglieder-liste { overflow-x: auto }`.

**Brief:**
- Intro: trim to the chat intro pattern (the current paragraph is documentation,
  not UI copy — move explanations into `.feld-hinweis`/callouts where they matter:
  near the invite form and the address-change flow).
- Invite form: area-4 card refinements; the "role" select keeps standard field
  vocabulary.
- The table is the mobile pain point. Keep the table on desktop (real tabular
  data), but at ≤700–800px switch to a **card-per-member list**: name/email as
  the row title, role/status/invitation as muted meta lines, actions as a wrapped
  button group, address change as the disclosure it already is. CSS-only via
  `display: block` re-composition or duplicated markup — choose CSS-first to keep
  test selectors working.
- Danger/disabling actions ("Deaktivieren") get the quiet treatment; confirm-safe
  actions stay primary. Busy copy unchanged ("Wird deaktiviert …").
- Status/invitation values as small labels using the `.lieder-status`-style
  outline pill (brand outline for "Aktiv", muted variant for "Eingeladen" — two
  tints max, both from existing palette).
- **Responsive:** card list on mobile replaces horizontal scroll (keep
  `overflow-x: auto` as fallback); all row actions reachable ≥44px; long emails
  wrap (`overflow-wrap: anywhere`).

**Playwright impact:** `mitglieder.spec.ts` (invite, resend, role change,
deactivate/reactivate, address change with mocks, re-login prompts). Tests target
labels/placeholders ("Neue E-Mail-Adresse", "Code") and button names — keep names,
update selectors only if the table→card switch breaks them (allowed within this
commit).

---

### Area 9 — Systemstatus (`/system/status`) & Wartung (`/wartung`)

**Files:** `app/system/status/page.tsx`, `components/system-status.tsx`,
`app/wartung/page.tsx`, `.system-status`, `.local-check`, `dl/dt/dd` styles in globals.

**Current state:** SystemStatus is a wash card with a 2-column `dl` (Anwendung, API,
Version, Umgebung), retry on error, and a development-only "Lokale Dienste prüfen"
checker with result output. Wartung is a static two-section page (intro +
archive-note) in Sie-form.

**Brief:**
- The `dl` becomes the chat's **key–value meta list**: muted small `dt`, 600
  weight `dd`, single column at ≤540px (already), and the card keeps the wash
  background but adopts the standard radius/padding scale. The status dot idea
  from `.chat-sucht` (small brand dot) can mark "API: Verbunden" — one dot, one
  place, that's the decoration budget.
- Retry and check buttons per the button vocabulary; `.check-result` output as a
  callout block on result (success wash/brand, failure same block with
  `role="status"`/alert as today).
- Wartung: only token adoption (callout block, compact intro, link styling) —
  its plain two-section layout is already close to the target. Keep Sie-form
  copy or unify to Sie deliberately (public page); note the decision in the commit.
- **Responsive:** already fine; verify the `dl` collapse and no overflow with long
  version strings (`dd` has `overflow-wrap: anywhere` — keep).

**Playwright impact:** `shell.spec.ts` ("German deep link…" asserts the heading,
`data-testid="build-version"`, refresh survival, retry-after-outage test routes
`/api/build`; "API failures stay JSON…" clicks "Lokale Dienste prüfen" in dev).
Keep `data-testid`, heading text, and button name exactly.

---

### Not an area: Fragen (`/fragen`) and the chat panel

`chat-bereich.tsx`/`chat.css`/`archiv-arbeitsplatz.tsx` are the design source and
stay visually as-is; only token *renames* from area 1 touch their CSS (aliases
keep them rendering identically). `chat.spec.ts` must stay green throughout — it
is the regression tripwire for the whole redesign.

---

## 3. Suggested implementation order

Dependencies: everything consumes area 1's tokens; area 2 is the chrome all pages
sit in; leaf pages can then proceed independently.

| # | Commit | Scope | Depends on |
| --- | --- | --- | --- |
| 1 | `refactor(archive): promote chat design tokens to shared styles` | Area 1 | — |
| 2 | `style(archive): rework header navigation and status chrome` | Area 2 | 1 |
| 3 | `style(archive): align home page with chat design language` | Area 3 | 1, (2) |
| 4 | `style(archive): restyle login and passkey forms` | Area 4 | 1, (2) |
| 5 | `style(archive): restyle member area and passkey list` | Area 5 | 1, (2) |
| 6 | `style(archive): restyle system status and maintenance pages` | Area 9 | 1, (2) |
| 7 | `style(archive): restyle song catalogue and search` | Area 6 | 1–4 (uses search composer from 3/4) |
| 8 | `style(archive): restyle song detail and material area` | Area 7 | 1, 2, 4 (form vocabulary) |
| 9 | `style(archive): restyle member administration` | Area 8 | 1, 2, 4 |

Notes on the split:
- Each commit is independently shippable and `check`/`build`/`test:browser` green.
- Commits 3–6 are parallelizable after 2; 7–9 land last because they touch the
  largest test surfaces.
- If a commit grows beyond ~400 lines of CSS+TSX diff, split markup-neutral CSS
  work from markup changes (e.g. 8a players/material display, 8b editor upload UI)
  as `style(archive): ...` follow-ups.
- Never bundle `chat.css`/`chat-bereich.tsx` behavior changes into these commits;
  if a token rename requires touching chat.css, do it in commit 1 and run
  `chat.spec.ts` specifically (`pnpm run test:browser -- --grep "Chat"` /
  `-- --grep "Frage"`).
- Every implementer: read `node_modules/next/dist/docs/` guides for anything
  router/hydration-related before editing TSX (see 1.7), and keep the generated
  `src/archive/frontend/AGENTS.md` block intact.
