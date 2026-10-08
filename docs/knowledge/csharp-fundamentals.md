# C# fundamentals, encapsulation, and dependency injection

[Learning index](README.md) · [Documentation index](../README.md)

Learn C# using small examples and code from this project. Snippets are teaching examples unless they link to a source file. The [learning index](README.md) covers the wider backend topics, and the [workflow diagrams](../workflows.md) show how the app behaves.

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
| SQL Server | Stores products, stock, orders, payments, refunds, shipments, returns, and the Outbox |
| Redis | Stores customer carts and short-lived search results |
| RabbitMQ | Delivers events between the publisher and consumer |
| Elasticsearch | Holds a searchable copy of product information |
| React and TypeScript | Build the browser storefront; this code does not run as C# |

The `.csproj` file defines the target .NET version and package dependencies. A `using` directive makes a namespace's types easier to name; it does not install a package. `bin/` and `obj/` are generated build output.

C# describes the program; .NET runs it. ASP.NET Core handles HTTP, and EF Core accesses SQL Server. Redis, RabbitMQ, and Elasticsearch run as separate services.

## C# building blocks

- A **class** defines a type; an **object** is an instance of it. A constructor receives values or dependencies when the object is created.
- A **field** stores data inside an object. A **property** exposes data through `get`, `set`, or `init`. A **method** performs work and can return a value.
- `var` asks the compiler to infer the type; the variable still has a specific C# type.
- `string?` permits null. Nullable annotations produce compiler warnings, but do not validate an HTTP request at runtime. The `!` operator suppresses a warning; it does not prevent null.
- **Generics** keep types explicit: `List<OrderItem>` is a list of order items, `Task<PaymentResult>` is asynchronous work that produces a payment result, and `IEnumerable<ITitleFormatter>` is a sequence of formatters.
- A **record** is useful for a data carrier. The real [CreateOrder DTO](../../src/Ecommerce.Api/Features/Orders/Contracts/CreateOrderRequest.cs) uses records for the requested items. Records are not automatically deeply immutable.

In [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs), `public class OrderService(ShopDbContext db)` uses a **primary constructor**. `db` is supplied when the service is created. Its `Create` method returns `Task<OrderResult>` because it performs asynchronous database work.

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

An `enum` gives names to a fixed set of values; for example, an illustrative `PaymentState` could contain `Pending`, `Succeeded`, and `Failed`. Order and payment statuses are stored as strings here. `CartWriteResult` is a real enum in [CartService](../../src/Ecommerce.Api/Features/Cart/Services/CartService.cs). A `switch` can choose behavior for each state, while `if` is often enough for one condition.

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

You can also write your own generic method:

```csharp
static T First<T>(IReadOnlyList<T> values)
{
    if (values.Count == 0)
        throw new ArgumentException("At least one value is required.");
    return values[0];
}

int firstId = First<int>(new[] { 1, 2, 3 });
string firstName = First(new[] { "Mouse", "Headphones" });
```

`T` stands for the caller's chosen type. The same method works with integers or strings, and its result keeps that type. C# can often infer `T`, as in the second call. It does not turn values into untyped objects.


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

In the real project, [ShopDbContext](../../src/Ecommerce.Api/Infrastructure/Persistence/ShopDbContext.cs) inherits from `IdentityDbContext<IdentityUser>` and overrides `OnModelCreating`. Its `base.OnModelCreating(model)` call runs the framework's base configuration before adding this project's mappings.

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

After `var second = first;`, two class variables can refer to the same object. Changing that object through one variable is visible through the other. Struct assignment copies the value, although a struct can itself contain references to shared objects.

Records generate equality from their members. Two [CreateOrderItemRequest](../../src/Ecommerce.Api/Features/Orders/Contracts/CreateOrderRequest.cs) records with the same product ID and quantity compare equal. Records do not automatically make nested lists immutable or compare their contents item by item. Likewise, `readonly` stops field reassignment and `init` limits property assignment; neither freezes a referenced object.

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

This snippet is **not** an existing E-commerce model. The current EF [models](../../src/Ecommerce.Api/Features/Orders/Models/Order.cs) mostly have public setters. In this project, important business rules are enforced by services and SQL transactions instead: [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs) checks idempotency and stock before changing an order, while the [order endpoint](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs) validates incoming HTTP data. DTOs also limit which internal data crosses the HTTP boundary. A private setter by itself would not solve concurrent stock updates; the project uses a conditional SQL update for that.

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
| HTTP request | Is the client's input well formed? | [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs) checks item counts, quantities, and the idempotency key |
| Business operation | Is this change allowed now? | [PaymentService](../../src/Ecommerce.Api/Features/Payments/Services/PaymentService.cs) rejects a new payment while another is `Pending` or `Unknown` |
| Database | Can concurrent requests violate the rule? | [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs) conditionally reduces stock inside a transaction |
| HTTP response | What data should callers see? | [OrderResponse](../../src/Ecommerce.Api/Features/Orders/Contracts/OrderResponse.cs) maps an internal order to a response DTO |

A private property setter helps keep code organized, but it is **not a database lock**. Two requests can each pass a C# check before either writes. That is why this project also uses SQL transactions, locks, constraints, and conditional updates. Likewise, a DTO protects the HTTP contract but does not replace server-side validation.

## Interfaces and dependency injection

Dependency injection (DI) means a class receives its dependencies instead of
constructing them itself. ASP.NET Core's container creates, supplies, reuses, and
cleans up the dependencies it owns.

- `[FromBody]` binds data from an HTTP request body to a parameter.
- `[FromServices]` resolves a registered service from the request's DI scope.
- An interface defines a contract; a registration chooses its implementation.
- A concrete class can also be injected directly. DI does not require an interface.

A request DTO bound with `[FromBody]` is request data, not a registered DI service.
See the real action in [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs):

```csharp
public async Task<IResult> Create(
    [FromBody] CreateOrderRequest request,
    [FromServices] OrderService service)
```

ASP.NET Core supplies `OrderService`, whose constructor needs `ShopDbContext`.
DI resolves that dependency too. Registration does not create every service at
startup; dependencies are resolved when needed.

### When to inject an interface

Use an interface for a capability you need to replace or substitute in tests.
This project registers the following in [Program.cs](../../src/Ecommerce.Api/Program.cs):

```csharp
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();
builder.Services.AddScoped<OutboxDispatcher>();
```

[OutboxDispatcher](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxDispatcher.cs)
receives `IEventPublisher` and calls `PublishAsync`. DI supplies
`RabbitMqEventPublisher`; verification code can supply another publisher. The
dispatcher has a collaborator rather than inheriting from it: this is composition.

`OrderService` is injected as a concrete class because there is one current
implementation of that workflow. Add an `IOrderService` when substitution solves
a real problem, rather than copying every service method into another file.

### Injection versus constructing a dependency

This is a teaching example, not a service registered by the application:

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

Passing `new ReportService(myFormatter)` manually is already constructor injection.
A DI container automates that wiring and recursively resolves dependencies:

```csharp
builder.Services.AddScoped<ITitleFormatter, PlainTitleFormatter>();
builder.Services.AddScoped<ReportService>();
```

The first registration means: when code requests `ITitleFormatter`, supply a
`PlainTitleFormatter`. It does not call `Format`; the consumer calls that method.

### Service lifetimes

| Lifetime | Creation and reuse | Disposal of container-owned disposable instances |
| --- | --- | --- |
| Singleton | One instance per registration in a root service provider, shared across scopes | When the provider is disposed, normally at application shutdown |
| Scoped | One instance per registration within a DI scope | When that scope is disposed |
| Transient | A new instance for each resolution | When its owning scope is disposed |

The project uses scoped `ShopDbContext` and business services, and singleton Redis
and Elasticsearch clients. It has no explicit application `AddTransient`
registrations. A possible transient registration for the teaching example is:

```csharp
builder.Services.AddTransient<PlainTitleFormatter>();
```

An HTTP request normally has one scope. Resolving `OrderService` twice in that
scope reuses its instance; another request gets a different scoped instance.
Different scoped registrations can coexist in a scope. They are created only when
needed, so an unused `RefundService` is not created just because it is registered.

Separate registrations can create separate objects even if they use the same class.
Separate root providers have separate singleton caches; an explicitly supplied
existing instance can still be shared. Singleton does not mean one object across
every process or container.

### HTTP request lifecycle

This flow illustrates the lifetimes. The formatter is a teaching example; the
application's actual registrations are in the [inventory](../architecture-review.md#di-and-lifetime-inventory).

```text
HTTP request A
  → Access request scope A
  → Resolve dependencies when needed
      OrderService A          scoped
      ShopDbContext A         scoped
      PlainTitleFormatter 1   transient
      PlainTitleFormatter 2   transient, if resolved again
      Redis multiplexer       singleton
  → Run the controller and services
  → Dispose scope A when the request ends
      Clean up owned disposable scoped/transient objects
      Keep the singleton for later requests

HTTP request B
  → New scoped objects and new transient resolutions
  → Reuse the same singleton from this provider
```

A transient object is not automatically disposed immediately after its method ends.
If a singleton captures a transient, that particular object stays referenced by
the singleton; its transient registration does not make it refresh on every call.

### Disposal versus garbage collection

`Dispose()` or `DisposeAsync()` releases resources according to the object's
implementation. Garbage collection later reclaims unreachable managed memory.
Disposal does not mean immediate memory removal.

DI cleans up owned `IDisposable`/`IAsyncDisposable` instances. Non-disposable services
receive no disposal callback. Disposable transients resolved from the root provider
can remain there until provider disposal; use a scope for short-lived work.

Type registrations and factory registrations normally give DI disposal ownership.
An existing instance passed to `AddSingleton(existingInstance)` remains the caller's
responsibility to dispose. Calling `new` outside such registrations does not
automatically give DI ownership. Do not manually dispose injected dependencies
owned by DI; dispose scopes you create. See [Microsoft's disposal guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines#disposal-of-services).

### DbContext versus Redis

| Dependency | Lifetime here | Reason |
| --- | --- | --- |
| EF Core `ShopDbContext` | Scoped | Mutable tracking state and a unit of work; not thread-safe |
| Redis `IConnectionMultiplexer` | Singleton | Designed for concurrent use and reusable Redis connections |

A context is not a physical SQL connection. EF Core uses connections as needed;
ADO.NET connection pooling reuses physical connections separately. Explicit
transactions can keep a connection open longer. See [data access and pooling](data-concurrency.md#how-connection-pooling-works-here)
and [EF context lifetimes](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/).

Singleton registration does not make an object thread-safe. Redis's multiplexer is
designed for shared use; see [StackExchange.Redis usage](https://seredis.dev/Basics.html).
[CartService](../../src/Ecommerce.Api/Features/Cart/Services/CartService.cs) stays
scoped because it also uses `ShopDbContext`. Using Redis does not require a business
service to be singleton.

### Multiple implementations of one interface

These alternative registrations use the teaching formatter classes above:

```csharp
builder.Services.AddScoped<ITitleFormatter, PlainTitleFormatter>();
builder.Services.AddScoped<ITitleFormatter, UppercaseTitleFormatter>();
```

With the built-in container, requesting one `ITitleFormatter` returns the last
registration, `UppercaseTitleFormatter`. Requesting `IEnumerable<ITitleFormatter>`
resolves both, in registration order, using their declared lifetimes. Selecting
one from the collection does not prevent the others from being resolved. Within
this scope, direct resolution and the collection share the same scoped instance
for the last registration. Resolving services does not call their `Format` methods.

Keyed services, available since .NET 8, select a registration explicitly. For
example, a keyed version of the formatter registration and consumer would be:

```csharp
builder.Services.AddKeyedScoped<ITitleFormatter, PlainTitleFormatter>("plain");
builder.Services.AddKeyedScoped<ITitleFormatter, UppercaseTitleFormatter>("upper");

public sealed class ReportService(
    [FromKeyedServices("upper")] ITitleFormatter formatter)
{
    public string CreateHeading(string title) => formatter.Format(title);
}
```

These are alternative teaching examples, not actual payment integrations or keyed
registrations in this project. See [registration rules](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection#service-registration-methods)
and [keyed services](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview#keyed-services).

### Rules to remember

1. Do not capture a scoped dependency in a singleton's constructor.
2. Do not run concurrent operations on one `DbContext`; await operations in order
   or use separate contexts for independent work.
3. Reuse pooled SQL connections, not a shared application-wide context.
4. Use singleton for appropriate shared services that support concurrent use.
5. Use scoped for request/unit-of-work services, and transient when a fresh
   instance per resolution is appropriate.
6. A DI scope is neither a database transaction nor a security boundary.
7. Background work using scoped services needs its own scope; it must not keep
   disposed request services.

[OutboxWorker](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxWorker.cs)
demonstrates the last rule: it creates an async scope, resolves `OutboxDispatcher`,
awaits its work, and disposes the scope. A singleton worker can safely use scoped
services this way without retaining them for the application lifetime.

## Lambdas, delegates, and LINQ

A **delegate** is a type for a function you can store or pass to another method. A **lambda** writes that function inline:

```csharp
Func<int, bool> isPositive = number => number > 0;
bool result = isPositive(3); // true
Action<string> print = text => Console.WriteLine(text);
print("Paid");
```

In `Func<int, bool>`, the first type is the input and the last is the result. `Func<int, int, long>` takes two integers and returns a long. `Func<string>` has no input and returns a string. `Action<string>` takes a string and returns nothing.

A **callback** is a function passed to other code for that code to call. It does not have to be asynchronous:

```csharp
static string MakeLabel(string name, Func<string, string> format)
{
    return format(name); // Call the supplied function.
}

string label = MakeLabel(" mouse ", name => name.Trim().ToUpperInvariant());
// label is "MOUSE".
```

`MakeLabel` decides when to call `format`; the caller decides what formatting does. Passing `format` and calling `format(name)` are separate steps.

The project uses this pattern in [CommerceTelemetry.MeasureAsync](../../src/Ecommerce.Api/Observability/CommerceTelemetry.cs):

```csharp
public static async Task<T> MeasureAsync<T>(
    string operation, string dependency,
    Func<Task<T>> action, Func<T, string> outcome)
```

This signature is an excerpt. `action` supplies asynchronous work returning `T`. `outcome` converts its result into a metric label. The wrapper calls `await action()` and then `outcome(value)`. [OrderService.Create](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs) passes lambdas that run checkout and classify the result as success, replay, or conflict. This combines generics, delegates, callbacks, and async work in real code.

**LINQ** provides query operations such as `Where`, `Select`, `OrderBy`, `Any`, and `SingleOrDefault`:

```csharp
var orderSummaries = await db.Orders
    .Where(order => order.CustomerId == customerId)
    .OrderByDescending(order => order.CreatedAt)
    .Select(order => new { order.Id, order.Status })
    .ToListAsync();
```

With EF Core's `DbSet`, supported LINQ expressions are translated into a SQL query; `ToListAsync` executes that query and returns a list. With an already loaded `List<Order>`, LINQ runs over objects in memory. Filtering before loading avoids fetching rows you do not need. See the real paginated query in [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs). `SingleOrDefaultAsync` returns one match or null, but throws if more than one match exists; `AnyAsync` asks whether at least one match exists.

## Async work, nullability, and failures

An `async` method can `await` asynchronous operations. It commonly returns `Task` or `Task<T>`:

```csharp
public async Task<Order?> FindOrderAsync(
    ShopDbContext db, Guid id, CancellationToken cancellationToken)
{
    return await db.Orders.SingleOrDefaultAsync(
        order => order.Id == id, cancellationToken);
}
```

`Order?` says the result might be null; the caller must handle that possibility. `await` lets the method resume after the database operation completes; it does not mean each call starts a new thread. A `CancellationToken` lets a caller signal that work is no longer needed. Do not run simultaneous operations on the same `DbContext`, and avoid `.Result` or `.Wait()` to block asynchronous request code.

`?.` safely accesses a member when the value may be null; `??` supplies a fallback. For example, `string display = name?.Trim() ?? "Unknown";` produces `"Unknown"` when `name` is null. `int?` similarly allows a value type to be absent. These operators make null-handling explicit; they do not validate a business rule by themselves.

**Compile-time null warnings are not runtime validation.** For example, check whether a request's idempotency key is blank before passing it to the service. The null-forgiving operator `value!` only silences a warning; if `value` is really null, it remains null at runtime.

`throw` reports an exceptional failure. `try`/`catch` handles one where the code can recover or translate it; `finally` runs when control leaves the block. [OutboxDispatcher](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxDispatcher.cs) catches a publish failure, records a retry time, and rethrows so its worker can log the failure. Expected business rejections in this project often use a result with an `Error` field instead of throwing an exception.

`using` has two meanings:

```csharp
using System.Text; // Make names from a namespace available.

using var stream = File.OpenRead("example.txt");
// The stream is disposed when this scope ends.
```

`await using` similarly disposes an asynchronously disposable resource, such as the transaction used by [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs). Disposing a database transaction is different from garbage collection; if it has not committed, it rolls back its uncommitted work.

### Cancellation and an uncertain outcome

Cancellation is cooperative: code must observe the token or pass it to an operation that supports it. Cancelling a request does not undo an already committed transaction. A client timeout also does not prove the server failed to save the order; retry the same request with the same idempotency key to discover the saved result.

Outbox and RabbitMQ workers pass stopping tokens through much of their async work. Expiration passes its token to the selection query and checks it between orders, but `OrderService.Expire` does not accept a token for an individual order’s SQL work. Product event processing also does not pass a token to the Redis generation increment. Current order endpoints and `OrderService.Create` do not propagate an HTTP token through every database call. The example above illustrates a pattern, not complete cancellation coverage; see the [review](../architecture-review.md#4-expiration-cannot-cancel-an-in-flight-sql-operation--low).

## Thread Pool and async/await

.NET maintains a Thread Pool to avoid creating a new OS thread for every task.

### Blocking

```csharp
Thread.Sleep(5000);
```

This occupies a thread for five seconds. If many requests block at once, the Thread Pool may struggle to supply threads for other work. This is called **Thread Pool starvation**.

### I/O-bound Work

```csharp
await db.Orders.ToListAsync();
```

While an asynchronous database operation waits for I/O, the thread can do other work. When the operation finishes, the method continues. `await` does not create a thread or make SQL faster.

### CPU-bound Work

Image processing, compression, encryption, and large calculations still need CPU time. `Task.Run` schedules work on the Thread Pool; it does not add CPU capacity. In a web app, moving every calculation to `Task.Run` can increase contention rather than improve throughput.

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

`new OutboxMessage` creates an object, and the braces set its public properties. The compiler knows the type on the right, so `var message` still has a specific type. When the expected type is already known, C# can shorten `new OutboxMessage()` to `new()`, as [BrokerEvent.ToOutboxMessage](../../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqEventPublisher.cs) does. Creating an object in memory does **not** save it to SQL Server; EF Core must track it and `SaveChangesAsync` must run.

### Two uses of `=>`

```csharp
// The first line is a member inside a class; the second is inside a method.
public string Label() => "Paid";           // Expression-bodied method.
var paid = orders.Where(o => o.Status == "Paid"); // Lambda passed to Where.
```

The first `=>` is a shorter way to write a method that returns one expression. The second creates a function for `Where` to use; `o` is its parameter. [OrderResponse.From](../../src/Ecommerce.Api/Features/Orders/Contracts/OrderResponse.cs) uses both forms.

### Extension methods

An extension method is a static method that can be called using object-style syntax. The `this` before its first parameter marks the type it extends:

```csharp
public static IMvcBuilder AddCommerceControllers(
    this IServiceCollection services, IHostEnvironment environment)
```

That declaration in [ControllerRegistration](../../src/Ecommerce.Api/Common/Controllers/ControllerRegistration.cs) allows [Program.cs](../../src/Ecommerce.Api/Program.cs) to call `builder.Services.AddCommerceControllers(builder.Environment)`. It does not change the `IServiceCollection` interface or require inheritance.

### Equality and pattern checks

For an ordinary class, two variables can refer to the same object, but two separately created objects are not automatically equal merely because their properties match. A `record` generates value-based equality from its members:

```csharp
var first = new CreateOrderItemRequest(1, 2);
var second = new CreateOrderItemRequest(1, 2);
bool sameValues = first == second; // true for this record.
```

`is null` and `is not` test a value or type. For example, `ex is not BrokerDeliveryUnavailableException` in [OutboxDispatcher](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxDispatcher.cs) checks which kind of error occurred. These checks do not modify the object.

## How these pieces fit together here

```text
POST /orders
  → ASP.NET Core selects the controller action and checks authorization
  → DI provides OrderService and its scoped ShopDbContext
  → endpoint validates the request shape
  → OrderService enforces business rules
  → EF Core writes stock, order, and Outbox row in one SQL transaction
  → endpoint returns a response DTO
```

The project uses **controllers**. `[HttpPost]` chooses a route and `[Authorize]` protects it. Controller actions use explicit `[FromBody]`, `[FromQuery]`, `[FromRoute]`, and `[FromServices]` parameters. The actions retain `IResult` responses to preserve the existing HTTP response behavior. Changing the HTTP layer does not change the business services, SQL transactions, or DI lifetimes. See [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs) and [src/Ecommerce.Api/Program.cs](../../src/Ecommerce.Api/Program.cs).

## Check your understanding

1. What is the difference between an `Order` class and one `Order` object?
2. Why can a `private set` property protect a rule inside one object but not prevent two SQL transactions from racing?
3. Why can `OutboxDispatcher` call `PublishAsync` without creating `RabbitMqEventPublisher` itself?
4. What is created when a request needs `OrderService`: every scoped registration, or only the required dependency graph?
5. What is the difference between an `abstract` method, a `virtual` method, and an `override`?
6. When does an EF Core LINQ query execute, and what changes when the data is already in a `List<T>`?
7. In `MeasureAsync<T>`, who supplies `action`, and who calls it?
8. Why must a long-lived hosted worker create a scope before resolving `ShopDbContext`?
9. Why does `new OutboxMessage { ... }` not itself insert a row into SQL Server?
10. What does `this IServiceCollection services` mean in an extension method?

---

[Learning index](README.md) · Next: [HTTP, ASP.NET Core, and security](http-apis-security.md)
