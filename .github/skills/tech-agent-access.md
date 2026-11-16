# Agent Access

Load when planning or changing an application’s public surface: UI features, CLI, REST, MCP, or library API. Rule: `copilot-instructions.md` Section 4.18.

Software is built so agents can use it. A UI is one client among several.

## Decide once per product

- ❗ Every application has an agent surface unless a requirement waives it with a reason (`REQ-AGENT-NONE`: why agents never need it). `/requirements` proposes it; Gate A confirms.
- ❗ Keep all domain operations in an application layer callable without a UI. No business rule in a page, component, or request handler.
- Build one surface first. Add another only when a requirement needs it.

| Product | First surface | Add when |
|---------|---------------|----------|
| Library | Public API, `docs/agents.md` | — |
| CLI or local tool | CLI with `--json` | MCP: the agent has no shell |
| Web application or service | REST with OpenAPI | CLI or MCP: agents act in real time, or need a session or workflow tools |
| Desktop or local UI | Local CLI over the application layer | Local MCP (stdio): no shell |

- MCP is a thin adapter over the same application layer. No logic in the adapter.
- Keep parity. Each user-visible operation has a surface operation, or a recorded reason for none. Plan parity as `TEST-AGENT-…` cards.

## Contract of every surface

| Property | Rule |
|----------|------|
| Output | Machine-readable (JSON on CLI behind `--json`), stable field names, deterministic order |
| Errors | Stable code, what failed, how to fix it (valid range, example). No stack trace |
| Exit | CLI: `0` success · `1` failure · `2` usage. REST: correct status |
| Non-interactive | Never prompt. Offer `--yes` and `--dry-run` for writes |
| Retry | Writes are idempotent or take an idempotency key |
| Volume | Pagination, filter, and `limit` with defaults. Say when a result was cut (Section 4.2) |
| Signal | Return names next to IDs. Default concise; offer `detail` |
| Long work | Return a handle and a status operation |
| Version | Version the contract. A breaking change is Decision class (Section 4.16) |

## Discoverability

- CLI `--help` is complete: every option, one example per command.
- Publish the OpenAPI file with the service.
- Write `docs/agents.md`, at most one screen: what an agent can do, how it authenticates, three worked calls with expected output.
- Parameters are unambiguous (`user_id`, not `user`). Describe a tool as to a new colleague.
- Build few tools around tasks, not one per endpoint. Namespace them (`export_create`, `export_list`).

## Security

- ❗ An agent is an untrusted caller: same authentication and authorization as any client, least privilege, scoped tokens.
- ❗ Treat data a surface returns as untrusted by the agent reading it: never place stored user content where it reads as an instruction.
- ❗ Destructive operations need an explicit confirm parameter, offer dry run, and write an audit entry.
- Remote MCP: OAuth, no token passthrough. Local MCP or local HTTP: bind to loopback and check `Origin`.
- Rate and size limits follow `tech-limits.md`.

## Tests

- Contract tests per surface: output shape, each error code, exit code.
- Run the three calls in `docs/agents.md` as a test.
- Never assert on a whole help text. Assert that each option appears.

## Docs

The chosen surface and any waiver go into a decision record. The operations go into `docs/agents.md`.
