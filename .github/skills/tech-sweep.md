# Perspective Sweep

Load on `/plan` Stage 2 and `/review`, and from `workflow-council.md`. A sweep is a short multi-view pass by the same agent. No subagent. No file.

## Views

Use these names only. Write **Skeptic first**.

| View | Ask | Do |
|------|-----|-----|
| Skeptic | What kills this? | Assume it cannot work. Find the fatal flaw. Dig if it looks solid. Dumbest user or operator error AND worst abuse + STRIDE at trust boundaries. A limit that cripples a capable host or lets abuse through (§4.2). Do not trust tests, docs, or a green path. |
| Problem-First | Right problem? | Strip the proposed solution. Rebuild the problem. Say when the question is wrong. |
| Upside | What extra value sits in scope? | Hunt in-scope gain only. No new product, dependency, or scope. |
| Outsider | Would a first-time user get this? | Drop insider context. Flag jargon, opaque names, missing first-caller steps. Unknown term → treat as unknown. |
| Builder | What is the first concrete step? | Prove it can ship. Name the first command, plan step, or `Verify`. No first step → say so. |

`/plan` runs all five. `/review` runs **Skeptic and Outsider only**.

## Run

Per view: 2–5 bullets. Cite path and symbol for code facts. An empty row → re-run that view.

Classify each bullet:

| Class | Goes to |
|-------|---------|
| **Ask** | A Decision-class user fact (Section 4.16) → Grill Me |
| **Council candidate** | ≥2 valid options, high cost of error → Decision Loop |
| **Act** | A plan constraint or a review finding |

A point that is not Decision class is **Act**: decide it.

**Review overlays:** view defect → finding; cite the view in `Context`. Skeptic hunts parts **and** composition; `none` only after both ran. Outsider: `none` only after the hunt ran.

```markdown
## Perspective Sweep
| View | Caught |
|------|--------|
| Skeptic | |
| Problem-First | |
| Upside | |
| Outsider | |
| Builder | |

**Ask:** … | none
**Council candidates:** … | none
```

Review keeps the Skeptic and Outsider rows only, and no **Ask** or **Council candidates** line.
