# Illustrate Workflow

Load on `/illustrate`, or when a plan/requirements/concept needs an interactive HTML illustration. Apply `copilot-instructions.md` Sections 2–4. Do not implement product code unless the user asked for the illustration only.

**Purpose:** Make one item of interest easier to understand. Slideshow or
interactive demo. Zip and open offline: relative paths, no build, no CDN in the
shipped HTML. Ask before vendoring allowlisted libraries. Playwright is an
agent create-time check only; the illustration does not mention or require it.

## Stage Order

1. Confirm scope
2. Choose form
3. External dependencies (blocking)
4. Build
5. Verify

## Stage 1 — Confirm scope

- Item of interest: plan, requirement set, architecture, API, flow, or named concept.
- Read that source **in full**.
- Output path: user path, else `illustrations/<slug>.html`, or `illustrations/<slug>/` if more than one file is justified.

## Stage 2 — Choose form

- Default: **one HTML file**. Extra files only when they clearly help; then keep them in one folder.
- Not a product web UI. Do not load `tech-web.md`. Do not apply Section 4.8
  responsive, dark-mode, or shared visual-language rules. The canvas does not
  have to reflow for phones.
- Self-contained: relative paths only; no build step; no npm; no bundler; no CDN
  URLs in the shipped HTML.
- SVG inlined or as sibling `.svg` files is welcome.
- Animation and interactivity are welcome when they **explain**. Do not decorate.
- Do **not** download vendor files or add `<script>` / stylesheet links until
  Stage 3 is answered.

Docs for the agent: [MDN HTML](https://developer.mozilla.org/en-US/docs/Web/HTML), [MDN SVG](https://developer.mozilla.org/en-US/docs/Web/SVG), [Mermaid](https://mermaid.js.org/intro/), [three.js](https://threejs.org/docs/), [Babylon.js](https://doc.babylonjs.com/).

## Stage 3 — External dependencies (blocking)

Ask whether this illustration may **vendor** allowlisted libraries. Create-time
only: download the pinned files into `vendor/`, then link them with relative
paths. Do not default to yes. Do not treat the allowlist as pre-approved.
Playwright is not an illustration library. Do not include it in this question.
Do not ship Playwright, `package.json`, or `*.spec.ts` in the illustration zip.

List every library **this illustration would actually vendor** if the user says
yes. Use the allowlist rows below. Omit rows this file will not use. For each
listed item: name, exact version, purpose, license, full URL.

If no library is needed, skip the question and build with inline HTML, CSS, and
SVG only (a single HTML file is fine).

Do not proceed to Build until the user answers (or the skip rule above applies).

```markdown
## Q{n} — External libraries
**Source:** Illustrate
**Context:** Inline HTML/CSS/SVG needs no download. If you say yes, the agent
downloads only the listed allowlist files into `vendor/` at create time. The
zip uses relative paths and works offline. The shipped HTML has no CDN and no
Playwright.
**Question:** Vendor these libraries into the illustration folder?
**Options:** 1) No — inline HTML/CSS/SVG only · 2) Yes — download only the
libraries listed below into `vendor/` · or free-text

Proposed (this illustration only):

- {name} {version} — {purpose}. License: {MIT | Apache-2.0 | BSD-like}. `{url}`
```

If **No:** do not download Pico CSS, Mermaid, three.js, OrbitControls,
Babylon.js, or any other extra file. Use inline CSS and inline SVG. Do not ship
3D engines; switch that form to SVG or a simpler layout.

If **Yes:** download **only** the named allowlist files (exact URL, exact
version) into `illustrations/<slug>/vendor/`. Same three.js version for core and
OrbitControls. HTML, CSS, and import maps point at `./vendor/…` only. Do not
leave a `cdn.jsdelivr.net` (or other host) URL in the shipped HTML. If a
download fails, stop; do not fall back to a CDN. 3D explains structure (API
graph, process, layout), not decoration.

Create-time fetch (no repo script; `curl` exists on Windows 10+, macOS, Linux):

```bash
curl -L --fail -o vendor/pico.min.css "https://cdn.jsdelivr.net/npm/@picocss/pico@2.0.6/css/pico.min.css"
```

Use the allowlist URL and a `vendor/` file name from the table. Do not `npm`,
`npx`, or a bundler. Do not rewrite the downloaded file. Record source URL,
version, and license in an HTML comment.

Anything else (D3, highlight.js, another 3D engine, another version) → another
Grill Me: name, what it does, why needed, license. Do not vendor it on a silent
yes.

**Allowlist** (eligible after Yes only; pin the exact version in the URL):

| Lib | License | Save as | URL |
|-----|---------|---------|-----|
| Pico CSS 2.0.6 | MIT | `vendor/pico.min.css` | `https://cdn.jsdelivr.net/npm/@picocss/pico@2.0.6/css/pico.min.css` |
| Mermaid 11.4.1 | MIT | `vendor/mermaid.min.js` | `https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js` |
| three.js 0.170.0 | MIT | `vendor/three.module.js` | `https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js` |
| three OrbitControls 0.170.0 | MIT | `vendor/OrbitControls.js` | `https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/controls/OrbitControls.js` |
| Babylon.js 7.54.0 | Apache-2.0 | `vendor/babylon.js` | `https://cdn.jsdelivr.net/npm/babylonjs@7.54.0/babylon.js` |

When any `vendor/` file exists, use a folder (`illustrations/<slug>/index.html` plus
`vendor/`), not a single HTML file. three.js + OrbitControls: import map
`"three"` → `./vendor/three.module.js`.

## Stage 4 — Build

- Semantic HTML. Keyboard usable. Visible strings in **English** (Section 4.6).
  Not required to be responsive. Do not load `tech-web.md`.
- 3D: only when Stage 3 was **Yes** for three.js or Babylon.js. Relative
  `./vendor/…` only: ES modules + import map for three.js, or a script tag for
  Babylon.js. Keep the camera and one explanatory object; do not ship a game.
- Inline SVG for diagrams that must ship offline. Mermaid only when Stage 3 was
  **Yes** for Mermaid (`./vendor/mermaid.min.js`).
- Copyright header on `.html` / `.svg` per `tech-solution.md` when that skill is
  loaded; otherwise `<!-- {COPYRIGHT} -->`.

### Slides

When the illustration is a slideshow, **all** of the following are required:

1. **Slide overview** — a visible list or grid of every slide (title + index). Clicking a row goes to that slide.
2. **Navigation** — previous / next, and a position indicator (`3 / 12`). Keyboard: `←` `→` (and `Home` / `End` when easy).
3. **One slide visible at a time** in the main stage; overview may stay on the
   side or as a drawer. Do not omit slides from the overview.

```html
<nav aria-label="Slide overview">
  <ol>
    <li><a href="#slide-1">Intent</a></li>
    <li><a href="#slide-2">Public API</a></li>
  </ol>
</nav>
<p><button type="button">Previous</button> <span>2 / 8</span> <button type="button">Next</button></p>
```

## Stage 5 — Verify

Create-time check for the agent. Load `tech-playwright.md`. Do not eyeball. The
shipped HTML, `vendor/` files, and zip must not mention Playwright, Node, or a
test runner.

- Playwright is a repo tool, not an illustration library. If the repo has no
  approved Playwright runner, New Dependency Protocol (`tech-playwright.md`
  Permission) before adding one. Do not `npm init`, do not add `package.json`
  to the illustration folder, and do not use `npx`.
- One-off `file://` (or static server) screenshot / headed check. Do not add
  `*.spec.ts` to the illustration folder or zip. Keep-tests, if the user asks,
  live elsewhere in the repo.
- Assert: page loads; for slides, overview count, next/prev, position text;
  for 3D, canvas (or WebGL) is visible and not empty.
- Zip test: copy the folder or file away from the repo; open `file://`;
  **no network**. Relative `./vendor/…` paths must resolve.

Run the Node (or other stack) commands in `tech-playwright.md` for this OS —
Unix env prefix or PowerShell `$env:NAME = "value"`. Equivalent:
`npm exec playwright test` when the repo uses npm. Do not treat a single
`pnpm` line as the only command. Do not add a `.ps1` wrapper.

## Completion

Status table, path, how to open (`file://`, no network), risks ≤5. Chat: path
only.
