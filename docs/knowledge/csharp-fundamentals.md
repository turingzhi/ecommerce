# C# fundamentals, encapsulation, and dependency injection

[Learning index](README.md) · [Documentation index](../README.md)

This guide covers C# language fundamentals, encapsulation, and dependency injection using examples from this **E-commerce** project. Code snippets are independent examples unless they link to an existing source file. For the wider backend concepts—HTTP, EF Core, SQL concurrency, queues, search, testing, and deployment—see [the backend learning guide](README.md). The [workflow diagrams](../workflows.md) show business behavior and the [README](../../README.md) describes the application.

## On this page

- [What each tool does](#what-each-tool-does)
- [C# building blocks](#c-building-blocks)
- [Encapsulation: protect how state changes](#encapsulation-protect-how-state-changes)
- [Interfaces and dependency injection](#interfaces-and-dependency-injection)
- [Lambdas, delegates, and LINQ](#lambdas-delegates-and-linq)
- [Async work, nullability, and failures](#async-work-nullability-and-failures)
- [Thread Pool and async/await](#thread-pool-and-asyncawait)
- [Syntax you will meet in this codebase](#syntax-you-will-meet-in-this-codebase)
- [How these pieces fit together here](#how-these-pieces-fit-together-here)
- [Check your understanding](#check-your-understanding)

## What each tool does

| Tool | Role in this project |
| --- | --- |
| C# | Language used to write classes, methods, conditions, and queries |
| .NET | Runtime that executes the compiled application; SDK that builds it |
| ASP.NET Core | HTTP routes, authentication, dependency injection, and background workers |
| EF Core | C# access to SQL Server, entity tracking, and migrations |
| SQL Server | Authoritative storage for orders, stock, payments, refunds, and the Outbox |
| RabbitMQ | Delivers events between the publisher and consumer |
| Elasticsearch | Holds a searchable copy of product information |

The `.csproj` file defines the target .NET version and package dependencies. A `using` directive makes a namespace's types easier to name; it does not install a package. `bin/` and `obj/` are generated build output.

Think of these as separate layers: **C# describes the program**, **.NET runs it**, **ASP.NET Core accepts HTTP requests**, **EF Core talks to SQL Server**, and RabbitMQ and Elasticsearch are separate services. C# language rules still apply when the web server or database is absent.

## C# building blocks

- A **class** defines a type; an **object** is an instance of it. A constructor receives values or dependencies when the object is created.
- A **field** stores data inside an object. A **property** exposes data through `get`, `set`, or `init`. A **method** performs work and can return a value.
- `var` asks the compiler to infer the type; the variable still has a specific C# type.
- `string?` permits null. Nullable annotations produce compiler warnings, but do not validate an HTTP request at runtime. The `!` operator suppresses a warning; it does not prevent null.
- **Generics** keep types explicit: `List<OrderItem>` is a list of order items, `Task<PaymentResult>` is asynchronous work that produces a payment result, and `IEnumerable<ITitleFormatter>` is a sequence of formatters.
- A **record** is useful for a data carrier. The real [CreateOrder DTO](../../Dtos/CreateOrder.cs) uses records for the requested items. Records are not automatically deeply immutable.

In [OrderService](../../Services/OrderService.cs), `public class OrderService(ShopDb db)` uses a **primary constructor**. `db` is supplied when the service is created. Its `Create` method returns `Task<OrderResult>` because it performs asynchronous database work.

### Variables, expressions, and control flow

```csharp
int quantity = 2;
long priceCents = 5000;
long totalCents = quantity * priceCents;
var isAffordable = totalCents <= 12000; // Inferred type: bool.

if (quantity <= 0)
    throw new ArgumentException("Quantity must be positive.");

foreach (var item in order.OrderItems)
    Console.WriteLine(item.Quantity);
```

`int`, `long`, `bool`, and `string` are common types. Operators such as `+`, `*`, `==`, and `&&` build expressions. `if`, `else`, `switch`, `for`, `foreach`, and `while` choose or repeat work. Braces `{ ... }` group statements; use them when the body is more than a simple line. In this project, money is stored as integer cents rather than floating-point amounts.

An `enum` gives names to a fixed set of values; for example, an illustrative `PaymentState` could contain `Pending`, `Succeeded`, and `Failed`. The current project's persisted statuses are strings, so this is a C# concept rather than a description of its model. A `switch` can choose behavior for each state, while `if` is often enough for one condition.

### Method signatures and returns

```csharp
public long CalculateTotal(long unitPriceCents, int quantity)
{
    if (quantity < 0)
        throw new ArgumentOutOfRangeException(nameof(quantity));

    return unitPriceCents * quantity;
}
```

The signature says the method is `public`, returns `long`, is named `CalculateTotal`, and takes two typed parameters. `return` supplies the value and ends the method. A `void` method returns no value. A `Task<T>` method represents asynchronous work that will eventually produce a `T`; a `Task` method is asynchronous work with no result. A method can have multiple `return` paths, as the project's services do for success, replay, and rejection.

Two methods can share a name if their **parameter lists differ**; that is an *overload*. Changing only the return type is not enough to make a new overload.

Parameters normally pass their values into a method. For a reference-type object, the method receives a copy of the reference to the same object, so it can change that object's mutable state. `ref` and `out` are special parameter modifiers for passing a variable by reference; they are not needed for the ordinary service calls in this project.

### Collections and generics

```csharp
var ids = new List<int> { 1, 2, 3 };
IEnumerable<int> values = ids;
foreach (var id in values)
    Console.WriteLine(id);
```

`List<int>` is a mutable collection of integers. `IEnumerable<int>` means the values can be enumerated; it does not promise a list or random access. `Dictionary<string, int>` associates keys with values. The type argument inside `<...>` makes a generic type reusable while retaining compile-time type checks. In this project, `List<OrderItem>` holds an order's items and `Task<OrderResult>` describes the result of an asynchronous service method.

An array such as `int[] numbers = [1, 2, 3];` has a fixed length. A `List<int>` can grow. Both hold multiple values of the same element type, but they expose different operations.

### Object members versus type members

An **instance member** belongs to one object. A **static member** belongs to the type itself:

```csharp
var order = new Order();
var orderId = order.Id;       // Id belongs to this order object.
var newId = Guid.NewGuid();   // NewGuid belongs to the Guid type.
```

A static method has no current object (`this`), so it cannot directly read an instance's fields or properties. A static class contains only static members and cannot be created with `new`.

### Inheritance, abstract, virtual, and override

**Inheritance** is an “is a” relationship: a derived class is a specialized form of its base class. A C# class can inherit from one base class and implement multiple interfaces.

| C# feature | Plain meaning |
| --- | --- |
| `abstract class` | An incomplete base class; you cannot create it directly with `new` |
| `abstract` method | Has no body; a concrete derived class must implement it |
| `virtual` method | Has a default body; a derived class may replace it |
| `override` method | Provides the derived class's version of an inherited `abstract` or `virtual` method |
| `base` | Refers to the base-class constructor or implementation |
| `this` | Refers to the current object |

This is an **illustrative snippet**, not an existing E-commerce model:

```csharp
public abstract class Notice
{
    public abstract string Format();
    public virtual string Preview() => $"Preview: {Format()}";
}

public sealed class EmailNotice : Notice
{
    public override string Format() => "Email notice";
}

Notice notice = new EmailNotice();
string text = notice.Format(); // "Email notice"
```

`Notice` cannot be constructed because it is abstract. `EmailNotice` supplies the required `Format` method. Although the variable is typed as `Notice`, C# calls the override on the actual `EmailNotice` object; this is **polymorphism**. A derived class may also override `Preview`, but it does not have to because `Preview` has a default body. `sealed` means no class can inherit from `EmailNotice`.

In the real project, [ShopDb](../../Data/ShopDb.cs) inherits from `IdentityDbContext<IdentityUser>` and overrides `OnModelCreating`. Its `base.OnModelCreating(model)` call runs the framework's base configuration before adding this project's mappings.

**Overload** and **override** are different. An overload adds another method with the same name but different parameters, such as `Format(string title)` and `Format(string title, string prefix)`. An override changes an inherited overridable method. Declaring a method with `new` in a derived class merely *hides* a base member and is not the same as overriding it.

An **interface** also describes required behavior, but implementing an interface method does not use `override`. Choose an interface when different types share a capability; choose an abstract class when related types need shared base state or implementation. A class can use another object without inheriting from it; that is **composition** (“has a” rather than “is a”).

### Useful keywords

| Keyword | Meaning |
| --- | --- |
| `public`, `private`, `protected`, `internal` | Control which code can access a member or type |
| `static` | Belongs to a type rather than one object |
| `readonly` | A field can be assigned during initialization, but not reassigned later |
| `const` | Compile-time constant |
| `init` | Property can be set during object initialization |
| `sealed` | Prevents further inheritance from a class |
| `abstract` | Defines an incomplete base type or required member |
| `virtual` / `override` | Allow a derived class to replace inherited behavior |

`static` is different from a DI **singleton**: a singleton is still an object managed by the DI container. Neither makes mutable shared data automatically safe for concurrent use.

### Class, struct, record, and small syntax rules

| Type | Main idea |
| --- | --- |
| `class` | Reference type: two variables can point to the same object |
| `struct` | Value type: assignment copies its value |
| `record` | Data-oriented type with generated value-based equality; a plain `record` is a reference type |

A record is not automatically deeply immutable. `readonly` stops a field from being reassigned after initialization, while `init` restricts when a property can be assigned; neither prevents a referenced mutable object from changing. `const` is for compile-time constants.

For example, after `var second = first;`, two `class` variables can refer to the same object, so changing that object's state through one variable is visible through the other. Two `struct` variables instead hold separate copies of the struct value. A plain `record` is a reference type with generated value-based equality, useful for data such as [CreateOrderItem](../../Dtos/CreateOrder.cs). Value-based equality does not automatically compare the contents of every nested collection.

## Encapsulation: protect how state changes

**Encapsulation** means an object exposes only the operations other code needs and controls changes to its own state. This illustrative class allows callers to read `Title`, but requires them to call `Rename` to change it:

```csharp
public class DocumentTitle
{
    public string Title { get; private set; }

    public DocumentTitle(string title) => Rename(title);

    public void Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title is required.", nameof(title));

        Title = title.Trim();
    }
}
```

This snippet is **not** an existing E-commerce model. The current EF [models](../../Models/Order.cs) mostly have public setters. In this project, important business rules are enforced by services and SQL transactions instead: [OrderService](../../Services/OrderService.cs) checks idempotency and stock before changing an order, while the [order endpoint](../../Endpoints/OrderEndpoints.cs) validates incoming HTTP data. DTOs also limit which internal data crosses the HTTP boundary. A private setter by itself would not solve concurrent stock updates; the project uses a conditional SQL update for that.

### What encapsulation protects

An **invariant** is a rule that must always remain true, such as “a title is not blank” or “available stock is not negative.” If outside code can set a property freely, it can bypass the rule. A method like `Rename` gives the class one place to check the rule before changing state.

Encapsulation is more than making fields `private`:

1. Decide which state callers may read.
2. Give callers operations that express valid changes.
3. Keep the checks next to those changes.
4. Do not expose a mutable collection if callers should not edit it directly.

For example, a `private readonly List<string> _notes` field cannot be reassigned after construction, but code inside the class can still call `_notes.Add(...)`. If the class returns that mutable list directly, outside code may change its contents. Expose a read-only view and provide an `AddNote` method when changes require validation.

### Encapsulation at different boundaries

| Boundary | Question | Example in this project |
| --- | --- | --- |
| HTTP request | Is the client's input well formed? | [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) checks item counts, quantities, and the idempotency key |
| Business operation | Is this change allowed now? | [PaymentService](../../Services/PaymentService.cs) rejects a new payment while another is `Pending` or `Unknown` |
| Database | Can concurrent requests violate the rule? | [OrderService](../../Services/OrderService.cs) conditionally reduces stock inside a transaction |
| HTTP response | What data should callers see? | [OrderResponse](../../Dtos/OrderResponse.cs) maps an internal order to a response DTO |

A private property setter helps keep code organized, but it is **not a database lock**. Two requests can each pass a C# check before either writes. That is why this project also uses SQL transactions, locks, constraints, and conditional updates. Likewise, a DTO protects the HTTP contract but does not replace server-side validation.

## Interfaces and dependency injection

An **interface** describes a contract. Different classes can implement the same contract. **Dependency injection (DI)** means a class receives a collaborator from outside rather than constructing it internally. Constructor injection is DI whether you pass the object manually or a container supplies it.

The project registers an interface and its implementation in [Program.cs](../../Program.cs):

```csharp
builder.Services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();
builder.Services.AddScoped<OutboxDispatcher>();
```

The [OutboxDispatcher](../../Services/OutboxDispatcher.cs) receives `IEventPublisher publisher` in its primary constructor and calls `publisher.PublishAsync(...)`. At runtime, ASP.NET Core supplies `RabbitMqEventPublisher`. This is **composition**: the dispatcher *has a* publisher; it does not inherit from one. The interface makes the publishing behavior replaceable, including in verification code.

### Injection versus constructing a dependency

This is an independent example:

```csharp
public interface ITitleFormatter
{
    string Format(string title);
}

public sealed class PlainTitleFormatter : ITitleFormatter
{
    public string Format(string title) => title.Trim();
}

public sealed class UppercaseTitleFormatter : ITitleFormatter
{
    public string Format(string title) => title.Trim().ToUpperInvariant();
}

public sealed class ReportService(ITitleFormatter formatter)
{
    public string CreateHeading(string title) => formatter.Format(title);
}
```

`ReportService` states what it needs, but does not decide how to create a formatter. Passing an object manually with `new ReportService(myFormatter)` is already constructor injection. A **DI container** automates the creation and wiring:

```csharp
builder.Services.AddScoped<ITitleFormatter, PlainTitleFormatter>();
builder.Services.AddScoped<ReportService>();
```

The first registration says, “When something asks for `ITitleFormatter`, create `PlainTitleFormatter`.” The second makes `ReportService` available. A class may have dependencies of its own; the container builds that **object graph** recursively. Registering a service does **not** create every service immediately. It creates a service when some code resolves it and also creates the dependencies that service needs.

In this project, an HTTP handler asks for `OrderService` as a parameter. ASP.NET Core supplies it and its `ShopDb` dependency. [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) shows this *method-parameter injection*; [OutboxDispatcher](../../Services/OutboxDispatcher.cs) shows *constructor injection*. Both are DI. `IEventPublisher` is an interface, not an object the container can construct; its registration points to the concrete `RabbitMqEventPublisher`.

| DI lifetime | Meaning | Example here |
| --- | --- | --- |
| Transient | New instance each time it is resolved | Possible for a small stateless helper |
| Scoped | One instance within a scope, usually an HTTP request | `ShopDb`, `OrderService`, `IEventPublisher` |
| Singleton | One instance for the application lifetime | `ElasticsearchClient` |

**Scoped** does not mean “load every scoped service.” A scope is a place to share instances. If one HTTP request asks for `OrderService` twice, it receives the same scoped instance within that request. A different request gets a different instance. If that request never asks for `RefundService`, DI does not create `RefundService` merely because it was registered.

Hosted workers live longer than an HTTP request. [OutboxWorker](../../Services/OutboxWorker.cs) creates a new scope for a dispatch batch and resolves the scoped `OutboxDispatcher` inside it. The dispatcher then receives its scoped `ShopDb` and publisher. The worker does not resolve every registered scoped service. Do not hold one `DbContext` in a singleton worker for its entire lifetime; `DbContext` is not thread-safe. Similarly, do not capture a scoped service in a singleton's constructor and keep it forever.

### Multiple implementations of one interface

If two classes implement `ITitleFormatter`, both can be registered:

```csharp
builder.Services.AddScoped<ITitleFormatter, PlainTitleFormatter>();
builder.Services.AddScoped<ITitleFormatter, UppercaseTitleFormatter>();
```

Requesting **one** `ITitleFormatter` gives the last registration (`UppercaseTitleFormatter`). Requesting `IEnumerable<ITitleFormatter>` gives **both**, in registration order. DI supplies the objects; your code decides which result to use. For a small fixed set, a service can select one by a defined ID. For many records identified by database IDs, query the database instead of registering every record in DI. These formatter classes are illustrative and are **not** in this project.

Unlike merely registering services, resolving `IEnumerable<ITitleFormatter>` asks DI for every matching implementation, so it creates both within that scope. It does not call `Format` on either one; your code must do that.

For example, if both formatter classes have an `Id` property in their shared interface, a service can select one from the injected collection and call `Format`. The shared interface guarantees the selected object has that method. DI does not select by ID or combine the returned strings automatically. If you ask only for `ITitleFormatter`, the last registration wins. If the choice is known at registration or injection time, keyed registrations are another option; the ordinary `IEnumerable<T>` approach is easier to understand first.

### What DI does not do

- DI does not make a class thread-safe, validate business rules, or save objects to the database.
- DI does not require an interface for every class. [OrderService](../../Services/OrderService.cs) is registered and injected as a concrete class.
- DI does not turn an `abstract` class into an instance. You must map an abstract service type to a constructible derived type, just as an interface maps to a concrete implementation.
- DI is not a database lookup. IDs for orders or payments belong in SQL Server, not in a collection of registered service objects.
- Avoid constructing `new RabbitMqEventPublisher(...)` inside `OutboxDispatcher`; that would tie the dispatcher to one implementation and make replacement harder.

In verification code, an `IEventPublisher` implementation can record messages or simulate a failure without using a real broker. The dispatcher remains unchanged because it depends on the interface contract. This is one practical reason to inject a collaborator.

## Lambdas, delegates, and LINQ

A **delegate** is a type for a callable method. A **lambda** writes a small function inline:

```csharp
Func<int, bool> isPositive = number => number > 0;
bool result = isPositive(3); // true
```

`Func<int, bool>` takes an `int` and returns a `bool`. `Action<int>` takes an `int` and returns nothing. The `number => number > 0` part is the lambda. In an endpoint, a lambda can be the HTTP handler; in `.Where(...)`, it describes a filter.

**LINQ** provides query operations such as `Where`, `Select`, `OrderBy`, `Any`, and `SingleOrDefault`:

```csharp
var orderSummaries = await db.Orders
    .Where(order => order.CustomerId == customerId)
    .OrderByDescending(order => order.CreatedAt)
    .Select(order => new { order.Id, order.Status })
    .ToListAsync();
```

With EF Core's `DbSet`, supported LINQ expressions are translated into a SQL query; `ToListAsync` executes that query and returns a list. With an already loaded `List<Order>`, LINQ runs over objects in memory. Filtering before loading avoids fetching rows you do not need. See the real paginated query in [OrderEndpoints](../../Endpoints/OrderEndpoints.cs). `SingleOrDefaultAsync` returns one match or null, but throws if more than one match exists; `AnyAsync` asks whether at least one match exists.

## Async work, nullability, and failures

An `async` method can `await` asynchronous operations. It commonly returns `Task` or `Task<T>`:

```csharp
public async Task<Order?> FindOrderAsync(
    ShopDb db, Guid id, CancellationToken cancellationToken)
{
    return await db.Orders.SingleOrDefaultAsync(
        order => order.Id == id, cancellationToken);
}
```

`Order?` says the result might be null; the caller must handle that possibility. `await` lets the method resume after the database operation completes; it does not mean each call starts a new thread. A `CancellationToken` lets a caller signal that work is no longer needed. Do not run simultaneous operations on the same `DbContext`, and avoid `.Result` or `.Wait()` to block asynchronous request code.

`?.` safely accesses a member when the value may be null; `??` supplies a fallback. For example, `string display = name?.Trim() ?? "Unknown";` produces `"Unknown"` when `name` is null. `int?` similarly allows a value type to be absent. These operators make null-handling explicit; they do not validate a business rule by themselves.

**Compile-time null warnings are not runtime validation.** For example, check whether a request's idempotency key is blank before passing it to the service. The null-forgiving operator `value!` only silences a warning; if `value` is really null, it remains null at runtime.

`throw` reports an exceptional failure. `try`/`catch` handles one where the code can recover or translate it; `finally` runs when control leaves the block. [OutboxDispatcher](../../Services/OutboxDispatcher.cs) catches a publish failure, records a retry time, and rethrows so its worker can log the failure. Expected business rejections in this project often use a result with an `Error` field instead of throwing an exception.

`using` has two meanings:

```csharp
using System.Text; // Make names from a namespace available.

using var stream = File.OpenRead("example.txt");
// The stream is disposed when this scope ends.
```

`await using` similarly disposes an asynchronously disposable resource, such as the transaction used by [OrderService](../../Services/OrderService.cs). Disposing a database transaction is different from garbage collection; if it has not committed, it rolls back its uncommitted work.

### Cancellation and an uncertain outcome

Cancellation is cooperative: code must observe the token or pass it to an operation that supports it. Cancelling a request does not undo an already committed transaction. A client timeout also does not prove the server failed to save the order; retry the same request with the same idempotency key to discover the saved result.

The background workers in this repository propagate their stopping tokens. Current order endpoints and `OrderService.Create` do not propagate an HTTP cancellation token through every database call; the token-bearing example above illustrates a pattern, not complete cancellation coverage in this app.

## Thread Pool and async/await

.NET maintains a Thread Pool to avoid creating a new OS thread for every task.

### Blocking

Example:

```csharp
Thread.Sleep(5000);
```

This keeps a thread occupied while doing no useful work.

Too much blocking can cause:

**Thread Pool Starvation**

### I/O-bound Work

Example:

```csharp
await db.Users.ToListAsync();
```

While waiting for the database, the current thread can return to the Thread Pool.

Important:

```text
await
≠ create a new thread
```

### CPU-bound Work

Examples:

- Image processing
- Compression
- Large calculations
- Encryption work

The CPU must actually perform the work.

`Task.Run()` moves work to the Thread Pool, but does not create extra CPU capacity.

## Syntax you will meet in this codebase

### Object creation and initializers

```csharp
var message = new OutboxMessage
{
    OrderId = order.Id,
    Type = "OrderPaid",
    Payload = "..."
};
```

`new OutboxMessage` creates an object, and the braces set its public properties. The compiler knows the type on the right, so `var message` still has a specific type. When the expected type is already known, C# can shorten `new OutboxMessage()` to `new()`, as [BrokerEvent.ToOutboxMessage](../../Services/RabbitMqEventPublisher.cs) does. Creating an object in memory does **not** save it to SQL Server; EF Core must track it and `SaveChangesAsync` must run.

### Two uses of `=>`

```csharp
// The first line is a member inside a class; the second is inside a method.
public string Label() => "Paid";           // Expression-bodied method.
var paid = orders.Where(o => o.Status == "Paid"); // Lambda passed to Where.
```

The first `=>` is a shorter way to write a method that returns one expression. The second creates a function for `Where` to use; `o` is its parameter. [OrderResponse.From](../../Dtos/OrderResponse.cs) uses both forms.

### Extension methods

An extension method is a static method that can be called using object-style syntax. The `this` before its first parameter marks the type it extends:

```csharp
public static WebApplication MapOrderEndpoints(this WebApplication app)
```

That declaration in [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) allows [Program.cs](../../Program.cs) to call `app.MapOrderEndpoints()`. It does not change the `WebApplication` class or require inheritance.

### Equality and pattern checks

For an ordinary class, two variables can refer to the same object, but two separately created objects are not automatically equal merely because their properties match. A `record` generates value-based equality from its members:

```csharp
var first = new CreateOrderItem(1, 2);
var second = new CreateOrderItem(1, 2);
bool sameValues = first == second; // true for this record.
```

`is null` and `is not` test a value or type. For example, `ex is not BrokerDeliveryUnavailableException` in [OutboxDispatcher](../../Services/OutboxDispatcher.cs) checks which kind of error occurred. These checks do not modify the object.

## How these pieces fit together here

```text
POST /orders
  → ASP.NET Core selects the Minimal API route and checks authorization
  → DI provides OrderService and its scoped ShopDb
  → endpoint validates the request shape
  → OrderService enforces business rules
  → EF Core writes stock, order, and Outbox row in one SQL transaction
  → endpoint returns a response DTO
```

The project uses **Minimal APIs**. `MapPost` chooses a route and `.RequireAuthorization()` protects it; a controller-based API could express comparable behavior with attributes. Choosing Minimal APIs does not change C# classes, interfaces, encapsulation, or DI. See [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) and [Program.cs](../../Program.cs).

## Check your understanding

1. What is the difference between an `Order` class and one `Order` object?
2. Why can a `private set` property protect a rule inside one object but not prevent two SQL transactions from racing?
3. Why can `OutboxDispatcher` call `PublishAsync` without creating `RabbitMqEventPublisher` itself?
4. What is created when a request needs `OrderService`: every scoped registration, or only the required dependency graph?
5. What is the difference between an `abstract` method, a `virtual` method, and an `override`?
6. When does an EF Core LINQ query execute, and what changes when the data is already in a `List<T>`?
7. Why must a long-lived hosted worker create a scope before resolving `ShopDb`?
8. Why does `new OutboxMessage { ... }` not itself insert a row into SQL Server?
9. What does `this WebApplication app` mean in an extension method?


---

[Learning index](README.md) · Next: [HTTP, ASP.NET Core, and security](http-apis-security.md)
