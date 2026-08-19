# Portable C# Guardrails

> **Scope note:** Where a rule below references a logging or DI API, the *principle* is portable but the *API name* is not — substitute your project's equivalents.

---

## A. Nullability and null safety

### G-1: Nullable reference types enabled, code is nullable-aware
`<Nullable>enable</Nullable>` in every `.csproj`. Reference types must be annotated, null checks present where required, and no "possible null reference" warnings introduced. Do not suppress with `!`; prefer an explicit upstream null check.

```csharp
// Compliant
public class MyService
{
    private readonly ILogger _logger;
    public MyService(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    public string? GetName() => null; // nullable return declared
}

// Non-compliant
public string GetName() => null!;     // suppressing instead of being nullable-aware
private ILogger _logger;              // CS8618: uninitialized
```

### G-2: Argument validation via static helpers, not manual if-blocks
Prefer `ArgumentNullException.ThrowIfNull(...)`, `ArgumentException.ThrowIfNullOrWhiteSpace(...)`, or coalesce expressions (`??`) for parameter validation. Do not write manual `if (x == null) throw ...` blocks. Do not use `!` to silence a warning where an explicit guard is possible.

```csharp
// Compliant
private static string ConcatStrings(string a, string b)
{
    ArgumentNullException.ThrowIfNullOrWhiteSpace(a, nameof(a));
    ArgumentNullException.ThrowIfNullOrWhiteSpace(b, nameof(b));
    return string.Concat(a, b);
}

// Non-compliant
private static string ConcatStrings(string a, string b) => string.Concat(a!, b!);
```

### G-3: Prefer null-coalescing for fallback values
When the intent is "first non-null value" or "initialize only if null," use `??` / `??=` rather than verbose `if/else` chains.

```csharp
// Prefer
var displayName = user.DisplayName ?? user.UserName ?? "Unknown User";

// Instead of
string displayName;
if (user.DisplayName != null) displayName = user.DisplayName;
else if (user.UserName != null) displayName = user.UserName;
else displayName = "Unknown User";
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
public class FusionRequest
{
    public required string Question { get; init; }
    public string? SessionId { get; init; }
}
// Non-compliant: public string Question { get; set; } = "";
```

### G-6: Primary constructors for simple exception types only
Use primary constructors (`ExceptionType(params) : base(...)`) only for custom exception types with minimal logic. Do not use primary constructors for service classes or classes with real behaviour.

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

### G-10: Avoid unjustified single-use abstractions
Extract a block when it is reused by ≥2 call sites **or** when the extracted boundary materially improves readability, lifecycle ownership, or testability. Do not create trivial pass-through helpers that merely rename one expression or force navigation without clarifying responsibility.

```csharp
// Compliant — the phase boundary makes orchestration readable and configuration independently testable.
ConfigurationPaths paths = ConfigurationBootstrap.ResolvePaths(requestedRepository);
IConfigurationRoot configuration = ConfigurationBootstrap.Build(args, paths);

// Non-compliant — a trivial single-use rename adds no clarity, ownership, or test seam.
private string FormatCommandId(Guid id) => id.ToString();
```

### G-11: Methods are cohesive and appropriately sized
Prefer cohesive methods with inline comments for nearby logical steps. Break up a long composition or workflow method when named phases clarify ordering, side effects, resource lifetime, or independently testable policy. Keep tiny implementation details inline when extraction would only fragment context.

### G-12: Existing patterns take precedence
Before adding/modifying code, find how similar problems are solved elsewhere in the codebase and follow that precedent. New patterns require explicit team-review flagging. Do not introduce foreign patterns (e.g. `Result<T>`/`Optional<T>`) where the codebase uses exceptions.

---

## E. Async, LINQ, and expression style

### G-13: Async conventions
- All async methods return `Task` or `Task<T>` (not `ValueTask` unless the project has adopted it).
- `CancellationToken` is the last parameter, defaulting to `default`.
- Always forward cancellation tokens.
- No `async void` — always `async Task`.
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
    .Select(e => Enum.TryParse<EntityType>(e, true, out var t) ? t : throw new NotSupportedException($"Unsupported: {e}"))
    .ToArray();

// Do not write
var result = from e in entityTypes select /*...*/;
```

### G-15: Prefer `var` for local variables whenever possible
Use `var` for local variable declarations whenever the compiler can infer the type, including built-in values, object creation, generic method calls, LINQ results, and complex domain concepts. Prefer names and surrounding code that make intent clear instead of repeating the static type in the declaration. Use an explicit local type only when C# requires it or when a declaration form cannot use `var`.

```csharp
var count = 0;
var namedEntities = new List<INamedEntity>();
var client = _httpClientFactory.CreateClient();
var result = new ReadOnlyCollection<INamedEntity>(namedEntities);
```

### G-16: Collection expressions for inline initialization
Use collection expression syntax `[a, b]` instead of `new[] { a, b }` or `new List<T> { a, b }` where the type can be inferred (C# 12+).

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

### G-18: XML doc comments on all public members
All `public` types, constructors, methods, and properties must have `/// <summary>` comments (plus `<param>`, `<returns>`, `<exception>`, `<remarks>` for non-obvious details). Internal/private members may omit them. Comments describe intent and usage, not a restatement of code.

```csharp
/// <summary>Acquires source data, caching the parsed result for subsequent calls.</summary>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Parsed <see cref="SubmissionData"/> object.</returns>
/// <exception cref="InvalidOperationException">Thrown when the required option is missing.</exception>
public async Task<SubmissionData> GetSourceDataAsync(CancellationToken cancellationToken = default)
```

### G-19: No `#region`
`#region` / `#endregion` blocks are not used. Do not introduce them.

---

## G. Exception handling

### G-20: Throw at the boundary, log at the catch site
Log exceptions at the catch site using your project's structured-logging API. Do not swallow exceptions silently. Re-throw (`throw;`) when the catch layer cannot handle the error meaningfully. Logging without re-throwing is acceptable only when there is a defined fallback path.

```csharp
try
{
    var result = await _pipelineProcessor.ExecuteAsync(/*...*/);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Pipeline execution failed: {message}", ex.Message); // your logging API
    throw; // re-throw if no fallback
}

// Never:
catch (Exception ex) { /* silent swallow */ }
```

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
        _processor = processor;
        _logger = logger;
    }
}

// Non-compliant — property injection
public class MyService { public ILogger Logger { get; set; } = null!; }
```

### G-22: Inject multi-registration collections as `IEnumerable<T>`
When a service expects a collection of implementations of an interface, inject `IEnumerable<T>` — not `List<T>` or `T[]`. Most DI containers resolve multi-registration this way; concrete collection types often fail to resolve.

```csharp
public MyService(IEnumerable<IPipelineDefinition> pipelineDefinitions, IProcessor processor, ILogger logger) { /*...*/ }
// Not: public MyService(List<IPipelineDefinition> definitions, ...) { }
```

### G-23: Prefer singletons; inject collections and select by name
- Register components as singletons unless they are stateful/transient for a good reason.
- Components registered as singletons should be used only as singletons (tests included).
- When injecting components registered in collections, prefer injecting the collection and selecting the appropriate implementation by name/resolution — not a specific concrete implementation. Injecting a specific implementation from a collection usually requires extra registration.

---

## I. Unit testing (principles — frameworks/versions are project-specific). 

### G-24: Arrange-Act-Assert
Always use AAA structure; mark sections with comments when non-obvious.

```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
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

### G-25: Static factory helpers for test data
Prefer static factory methods (`Submissions.TestSubmissionValid()`) over inline construction in each test. Static helper classes make test data reusable and readable.

### G-26: Dummy/stub implementations for complex interfaces
Define minimal in-test implementations (`DummyPipelineStep`, `StubRepository`) for interfaces under test when a mock is insufficient.

### G-27: No cross-project test helpers
Each test project is self-contained. Do not share test helpers across test projects — duplicate as needed.

### G-28: Nullable-enabled test projects
Test projects enable `<Nullable>enable</Nullable>`. `null!` initialization is acceptable **only** for fields set by a test-initialize method (e.g. `[TestInitialize]`/`[OneTimeSetUp]`), not as a general suppress.

### G-29: Test class and file naming
Test classes are named `{ClassUnderTest}_Tests` (or `{ClassUnderTest}Tests` for MSTest-style). Files follow the same convention. Dummy/stub files are `Dummy{Thing}.cs` / `Test{Thing}.cs`.

### G-30: Member ordering is build-enforced
Arrange members in the order required by StyleCop SA1202. Keep members with the same accessibility and kind together; in particular, do not place a private helper between public interface or API implementations. Reorder a member deliberately when adding it rather than relying on automated cleanup.

### G-31: Stateless members must be static
Mark a member `static` when it does not access instance state or call instance members. Treat CA1822 as a build error; use Visual Studio's **Make member static** quick action and **Fix all in Solution** when remediating existing violations. Retain an instance member only when it is required by an interface, override, or an intentional object-oriented boundary.

---
