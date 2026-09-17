# Blazor / Razor Rules

Load when `.razor`, `.razor.cs`, or `.razor.css` files are in scope. Extends `tech-web.md`, Sections 4.2, 4.8 in `copilot-instructions.md`, and `tech-csharp.md`.

## Structure

- Organize by feature, not by type.
- Put feature components, services, and view-models in feature folders.
- Put a type in `Shared/` only when two or more features use it.

## Components

- Match `PascalCase.razor` file name to class name.
- Put logic in `ComponentName.razor.cs`.
- Do not put business logic in markup.

## Parameters, Events, DI

- Mark mandatory inputs `[Parameter]` and `[EditorRequired]`.
- Validate parameter invariants in `OnParametersSet` / `OnParametersSetAsync`.
- Raise events with `EventCallback<T>`.
- Inject with `[Inject]` in code-behind only.

```csharp
[Parameter, EditorRequired]
public required string Title { get; set; }

[Parameter]
public EventCallback<string> TitleChanged { get; set; }
```

## Lifecycle and Rendering

- Initialize asynchronously in `OnInitializedAsync`.
- Unsubscribe in `Dispose` / `DisposeAsync` when you subscribe.
- Do not run CPU-heavy work on the render path.
- Pass every available `CancellationToken` (parameter, dispose-linked `CancellationTokenSource`, injected API) into cancellable calls. Do this especially for HTTP, streams, delays, and background loops. Do not drop the token at an intermediate call.
- Cancel a `CancellationTokenSource` in `Dispose` / `DisposeAsync` when async work outlives one lifecycle method.

```csharp
await _http.GetAsync(url, _cts.Token);
await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
```

- Call `await InvokeAsync(StateHasChanged)` for external notifications.

## Render Mode

- Choose per component: Static SSR, Interactive Server, Interactive WebAssembly, or Auto.
- Declare `@rendermode` when the component is interactive.
- Document the render-mode rationale in the component XML summary.

## Markup, State, Security, Layout

- Add `@key` in `@foreach` repeats.
- Extract a child component instead of deep nesting.
- Bind with explicit `@bind-Value` plus the event.
- Wrap risky subtrees in `<ErrorBoundary>` with recovery UI.
- Keep per-user state in scoped services. Keep shared app state in singletons. Never store user state in static fields.
- Apply `[Authorize]` / `<AuthorizeRouteView>` where required.
- Never trust `[Parameter]` data without validation.
- Validate user input server-side.

## CSS

Shared look, locked breakpoints, CSS layers, JS vs CSS: `tech-web.md`.

- Put component-only rules in `ComponentName.razor.css`.
- Use `::deep` only when a parent must style a child it owns visually. Otherwise add a shared class in app/layout CSS.

## Testing

- Component logic: bUnit (render states, parameters, user events, auth visibility, error boundaries).
- Code-behind, view-models, services: TUnit; exit-point gate: `tech-tunit.md`.
- Page journeys and UI debug: `tech-playwright.md` (`TUnit.Playwright` in `{App}.UiTest`).
- Case design: `tech-test.md`.

## Commands

```bash
dotnet test path/App.Tests.csproj -c Release
```

UI test commands: `tech-playwright.md`.
