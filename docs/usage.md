# Usage guide

Every snippet here is taken from, or matches, runnable code:
[`samples/Cordis.Examples`](../samples/Cordis.Examples) (run one with
`dotnet run --project samples/Cordis.Examples -- <name>`) and the end-to-end
[`samples/Cordis.Sample`](../samples/Cordis.Sample).

- [Setup](#setup)
- [The root context](#the-root-context)
- [Writing plugins](#writing-plugins)
- [Effects: owning resources](#effects-owning-resources)
- [Services and dependencies](#services-and-dependencies)
- [Config and validation](#config-and-validation)
- [Events](#events)
- [Isolation and intercepts](#isolation-and-intercepts)
- [Timers](#timers)
- [Declarative trees: the loader](#declarative-trees-the-loader)
- [Swapping implementations](#swapping-implementations)
- [Observing the lifecycle](#observing-the-lifecycle)
- [Patterns](#patterns)
- [Pitfalls](#pitfalls)
- [Testing](#testing)

## Setup

Reference the projects you need (there is no NuGet package yet):

```xml
<ProjectReference Include="..\Cordis.NET\src\Cordis\Cordis.csproj" />
<ProjectReference Include="..\Cordis.NET\src\Cordis.Timer\Cordis.Timer.csproj" />   <!-- optional -->
<ProjectReference Include="..\Cordis.NET\src\Cordis.Loader\Cordis.Loader.csproj" /> <!-- optional -->
```

Namespaces: `Cordis`, `Cordis.Timers`, `Cordis.Loading`. Targets .NET 10; build with
`-p:CordisTfm=net8.0` on an older SDK.

## The root context

```csharp
var root = new Context();
root.Loggers.Level = LogLevel.Debug;   // default level for the whole tree

// ... add plugins ...

await root.DisposeAsync();             // tears the whole tree down, leaves first
```

The root context is unrestricted: it can read any service without declaring it. Plugin contexts
cannot (see [Services](#services-and-dependencies)).

## Writing plugins

A plugin is any of the following. All forms get a `Context` and may take a config.

**A class.** The constructor is the body; the instance lives as long as the load.

```csharp
[Inject("database")]
public sealed class Reporter(Context ctx)
{
    private readonly DatabaseService _db = ctx.Get<DatabaseService>();
}

var fiber = root.Plugin<Reporter>();
```

The constructor may be `(Context)` or `(Context, TConfig)`. If the instance implements
`IDisposable` / `IAsyncDisposable` it is disposed on unload; if it implements `IAsyncInit`,
`InitAsync` runs before the plugin counts as loaded.

**An `IPlugin` instance.** Useful when the plugin object carries state across loads, or is built
at runtime.

```csharp
public sealed class Banner : IPlugin<string>
{
    public ValueTask ApplyAsync(Context ctx, string config) { /* ... */ return default; }
}

await root.Plugin(new Banner(), "v1");
```

`IPlugin` also exposes `PluginName`, `Inject` and `ConfigSchema`.

**An inline body.**

```csharp
root.Apply("audit", ctx => ctx.On(OrderPlaced, (_, order) => Log(order)));
root.Inject(["database", "timer"], ctx => { /* runs while both exist */ });
root.ApplyAsync("warmup", async ctx => await WarmAsync(ctx), "cache");
```

**Waiting.** `ctx.Plugin(...)` returns a `Fiber` immediately; the body runs on a later turn.
`await fiber` waits until it settles and rethrows a load failure. A fiber waiting for
dependencies settles as `Pending`.

## Effects: owning resources

Acquire everything through `ctx` and you never write teardown bookkeeping. Example:
[`EffectsExample.cs`](../samples/Cordis.Examples/EffectsExample.cs).

```csharp
ctx.Effect(() =>                                   // sync
{
    var socket = Open();
    return () => socket.Close();
}, "socket");

ctx.EffectAsync(async () =>                        // async: the plugin stays Loading until done
{
    var conn = await ConnectAsync();
    return () => conn.DisposeAsync();
});

ctx.EffectMany(Resources);                         // generator: one disposer per resource
async IAsyncEnumerable<Disposer> Resources()
{
    var a = await OpenAsync("a");
    yield return a.DisposeAsync;
    var b = await OpenAsync("b");
    yield return b.DisposeAsync;
}

ctx.Use(new FileStream(path, FileMode.Open));      // any IDisposable / IAsyncDisposable
ctx.Apply("child", child => { /* ... */ });        // a child plugin is an effect too
```

On unload, effects are disposed newest first, one at a time; a child plugin is fully unloaded
before the parent's earlier effects run. Every call returns an `EffectHandle`; dispose it to
release that one resource early.

Effects can only be created while the fiber is alive; afterwards they throw
`InactiveFiberException`.

## Services and dependencies

A service is a value published under a name. Example:
[`ServicesExample.cs`](../samples/Cordis.Examples/ServicesExample.cs).

**Providing.** Either derive from `Service` (the plugin provides itself) or call `ctx.Provide`.

```csharp
[ServiceName("cache")]
public sealed class CacheService(Context ctx) : Service(ctx, "cache"), IAsyncInit
{
    public async ValueTask InitAsync() => await LoadAsync();  // dependents wait for this
    protected override bool CheckAvailable() => _healthy;     // false keeps dependents unloaded
}

ctx.Provide("clock", new SystemClock());   // from any plugin body; withdrawn on unload
ctx.Set("clock", new FakeClock());         // swap the value; dependents reload
```

`[ServiceName]` on the type is what `ctx.Get<T>()` and `[Inject(typeof(T))]` look up; without it
the full type name is used. The base constructor comes in three forms that agree with that lookup:
`Service(ctx)` takes the name from the type, `Service<TSelf>(ctx)` does the same through the type
parameter, and `Service(ctx, "name")` sets it explicitly.

**Consuming.** Declare, then get:

```csharp
[Inject("cache")]
[Inject(typeof(TimerService))]
public sealed class Warmer(Context ctx)
{
    private readonly CacheService _cache = ctx.Get<CacheService>();   // throws if not declared
}

var maybe = ctx.TryGet<CacheService>();   // null instead of throwing (still must be declared)
var raw = ctx.Get("cache");               // by name
```

What happens over time:

| Event | Consumer |
| --- | --- |
| created before the provider | waits in `Pending` |
| provider becomes active (after `InitAsync`) | loads |
| provider restarts, is replaced, or calls `Set` | unloads, then loads against the new value |
| provider goes away | unloads, back to `Pending` |

The provider is cleaned up only after its consumers have unloaded, so a consumer may still use
the service in its own cleanup.

**Rules of access.** A plugin can read a service it declared, or one it provides itself. It
cannot read a service it did not declare, even one its parent declared or one provided on the
root context. Accessing an undeclared service throws `ServiceNotInjectedException`; a declared one
that is unavailable throws `ServiceUnavailableException`.

## Config and validation

Declare a schema as a public static `Schema` (or `IPlugin.ConfigSchema`). Example:
[`ConfigExample.cs`](../samples/Cordis.Examples/ConfigExample.cs).

```csharp
public sealed class Poller
{
    public sealed class Options
    {
        [Range(10, 10_000)] public int IntervalMs { get; set; } = 100;
        [Required, MinLength(1)] public string Target { get; set; } = "localhost";
    }

    public static readonly IConfigSchema Schema = DataAnnotationsSchema<Options>.Instance;

    public Poller(Context ctx, Options config) { /* config is validated */ }
}

var poller = await root.Plugin<Poller>(new Poller.Options { Target = "db-1" });
await poller.UpdateAsync(new Poller.Options { Target = "db-2" });   // unload + reload
```

For hand-written validation or normalisation use
`new DelegateSchema<T>(input => ..., simplify: value => ...)`; throw `ConfigValidationException`
with `ConfigIssue`s to reject.

Behaviour to rely on:

- The config is validated **on every load**. `fiber.Config` holds the config as you passed it.
- A plugin created with an invalid config ends `Failed` with a `ConfigValidationException`.
- `UpdateAsync` on a running plugin validates first; an invalid config throws and the plugin
  keeps running with its old config.
- A `Failed` plugin stays failed through restarts and dependency changes until `UpdateAsync`
  recovers it.
- `UpdateAsync(config, noSave: true)` skips `Internal.Committed`, so the loader does not write it
  back.

**Applying a config in place.** By default an update restarts the plugin. A plugin that can adopt
a new config without restarting registers a handler; not calling `next` keeps it running.

```csharp
ctx.OnConfigUpdate((resolved, next) =>
{
    if (resolved is not Options options || options.Endpoint != _endpoint) return next(); // restart
    _interval = options.Interval;                                                          // in place
    return default;
});
```

Handlers are effects of the fiber, so registering one on every load does not accumulate them.

## Events

Define a key per event; the key type picks the dispatch mode and fixes the handler signature.
Example: [`EventsExample.cs`](../samples/Cordis.Examples/EventsExample.cs).

```csharp
public static readonly EventKey<Order> OrderPlaced = new("order-placed");
public static readonly BailKey<string, decimal?> PriceLookup = new("price-lookup");
public static readonly WaterfallKey<string> RenderReceipt = new("render-receipt");

// listen (inside a plugin body; removed when the plugin unloads)
ctx.On(OrderPlaced, (c, order) => { if (order.Total >= 100) Audit(order); });
ctx.On(OrderPlaced, async (c, order) => await IndexAsync(order), prepend: true);
ctx.On(PriceLookup, (c, sku) => new ValueTask<decimal?>(Find(sku)));
ctx.On(RenderReceipt, async (c, text, next) => $"[{await next(text)}]");
ctx.Once(OrderPlaced, (c, order) => { First(order); return default; });

// dispatch
await root.EmitAsync(OrderPlaced, order);                 // sequential
await root.ParallelAsync(OrderPlaced, order);             // concurrent
decimal? price = await root.BailAsync(PriceLookup, "apple");
string receipt = await root.WaterfallAsync(RenderReceipt, "thanks", t => new ValueTask<string>(t + "!"));
```

### Choosing a dispatch mode

| Mode | Order | Every handler runs? | Handler can change what the next one sees? | Result | Failures |
| --- | --- | --- | --- | --- | --- |
| `EmitAsync` | sequential, registration order | yes | no | none | the first exception stops the dispatch and propagates |
| `ParallelAsync` | concurrent | yes | no | none | collected into one `AggregateError` naming the event |
| `BailAsync` | sequential | until one returns non-null | no | the first non-null result, or `null` | the first exception propagates |
| `WaterfallAsync` | sequential, nested | only if each calls `next` | yes, in both directions | what the outermost handler returns | an exception unwinds through the outer handlers |

**Emit vs waterfall.** Both are sequential, but emit is a *loop* and waterfall is a *nest*.

- `EmitAsync` calls handler 1, waits, calls handler 2, waits, and so on. Each handler gets the same
  arguments and returns nothing; none of them can affect the others or stop the chain short of
  throwing. Use it to *notify*.
- `WaterfallAsync` calls only handler 1. Handler 1 calls `next(value)` to run handler 2 (with a
  value it may have changed), and gets back handler 2's result (which it may change again before
  returning). Not calling `next` stops the chain there and the terminal never runs. `next` may be
  called at most once. Use it for *middleware*: transform, wrap, cache, or veto.

```csharp
// frame wraps shout, shout transforms the input, the terminal appends "!"
ctx.On(RenderReceipt, async (_, text, next) => $"[{await next(text)}]");          // registered first
ctx.On(RenderReceipt, (_, text, next) => next(text.ToUpperInvariant()));          // registered second
await root.WaterfallAsync(RenderReceipt, "thanks", t => new ValueTask<string>(t + "!"));  // "[THANKS!]"
```

Use `BailAsync` to *ask*: the first handler with an answer wins. Use `ParallelAsync` when handlers
are independent and slow.

Order is registration order; `prepend: true` puts a listener first. A listener disposed during a
dispatch (by an earlier listener, say) is skipped by that dispatch.

## Isolation and intercepts

Example: [`IsolateExample.cs`](../samples/Cordis.Examples/IsolateExample.cs).

**Isolate** a service name for a subtree: providers and consumers inside it see each other only.

```csharp
root.Provide("store", sharedStore);

var tenantA = root.Isolate("store");
tenantA.Provide("store", storeA);
tenantA.Plugin<Reporter>();          // sees storeA; everything else is still shared

var analytics = new IsolateKey("store");                     // a named realm
root.Isolate("store", analytics).Provide("store", storeC);
otherScope.Isolate("store", analytics).Plugin<Reporter>();   // same key, same realm
```

**Intercept** attaches config for a name to a subtree; the nearest layer wins.

```csharp
root.Intercept(LoggerService.ServiceName, new LoggerConfig { Level = LogLevel.Debug })
    .Plugin<Noisy>();                                        // debug logging for this subtree only

var level = ctx.GetInterceptConfig<LoggerConfig>("logger")?.Level;
```

A plugin's own `[Inject("name", Config = ...)]` value is the lowest layer, under every context
intercept, and is inherited by child plugins of the declaring fiber.

## Timers

`TimerService` hands each caller its own bound copy, so every timer belongs to the caller's fiber
and stops when that plugin unloads.

```csharp
await root.Plugin<TimerService>();

[Inject("timer")]
public sealed class Heartbeat(Context ctx)
{
    private readonly EffectHandle _beat = ctx.Get<TimerService>()
        .Interval(() => ctx.Logger.Info("beat"), TimeSpan.FromSeconds(1));
}
```

| Method | Behaviour |
| --- | --- |
| `Timeout(callback, delay)` | once, cancelled if the caller unloads first |
| `Interval(callback, period)` | repeats until the handle or the caller is disposed |
| `Delay(delay)` | awaitable; faults with `InactiveFiberException` if the caller unloads |
| `Debounce<T>(callback, delay)` | trailing edge; inert after unload |
| `Throttle(callback, period)` | leading edge; inert after unload |

## Declarative trees: the loader

`LoaderService` turns an entry list into a plugin tree and reconciles it on every change;
`JsonConfigFile` feeds it from a watched file and writes committed configs back. Examples:
[`LoaderExample.cs`](../samples/Cordis.Examples/LoaderExample.cs), section 4 of
[`Cordis.Sample`](../samples/Cordis.Sample/Program.cs).

```json
[
  { "id": "timer", "name": "TimerService" },
  { "id": "orders", "name": "group", "group": true, "isolate": { "store": "orders" },
    "children": [
      { "id": "store",  "name": "Store" },
      { "id": "intake", "name": "Worker", "config": { "queue": "intake" } }
    ] },
  { "id": "audit", "name": "Worker", "config": { "queue": "audit" }, "disabled": true }
]
```

```csharp
await root.Plugin<LoaderService>();
root.Get<LoaderService>().Resolver = new TypePluginResolver(typeof(Worker).Assembly);
await root.Plugin<JsonConfigFile>(new JsonConfigFile.Options { Path = "cordis.json" });
```

| Field | Meaning |
| --- | --- |
| `id` | unique among siblings; nested entries are addressed as `parent:child`; must not contain `:` |
| `name` | plugin type, resolved by `IPluginResolver` (`TypePluginResolver`: full or short type name) |
| `config` | JSON bound to the plugin's config type (from `Schema` or the constructor) |
| `group` | a context node with no plugin of its own; `children` are created under it |
| `disabled` | keep the entry but do not run it |
| `inject` | extra dependencies |
| `isolate` | service name to realm label: equal labels share a realm, `true` makes a private one; any string, `""` included, is a label |

Reconciliation per entry: new entries are created, removed ones disposed (children first), a
config-only change becomes `UpdateAsync(config, noSave: true)`, and any other change recreates the
entry. At runtime, `loader.ApplyAsync(entries)`, `loader.SetDisabledAsync(id, bool)`,
`loader.Find(id)` and `loader.Snapshot()` work without a file. `JsonConfigFile.Options` has `Path`,
`Watch` (default on), `DebounceMs` and `Initial` (written when the file does not exist).

## Swapping implementations

`Registry.Replace(old, new)` moves every fiber of `old` to `new` in place: same position, config
and identity; `new`'s dependencies; a reload. Afterwards `ctx.Plugin(old)` creates a fiber of
`new`. Plugins are a `Type` or an `IPlugin` instance. Example:
[`ReplaceExample.cs`](../samples/Cordis.Examples/ReplaceExample.cs).

```csharp
var greeter = await root.Plugin<GreeterV1>(options);
root.Registry.Replace(typeof(GreeterV1), typeof(GreeterV2));   // reloads as V2
root.Registry.Replace(typeof(GreeterV2), typeof(GreeterV1));   // and back
```

Use it for hot reload hosts, feature flags that switch an implementation, or blue/green swaps.
`Registry.RemoveAsync(plugin)` unloads every fiber of a plugin instead.

## Observing the lifecycle

```csharp
root.Internal.StatusChanged += (fiber, old) => Console.WriteLine($"{fiber.Name}: {old} -> {fiber.State}");
root.Internal.ServiceChanged += (name, value) => { /* a binding appeared, changed or went away */ };
root.Internal.PluginChanged += fiber => { /* created (Uid set) or disposed (Uid null) */ };
root.Internal.Committed += (fiber, config) => { /* an update to persist */ };
root.Internal.Replaced += fiber => { /* moved by Registry.Replace */ };
```

These are raised synchronously under the tree lock: keep handlers short, and unsubscribe inside an
effect if you subscribe from a plugin:

```csharp
ctx.Internal.StatusChanged += OnStatus;
ctx.Effect(() => () => ctx.Internal.StatusChanged -= OnStatus);
```

`fiber.State`, `fiber.Error`, `fiber.Config`, `fiber.Instance`, `fiber.Effects` and
`root.Registry.Runtimes` are available for inspection.

## Patterns

### Pattern: gate a plugin on an external signal

The lifecycle follows services only, so turn the signal into a service. Example:
[`GateExample.cs`](../samples/Cordis.Examples/GateExample.cs).

```csharp
root.Apply("network-gate", ctx =>
{
    EffectHandle? online = null;
    ctx.On(NetworkChanged, async (_, up) =>
    {
        if (up && online is null) online = ctx.Provide("network", new object());
        else if (!up && online is not null) { var leaving = online; online = null; await leaving.DisposeAsync(); }
    });
});

root.Inject(["network"], ctx => { /* runs while the network is up, cleaned up when it drops */ });
```

The same shape works for feature flags, leader election, maintenance windows, or a connection
that drops: whatever the signal, the gate provides while it holds.

### Pattern: availability that changes on its own

`CheckAvailable` is evaluated only when the service is notified, not polled. If availability
depends on something outside Cordis, watch that thing and push: call `ctx.Set(name, value)` (or
restart the service) when it changes, so dependents re-evaluate.

### Pattern: per-tenant subtrees

Give each tenant an isolated context for its stateful services and load the same plugins into
each; shared infrastructure (timers, logging) stays outside the isolation. With the loader, give
each tenant group its own `isolate` label.

### Pattern: config hot reload without restarts

Persist config in a `JsonConfigFile`, and let plugins that can adopt changes cheaply implement
`ctx.OnConfigUpdate`; everything else restarts on change.

## Pitfalls

- **Undeclared access throws.** Declare every service a plugin reads, including in child plugins.
- **`await fiber` on a plugin waiting for dependencies returns `Pending`,** it does not wait for
  the dependencies. Await the provider, or poll `State` in tests.
- **Per-caller cloning is shallow.** Only enable `ScopePerCaller` on stateless facades.
- **Internal event handlers run under the lock.** Do not block in them or wait on other fibers.
- **Plugin bodies run later.** `ctx.Plugin(...)` returns before the body runs; read results after
  `await`.
- **Independent fibers reload concurrently.** Do not rely on the order in which sibling plugins
  load after a shared change.

## Testing

- Build a fresh `new Context()` per test; dispose it at the end.
- Silence or capture logs: `root.Loggers.ClearExporters(); root.Loggers.AddExporter(new MemoryExporter())`.
- `await fiber` and `fiber.WaitAsync()` wait for the current transition; for state reached through
  another fiber, wait for a condition (the test harness has `Assert.Eventually`).
- Gate async loads with a `TaskCompletionSource` inside `ctx.EffectAsync` to test what happens
  mid-load.
- [`tests/Cordis.Tests`](../tests/Cordis.Tests) has worked examples of every lifecycle edge case.
