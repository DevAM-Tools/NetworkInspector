# Blazor / Razor Rules

Load when `.razor`, `.razor.cs`, or `.razor.css` files are in scope. Extends Sections 4.2, 4.8 in `copilot-instructions.md` and `tech-csharp.md`.

## Structure

- Organize by feature, not by type.
- Keep feature components, services, and view-models in feature folders.
- Use `Shared/` only for cross-feature reuse.

## Components

- Match `PascalCase.razor` file name to class name.
- Keep logic in `ComponentName.razor.cs` partial class.
- Use CSS isolation (`ComponentName.razor.css`).
- Do not put business logic in markup.

## Parameters, Events, DI

- Use `[Parameter]` and `[EditorRequired]` for mandatory inputs.
- Validate parameter invariants in `OnParametersSet` / `OnParametersSetAsync`.
- Use `EventCallback<T>` for component events.
- Inject with `[Inject]` in code-behind only.

## Lifecycle and Rendering

- Prefer `OnInitializedAsync` for async init.
- Subscribe and unsubscribe cleanly; implement dispose when needed.
- Do not block CPU-heavy work on render path.
- Pass cancellation tokens to long-running async operations.
- Call `await InvokeAsync(StateHasChanged)` for external notifications.

## Render Mode

- Choose per component: Static SSR, Interactive Server, Interactive WebAssembly, or Auto.
- Declare `@rendermode` when interactive behavior exists.
- Document render-mode rationale in component XML summary.

## Markup, State, Security

- Add `@key` in `@foreach` repeats.
- Avoid deep nesting; extract child components.
- Prefer explicit `@bind-Value` plus event for two-way binding.
- Wrap risky subtrees in `<ErrorBoundary>` with recovery UI.
- Keep per-user state in scoped services.
- Keep shared app state in singleton services.
- Never use static fields for user state.
- Apply `[Authorize]` / `<AuthorizeRouteView>` where required.
- Never trust `[Parameter]` data without validation.
- Validate user input server-side.

## Testing

- ❗ Test Razor and Blazor pages and components extensively with bUnit.
- Cover render states, parameters, user events, auth visibility, and error boundaries.
- Use TUnit for code-behind, view-models, and services; exit-point gate per `tech-tunit.md`.
