<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Blazor / Razor Coding Style

## File Structure

Organize files by feature, not by type. All files belonging to a feature live together in a dedicated feature folder.

```
Features/
  FeatureName/
    FeatureNamePage.razor          # page or root component for the feature
    FeatureNamePage.razor.cs       # code-behind
    FeatureNamePage.razor.css      # scoped styles
    ChildWidget.razor              # sub-components owned by this feature
    ChildWidget.razor.cs
    ChildWidget.razor.css
    FeatureNameService.cs          # feature-scoped services
    FeatureNameViewModel.cs        # view models or state classes
Shared/                            # cross-feature reusable components only
  SharedComponent.razor
  SharedComponent.razor.cs
```

- A file belongs in `Shared/` only if it is genuinely reused by two or more unrelated features.
- Never put a feature-specific component in `Shared/` just because it is used in multiple places within the same feature.
- Services and view models that are exclusively used by one feature stay inside that feature's folder.

## Component Structure

- Component file name: `PascalCase.razor` matching the class name (e.g., `UserProfile.razor`).
- Code-behind: place all logic in `ComponentName.razor.cs` as a `partial class`; keep `@code` blocks empty or minimal.
- CSS isolation: `ComponentName.razor.css` for component-scoped styles.
- No business logic or non-trivial expressions in markup; use computed properties or methods in the code-behind.

## Parameters and Events

- Annotate parameters with `[Parameter]`; add `[EditorRequired]` for parameters that must be provided by the caller.
- Validate parameter combinations and invariants in `OnParametersSet` or `OnParametersSetAsync`.
- Use `EventCallback<T>` for component events — not `Action<T>`, `Func<T, Task>`, or plain delegates.
- Cascading values only for genuinely global application state (e.g., theme, authentication context); avoid for local data.

## Dependency Injection

- Inject services using `[Inject]` in the code-behind only; never inject directly in `.razor` markup.
- Name injected properties clearly to reflect the service's role; add an XML doc comment stating the purpose.

## Lifecycle

- Prefer `OnInitializedAsync` over `OnInitialized` for any asynchronous initialization work.
- Subscribe to events or external state in `OnInitializedAsync`; unsubscribe in `Dispose` or `DisposeAsync`.
- Implement `IDisposable` / `IAsyncDisposable` whenever the component holds resources or subscribes to events.
- Never perform CPU-intensive or long-blocking work in `OnInitializedAsync` / `OnParametersSetAsync`; offload to background services or use streaming rendering. Blocking the circuit's render thread degrades responsiveness for the user.
- Pass `CancellationToken` (sourced from a `CancellationTokenSource` disposed in `DisposeAsync`) to all async operations started by the component. Without cancellation, in-flight HTTP calls, DB queries, or background tasks outlive navigation and become fire-and-forget leaks.
- Call `StateHasChanged()` only when notified from outside the Blazor render cycle (e.g., from a background service callback or timer). Always wrap the call as `await InvokeAsync(StateHasChanged)` — calling `StateHasChanged` directly from a non-render thread causes race conditions or silent no-ops.

## Render Mode

Every interactive component must carry a deliberate render mode decision. Use the following criteria:

| Render mode | When to choose |
|---|---|
| **Static SSR** (no `@rendermode`) | No user interactivity needed; page is SEO-critical or content-only; component is a layout or wrapper with no events. |
| **Interactive Server** | Component needs server resources (database, secrets, file system); real-time server push (SignalR); complex server-side authorization; payload size matters more than interaction latency. |
| **Interactive WebAssembly** | Offline capability required; interactions must be instant with zero server round-trips after load; no server-side secrets accessed; CPU-intensive client-side computation. |
| **Auto** | Both fast first-load (served as Interactive Server) and offline/standalone WASM capability after download are required. |

- Declare `@rendermode` explicitly at the component level; never rely on inherited or ambient render mode by accident.
- If a sub-component has a different render mode requirement than its parent, extract it into a separate component and declare the mode there.
- Document the chosen render mode and its rationale in the component's XML `<summary>` (see Documentation).
- Avoid Interactive Server for components with high interaction frequency and strict latency requirements — the SignalR round-trip adds measurable lag.
- Avoid Interactive WebAssembly for components that access sensitive server-side resources; those resources would need to be exposed via an API, increasing attack surface.

## Markup

- Line length: 160 characters maximum.
- Add `aria-*` attributes on all interactive elements for accessibility.
- Use `@key` on repeated elements in `@foreach` loops to ensure stable diffing.
- Avoid deeply nested markup; extract child components to improve readability and reusability.
- Declare `@rendermode` at the component level, not scattered throughout markup.
- Prefer `@bind-Value` with explicit `@bind-Value:event` over manual event wiring for two-way binding.
- Wrap component sub-trees in `<ErrorBoundary>` to contain rendering failures gracefully. The global rule "never fail silently" still applies — log the error and display a meaningful recovery UI; `<ErrorBoundary>` prevents a single component crash from tearing down the entire circuit.

## State Management

- Per-user / per-circuit state lives in `Scoped` services. Each Blazor Server circuit gets its own DI scope; misusing lifetime leads to data leakage between users or premature disposal.
- Shared application state (e.g., job queue status, caches) lives in `Singleton` services. Document thread-safety explicitly in the type's XML `<summary>`.
- Never use `static` fields for user state — they are shared across all circuits and all users.

## Security

- Guard interactive pages with `[Authorize]` or `<AuthorizeRouteView>`; never rely solely on UI hiding for access control.
- Never trust data from `[Parameter]` bindings without validation — parameters are caller-supplied and may be tampered with via URL or parent component.
- Validate all user input server-side, regardless of any client-side validation.
- Never log sensitive data (passwords, tokens, PII) — not in components, services, or middleware.

## Naming

- Component files: PascalCase matching the class name.
- Parameters: PascalCase (`UserName`, `IsLoading`).
- Private members and local variables in code-behind: follow [C# Coding Style](CsharpCodingStyle.md).

## Documentation

- XML doc comments on all public components, parameters, and event callbacks.
- In the component `<summary>`, state the chosen render mode (Static SSR, Interactive Server, Interactive WebAssembly, or Auto) and a one-sentence rationale explaining why that mode was chosen over the alternatives.
- Document thread-safety expectations if the component is accessed from background threads.
