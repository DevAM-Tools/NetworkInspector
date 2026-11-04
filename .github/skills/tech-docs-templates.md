# Documentation Templates

Load with `tech-docs.md` when creating or restructuring a project doc, and on `/docs-repair`. Rules (doc set, reading tiers, Sync check) live in `tech-docs.md`; this file only gives the shapes.

Copy the depth, not the content. Every section is required unless marked optional. A section that does not apply says why in one line; never `n/a` alone. Links follow Section 4.6 (clickable, relative). Diagrams: Mermaid `TD`.

## README.md

```markdown
# {Name}

{One paragraph: what it is, who it is for, what problem it removes.}

## Quickstart

{Prerequisites with minimum versions. Install command. Smallest run. Expected output.}

## Usage

{Main commands or API entry points; option table or link to the reference.}

## Build and test

{Exact commands from the tech skill, Release configuration.}

## Documentation

[Architecture](docs/architecture.md) · [Concepts](docs/concepts.md) · [Domain](docs/domain.md) · [Decisions](docs/decisions/) · [Changelog](CHANGELOG.md)
{List only the docs that exist.}

## License
```

## docs/architecture.md

The Overview is tier 1: every agent reads it before it acts. Keep it on one screen.

````markdown
# Architecture — {system}

## Overview

{Two or three sentences: what the system does, for whom, where it runs.}

| Component | Folder · entry | Responsibility | May depend on |
|-----------|----------------|----------------|---------------|
| [Cli](#cli) | [`src/Cli/`](../src/Cli/) · `Program.Main` | Parses arguments; picks the command | Import |
| [Import](#import) | [`src/Import/`](../src/Import/) · `ImportCommand` | Reads invoice files; stores the valid ones | Validation |
| [Validation](#validation) | [`src/Validation/`](../src/Validation/) · `InvoiceValidator` | Checks invoice rules | — (no I/O) |

A dependency not listed in **May depend on** is a defect or needs a decision record.
{Optional: Mermaid TD context diagram — users, neighbor systems, trust levels.}

## Cli

{Responsibility in two sentences. Key types, one line each.}

**Flow:**
1. `Program.Main` parses options with `CliParser.Parse`.
2. {Next step: symbol, what crosses to which component.}

**Invariants:**
- {Rule other code relies on.} Enforced by `{symbol}`.

**Easy to miss:** {when you change X, also change Y and Z; why the compiler does not catch it. Or `none`.}

## Cross-cutting

| Concern | Approach | Enforced in |
|---------|----------|-------------|
| Errors | {result types at boundaries; exceptions only for bugs} | {link} |
| Configuration and limits | {Section 4.2 rows or link} | |
| Security boundaries | {STRIDE summary per boundary} | |
| Concurrency | {threading model; shared state; primitive} | |
| Performance | {hot paths and budgets} | |
````

## docs/concepts.md

Created with the first concept. Read by heading, never in full.

```markdown
# Concepts — {system}

How the solution works inside, across components. Structure: [architecture](architecture.md). Problem terms: [domain](domain.md).

## {Concept}

**Idea:** {two to four sentences: the model, algorithm, or format, and why it fits the problem.}
**Where:** [{component}](architecture.md#{component}) · `{entry symbol}`
**Rules:** {what must hold; what breaks the concept}
**Rejected:** {the simpler idea and why it fails, or a link to the decision record}
```

## docs/domain.md

Created with the first domain fact. Grows during the project: every workflow adds what it learns. Read by heading.

```markdown
# Domain — {system}

The problem world: true even if the code were rewritten.

## Glossary

| Term | Meaning | In code | Avoid |
|------|---------|---------|-------|
| Credit note | An invoice with a negative total that cancels an earlier one | `CreditNote` | “refund” (means a payment) |

## Rules

### {Rule name}

{The rule in one or two sentences.} **Source:** {user answer, requirement, spec link § section}. **Enforced in:** `{symbol}`.

## External facts

| Fact | Source |
|------|--------|
| A currency code has three uppercase letters | [ISO 4217](https://…) § codes |
```

Over ~300 lines: split into `docs/domain/<area>.md`; `docs/domain.md` becomes the index.

## docs/decisions/{slug}.md

A decision record: one decision, why it was made, what was rejected. It keeps the next agent from undoing a choice it does not understand. Some teams call it an ADR (architecture decision record).

```markdown
# Decision — {decision in a few words}

**Status:** proposed | accepted | superseded by [{slug}]({slug}.md)

**Context:** {forces, constraints, evidence; links}

**Decision:** {one paragraph}

**Rejected:** {option} — {why} · {option} — {why}

**Consequences:** {what gets easier; what gets harder; what to watch}

**Enforced in:** `{symbol}` in [{path}](../../{path})
```

No dates in the body; git records them. A superseded record keeps its text and names its successor.

## docs/guides/{slug}.md

```markdown
# Guide — {task}

**For:** {group} · **Prerequisites:** {tools, access, state}

## Steps

1. `{command}` → {expected observable}

## Verify

{Command and the observable that proves success.}

## Troubleshoot

| Symptom | Cause | Fix |
|---------|-------|-----|
```
