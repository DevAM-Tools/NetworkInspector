# Illustrate Workflow

Load on `/illustrate`, or when a plan, requirement, or concept needs an interactive HTML illustration. Apply `copilot-instructions.md`. Do not implement product code unless asked.

**Purpose:** Make a person understand one thing they did not understand before. The content is the subject of the animation, not its caption. The result opens offline: relative paths, no build, no CDN.

## Stages

1. Confirm scope
2. Teaching design
3. Choose form
4. External dependencies
5. Build
6. Verify

## Stage 1 — Confirm scope

- Item: plan, requirement set, architecture, API, flow, or named concept. Read that source in full.
- Output: user path, else `illustrations/<slug>.html`, or `illustrations/<slug>/` when more than one file is needed.

## Stage 2 — Teaching design

❗ Design the lesson before drawing anything. Write this card as an HTML comment at the top of the file:

```text
Learner:        who, and what they already know
Insight:        the one sentence they understand afterwards and did not before
Mechanism:      what changes, what causes the change, what stays the same
Misconception:  the wrong mental model this must break
Example:        one concrete case with real values (the invoice with 3 rows, the key "cat" hashing to 7)
Failure:        the case where it breaks or the naive approach loses
Check question: what the learner can answer at the end
```

Then write the storyboard, one row per beat:

```text
Beat | What visibly changes in the subject | What the learner realizes | Caption
```

Arc, in this order. Drop a beat only when the subject has none:

1. **Problem:** what hurts without this, shown happening.
2. **Naive attempt:** the obvious approach, and how it fails.
3. **Idea:** the one change that fixes it.
4. **Worked example:** the mechanism, step by step, with real values.
5. **Failure or edge:** where it breaks, or what it costs.
6. **Takeaway:** the Insight in one sentence, and the Check question.

## Stage 3 — Choose form

Not a product UI: do not load `tech-web.md` or apply Section 4.8. Default is one HTML file with inline SVG and JavaScript.

❗ Every motion shows a state change of the subject itself: data transforms, values change, structures grow or split, pointers move, memory fills, a queue drains.

```text
Wrong: Dots travel along arrows between boxes.
Wrong: Clicking "next" thickens the border of the next box.
Wrong: Bullet points fade in beside a static diagram.
Right: The array sorts: two bars swap, the comparison count rises, the matching code line lights up.
Right: A packet shows its real sequence number; each side's state changes when it arrives; the lost one leaves a visible gap.
Right: The hash table reaches its load limit, doubles, and each key visibly moves to its new bucket.
```

Pick the form from the subject:

| Subject | Show |
|---------|------|
| Algorithm | Real input traced: data moves, variables show values, the current code line beside it |
| Protocol or flow | Messages with their real content on a timeline; each party’s state changes |
| Data structure or memory | Actual cells and pointers growing, splitting, being reclaimed |
| Concurrency | Parallel timelines; the learner picks the interleaving and sees the race produce a wrong value |
| Architecture or API | One concrete request followed through; what each part adds; then the failure path |
| Trade-off or performance | A slider changes the parameter; curves or counters respond live; the break-even shows |
| Before and after | The same input through both, side by side; the outcome differs visibly |

A still diagram or stepped sequence is the right form when nothing changes over time. Motion that adds nothing is worse than none.

**Interaction changes the outcome.** Offer at least one where the subject allows it:

- The learner changes the input: types a value, drags a slider, removes an element.
- **Predict, then reveal:** ask what happens next before showing it.
- The learner can break it: trigger the failure case.

Clicking through a fixed sequence is not interaction; it is paging.

## Stage 4 — External dependencies

No library needed: skip this stage. Inline HTML, CSS, SVG, and JavaScript are the default.

Otherwise ask before any download or `<script>` link (Section 4.16 form, `**Source:** Illustrate`). List only what this illustration would vendor: name, exact version, purpose, license, full URL. Do not default to yes. Do not proceed until answered.

**Yes:** download only the named files, exact URL and version, into `illustrations/<slug>/vendor/`. Link with `./vendor/…`. Leave no CDN URL in the shipped HTML. A failed download stops the run; do not fall back to a CDN. Record source URL, version, and license in an HTML comment. Do not rewrite the file. Do not use `npm`, `npx`, or a bundler.

```bash
curl -L --fail -o vendor/pico.min.css "https://cdn.jsdelivr.net/npm/@picocss/pico@2.0.6/css/pico.min.css"
```

**No:** download nothing. Use inline CSS and SVG. No 3D.

| Lib | License | Save as | URL |
|-----|---------|---------|-----|
| Pico CSS 2.0.6 | MIT | `vendor/pico.min.css` | `https://cdn.jsdelivr.net/npm/@picocss/pico@2.0.6/css/pico.min.css` |
| Mermaid 11.4.1 | MIT | `vendor/mermaid.min.js` | `https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js` |
| three.js 0.170.0 (server-only) | MIT | `vendor/three.module.js` | `https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js` |
| three OrbitControls 0.170.0 (server-only) | MIT | `vendor/OrbitControls.js` | `https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/controls/OrbitControls.js` |
| Babylon.js 7.54.0 | Apache-2.0 | `vendor/babylon.js` | `https://cdn.jsdelivr.net/npm/babylonjs@7.54.0/babylon.js` |

- Anything else (D3, highlight.js, another version): another question. Playwright is never an illustration library.
- Any `vendor/` file: use a folder, not a single HTML file.
- **3D and `file://`:** Chromium blocks ES modules from `file://`, and three.js ≥ r160 ships modules only. Default to Babylon.js through a classic `<script>`. Offer three.js only when the user accepts “open through a local static server”; say so in the question and in Completion. three.js: import map `"three"` → `./vendor/three.module.js`, same version for core and OrbitControls.

## Stage 5 — Build

**Simulate, do not choreograph.** Run the real logic and draw its states, so stepping back, changing the input, and the failure case come for free.

```js
const trace = runAlgorithm(input); // real logic; returns one state per step
function render(step) { draw(trace[step]); caption(trace[step].why); }
```

**Controls** (animated subject): play and pause, step forward and back, restart, speed. Default is learner-paced stepping with animated transitions, not autoplay. Honor `prefers-reduced-motion`: step without transitions.

**Attention:**

- Move one thing at a time; dim the rest.
- Put the caption next to the moving object, at the moment it moves. No legend to decode: label objects directly.
- A caption says what happens and why, in one sentence. No bullet walls.
- A slide title states the claim, not the topic: “Doubling keeps inserts cheap”, not “Resizing”.

**Visual quality:**

- No default browser look. One type scale, generous space, a deliberate palette.
- Each concept owns one color for the whole illustration. Color encodes meaning, never decoration.
- Transitions 200–600 ms with easing. Scales and sizes are truthful.

**Rules:**

- Semantic HTML. Keyboard usable. Visible strings in English (Section 4.6). Responsive layout is not required.
- 3D only after a Yes in Stage 4, and only when depth explains structure. One explanatory object and a camera; no game.
- Mermaid only after a Yes.
- Copyright header on `.html` and `.svg` per `tech-solution.md`; without that skill loaded, `<!-- {COPYRIGHT} -->`.

**Slides**, when the form is a slideshow, require all of:

1. A visible overview of every slide (title and index); a click goes there.
2. Previous and next, position indicator (`3 / 12`), keys `←` `→` (`Home` and `End` when easy).
3. One slide visible at a time.

```html
<nav aria-label="Slide overview"><ol><li><a href="#slide-1">The problem</a></li></ol></nav>
<p><button type="button">Previous</button> <span>2 / 8</span> <button type="button">Next</button></p>
```

## Stage 6 — Verify

Load `tech-playwright.md`. Do not eyeball. The shipped files never mention Playwright, Node, or a test runner.

❗ **Teaching checks.** Take screenshots at start, middle, and end, look at them, and fix what fails:

| Check | Fails when |
|-------|------------|
| Mute | With all captions hidden, the screenshots do not show what happened |
| Freeze | The middle screenshot says the same as the animation |
| Replace | Replacing the motion with a highlight loses nothing |
| Insight | The Check question cannot be answered from the illustration |
| Real data | A step shows no concrete value |

**Technical checks:**

- Assert: the page loads; stepping changes **displayed values**, not only classes; step back restores them; changing the input changes the result. Slides: overview count, next and previous, position text. 3D: canvas visible and not empty.
- Quick look needs no spec: `playwright screenshot … file:///…` or the integrated browser.
- Asserted check: spec at `tests/illustrations/<slug>.spec.ts`, outside the illustration folder. Delete it after the run unless the user keeps it. Commands per OS: `tech-playwright.md`.
- No approved Playwright runner in the repo: New Dependency Protocol (Section 4.7). Never `npm init` or `package.json` in the illustration folder. No `npx`. No `.ps1` wrapper.
- Zip test: copy away from the repo, open via `file://`, no network; `./vendor/…` resolves. A static server is for debugging only, except for an accepted three.js illustration.

## Completion

Chat: the path, how to open it (`file://`, no network; or the local server for an accepted three.js illustration), the Insight, and every risk ordered by impact.
