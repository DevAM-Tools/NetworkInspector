<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Limits

Trust-boundary limits for any .NET application. It depends on nothing application-specific: the package references the .NET runtime
and no other package.

A limit is a bound that protects your application at a trust boundary: the largest file you read, the deepest nesting you follow,
the longest time you wait. Written as a bare constant, a limit is invisible, cannot be changed, and fails without telling anyone why.
This package makes each limit **one declaration** that everything else reads: the setting, the command-line option, the help text,
the message when the limit is hit, and any user interface.

## What you get

The package has a small core and an optional utility part. The core never uses the utility part.

| Part | Type | What it is |
|------|------|------------|
| Core | `LimitDefinition` | The record: key, unit, fixed default, hard range, `allowOff`, `readOnly`, and seven texts (the title is the UI name). The constructor refuses an inconsistent declaration, such as a default outside the range |
| Core | `LimitValue` | A number in the base unit or `off`; `0` never means off |
| Core | `Limit` | The read side of the live holder for hot loops: `Ceiling` is one volatile read, no lock, no allocation; it cannot change the value |
| Core | `LimitWriter` | The owner of one `Limit` and the only way to change it: `TrySet` checks the range and the flags |
| Core | `ILimitUnit` | The seam to the unit of the values: five members; implement it for a unit that is not table-shaped |
| Core | `LimitCatalog.Check` | Duplicate keys in a list of definitions |
| `Utility` | `UnitWordSet`, `TableUnit` | Compose a unit from words: `Binary("B")`, `Decimal("bit/s")`, `Of("ms").With("s", 1000)` |
| `Utility` | `LimitUnits` | The ready units `Count`, `Bytes`, `Duration` |
| `Utility` | `LimitMessages.HitMessage` | One sentence for every hit: what happened, the limit in effect, the key, and how to change it |

| Term | Meaning |
|------|---------|
| `LimitDefinition` | What the limit is (static, shared, immutable) |
| `LimitValue` | One value of it |
| `Limit` | The cell that enforcing code reads; it has no way to change itself |
| `LimitWriter` | Owns one `Limit` and changes it; create one per consumer, not per definition, and keep it in the store |

## Quickstart

Prerequisites: .NET 10 or later.

```text
dotnet add package NetworkInspector.Limits
```

```csharp
using NetworkInspector.Limits;
using NetworkInspector.Limits.Utility;

TextWriter output = Console.Out;

// 1. Declare the limit once: key, unit, default, hard range, and the seven texts that explain it.
LimitDefinition maxEntrySize = new(
    "archive.maxEntrySize",
    LimitUnits.Bytes,
    defaultValue: 128L * 1024 * 1024,
    minimum: 1024,
    maximum: 1L << 40,
    new LimitTexts(
        Title: "Largest archive entry",
        Description: "The largest size of one entry that is read from an archive.",
        Reason: "A hostile archive could otherwise claim an entry that uses all memory.",
        Benefit: "A broken or hostile archive is refused with a clear message.",
        SmallerValues: "Entries above the value are refused: 1 MiB refuses a 2 MiB entry.",
        LargerValues: "More memory may be used per entry: 1 GiB lets a 900 MiB entry through.",
        OffEffect: "Every entry is accepted and a hostile archive can use all memory."));

// 2. Show the default.
output.WriteLine(maxEntrySize.Format(maxEntrySize.DefaultValue));

// 3. Parse what a user wrote: a whole number with a unit, or off. Everything else is refused with a suggestion.
if (!maxEntrySize.TryParse("256 MiB", out LimitValue chosen, out string? problem))
{
    output.WriteLine(problem);
}

output.WriteLine(maxEntrySize.Format(chosen));
if (!maxEntrySize.TryParse("256 MB", out _, out problem))
{
    output.WriteLine(problem);
}

// 4. Hold the live value and read it in the hot loop: one volatile read, no allocation, no lookup.
LimitWriter writer = new(maxEntrySize);
Limit live = writer.Limit;
if (!writer.TrySet(chosen, out problem))
{
    output.WriteLine(problem);
}

long entrySize = 300L * 1024 * 1024;
if (entrySize > live.Ceiling)
{
    // Report the hit with the standard sentence; the application supplies how to change the limit.
    output.WriteLine(maxEntrySize.HitMessage("Entry is 300 MiB", live.Value, "Change it with app set archive.maxEntrySize <value>."));
}

// 5. Check the catalog once in a test: duplicate keys.
IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check([maxEntrySize, maxEntrySize]);
output.WriteLine(problems[0].Message);
```

<!-- Sync: NetworkInspector.Limits.Tests/GuideSnippetTests.cs TheQuickstartWritesTheDocumentedOutput; docs/guides/limits.md § Declare a limit. -->

Output:

```text
128 MiB
256 MiB
archive.maxEntrySize: 'MB' is a decimal unit and is not accepted; use 256 MiB
Entry is 300 MiB. Limit: 256 MiB (setting archive.maxEntrySize). Change it with app set archive.maxEntrySize <value>.
The key 'archive.maxEntrySize' appears twice; a key names one limit.
```

## Value grammar

A user writes a value as a whole number with an optional unit word, or `off`. Digits may be separated by `_` (`10_000`); case of
`off` and of unit words is ignored. The parser is a hand-written single pass, so a hostile value cannot make it slow.

| Unit | Unit words | Notes |
|------|------------|-------|
| `LimitUnits.Bytes` | `B`, `KiB`, `MiB`, `GiB`, `TiB` | 1 KiB = 1024 B; decimal units such as `MB` are refused with the binary replacement |
| `LimitUnits.Duration` | `ms`, `s`, `min`, `h`, `d`, `w` | A word is required; a bare number does not say seconds. A month or a year has no fixed length and is an unknown word |
| `LimitUnits.Count` | none | A plain whole number |
| your own `TableUnit` | the words you compose | See [the guide](../docs/guides/limits.md#compose-a-unit-from-parts) |
| any | `off` | The limit is switched off; `Limit.Ceiling` is then `long.MaxValue`. A definition may refuse it |

No sign, no fraction, no hexadecimal form. An ambiguous or inexact text is refused with the exact text to write instead.
Words compare ignoring case, so `Mbit/s` and `mbit/s` are the same word of a decimal set.

<!-- Sync: NetworkInspector.Limits/Utility/TableUnit.cs (grammar and refusal texts). -->

## What your application decides

The library has no opinion on these; each is a parameter or your own code.

| Decision | Where it enters |
|----------|-----------------|
| The default number | `defaultValue` of the `LimitDefinition` constructor: one fixed number inside the range, chosen for the machine you size the application for |
| The sentence that says how a user changes a limit | `changeHint` of `LimitMessages.HitMessage` (required) |
| Who may change a limit, and through which front end | Your settings and your command line; the library only parses and validates |
| Whether a limit offers `off` to your users | `allowOff` of the constructor (default `true`); a ceiling that protects other users says `false` |
| Whether a limit can change after creation | `readOnly` of the constructor (default `false`); a read-only limit accepts only its default |
| Units of your domain (frames, connections, bit rates) | `new TableUnit(...)` with a `UnitWordSet`, or your own `ILimitUnit` |

## Build and test

```text
dotnet build NetworkInspector.Limits/NetworkInspector.Limits.csproj -c Release
dotnet test NetworkInspector.Limits.Tests/NetworkInspector.Limits.Tests.csproj -c Release
```

## Documentation

[Guide: declare, expose, and report a limit](../docs/guides/limits.md)

## License

MIT. See the license in the repository root.
