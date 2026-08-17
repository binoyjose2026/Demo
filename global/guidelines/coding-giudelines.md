# C# Coding Guidelines

These guidelines are based on Microsoft's official [C# Coding Conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions) and [.NET engineering / API design guidelines](https://learn.microsoft.com/dotnet/standard/design-guidelines/) published on Microsoft Learn, and reflect standard practices used across the .NET runtime and ASP.NET Core repositories. They are intended as the baseline coding standard for this project. Where the team needs stricter or project-specific rules, they should be layered on top of — not in conflict with — these conventions.

---

## 1. Naming Conventions

- **PascalCase** for: namespaces, classes, records, structs, interfaces (with `I` prefix), enums, enum members, methods, properties, events, and public fields/constants.
- **camelCase** for: local variables, method parameters.
- **`_camelCase`** for: private and internal instance fields (leading underscore, no `m_` or Hungarian prefixes).
- **`I` prefix** for interfaces: `IRepository`, `IDisposable`.
- Type parameters use a `T` prefix: `TKey`, `TValue`, or simply `T` for a single generic parameter.
- Avoid Hungarian notation (`strName`, `iCount`) — the type is already known via tooling and type inference.
- Prefer meaningful, descriptive names over abbreviations. Avoid single-letter names except for trivial loop counters (`i`, `j`) or LINQ lambda parameters where the context is obvious.
- Async methods should have an `Async` suffix (see section 5).

```csharp
// Good
public class OrderProcessor
{
    private readonly ILogger _logger;

    public int RetryCount { get; set; }

    public async Task<Order> GetOrderAsync(int orderId) { ... }
}

// Bad
public class order_processor
{
    public ILogger m_logger;
    public int iRetryCount;

    public async Task<Order> GetOrder(int id) { ... } // no Async suffix, cryptic param name
}
```

---

## 2. File & Project Organization

- **One type per file**, with the file name matching the type name (e.g., `OrderProcessor.cs` contains `class OrderProcessor`).
- **Folder structure should mirror namespace structure** — e.g., `Company.Product.Billing.Invoices` lives under `Billing/Invoices/`.
- Use **file-scoped namespaces** (C# 10+) to reduce nesting:

```csharp
// Good (file-scoped)
namespace Company.Product.Billing;

public class Invoice { }

// Avoid (block-scoped, unnecessary indentation)
namespace Company.Product.Billing
{
    public class Invoice { }
}
```

- `using` directives go at the top of the file, outside the namespace, with `System.*` namespaces first, followed by third-party/project namespaces, each group alphabetized. Remove unused usings.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Company.Product.Billing.Models;
```

---

## 3. Formatting

- **Braces**: Allman style (opening brace on its own new line) — the convention used throughout the .NET runtime and Visual Studio defaults.

```csharp
if (isValid)
{
    Process();
}
```

- **Indentation**: 4 spaces per level; no tabs.
- **Spacing**: one space after keywords (`if`, `for`, `while`), around binary operators, and after commas; no space between a method name and its parentheses.
- **One statement / one declaration per line.**
- **Line length**: keep lines reasonably short (soft guideline of ~120 characters) to preserve readability in diffs and side-by-side views; wrap long parameter lists or method chains onto multiple lines with consistent indentation.
- Use `var` per the rules in section 4 rather than to save formatting effort.
- The standard, tooling-enforced mechanism for all formatting rules is an **`.editorconfig`** file at the solution root (as used in the `dotnet/runtime` and `dotnet/aspnetcore` repos). It should define indentation, spacing, brace style, `var` preferences, and naming rules so they are enforced automatically by the IDE and `dotnet format` in CI, rather than relying on manual review.

---

## 4. Language Usage Guidelines

### `var`
Use `var` when the type is obvious from the right-hand side of the assignment; use an explicit type when it improves readability.

```csharp
var customer = new Customer();        // Good — type is obvious
var items = GetOrderItems();          // Prefer explicit type if the return type isn't clear from the name
List<OrderItem> items = GetOrderItems();
```

### Nullable reference types
Enable `<Nullable>enable</Nullable>` in project files. Treat compiler nullable warnings as errors where practical. Use `?` explicitly for values that can legitimately be null, and avoid null-forgiving (`!`) operators unless you can justify why the compiler's null analysis is wrong.

```csharp
public string? MiddleName { get; set; }   // may legitimately be absent
public string FirstName { get; set; } = string.Empty;
```

### Expression-bodied members
Use for simple, single-expression members; avoid for anything with multiple statements or complex branching.

```csharp
// Good
public string FullName => $"{FirstName} {LastName}";

// Avoid — logic is too complex for an expression body
public decimal CalculateTotal() =>
    Items.Sum(i => i.Price * i.Quantity) - Discount + (IsTaxable ? Tax : 0);
```

### Pattern matching
Prefer pattern matching over manual type checks and casts.

```csharp
// Good
if (shape is Circle { Radius: > 0 } circle)
{
    Process(circle);
}

// Avoid
if (shape is Circle)
{
    var circle = (Circle)shape;
    if (circle.Radius > 0) Process(circle);
}
```

### LINQ
Prefer method syntax for simple chains and query syntax when it materially improves readability (e.g., multiple `join`/`from` clauses). Avoid overly long chained LINQ expressions that hurt readability — break into named intermediate variables when needed.

```csharp
var activeCustomers = customers
    .Where(c => c.IsActive)
    .OrderBy(c => c.Name)
    .ToList();
```

### String interpolation
Prefer string interpolation over concatenation for readability.

```csharp
// Good
var message = $"Order {orderId} shipped on {shipDate:d}";

// Avoid
var message = "Order " + orderId + " shipped on " + shipDate.ToString("d");
```

### Magic numbers/strings
Avoid unexplained literals; use named constants or enums.

```csharp
// Good
private const int MaxRetryCount = 3;

public enum OrderStatus { Pending, Shipped, Delivered, Cancelled }

// Bad
if (retryCount > 3) { ... }
if (status == 2) { ... }
```

---

## 5. Async/Await Conventions

- Name asynchronous methods with an **`Async`** suffix: `GetOrderAsync`, `SaveChangesAsync`.
- **Avoid `async void`** — it cannot be awaited and exceptions thrown inside it cannot be caught by the caller. The only accepted exception is **event handlers**, which must be `async void` because the event signature requires it.
- In **library/reusable code**, call `ConfigureAwait(false)` on awaited tasks to avoid forcing a resumption on the original synchronization context. This is not necessary in top-level application/ASP.NET Core code (which has no synchronization context by default), but is still common defensive practice in shared libraries.

```csharp
public async Task<Order> GetOrderAsync(int id)
{
    return await _repository.FindAsync(id).ConfigureAwait(false);
}
```

- **Never block on async code** with `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` in a way that can deadlock (particularly on code that captures a context) — use `await` throughout the call chain instead.

```csharp
// Bad — can deadlock, and swallows AggregateException wrapping
var order = GetOrderAsync(id).Result;

// Good
var order = await GetOrderAsync(id);
```

- Propagate `CancellationToken` parameters through async call chains where cancellation is meaningful.

---

## 6. Error Handling & Exceptions

- Throw **specific, meaningful exception types** (`ArgumentNullException`, `InvalidOperationException`, or a custom exception) rather than the generic `Exception`.
- **Never swallow exceptions silently** — an empty `catch { }` block hides bugs. At minimum, log the exception; only catch what you can meaningfully handle or add context to.
- Exception messages should be clear, actionable, and free of sensitive data.
- Use **guard clauses** at the top of a method to fail fast on invalid input, rather than nesting the main logic inside a big `if`.

```csharp
public void SetDiscount(decimal discount)
{
    if (discount < 0 || discount > 1)
    {
        throw new ArgumentOutOfRangeException(nameof(discount), "Discount must be between 0 and 1.");
    }

    _discount = discount;
}
```

```csharp
// Bad — swallowed exception
try
{
    ProcessPayment(order);
}
catch (Exception)
{
}
```

- Prefer exceptions for **truly exceptional, unexpected conditions** (I/O failure, invalid state, programmer errors). For **expected failure paths that are part of normal control flow** (e.g., "user not found," validation failure), prefer a **return code / result pattern** (`TryGetValue`, nullable returns, or a `Result<T>`-style type) rather than using exceptions for control flow, since exceptions are relatively expensive and can obscure intent.
- Only catch exceptions you can act on; let unexpected exceptions propagate to a top-level handler/logging middleware.

---

## 7. Documentation & Comments

- Use **XML doc comments** (`///`) on all **public** types and members (classes, interfaces, public methods, properties) so IntelliSense and generated API docs are meaningful.

```csharp
/// <summary>
/// Retrieves the order with the specified identifier.
/// </summary>
/// <param name="orderId">The unique identifier of the order.</param>
/// <returns>The matching <see cref="Order"/>, or <c>null</c> if not found.</returns>
public async Task<Order?> GetOrderAsync(int orderId) { ... }
```

- Write **self-documenting code** first: clear names, small focused methods, and well-named intermediate variables reduce the need for comments.
- Use inline comments to explain **why**, not **what** — the code already says what it does. Avoid comments that just restate the code.

```csharp
// Bad — restates the obvious
// increment i by 1
i++;

// Good — explains non-obvious intent
// Skip the header row returned by the legacy export format.
i++;
```

- Avoid commented-out code left in the codebase; rely on source control history instead.

---

## 8. Object-Oriented Design Principles

Apply **SOLID** principles as the primary guide for class and interface design:

- **S — Single Responsibility**: a class should have one reason to change (e.g., separate `OrderValidator` from `OrderRepository`).
- **O — Open/Closed**: prefer extending behavior via new implementations/strategies over modifying existing tested code.
- **L — Liskov Substitution**: derived types must be usable anywhere the base type is expected without breaking correctness.
- **I — Interface Segregation**: prefer several small, focused interfaces (`IReadable`, `IWritable`) over one large interface clients are forced to implement in full.
- **D — Dependency Inversion**: depend on abstractions (interfaces), not concrete implementations — enabled in .NET via built-in **dependency injection** (`Microsoft.Extensions.DependencyInjection`).

```csharp
public interface IOrderRepository
{
    Task<Order?> GetAsync(int id);
}

public class OrderService
{
    private readonly IOrderRepository _repository;

    public OrderService(IOrderRepository repository) => _repository = repository;
}
```

- **Favor composition over inheritance**: build behavior by combining small, focused collaborators (injected via constructor) rather than deep inheritance hierarchies, which tend to become brittle as requirements evolve.
- Register services with an appropriate lifetime (`Transient`, `Scoped`, `Singleton`) and inject dependencies through constructors rather than reaching for static/service-locator access.

---

## 9. Testing Conventions

- **Test naming**: `MethodName_Scenario_ExpectedBehavior`, e.g. `CalculateTotal_WithDiscount_ReturnsDiscountedAmount`.
- Structure tests using **Arrange / Act / Assert (AAA)**, with clear separation (blank line or comments) between each phase.
- Each test should focus on **one logical behavior/assertion concern** — avoid testing multiple unrelated behaviors in a single test method, even if multiple `Assert` calls are needed to verify that one behavior.

```csharp
[Fact]
public void CalculateTotal_WithDiscount_ReturnsDiscountedAmount()
{
    // Arrange
    var order = new Order(100m);

    // Act
    var total = order.CalculateTotal(discount: 0.1m);

    // Assert
    Assert.Equal(90m, total);
}
```

- Keep test data setup minimal and readable; prefer builders/factory helpers over duplicated setup boilerplate.
- Tests should be independent and repeatable — no shared mutable state or ordering dependencies between tests.

---

## 10. Security Guidelines

- **Validate all external input** (user input, API payloads, query strings) at the boundary before acting on it; never trust client-supplied data.
- **Never hardcode secrets, connection strings, API keys, or credentials** in source code. Use configuration providers (`appsettings.json` + environment-specific overrides, environment variables, or a secrets manager such as Azure Key Vault / user-secrets in development).

```csharp
// Bad
var connectionString = "Server=prod-db;User Id=sa;Password=P@ssw0rd123;";

// Good
var connectionString = _configuration.GetConnectionString("DefaultConnection");
```

- **Use parameterized queries** (or an ORM such as EF Core) instead of building SQL via string concatenation, to prevent SQL injection.

```csharp
// Bad — SQL injection risk
var sql = $"SELECT * FROM Users WHERE Name = '{userName}'";

// Good
var user = await _context.Users
    .Where(u => u.Name == userName)
    .FirstOrDefaultAsync();
```

- **Deserialize safely**: avoid deserializing untrusted data with binary formatters (`BinaryFormatter` is obsolete/insecure and should not be used); prefer `System.Text.Json` with explicit types, and avoid enabling polymorphic/type-name handling for untrusted input.
- Encode output appropriately to prevent XSS when rendering user-supplied data in HTML contexts.

---

## 11. Performance Considerations

- **Avoid unnecessary allocations** in hot paths — e.g., avoid boxing value types, avoid creating short-lived objects inside tight loops when they can be reused or avoided.
- Use **`StringBuilder`** instead of repeated string concatenation in loops, since `string` is immutable and each `+` creates a new object.

```csharp
// Bad — O(n²) allocations
var result = "";
foreach (var item in items)
{
    result += item.Name + ", ";
}

// Good
var sb = new StringBuilder();
foreach (var item in items)
{
    sb.Append(item.Name).Append(", ");
}
```

- Be deliberate about **`IEnumerable<T>` vs. `List<T>`/arrays**: expose `IEnumerable<T>` for lazy, one-pass, or deferred-execution scenarios; use a concrete collection (`List<T>`, `IReadOnlyList<T>`) when the caller needs indexing, a known count, or multiple enumerations, to avoid re-running expensive LINQ pipelines repeatedly.
- **Avoid premature optimization**: write clear, correct code first; profile before optimizing, and optimize only the parts that measurably matter. Do not sacrifice readability for speculative performance gains.

---

## 12. Source Control & Code Review Hygiene

- Keep commits **small and focused** on a single logical change; avoid mixing unrelated changes (formatting-only changes should be a separate commit from behavior changes).
- Write **meaningful commit messages** that explain *why* a change was made, not just what changed.
- Keep **pull requests small and reviewable**; large, sprawling PRs slow down review quality and increase risk.
- Ensure code builds, passes tests, and is formatted (`dotnet format` / `.editorconfig` compliant) before requesting review.
