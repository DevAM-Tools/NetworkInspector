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

Treat Playwright as a **permanent** repo tool. Add it through the New Dependency Protocol (`copilot-instructions.md` Section 4.7). Do not `npm init` or add a stack silently.

## Runner (stack-native; no `npx`)

❗ Do **not** use `npx`. Install Playwright per the New Dependency Protocol, then run that stack’s CLI.

| Stack | Install (after approval) | Run tests | Extra browser install |
|-------|--------------------------|-----------|------------------------|
| **.NET / Blazor** | `TUnit` + `TUnit.Playwright` in a dedicated `{App}.UiTest` project — not the unit `.Tests` sibling, not `Microsoft.Playwright.NUnit` / `.MSTest` / `.Xunit` | `dotnet test path/App.UiTest/App.UiTest.csproj -c Release` | **Default:** installed system browser — no download. **Bundled only:** see Commands |
| **Python** | `playwright`, `pytest-playwright` | `uv run pytest tests/e2e/` | **Default:** system browser via the `conftest.py` below. **Bundled only:** `uv run playwright install chromium` |
| **Node / TypeScript** (`*.spec.ts`) | `@playwright/test` as a **devDependency** in an approved `package.json` | `pnpm exec playwright test` · `npm exec playwright test` · or an approved `package.json` script | **Default:** system browser via `playwright.config.ts` below. **Bundled only:** `pnpm exec playwright install chromium` |
| **Rust** | No first-class Microsoft runner | Approved sidecar: Python `pytest-playwright` or Node `@playwright/test`. `playwright-rust` only if the user accepts it in the dependency protocol | That sidecar’s browser rule |

**.NET default:** C# UI tests are TUnit. Inherit a project `JourneyTest` base (or `PageTest` with shared launch options). Do not add a Node Playwright suite for a .NET UI unless the user asks. Node `@playwright/test` is for Node apps, create-time illustration verify, and approved sidecars.

Do not mix unit tests and browser journeys in the same project.

## Browser selection

These skills serve many repositories. Every project that adopts them uses the
same two environment variables and the same order, so a developer configures a
machine once.

❗ **Default:** the **installed system browser** through a Playwright channel —
no Playwright-downloaded Chromium on a normal dev machine with Edge or Chrome.

| Variable | Value | Behavior |
|----------|-------|----------|
| `PLAYWRIGHT_BROWSER_EXECUTABLE` | path to a browser executable | Wins over the channel. An installed Chromium-based browser that the organization permits for test automation. No download |
| `PLAYWRIGHT_BROWSER_CHANNEL` | *(unset)* or `system` | `msedge` on Windows, `chrome` on Linux x64 and macOS, bundled Chromium on Linux ARM64 (no branded Chrome exists there) |
| | `msedge`, `chrome`, `msedge-beta`, `chrome-beta`, … | That Playwright channel |
| | `bundled` or `chromium` | Playwright-managed Chromium — requires the stack’s install command once |
| `PLAYWRIGHT_BROWSERS_PATH` | shared folder, for example `%LOCALAPPDATA%\ms-playwright` or `~/.cache/ms-playwright` | Playwright’s own variable. Set it once per machine (user environment) so every project reuses one bundled download |

Keep the engine `chromium` when using branded channels. Set `Channel` (or
`ExecutablePath`) on launch options only. A launch that fails is reported with
the browser’s error; the helpers below never fall back silently (Section 4.1).

### Managed (enterprise) browsers

An organization can block automation of its managed Edge or Chrome through
Group Policy or MDM — for example `RemoteDebuggingAllowed` disabled, forced
extensions, or blocked command-line switches. Symptoms: the launch fails, the
browser closes at once, or the launch times out. `edge://policy` or
`chrome://policy` lists the active policies.

❗ Do not work around browser policy. No launcher or batch files, switches,
registry or profile edits, or portable copies meant to escape it. The policy is
the organization’s security control. Use the next option instead, in order:

1. **Another installed channel** that the policy does not restrict
   (`PLAYWRIGHT_BROWSER_CHANNEL=chrome` when only Edge is managed).
2. **An approved executable:** `PLAYWRIGHT_BROWSER_EXECUTABLE` pointing at an
   installed Chromium-based browser that IT permits for test automation.
3. **Bundled Chromium with a shared cache:** `PLAYWRIGHT_BROWSER_CHANNEL=bundled`
   plus `PLAYWRIGHT_BROWSERS_PATH`, so the download happens once per machine.
   Headless-only runs (CI) may install the smaller headless shell with
   `install --only-shell` (Playwright ≥ 1.49).
4. **Ask IT** for a test-automation exception for the system browser.

Record the chosen option in the project’s UI-test README section so the next
developer does not rediscover it.

### Launch options per stack

**.NET** (`{App}.UiTest/PlaywrightBrowserSettings.cs`):

```csharp
using System.Runtime.InteropServices;
using Microsoft.Playwright;

/// <summary>
/// Builds Playwright launch options from PLAYWRIGHT_BROWSER_EXECUTABLE and
/// PLAYWRIGHT_BROWSER_CHANNEL. Thread safety: stateless; safe from parallel tests.
/// </summary>
internal static class PlaywrightBrowserSettings
{
    private const string _ExecutableVariable = "PLAYWRIGHT_BROWSER_EXECUTABLE";
    private const string _ChannelVariable = "PLAYWRIGHT_BROWSER_CHANNEL";

    public static BrowserTypeLaunchOptions CreateLaunchOptions()
    {
        // An approved executable wins: no channel lookup, no download.
        string? executable = Environment.GetEnvironmentVariable(_ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(executable))
        {
            return new BrowserTypeLaunchOptions { ExecutablePath = executable };
        }

        // Unset means the branded system browser of this OS.
        string requested = Environment.GetEnvironmentVariable(_ChannelVariable) ?? "system";
        string? channel = requested switch
        {
            "bundled" or "chromium" => null,
            "system" => _SystemChannel(),
            _ => requested,
        };

        return new BrowserTypeLaunchOptions { Channel = channel };
    }

    private static string? _SystemChannel()
    {
        // Linux ARM64 has no branded Chrome build; bundled Chromium is the only option.
        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            return null;
        }

        return OperatingSystem.IsWindows()
            ? "msedge"
            : "chrome";
    }
}
```

**Node** (`playwright.config.ts`):

```ts
import { defineConfig } from "@playwright/test";

// Same variables and order as the .NET and Python helpers.
const executablePath = process.env.PLAYWRIGHT_BROWSER_EXECUTABLE || undefined;
const requested = process.env.PLAYWRIGHT_BROWSER_CHANNEL ?? "system";

function systemChannel(): string | undefined {
  if (process.platform === "win32") {
    return "msedge";
  }
  // Linux ARM64 has no branded Chrome build.
  if (process.platform === "linux" && process.arch === "arm64") {
    return undefined;
  }
  return "chrome";
}

function resolveChannel(): string | undefined {
  if (executablePath || requested === "bundled" || requested === "chromium") {
    return undefined;
  }
  return requested === "system" ? systemChannel() : requested;
}

export default defineConfig({
  use: { launchOptions: { executablePath, channel: resolveChannel() } },
});
```

**Python** (`tests/e2e/conftest.py`; overrides the pytest-playwright fixture so
the variables work without CLI flags):

```python
import os
import platform
import sys

import pytest


@pytest.fixture(scope="session")
def browser_type_launch_args(browser_type_launch_args: dict[str, object]) -> dict[str, object]:
    # An approved executable wins: no channel lookup, no download.
    executable = os.environ.get("PLAYWRIGHT_BROWSER_EXECUTABLE")
    if executable:
        return {**browser_type_launch_args, "executable_path": executable}

    # An explicit --browser-channel on the command line wins over the default.
    if "channel" in browser_type_launch_args and "PLAYWRIGHT_BROWSER_CHANNEL" not in os.environ:
        return browser_type_launch_args

    requested = os.environ.get("PLAYWRIGHT_BROWSER_CHANNEL", "system")
    if requested in ("bundled", "chromium"):
        return browser_type_launch_args
    if requested != "system":
        return {**browser_type_launch_args, "channel": requested}

    # Linux ARM64 has no branded Chrome build.
    if sys.platform == "linux" and platform.machine() in ("aarch64", "arm64"):
        return browser_type_launch_args
    channel = "msedge" if sys.platform == "win32" else "chrome"
    return {**browser_type_launch_args, "channel": channel}
```

## VS Code / Cursor integration

Use **Playwright Test for VS Code** (`ms-playwright.playwright`) for headed debug on **Node/TypeScript** suites only: Test Explorer, Pick Locator, Record, trace viewer, codegen. Use the workspace’s local `@playwright/test` — not `npx`.

.NET UI tests (`{App}.UiTest`): Unix `PWDEBUG=1`, PowerShell `$env:PWDEBUG = "1"`,
the IDE test runner, or .NET trace files. Do not use that extension.

## Integrated browser

❗ Use the integrated browser on the running UI while implementing, verifying,
and debugging it (Section 4.8): open the page, navigate, click, type, read what
rendered, check the console. It does not replace a Playwright journey.

| Tool | Browser |
|------|---------|
| Cursor | Agent Browser: navigate, click, type, fill, read, screenshot, console, network |
| VS Code (Copilot) | Integrated Browser (chat tools on by default via `workbench.browser.enableChatTools`) |
| Claude Code desktop app | Browser pane |
| Claude Code CLI / VS Code extension | Claude in Chrome browser tools when connected |

No browser tool in this session: say so once, then use headed Playwright or a
trace (Commands).

## Commands

Run the journey that covers the change. A full UI-project run is for a cross-cutting UI change or final verification (`tech-test.md`). Paths start at the repository root.

### .NET UI tests (TUnit)

Tree filter levels: `/Assembly/Namespace/Class/Test` (`tech-tunit.md`). Exit code 8 = zero tests ran = wrong filter.

```bash
dotnet build path/App.UiTest/App.UiTest.csproj -c Release
dotnet test path/App.UiTest/App.UiTest.csproj -c Release -- --treenode-filter "/*/*/HomePageLoads/*"
dotnet test path/App.UiTest/App.UiTest.csproj -c Release
```

Headed debug (Unix, then Windows PowerShell):

```bash
PWDEBUG=1 dotnet test path/App.UiTest/App.UiTest.csproj -c Release -- --treenode-filter "/*/*/HomePageLoads/*"
```

```powershell
$env:PWDEBUG = "1"
dotnet test path/App.UiTest/App.UiTest.csproj -c Release -- --treenode-filter "/*/*/HomePageLoads/*"
```

Switch browser (Unix, then Windows PowerShell):

```bash
PLAYWRIGHT_BROWSER_CHANNEL=chrome dotnet test path/App.UiTest/App.UiTest.csproj -c Release
PLAYWRIGHT_BROWSER_EXECUTABLE=/opt/approved-browser/browser dotnet test path/App.UiTest/App.UiTest.csproj -c Release
```

```powershell
$env:PLAYWRIGHT_BROWSER_CHANNEL = "chrome"
dotnet test path/App.UiTest/App.UiTest.csproj -c Release
```

Bundled Chromium only (Section 4.7 exemption: `playwright.ps1` is Playwright’s
vendor-shipped install tool, not a repo script; it needs PowerShell 7 `pwsh`,
also on Linux and macOS). Build first; the tool is generated into the build
output.

```bash
dotnet build path/App.UiTest/App.UiTest.csproj -c Release
PLAYWRIGHT_BROWSERS_PATH="$HOME/.cache/ms-playwright" pwsh path/App.UiTest/bin/Release/net10.0/playwright.ps1 install chromium
PLAYWRIGHT_BROWSER_CHANNEL=bundled dotnet test path/App.UiTest/App.UiTest.csproj -c Release
```

```powershell
dotnet build path/App.UiTest/App.UiTest.csproj -c Release
$env:PLAYWRIGHT_BROWSERS_PATH = "$env:LOCALAPPDATA\ms-playwright"
pwsh path/App.UiTest/bin/Release/net10.0/playwright.ps1 install chromium
$env:PLAYWRIGHT_BROWSER_CHANNEL = "bundled"
dotnet test path/App.UiTest/App.UiTest.csproj -c Release
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

MTP filter: `tech-tunit.md`. Do not pass VSTest-style `--filter FullyQualifiedName~…`.

### Python

The `conftest.py` above reads the variables. `--browser-channel` on the command line also works.

```bash
uv run pytest tests/e2e/test_export.py -v
uv run pytest tests/e2e/test_export.py --headed --slowmo=500
PLAYWRIGHT_BROWSER_CHANNEL=chrome uv run pytest tests/e2e/test_export.py -v
PLAYWRIGHT_BROWSER_CHANNEL=bundled uv run playwright install chromium
PLAYWRIGHT_BROWSER_CHANNEL=bundled uv run pytest tests/e2e/test_export.py -v
```

```powershell
$env:PLAYWRIGHT_BROWSER_CHANNEL = "chrome"
uv run pytest tests/e2e/test_export.py -v
$env:PLAYWRIGHT_BROWSER_CHANNEL = "bundled"
uv run playwright install chromium
uv run pytest tests/e2e/test_export.py -v
```

No `uv`: use the project’s declared runner (`tech-python.md`).

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

Fix common misses: wrong cwd; `PLAYWRIGHT_BROWSER_CHANNEL=bundled` without that stack’s install command; branded browser missing on CI (use `bundled` there); a managed browser blocked by policy (Managed browsers above); targeting Debug instead of the running app URL.

## Specs

Mechanics only. Case design and suite size: `tech-test.md`.

- One journey per spec/class when possible. Node name: `feature_scenario.spec.ts`. C#: PascalCase (`tech-tunit.md`).
- Assert what the user can see (heading, table row, error text, URL).
- **Named UI requirements:** each concrete UI requirement ID / `TEST-<AREA>-<TOPIC>` gets an observable assert. Do not stop at smoke (“heading visible”) when the requirement names validation, navigation, or layout.
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
