<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# ASP.NET Core Coding Style

> Extends the global engineering rules in [copilot-instructions.md](../copilot-instructions.md) and [CsharpCodingStyle.md](CsharpCodingStyle.md). Read all three.
> Global and C# policies are not repeated here unless an ASP.NET Core-specific mechanism must be named explicitly.

## Minimal APIs vs. Controllers

- Use **Minimal APIs** for simple, focused endpoints with few dependencies and no cross-cutting logic.
- Use **Controllers** when the endpoint requires filters, complex action result handling, versioned API surfaces, or the team has an established controller-based baseline.
- Do not mix both styles in the same feature area; choose one approach per API surface and apply it consistently.
- Group related minimal API endpoints using `RouteGroupBuilder` (`app.MapGroup(...)`) to share prefixes, filters, and authorization policies.

## Route Naming

- Use **kebab-case** path segments: `/user-profiles`, not `/UserProfiles` or `/userProfiles`.
- Version the API in the URL path for public-facing APIs: `/api/v1/resource`.
- Do not encode the HTTP verb in the route: `/orders/{id}` not `/get-order/{id}`.
- Route parameter names must match action/handler parameter names exactly (case-insensitive matching is not sufficient for readability).
- Avoid optional route segments; prefer explicit endpoints for each supported shape.

## Middleware Order

Register middleware in this order (omit sections not applicable to the project):

```
app.UseExceptionHandler() / app.UseDeveloperExceptionPage()
app.UseHsts()
app.UseHttpsRedirection()
app.UseStaticFiles()
app.UseRouting()
app.UseCors()
app.UseAuthentication()
app.UseAuthorization()
app.UseRateLimiter()
app.MapControllers() / app.MapEndpoints()
```

- Never place `UseAuthentication` after `UseAuthorization`.
- Never place `UseCors` after `UseAuthentication` if the CORS policy must run before auth headers are read.
- Custom middleware that modifies the request must be placed before `UseRouting`; middleware that inspects route data must be placed after it.

## Error Handling

- Return `ProblemDetails`-compliant responses for all error conditions; use `Results.Problem(...)` (minimal APIs) or `Problem(...)` (controllers).
- Register a global exception handler via `app.UseExceptionHandler(...)` or `app.UseExceptionHandler<GlobalExceptionHandler>()`; do not let unhandled exceptions propagate to the client as stack traces.
- Map domain-specific exceptions to HTTP status codes in the exception handler, not in individual action methods.
- Never return `200 OK` with an error body; use the appropriate 4xx or 5xx status code.
- Validation errors must return `400 Bad Request` with a `ProblemDetails` body enumerating the invalid fields.

## Request Validation

- Validate all incoming data at the API boundary before it reaches the application layer.
- Use model binding validation (`[Required]`, `[Range]`, etc.) or FluentValidation; do not perform ad-hoc validation scattered across handler logic.
- Return `422 Unprocessable Entity` for semantic validation failures (data is syntactically valid but violates business rules), `400 Bad Request` for structural/format failures.
- Never trust client-supplied IDs or claims without verification against the authoritative source.

## Response Types

- Annotate every endpoint with its possible response types:
  - Minimal APIs: use `Produces<T>(statusCode)` and `ProducesProblem(statusCode)` on the route.
  - Controllers: use `[ProducesResponseType<T>(StatusCodes.Status200OK)]` attributes.
- Use `TypedResults` (minimal APIs) rather than `Results` where possible to retain static type information for OpenAPI generation.
- Do not return raw `object`; always return a typed DTO or a typed `IResult`.

## HTTP Client

- Never instantiate `HttpClient` directly with `new`; always obtain it from `IHttpClientFactory`.
- Register named or typed HTTP clients in `Program.cs` via `builder.Services.AddHttpClient<T>(...)`.
- Configure base addresses, default headers, and resilience policies (retry, circuit breaker) at registration time, not at the call site.
- Always pass and respect `CancellationToken` in every HTTP client call.

## Configuration and Options

- Bind configuration sections to strongly typed options classes via `services.AddOptions<T>().BindConfiguration("Section").ValidateDataAnnotations().ValidateOnStart()`.
- Inject `IOptions<T>`, `IOptionsSnapshot<T>`, or `IOptionsMonitor<T>` as appropriate; do not inject `IConfiguration` directly into application services.
- Never hard-code configuration values in source; use `appsettings.json`, environment variables, or secrets management.
- Validate options at startup (`ValidateOnStart`) so misconfiguration fails fast rather than at runtime.

## Security

- Require explicit authorization on every endpoint; use `app.UseAuthorization()` + `RequireAuthorization()` globally, then selectively relax with `AllowAnonymous`.
- Define CORS policies explicitly by name; never use `AllowAnyOrigin` with `AllowCredentials`.
- Set `SameSite`, `HttpOnly`, and `Secure` flags on all cookies.
- Use `FromServices` / DI injection rather than `HttpContext.RequestServices.GetService` in handler code.
- Never log request bodies, authorization headers, or any field that may contain PII or secrets.

## Cancellation

- Every controller action and minimal API handler must accept a `CancellationToken` parameter and pass it to all downstream async calls.
- Do not catch `OperationCanceledException` and return a success response; let the framework handle cancellation or return `499`/`408` as appropriate.

## Testing

- Use `WebApplicationFactory<TProgram>` for integration tests; do not start a real server process.
- Override services in the test factory using `ConfigureTestServices` to replace external dependencies with fakes.
- Test each endpoint for: success (2xx), validation failure (400/422), unauthorized (401), forbidden (403), not found (404), and server error (500).
- Corner cases: empty request body, oversized payload, malformed JSON, missing required headers, concurrent requests to stateful endpoints, requests with expired or tampered tokens.
