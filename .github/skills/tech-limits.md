# Limits

Load when a trust-boundary limit is in scope (Section 4.2): a size, count, depth, time, or retention bound on input, memory, processors, or disk. Decisions of a project: its `custom_instructions.md` or a decision record. Implementations: § Reference implementations.

## Principles

A limit protects; it must not cripple the host. These come first; the mechanism implements them.

- ❗ Agree every limit with the user. Never invent a silent default. Under `/evolve`, the Spec delta is that agreement.
- ❗ Tie each limit to the threat it stops (exhaustion, amplification, abuse). The host is unknown: do not cap below what it may handle just to pick a number.
- Prefer a bound that scales: stream instead of buffer (Section 4.4).
- **Constant** only for facts of a format, protocol, or type. A value that depends on host, data, deployment, or risk appetite is **configurable**.
- ❗ Configurable: record where it lives, who may change it (code, operator, end user), the default, and the allowed range. Validate the configured value as external input. A ceiling that protects other users or the system stays out of end-user reach.
- ❗ Label the **posture** per limit: **secure by default** (tight default; loosening is opt-in) or **open by default** (loose default; tightening is opt-in). Choose by exposure (untrusted, remote, or multi-user input → secure) and record why. Mark an open default in the docs and next to the setting.
- ❗ End-user setting: show what it limits, why it exists, the unit, the allowed range, the default, and the risk of raising or lowering it. Reject an out-of-range value with a message that names the range.
- ❗ Exceeding a limit is an expected failure. Name the limit in the error.
- ❗ Record per limit, in the plan and in a comment at its definition: value or range · threat · constant or configurable (where, who) · posture and why.

```text
Threat: one hostile file exhausts memory. Secure by default: imports come from untrusted senders.
End user may set 1 MB to available RAM in Settings. Rejected: a constant the user cannot change (cripples large hosts).
Residual risk: a value near RAM size can still stall the app; the setting warns about it.
```

## Mechanism

- ❗ Declare each limit once. The setting, option, field, help, hit text, and any UI read that one declaration. A bound as a bare constant, long option, or attribute at a trust boundary is a defect.
- ❗ Give each limit a hard range and a distinct `off` state; `0` never means off. The declaration says what `off` can cause.
- ❗ Give each limit seven user-readable texts: title, description, reason, benefit, smaller values and larger values (each with an example that names a number), effect of `off`. Refuse an incomplete or inconsistent declaration when it is built.
- Fix each default as one number sized for the reference machine of the project. Never read memory or processors of the machine at run time.
- ❗ Make every limit a setting of the application. Which front end may change it is the application's decision.
- ❗ Report a hit as an expected failure with one sentence: what happened with the measured value, the limit in effect with its unit, the key, and the change hint the application supplies. Never truncate, drop, or clamp silently.
- Store only changed values. Precedence: one-run override, stored value, default. Ignore a stored value that a newer declaration refuses, with a visible warning.
- Accept a closed value grammar: a whole number with an optional unit of the limit's family, or `off`. Refuse everything ambiguous with a suggestion. Check overflow.
- Keep hot paths free: read the effective ceiling once per decision into a local number and compare.
- Make a parameter that reports problems required, or return the problems. An optional callback lets a caller drop a warning silently.

## Decisions of the project

The kit states no numbers. Record each decision in `custom_instructions.md` or a decision record. With no record, use the default.

| Decision | Default when the project chose nothing |
|----------|----------------------------------------|
| Posture per limit | Secure by default: active, finite, never tighter than the host handles |
| Reference machine and workload | The project names them before it sizes a default; a default is a number for that machine |
| Safety margin of defaults (per item, tables, durations) | The project names it |
| Front ends that may change a limit | Settings file and command line |
| Whether `off` is offered per limit | Yes |
| Key and text style | The project names it |
| Home of the register | A tracked document, never a git-ignored folder |

## Audit and register

- An audit is a `/review` whose scope names it. Classify every bound at a trust boundary; the result is the register.
- Keep one register per project or area. Keep it in a tracked document. Name no git-ignored path in a rule.
- Write per row: location · bound today · class · decision · key · default with origin · range · unit.
- After a release, audit the delta against that release.
- A bound becomes a limit when any answer is yes; when unsure, it becomes a limit: does the right value depend on the machine, on the data, or on how much the user trusts the input? Does it make a run wait, retain data, or use a resource the user may trade?

| Class | Meaning | Decision |
|-------|---------|----------|
| `INPUT` | Guards untrusted input (size, count, depth, rate, amplification) | Limit |
| `RESOURCE` | Budgets memory, processors, disk, threads | Limit |
| `TIME` | Waits for something outside | Limit (duration) |
| `RETENTION` | Keeps history, logs, recent entries | Limit |
| `FACT` | Fact of a format, protocol, type, OS, or library | Constant with a reason comment |
| `TUNE` | Internal tuning that does not change results | Constant with a reason comment |
| `PRESENT` | Display rule; the full value stays reachable | Constant with a reason comment |
| `UNBOUNDED` | Trust-boundary input without any bound | Limit, or a recorded reason that no limit is needed |
| `ARTIFICIAL` | Bound without format fact or threat | Remove, or reclassify with a reason |

## Tests

- Test each limit: at the limit (accepted), one above (refused; the message names key, value, and limit), `off` when offered, the default.
- Test each catalog: every declaration valid, keys unique.

## Reference implementations

| Language | Implementation |
|----------|----------------|
| C# | NuGet `NetworkInspector.Limits` (declaration, value, live holder, unit parts, hit sentence, duplicate check) and `NetworkInspector.Settings` (storage) |
| Rust | None yet. Implement the mechanism in the idiom of `tech-rust.md` |
| Python | None yet. Implement the mechanism in the idiom of `tech-python.md` |
