# Portable C# Guardrails

> **Scope:** Follow the repository's `Directory.Build.props` and `.editorconfig` for shared compiler settings, analyzer enforcement, and documented exceptions. Logging and DI API names in examples stand for the project's equivalents; snippets omit unrelated documentation and members.

---

## A. Nullability and null safety

### G-1: Nullable analysis and null suppression
Nullable analysis is enabled for every project, including tests, through `Directory.Build.props`; do not duplicate the setting in individual project files. Annotate nullable references, guard where needed, and introduce no nullable warnings. The null-forgiving operator (`!`) is prohibited outside test projects. Test projects may use it, including for invalid-input tests and fixture setup; prefer annotations or guards when practical.

```csharp
public static string? GetName() => null;

// Non-compliant outside test projects
public static string GetName() => null!;
```

### G-2: Validate arguments with static helpers
Use `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException.ThrowIfNullOrWhiteSpace(...)`, or `?? throw` for argument validation instead of manual null-check-and-throw blocks.

```csharp
private static string ConcatStrings(string a, string b)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(a);
    ArgumentException.ThrowIfNullOrWhiteSpace(b);
    return string.Concat(a, b);
}
```

### G-3: Prefer null-coalescing for fallback values
Use `??` for the first non-null value and `??=` to initialize only when null.

```csharp
var displayName = user.DisplayName ?? user.UserName ?? "Unknown User";
```

---

## B. Types and immutability

### G-4: `record` for data, `class` for behaviour
Use `record` for DTOs, request/response models, value objects — types where value equality is appropriate. Use `class` for services, processors, and anything with behaviour or mutable state. Do not use `record` for a service.

```csharp
// Data — record
public record NerEntityModel
{
    public required string EntityType { get; init; }
    public required string EntityName { get; init; }
}

// Behaviour — class
public class LLMNERService : INERService { /* ... */ }
```

### G-5: `init` / `required` for immutable-after-construction properties
Use `init` instead of `set` for properties that should only be set during object initialization. Use `required` for mandatory properties on `record` and data types. This communicates intent and allows object-initializer syntax while preventing post-construction mutation.

```csharp
public record FusionRequest
{
    public required string Question { get; init; }
    public string? SessionId { get; init; }
}
// Non-compliant: public string Question { get; set; } = "";
```

### G-6: Class primary constructors only for simple exceptions
For non-record classes, use primary constructors only for custom exceptions with minimal logic. Do not use them for services or other classes with behaviour. Positional records remain valid for data types under G-4.

```csharp
// Compliant — minimal exception
public class IrrelevantInputException(string label, string message) : Exception(message)
{
    public string Label { get; } = label;
}

// Non-compliant — service with a primary constructor
public class MyService(ILogger logger, IProcessor processor)
{
    public async Task ExecuteAsync() { /* ... */ }
}
```

---

## C. Naming and namespace conventions

### G-7: Naming conventions

| Kind | Convention | Examples |
|---|---|---|
| Classes, records, structs | PascalCase | `FusionCommand`, `NerEntityModel` |
| Interfaces | `I` prefix + PascalCase | `IFusion`, `INERService` |
| Enums | PascalCase type; PascalCase members | `EntityType.Organization` |
| Custom exceptions | PascalCase + `Exception` suffix | `IrrelevantInputException` |
| Generic type params | `T` or `T` + PascalCase noun | `T`, `TResult`, `TPayload` |
| Methods | PascalCase | `GetNamedEntitiesAsync` |
| Async methods | PascalCase + `Async` suffix | `GetNamedEntitiesAsync` |
| Properties | PascalCase | `Name`, `AuthToken` |
| Private instance fields | `_camelCase` | `_logger`, `_pipelineProcessor` |
| Private static/const | PascalCase or `_camelCase` | `DefaultProviderName` |
| Method parameters | `camelCase` | `entityTypes`, `cancellationToken` |
| Local variables | `camelCase` | `namedEntities`, `result` |

### G-8: File-scoped namespaces preferred
New files use `namespace X.Y;`. Block-scoped namespaces exist in legacy files but are not introduced in new files.

```csharp
// Preferred
namespace Threadsmith;

public class MyClass { }
```

### G-9: Namespace mirrors folder structure
Namespace segments map to the folder path under the project root.

---

## D. Method and decomposition discipline

### G-10: Extract only useful boundaries
Extract code when reused by at least two call sites or when a named phase materially improves readability or clarifies ordering, side effects, resource ownership, or testable policy. Keep trivial steps inline; avoid pass-through helpers that only rename an expression or fragment context.

```csharp
// Named phases clarify orchestration and make configuration testable.
var paths = ConfigurationBootstrap.ResolvePaths(requestedRepository);
var configuration = ConfigurationBootstrap.Build(args, paths);

// Non-compliant: a single-use rename adds no useful boundary.
private static string FormatCommandId(Guid id) => id.ToString();
```

### G-11: Keep methods cohesive
Keep each method focused on one responsibility; use G-10 to decide when to extract phases.

### G-12: Follow existing compliant patterns
Before adding/modifying code, find similar solutions and follow their patterns when consistent with the applicable contracts and guardrails. New patterns require explicit team-review flagging. Do not introduce foreign patterns (e.g. `Result<T>`/`Optional<T>`) where the codebase uses exceptions.

---

## E. Async, LINQ, and expression style

### G-13: Async conventions
- Async methods return `Task`, `Task<T>`, or `IAsyncEnumerable<T>` for async streams. Use `ValueTask` only where the project has adopted it or a required contract specifies it.
- `CancellationToken` is the last parameter, defaulting to `default`.
- Always forward cancellation tokens.
- No `async void`.
- When an interface/base defines a `Task`/`Task<T>` return but the implementation performs no async work, do **not** add the `async` modifier — return `Task.CompletedTask` or `Task.FromResult(...)`.
- `ConfigureAwait(false)` is not used in ASP.NET Core hosts (no synchronization context); apply the project's convention rather than defaulting to it.

```csharp
public async Task<ReadOnlyCollection<INamedEntity>> GetNamedEntitiesAsync(
    IEnumerable<EntityType> entityTypes,
    Language language,
    string inputText,
    CancellationToken cancellationToken = default)
{
    var result = await _pipelineProcessor.ExecuteAsync(/*...*/, cancellationToken);
    return new ReadOnlyCollection<INamedEntity>(/*...*/);
}
```

### G-14: LINQ method syntax only
Use method syntax (`.Select(...).Where(...).ToArray()`), not query syntax (`from x in y select z`). Break chains across lines when they exceed ~80 characters.

```csharp
// Preferred
var entityTypesEnum = entityTypes
    .Select(e => Enum.TryParse<EntityType>(e, true, out var value)
        ? value
        : throw new NotSupportedException($"Unsupported: {e}"))
    .ToArray();

// Do not write
var result = from e in entityTypes select /*...*/;
```

### G-15: Prefer `var` for local variables whenever possible
Use `var` whenever C# can infer a local variable's type. Use explicit types only when required by the declaration, including target typing for collection expressions (G-16). Choose names that make intent clear.

```csharp
var count = 0;
var namedEntities = new List<INamedEntity>();
var client = _httpClientFactory.CreateClient();
var result = new ReadOnlyCollection<INamedEntity>(namedEntities);
```

### G-16: Collection expressions for inline initialization
Prefer collection expressions (`[a, b]`) where the target type is known and source and target types match exactly, as configured in `.editorconfig`. Preserve the collection's runtime type and behavior.

```csharp
public EntityType[] SupportedTypes => [
    EntityType.Organization,
    EntityType.Person,
    EntityType.Location
];
```

---

## F. Access modifiers, documentation, and formatting

### G-17: Access modifiers by intent

| Scenario | Modifier |
|---|---|
| Types consumed outside the project | `public` |
| Types internal to a project | `internal` |
| Test-only types within test classes | `private` (nested) |
| Extension method classes | `public static` (or `internal static` if project-internal) |

### G-18: XML documentation follows build policy
Document public product types and members with `/// <summary>` and additional XML tags for non-obvious details. Describe intent and usage, not a restatement of code. Follow configured SA1600/SA1601 requirements for other members. Tests and spikes are exempt from XML-documentation requirements under `Directory.Build.props` and `.editorconfig`.

```csharp
/// <summary>Acquires source data, caching the parsed result for subsequent calls.</summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Parsed <see cref="SubmissionData"/> object.</returns>
/// <exception cref="InvalidOperationException">Thrown when the required option is missing.</exception>
public async Task<SubmissionData> GetSourceDataAsync(CancellationToken cancellationToken = default)
```

### G-19: No `#region`
`#region` / `#endregion` blocks are not used. Do not introduce them.

### G-30: Follow configured member ordering
Follow SA1202 accessibility ordering and its `.editorconfig` exceptions. Do not insert private helpers between public method implementations. Kind ordering (SA1201) and static-before-instance ordering (SA1204) are disabled; preserve useful locality instead of imposing those orders.

### G-31: Stateless members must be static
Mark members `static` when they do not use instance state or call instance members; CA1822 is a build error. Retain instance members required by interfaces, overrides, or intentional instance contracts. Limit remediation to the relevant change.

---

## G. Exception handling

### G-20: Handle deliberately and log once
Catch exceptions only to recover, translate at an owning boundary, or add necessary context. Otherwise let them propagate; use `throw;` when rethrowing to preserve the stack. Log once, with structured context, at the layer that handles or reports the failure. Do not add catch-log-rethrow blocks at every layer or silently discard failures. A handled failure must have a defined recovery or fallback. Propagate expected cancellation without logging it as an error.

---

## H. Dependency injection (principles — API names are project-specific)

### G-21: Constructor injection only — no property injection
All dependencies are injected via the constructor. Property injection is not used. Constructor injection makes dependencies explicit and enables null checks at construction time.

```csharp
// Compliant
public class MyService
{
    private readonly ILogger _logger;
    private readonly IProcessor _processor;
    public MyService(IProcessor processor, ILogger logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
}

// Non-compliant — property injection
public class MyService { public ILogger Logger { get; set; } = null!; }
```

### G-22: Inject collections for multiple implementations
Inject `IEnumerable<T>` for multiple registered implementations, rather than `List<T>` or `T[]`. When selecting one implementation, use the collection's established identifier or resolver instead of depending on a concrete member of that collection.

```csharp
public MyService(IEnumerable<IPipelineDefinition> pipelineDefinitions) { /* ... */ }
```

### G-23: Choose lifetimes by ownership
- Choose singleton, scoped, or transient lifetimes to match state, resource ownership, and disposal needs.
- Singletons must be safe for concurrent use and must not capture shorter-lived dependencies beyond their intended lifetime.
- A singleton registration has one instance per service provider. Tests may create fresh providers or isolated instances rather than sharing a global fixture; tests of composition and lifetime behavior must exercise production registrations.

---

## I. Unit testing

### G-24: Arrange-Act-Assert
Always use AAA structure; mark sections with comments when non-obvious.

```csharp
[Fact]
public static void MethodReturnsExpectedValue()
{
    // Arrange
    var sut = new YourClass();
    // Act
    var result = sut.DoSomething();
    // Assert
    Assert.NotNull(result);
    Assert.Equal("expected", result.Value);
}
```

### G-25: Extract test-data factories when useful
Prefer static factories when setup is reused or materially clearer behind a named boundary, using G-10's criteria. Keep simple, single-use test data inline.

### G-26: Dummy/stub implementations for complex interfaces
Define minimal in-test implementations (`DummyPipelineStep`, `StubRepository`) for interfaces under test when a mock is insufficient.

### G-27: No cross-project test helpers
Each test project is self-contained. Do not share test helpers across test projects — duplicate as needed.

### G-28: Test nullability
G-1 owns nullable analysis and the test-project exception for null suppression.

### G-29: Test class and file naming
Name test classes `{Subject}Tests` and files `{Subject}Tests.cs`, where the subject is the class, feature, or contract under test. Name dummy/stub files by role, such as `Dummy{Thing}.cs`, `Stub{Thing}.cs`, or `Test{Thing}.cs`.

---
