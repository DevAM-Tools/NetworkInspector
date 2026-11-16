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
- ❗ Do **not** require page-level horizontal scroll. Code blocks, charts, and maps may scroll **inside** their own region. Tables may not (§ Tables).
- `overflow-x: hidden` on `body` / the viewport is not a responsive layout. Fix the overflowing box.
- Stack on phone. Do not hide primary actions off-canvas with no visible control and no alternative.
- Breakpoints are for **layout changes** (stack vs columns, header vs side nav). Do not add a query just to bump font size or padding by a few pixels — use `clamp()` / fluid spacing instead.
- Include `<meta name="viewport" content="width=device-width, initial-scale=1">` (or the stack equivalent). Without it, CSS breakpoints do not apply on phones.

```text
Wrong: min-width 1100px app; phone users pinch-zoom
Right: column flex on phone; row + side nav from 1280px
```

## Breakpoints

Lock these values. Do not invent extra bands unless the user asks. Custom properties **cannot** drive `@media` conditions: repeat the **literal pixels** in CSS and in `matchMedia`.

| Band | CSS | Reference devices | Layout duty |
|------|-----|-------------------|-------------|
| **phone** | no query (default) | iPhone SE 1st gen (320), SE 2nd/3rd gen (375) | one column; cards; stacked chrome |
| **tablet** | `@media (min-width: 744px)` | iPad Mini (744 portrait, 1133 landscape) | two columns; tables that fit |
| **notebook** | `@media (min-width: 1280px)` | 14-inch notebook (1280–1536) | full chrome; side nav |
| **desktop** | `@media (min-width: 1920px)` | Full HD; 28–32-inch 4K at 200 % | more columns or panels |
| **ultrawide** | `@media (min-width: 2560px)` | 4K at 150 %; 34–49-inch ultrawide | per § Width policy |

Write **mobile-first** `min-width` queries. Never define the phone layout with `max-width`.

```css
/* Right — phone is the default; larger bands add layout */
.toolbar { display: flex; flex-direction: column; gap: 0.75rem; }

@media (min-width: 744px) {
  .toolbar { flex-direction: row; align-items: center; }
}

@media (min-width: 1280px) {
  .shell { grid-template-columns: 16rem minmax(0, 1fr); }
}
```

```css
/* Wrong — desktop-first; phone is an afterthought */
.toolbar { display: flex; flex-direction: row; }

@media (max-width: 743px) {
  .toolbar { flex-direction: column; }
}
```

**Verify** with these viewports (Playwright: `tech-playwright.md`):

| Band | Viewport | Required |
|------|----------|----------|
| phone | `320 × 568` | Always |
| tablet | `744 × 1133` | Always |
| notebook | `1440 × 900` | Always |
| desktop | `1920 × 1080` | The page changes at 1920 |
| ultrawide | `3440 × 1440` | The page changes at 2560 |

## Width policy

❗ Decide the width per page type in the plan. Neither a narrow column for everything nor stretched text by default.

| Page type | Width |
|-----------|-------|
| Reading text | Lines ≤ ~75ch, centered |
| Forms | ≤ ~40rem |
| Data, lists, dashboards, editors | Use the full width: add columns or panels at each larger band |

Offer ultrawide layouts where the application gains from them. Otherwise cap the shell and center it.

## Tables

❗ A table only where its columns fit. On smaller screens the same data is a card list that scrolls vertically only. Design the card first.

- ❗ Never let a table scroll horizontally. Never hide a column on small screens without showing its value in the card.
- Default CSS is the card layout. Raise it to a table with a **container query** at the width where all columns fit: the sum of their minimum widths. That width is a property of the table, not a new band.
- Keep one markup. Label each card field from `data-label`. Set `role="table"`, `row`, `columnheader`, `cell` where a `display` change removes table semantics.
- Sorting and filtering stay available in card mode; column headers are gone there.
- In a card: the primary field is the title; actions are an icon row.

```css
.list-slot { container-type: inline-size; }

/* Default: cards */
.data-table, .data-table tbody, .data-table tr, .data-table td { display: block; }
.data-table thead { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
.data-table td::before { content: attr(data-label); font-weight: 600; margin-inline-end: 0.5rem; }

/* All five columns need 52rem */
@container (min-width: 52rem) {
  .data-table { display: table; }
  .data-table thead { display: table-header-group; position: static; width: auto; height: auto; clip-path: none; }
  .data-table tbody { display: table-row-group; }
  .data-table tr { display: table-row; }
  .data-table td { display: table-cell; }
  .data-table td::before { content: none; }
}
```

## Overflow

❗ No element paints outside its box, at any band.

- Set `min-width: 0` on flex and grid children that hold text. Prefer `minmax(0, 1fr)`.
- Set `overflow-wrap: anywhere` on user text, IDs, paths, and URLs.
- No fixed width on content. Media: `max-width: 100%`.
- `overflow: hidden` to hide an overflow is a defect, unless it truncates with an ellipsis and the full value stays reachable by tooltip.

## Icons and tooltips

Prefer an icon language: small icon buttons with tooltips over long text buttons.

- Default: icon button plus tooltip. Add visible text when an icon is ambiguous, and on destructive or irreversible actions.
- ❗ Every icon button has an accessible name (`aria-label`). The icon is `aria-hidden`.
- ❗ A tooltip opens on hover and on keyboard focus, closes with `Escape`, and stays open while pointed at. A native `title` alone fails keyboard and touch.
- ❗ Touch has no hover: open the tooltip on long-press, and never put information the task needs only in a tooltip on a touch layout.
- One icon set, inline SVG. One icon, one meaning across the app. An icon package is a dependency (Section 4.7).
- Target size ≥ 24 × 24 CSS px; ≥ 44 × 44 under `@media (pointer: coarse)`.

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

/* Right — tokens in app.css; this card follows its container.
   The slot that hosts the card must declare itself a container,
   or the @container rule never matches. */
.card-slot { container-type: inline-size; }
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
  document.body.classList.toggle("mobile", innerWidth < 744);
});

// Right — JS branches only when CSS cannot
const notebook = window.matchMedia("(min-width: 1280px)");
notebook.addEventListener("change", syncChartSize);
syncChartSize(notebook);
```

## Accessibility

- Put `aria-*` on interactive controls when visible text does not already name the control or its state.
- Pair every input with a visible label. Show errors next to the field.
- Make the UI keyboard-usable: sensible tab order, visible focus, `Enter` / `Escape` where expected.

## Limit settings

An end-user setting for a trust-boundary limit (Section 4.2) explains itself where the user changes it.

- Label with the unit. Help text next to the field: what it limits, why it exists, the default, the allowed range, and the risk of raising or lowering it.
- Mark an **open by default** limit visibly. Offer a way back to the default.
- Set `min` / `max` / `step` on the input and validate again on the server. An out-of-range value shows the range next to the field.

```text
Wrong: "Max size: [____]"
Right: "Max import size (MB): [1024]  1–65536 · default 1024.
        Stops one file from filling memory. Higher values need more RAM."
```

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

Journeys, viewports, and debug: `tech-playwright.md`. Integrated browser: Section 4.8. Case design and suite size: `tech-test.md`.
