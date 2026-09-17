# Playwright (web UI test and debug)

Load when a web UI is in scope (Blazor, HTML, CSS, or `*.spec.ts`), when
**planning** a product web UI before spec files exist, or when verifying HTML
illustrations. Case design and suite size: `tech-test.md`. Product look and
layout: `tech-web.md` (product UI only — not illustrations). Blazor component
tests: `tech-blazor.md`. C# runner: `tech-tunit.md`.

## Role

- Browser journeys and UI debug (failed `Verify` → Playwright before redesign, Section 4.15). Use Playwright **intensively** for observable UI behavior (`tech-test.md`). Not a smoke-only suite.
- Illustrations: agent create-time (and `/review` full) only. Asserts in
  `workflow-illustrate.md`. Do not ship Playwright, `package.json`, or specs in
  the illustration zip. `/review` launches the browser in **full** mode;
  illustrate always may.
- Not for component-logic tests (bUnit or the repo’s component runner).

## Permission

Treat Playwright as a **permanent** repo tool: New Dependency Protocol. Do not `npm init` or add a stack silently. Do not wrap it in a new script without approval.

## Runner (stack-native; no `npx`)

❗ Do **not** use `npx`. Install Playwright per New Dependency Protocol, then run that stack’s CLI.

| Stack | Install (after approval) | Run tests | Extra browser install |
|-------|--------------------------|-----------|------------------------|
| **.NET / Blazor** | `TUnit` + `TUnit.Playwright` in a dedicated `{App}.UiTest` project — not the unit `.Tests` sibling, not `Microsoft.Playwright.NUnit` / `.MSTest` / `.Xunit` | `dotnet test path/App.UiTest.csproj -c Release` | **Default:** installed system browser via `Channel` — no download. **Optional bundled Chromium:** after `dotnet build`, `pwsh bin/<Config>/netX/playwright.ps1 install` when `PLAYWRIGHT_BROWSER_CHANNEL=bundled` |
| **Python** | `playwright`, `pytest-playwright` | `pytest tests/e2e/` | **Default:** system browser via `channel` in launch options. **Optional bundled:** `playwright install chromium` when channel is `bundled` / unset in bundled mode |
| **Node / TypeScript** (`*.spec.ts`) | `@playwright/test` as a **devDependency** in an approved `package.json` | `pnpm exec playwright test` · `npm exec playwright test` · or an approved `package.json` script | **Default:** system browser via `launchOptions.channel` in config. **Optional bundled:** `pnpm exec playwright install` when using Playwright-managed browsers |
| **Rust** | No first-class Microsoft runner | Approved sidecar: Python `pytest-playwright` or Node `@playwright/test`. `playwright-rust` only if the user accepts it in the dependency protocol | That sidecar’s browser rule |

**.NET default:** C# UI tests are TUnit. Inherit a project `JourneyTest` base (or `PageTest` with shared launch options). Do not add a Node Playwright suite for a .NET UI unless the user asks. Node `@playwright/test` is for Node apps, create-time illustration verify, and approved sidecars.

Do not mix unit tests and browser journeys in the same project.

## Browser (default: system; switchable)

❗ **Default:** use the **installed system browser** through Playwright `Channel` — not Playwright-downloaded Chromium. No `playwright install` / `playwright.ps1 install` on a normal dev machine with Chrome or Edge.

Switch with **`PLAYWRIGHT_BROWSER_CHANNEL`** (same name across stacks when possible):

| Value | Behavior |
|-------|----------|
| *(unset)* or `system` | Branded system browser: `msedge` on Windows, `chrome` on Linux/macOS |
| `chrome`, `msedge`, `chrome-beta`, `msedge-beta`, … | Explicit Playwright channel (see Playwright .NET docs) |
| `bundled` or `chromium` | Playwright-managed Chromium — requires that stack’s install command |

Keep `BrowserName` / engine as **`chromium`** when using branded channels. Set `Channel` on launch options only.

**.NET:** shared helper + base class in `{App}.UiTest` (see Commands). **Node:** `launchOptions.channel` in `playwright.config.ts` reading the env var. **Python:** `browser_channel` fixture or launch `channel=` from the env var.

CI without a branded browser: set `PLAYWRIGHT_BROWSER_CHANNEL=bundled` and run the stack install step once on the agent image.

## VS Code / Cursor integration

Use **Playwright Test for VS Code** (`ms-playwright.playwright`) for headed debug on **Node/TypeScript** suites only: Test Explorer, Pick Locator, Record, trace viewer, codegen. Use the workspace’s local `@playwright/test` — not `npx`.

.NET UI tests (`{App}.UiTest`): Unix `PWDEBUG=1`, PowerShell `$env:PWDEBUG = "1"`,
the IDE test runner, or .NET trace files. Do not use that extension.

## Commands

### .NET UI tests (TUnit)

```bash
dotnet build path/App.UiTest.csproj -c Release
dotnet test path/App.UiTest.csproj -c Release
dotnet test path/App.UiTest.csproj -c Release -- --treenode-filter "/*HomePageLoads/*"
```

Headed debug env (Unix, then Windows PowerShell):

```bash
PWDEBUG=1 dotnet test path/App.UiTest.csproj -c Release -- --treenode-filter "/*HomePageLoads/*"
```

```powershell
$env:PWDEBUG = "1"
dotnet test path/App.UiTest.csproj -c Release -- --treenode-filter "/*HomePageLoads/*"
```

Bundled Chromium only (CI or pinned revision). Unix, then Windows PowerShell.
`pwsh playwright.ps1` is the .NET install step on every OS.

```bash
PLAYWRIGHT_BROWSER_CHANNEL=bundled pwsh bin/Release/net10.0/playwright.ps1 install chromium
PLAYWRIGHT_BROWSER_CHANNEL=bundled dotnet test path/App.UiTest.csproj -c Release
```

```powershell
$env:PLAYWRIGHT_BROWSER_CHANNEL = "bundled"
dotnet build path/App.UiTest.csproj -c Release
pwsh bin/Release/net10.0/playwright.ps1 install chromium
dotnet test path/App.UiTest.csproj -c Release
```

Switch system browser. Unix, then Windows PowerShell:

```bash
PLAYWRIGHT_BROWSER_CHANNEL=chrome dotnet test path/App.UiTest.csproj -c Release
PLAYWRIGHT_BROWSER_CHANNEL=msedge dotnet test path/App.UiTest.csproj -c Release
```

```powershell
$env:PLAYWRIGHT_BROWSER_CHANNEL = "chrome"
dotnet test path/App.UiTest.csproj -c Release
$env:PLAYWRIGHT_BROWSER_CHANNEL = "msedge"
dotnet test path/App.UiTest.csproj -c Release
```

```csharp
public abstract class JourneyTest : PageTest
{
    protected JourneyTest() : base(PlaywrightBrowserSettings.CreateLaunchOptions()) { }

    public override string BrowserName => "chromium";
}

public sealed class HomePageLoads : JourneyTest
{
    [Test]
    public async Task ExportHeadingIsVisible()
    {
        await Page.SetViewportSizeAsync(375, 667);
        await Page.GotoAsync(url);
        await Assert.That(await Page.GetByRole(AriaRole.Heading, new() { Name = "Export" }).IsVisibleAsync()).IsTrue();
    }
}
```

MTP filter: `tech-tunit.md`. Do not pass `--filter FullyName`.

### Python

```bash
pytest tests/e2e/test_export.py -v
pytest tests/e2e/test_export.py --headed --slowmo=500
PLAYWRIGHT_BROWSER_CHANNEL=chrome pytest tests/e2e/test_export.py -v
PLAYWRIGHT_BROWSER_CHANNEL=bundled playwright install chromium
PLAYWRIGHT_BROWSER_CHANNEL=bundled pytest tests/e2e/test_export.py -v
```

```powershell
$env:PLAYWRIGHT_BROWSER_CHANNEL = "chrome"
pytest tests/e2e/test_export.py -v
$env:PLAYWRIGHT_BROWSER_CHANNEL = "bundled"
playwright install chromium
pytest tests/e2e/test_export.py -v
```

### Node / TypeScript (local dependency — not `npx`)

```bash
pnpm exec playwright test
pnpm exec playwright test --project=chromium e2e/export.spec.ts
pnpm exec playwright test --debug e2e/export.spec.ts
pnpm exec playwright show-trace test-results/.../trace.zip
pnpm exec playwright screenshot --viewport-size=375,667 file:///ABS/illustrations/slug/index.html
PLAYWRIGHT_BROWSER_CHANNEL=chrome pnpm exec playwright test
```

```powershell
$env:PLAYWRIGHT_BROWSER_CHANNEL = "chrome"
pnpm exec playwright test
pnpm exec playwright screenshot --viewport-size=375,667 file:///ABS/illustrations/slug/index.html
```

`npm exec playwright test` is equivalent when the repo uses npm. Use a documented
`package.json` script when the repo already defines one. Unix env prefix or
PowerShell `$env:NAME = "value"` — not a `.ps1` wrapper.

Run headed debug with `--debug` or `--headed`. Trace on failure; do not sleep.

Fix common misses: wrong cwd; `PLAYWRIGHT_BROWSER_CHANNEL=bundled` without that stack’s install command; branded browser missing on CI (use `bundled` there); targeting Debug instead of the running app URL.

## Specs

Mechanics only. Case design and suite size: `tech-test.md`.

- One journey per spec/class when possible. Node name: `feature_scenario.spec.ts`. C#: PascalCase (`tech-tunit.md`).
- Assert what the user can see (heading, table row, error text, URL).
- **Named UI requirements:** each concrete UI `REQ{n}` / `TEST{n}` gets an observable assert. Do not stop at smoke (“heading visible”) when the requirement names validation, navigation, or layout.
- **Product UI:** run the journey at the phone (`375 × 667`) and desktop (`1280 × 720`) viewports in `tech-web.md` unless the user waived responsiveness. Add tablet (`768 × 1024`) when layout changes at 768px.
- **Illustrations:** asserts in `workflow-illustrate.md` only. Do not apply
  product viewports from `tech-web.md`. Spec files are optional keep-tests in
  the repo, never part of the zip. Illustrations are not required to be
  responsive.
- Wait for locators. Do not use `waitForTimeout` as a sync strategy.
- Seed data in-process or via a documented test hook. Do not click through setup that is out of scope.

```ts
// Wrong
await page.waitForTimeout(1000);
// Right
await expect(page.getByRole("heading", { name: "Export" })).toBeVisible();
```
