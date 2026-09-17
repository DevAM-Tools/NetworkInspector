# Web UI Rules

Load when a **product** web UI is in scope (Blazor, Rust+HTML, Python web, HTML/CSS in an app), including **planning** before files exist. Stack extras: `tech-blazor.md` plus the language skill. Journeys and debug: `tech-playwright.md`. Do **not** load this skill for `illustrations/**` (`workflow-illustrate.md`).

Implements Section 4.8 in `copilot-instructions.md` for **product** web UIs.

## Visual language

- Keep one look across pages (type, color, spacing, chrome).
- Design for **dark mode**. Set `color-scheme: dark` (or the stack equivalent). Do not build a light theme, theme switcher, or extra palettes unless the user asks — those are out of scope.
- CSS and JS file placement: sections below.

```text
Wrong: --accent copied into home.css, export.css, settings.css
Right: --accent in app.css; this card’s grid in card.css
```

## Responsive (mandatory)

❗ Every product UI is **responsive**. That is the default, not a polish pass. Desktop-only layout is allowed only when the user **explicitly** waives it.

- Ship a real phone layout, then enhance at the locked breakpoints. Do not design desktop first and squeeze it.
- Supported width **floor is 320 CSS pixels**. Content, primary actions, and chrome stay usable from that floor up.
- Do **not** require page-level horizontal scroll. Isolated widgets (wide tables, code, charts) may scroll **inside** their own region.
- `overflow-x: hidden` on `body` / the viewport is not a responsive layout. Fix the overflowing box.
- Stack on phone. Do not hide primary actions off-canvas with no visible control and no alternative.
- Breakpoints are for **layout changes** (stack vs columns, header vs side nav). Do not add a query just to bump font size or padding by a few pixels — use `clamp()` / fluid spacing instead.
- Include `<meta name="viewport" content="width=device-width, initial-scale=1">` (or the stack equivalent). Without it, CSS breakpoints do not apply on phones.

```text
Wrong: min-width 1100px app; phone users pinch-zoom
Right: column flex on phone; row + side nav from 1024px
```

## Breakpoints

Lock these values. Do not invent 576 / 640 / 992 / 1400 extra bands unless the user asks. They come from the usual layout hops (tablet portrait, small laptop, desktop) used by Bootstrap `md`/`lg`, Tailwind `md`/`lg`/`xl`, and this repo’s Playwright sizes — not from a single device.

Custom properties **cannot** drive `@media` conditions. Repeat the **literal pixels** in CSS and in `matchMedia`.

| Band | CSS | Width | Layout duty |
|------|-----|-------|-------------|
| **phone** | no query (default) | `< 768px` | one column; stacked chrome; 320px floor |
| **tablet** | `@media (min-width: 768px)` | `≥ 768px` | two columns allowed; more padding |
| **desktop** | `@media (min-width: 1024px)` | `≥ 1024px` | full chrome; side nav OK |
| **wide** | `@media (min-width: 1280px)` | `≥ 1280px` | optional; max content width, extra columns |

Write **mobile-first** `min-width` queries. Do not use `max-width` as the primary way to define the phone layout.

```css
/* Right — phone is the default; larger bands add layout */
.toolbar { display: flex; flex-direction: column; gap: 0.75rem; }

@media (min-width: 768px) {
  .toolbar { flex-direction: row; align-items: center; }
}

@media (min-width: 1024px) {
  .shell { grid-template-columns: 16rem minmax(0, 1fr); }
}
```

```css
/* Wrong — desktop-first; phone is an afterthought */
.toolbar { display: flex; flex-direction: row; }

@media (max-width: 767px) {
  .toolbar { flex-direction: column; }
}
```

**Verify** with these viewports (Playwright: `tech-playwright.md`):

| Band | Viewport |
|------|----------|
| phone | `375 × 667` |
| tablet | `768 × 1024` |
| desktop | `1280 × 720` |

Phone **and** desktop journeys are required. Add tablet when the UI actually changes at 768px.

## CSS

Before adding a rule, choose the highest file that still stays correct: **app** → **layout / page** → **this component**. Do not copy the same rule into every page.

| Layer | Lives in | Owns |
|-------|----------|------|
| App | `wwwroot/app.css`, `styles.css`, or stack global sheet | tokens, reset, `color-scheme`, typography, shared utilities |
| Layout / page | layout or page stylesheet next to that component | shell grid, header, nav, footer |
| Component | `Name.razor.css`, `name.css` beside the component | that control only |

- Blazor component sheets: `tech-blazor.md` (`::deep` rules stay there).
- Do not add Bootstrap, Tailwind, or another CSS framework to “get” breakpoints. Use this table in the app’s own CSS unless the user approved a dependency.
- Prefer flex / grid, `gap`, `minmax(0, 1fr)`, and `clamp()` for type and spacing.
- Page chrome uses the viewport queries above. Components that sit in a **variable-width slot** (card in a sidebar or in the main column) should use **container queries** (`@container`) so they follow their parent, not the window.
- Do not put layout in `style=""` attributes. Do not inject breakpoint CSS from JavaScript.
- Keep selectors short. Restyle via classes on markup you own, not long descendant chains into child components.

```css
/* Wrong — component copies app tokens and guesses a new breakpoint */
.card { --accent: #7dd; }
@media (min-width: 900px) { .card { grid-template-columns: 1fr 1fr; } }

/* Right — tokens in app.css; this card follows its container */
.card { display: grid; gap: 1rem; }
@container (min-width: 768px) {
  .card { grid-template-columns: 1fr 1fr; }
}
```

## JavaScript

JavaScript is for **behavior** (events, fetch, dialogs, charts). CSS owns **layout**. Do not use JS as a responsive engine.

- Colocate: app startup script for boot; `Name.razor.js` / `name.js` beside the component. Blazor isolation: `tech-blazor.md`.
- Do not toggle `mobile` / `desktop` classes from `window.resize` or `innerWidth`.
- When JS **must** know the band (canvas, virtualized lists, moving a node CSS cannot reorder), use `matchMedia` with the **same pixels** as the table. Listen to `change`; do not poll `resize`.
- Prefer CSS (`flex-direction`, `grid-template-areas`, `order`) over moving DOM nodes at a breakpoint.
- Do not put CSS in JS strings. Do not sniff the user agent to pick a layout.
- Forward abort/cancel into in-flight requests when the stack provides a token (`AbortSignal`, `CancellationToken`).

```js
// Wrong
window.addEventListener("resize", () => {
  document.body.classList.toggle("mobile", innerWidth < 768);
});

// Right — JS branches only when CSS cannot
const desktop = window.matchMedia("(min-width: 1024px)");
desktop.addEventListener("change", syncChartSize);
syncChartSize(desktop);
```

## Accessibility

- Put `aria-*` on interactive controls when visible text does not already name the control or its state.
- Pair every input with a visible label. Show errors next to the field.
- Make the UI keyboard-usable: sensible tab order, visible focus, `Enter` / `Escape` where expected.

## Markup and behavior

- Use semantic HTML (`header`, `nav`, `main`, `button`). Do not use `div` + click as a button.

```html
<!-- Wrong -->
<div onclick="save()">Save</div>
<!-- Right -->
<button type="button">Save</button>
```

- Validate on the server. Do not trust the browser (Section 4.2).

## Testing

Journeys, viewports, and debug: `tech-playwright.md`. Case design and suite size: `tech-test.md`.
