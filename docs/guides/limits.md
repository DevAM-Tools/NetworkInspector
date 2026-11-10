<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Guide — Declare, expose, and report a limit

**For:** developers who add a trust-boundary limit to a .NET application · **Prerequisites:** .NET 10, the package `NetworkInspector.Limits` ([README](../../NetworkInspector.Limits/README.md)).

A limit protects the application from input it does not control. This guide takes one limit from decision to hit message. The
code is the body of a passing test. Storing and changing a limit as a setting is the job of the settings library ([guide](settings.md)).

## Decide before you declare

Answer these once per limit. Each answer becomes an argument of the declaration.

| Decision | Choices | Rule of thumb |
|----------|---------|---------------|
| Key | `group.lowerCamelName` | The name never ends in a unit word (`maxEntrySize`, not `maxEntryBytes`): the unit belongs to the value |
| Unit | `LimitUnits.Count`, `Bytes`, `Duration`, or [your own](#compose-a-unit-from-parts) | Fixes the base unit and the words a user may write |
| Default | one number in the base unit | Choose it for the machine you size the application for; it must lie inside the range, or the declaration is refused. Never read the machine at run time |
| Hard range | minimum and maximum in the base unit | The only accepted numbers; there is no warning zone |
| `off` | `allowOff: true` (default) or `false` | A ceiling that protects other users or the system from this one is `false`: `off` is then refused like a number outside the range |
| Read-only | `readOnly: false` (default) or `true` | `true` fixes the value after creation: only the default is accepted, by `Limit` and by a settings store |
| Seven texts | title, description, reason, benefit, smaller values, larger values, effect of `off` | Each says something a user can act on; the title is the UI name; a declaration with an empty text is refused when it is built |

## Declare a limit

1. Create the definition once and keep it as a static member of the type that enforces the limit. The constructor throws an
   `ArgumentException` that names the key and the field when the declaration is incomplete or contradicts itself (a default
   outside the range, a key that names its unit); that is a bug of the declaring code, found at its first run.
2. Show the default with `Format(DefaultValue)`.
3. Parse user text with `TryParse`; it returns the refusal as text that names the key. `TryValidate` checks a number that is
   already parsed.
4. Hold the live value in a `Limit` and read `Ceiling` in the hot loop ([below](#read-the-limit-in-a-hot-loop)).
5. Report a hit with `HitMessage` and your own change hint. Never truncate, drop, or clamp the input silently.
6. Check the whole list in one test with `LimitCatalog.Check`.

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

<!-- Sync: NetworkInspector.Limits.Tests/GuideSnippetTests.cs TheQuickstartWritesTheDocumentedOutput; NetworkInspector.Limits/README.md § Quickstart. -->

## Read the limit in a hot loop

`LimitDefinition` is the declaration; `Limit` is the read side of the live value that enforcing code reads, and `LimitWriter` is the
one object that changes it. A store keeps the writer and hands out only the `Limit` (a settings manager does so with
`GetLiveLimit`), so a component that may read a limit cannot change it. Keep the `Limit` object and read its ceiling once per
decision into a local number:

```csharp
long ceiling = live.Ceiling;   // long.MaxValue when the limit is off, so "size > ceiling" is never true
```

`Ceiling` is one volatile read of one word: no lock, no allocation, no lookup. A set by any thread is visible to the next read of
any other thread, and a reader never sees an invalid value. Create one `LimitWriter` per consumer, not per definition: two stores
that share a definition must not share a holder. The members that build text (`Format`, `HitMessage`) belong on the cold path of a
hit or a help page. A read-only limit accepts only its default in `LimitWriter.TrySet`; the same value is accepted, so an
idempotent apply works.

## Compose a unit from parts

The core knows a unit only through `ILimitUnit` (five members). The `NetworkInspector.Limits.Utility` namespace builds one from
parts: a `UnitWordSet` lists the words smallest first (the first has the factor 1, the base unit), a `TableUnit` turns the set
into a unit, and a user may write any word after the number, in any letter case.

```csharp
// A unit of your own domain: a word set (the base word and larger words), wrapped in a table unit.
TableUnit frames = new("frames", "10 kframes", UnitWordSet.Of("frames").With("kframes", 1_000).With("Mframes", 1_000_000));

// A ceiling that protects other users cannot be switched off.
LimitDefinition maxFrames = new("capture.maxFrames", frames, defaultValue: 50_000_000, minimum: 1_000, maximum: 10_000_000_000,
    texts, allowOff: false);

output.WriteLine(maxFrames.Format(maxFrames.DefaultValue));
output.WriteLine(maxFrames.FormatRange());
if (!maxFrames.TryParse("off", out _, out problem))
{
    output.WriteLine(problem);
}

if (!maxFrames.TryParse("5 MiB", out _, out problem))
{
    output.WriteLine(problem);
}

// The prefix ladders save the words: Decimal("bit/s") is bit/s, kbit/s, Mbit/s, Gbit/s, Tbit/s.
TableUnit bitRate = new("bit rates", "100 Mbit/s", UnitWordSet.Decimal("bit/s"), wordRequired: true, smallestMinimum: 1);
LimitDefinition maxSpeed = new("net.maxSpeed", bitRate, 1_000_000_000, 1_000_000, 100_000_000_000, texts);
output.WriteLine(maxSpeed.Format(maxSpeed.DefaultValue));
if (!maxSpeed.TryParse("1.5 Gbit/s", out _, out problem))
{
    output.WriteLine(problem);
}
```

```text
50 Mframes
1 kframes – 10000 Mframes
capture.maxFrames: off is not accepted for this limit; write a whole number such as 10 kframes
capture.maxFrames: unknown unit 'MiB'; this value takes frames: frames, kframes, Mframes
1 Gbit/s
net.maxSpeed: whole number expected; use 1500 Mbit/s
```

<!-- Sync: NetworkInspector.Limits.Tests/GuideSnippetTests.cs TheOwnUnitExamplesOfTheGuideProduceTheDocumentedTexts. -->

| Part | Use it for |
|------|------------|
| `UnitWordSet.Of("ms")` | One base word; add larger words with `With(word, factor)` |
| `UnitWordSet.Binary("B")` | `B KiB MiB GiB TiB`; the decimal spellings `KB MB GB TB` are refused with the binary word |
| `UnitWordSet.Decimal("bit/s")` | `bit/s kbit/s Mbit/s Gbit/s Tbit/s` |
| `UnitWordSet.None` | A plain number (counts) |
| `TableUnit` `wordRequired` | A bare number would not say its scale (a duration); the value is refused with the two smallest spellings |
| `TableUnit` `smallestMinimum` | 1 where a zero value would refuse everything at once; a definition with a smaller minimum is refused |
| `TableUnit` `forbiddenKeySuffixes` | A key must not end with a word that names the unit (`Bytes`, `_ms`) |

The example of a `TableUnit` must parse as a value of the unit, and a word must not start with `e` (a number followed by `e`
reads as an exponent); the declaration refuses either. A word is one to 16 ASCII letters, `%`, or `/` (`Mbit/s` is a word), and
two words that differ only in letter case are the same word. A unit that is not table-shaped implements `ILimitUnit` itself.

## Verify

Run the snippet test and your own catalog test:

```text
dotnet test NetworkInspector.Limits.Tests/NetworkInspector.Limits.Tests.csproj -c Release -- --treenode-filter "/*/*/GuideSnippetTests/*"
```

The test passes and the output block of the README is the output of the snippet.

## Troubleshoot

These are the refusal texts a user can see from `TryParse`: the first four for the limit of the snippet (range 1 KiB – 1 TiB),
the last two for a duration limit `x.keepFor` and a limit `x.maxSpeed` of a bit-rate unit declared with `allowOff: false`.

| Text | Cause | Fix |
|------|-------|-----|
| `archive.maxEntrySize: 'MB' is a decimal unit and is not accepted; use 256 MiB` | A decimal unit is ambiguous (10^6 or 2^20) | Write the binary unit the text suggests |
| `archive.maxEntrySize: whole number expected; use 1536 MiB` | A fraction such as `1.5 GiB` | Write the whole number in a smaller unit |
| `archive.maxEntrySize: allowed 1 KiB – 1 TiB, or off` | The number is outside the hard range | Choose a number inside the range, or `off` |
| `archive.maxEntrySize: unknown unit 's'; this value takes bytes: B, KiB, MiB, GiB, TiB` | A word that the unit does not have | Use a word of the unit the text lists |
| `x.keepFor: unknown unit 'month'; this value takes durations: ms, s, min, h, d, w` | A month or a year in a duration (`2month`): no fixed length | Write the days (`60 d`) |
| `x.maxSpeed: off is not accepted for this limit; write a whole number such as 100 Mbit/s` | `off` for a limit declared with `allowOff: false` | Write a number inside the range |
