# Cordis.NET

> **Ported from [cordiverse/cordis](https://github.com/cordiverse/cordis).**
> This repository is a C# / .NET reimplementation of that TypeScript project; the design,
> the terminology (context, fiber, effect, isolate, intercept), and the semantics are theirs.
> See [Concept mapping](#concept-mapping) for how each upstream construct is expressed here,
> and [Not ported](#not-ported) for what was deliberately left out.

Targets .NET 10. A dynamic DI container where **service availability is a value that
changes over time**. Plugins declare what they need, load themselves when it appears, and
unload themselves when it goes away.

```
src/Cordis           core: Context, Fiber, effects, services, registry, events, logger
src/Cordis.Timer     TimerService - timers owned by the calling fiber
src/Cordis.Loader    LoaderService + JsonConfigFile - declarative, hot-reloading entry tree
tests/Cordis.Tests   39 behavioural tests (no external test framework)
samples/Cordis.Sample runnable end-to-end demo
samples/Cordis.Examples one runnable example per feature
docs/                design, architecture, implementation notes, usage guide
```

```bash
dotnet run --project tests/Cordis.Tests    # 39 tests, custom runner
dotnet run --project samples/Cordis.Sample
dotnet run --project samples/Cordis.Examples -- events   # or effects, services, isolate, gate, config, replace, loader
```

Documentation: [usage guide](docs/usage.md), [design](docs/design.md),
[architecture](docs/architecture.md), [implementation notes](docs/implementation.md).

No NuGet dependencies. `Directory.Build.props` targets `net10.0`; pass `-p:CordisTfm=net8.0`
to build with an older SDK.

## Concept mapping

| cordis (TypeScript) | Cordis.NET | Notes |
| --- | --- | --- |
| `Context` + `Proxy` get-trap | `Context` + `ServiceStore.Resolve` | explicit resolution; no proxies |
| `Object.create(parent)` chain | `ChainMap<T>` + parent links | lookups fall through, writes stay local |
| `Symbol(name)` isolate keys | `IsolateKey` (reference identity) | |
| `Fiber` + epoch + inertia | `Fiber`, same algorithm | epoch = concatenated provider uids |
| `ctx.effect(() => dispose)` | `ctx.Effect(Func<Action?>)` | |
| `ctx.effect(async () => …)` | `ctx.EffectAsync(Func<ValueTask<Disposer?>>)` | keeps the fiber `Loading` until it settles |
| `ctx.effect(function* () { yield … })` | `ctx.EffectMany(Func<IAsyncEnumerable<Disposer>>)` | pass an iterator local function |
| `Service.init` | `IAsyncInit.InitAsync` | gates dependents |
| `registry.replace(old, new)` | `Registry.Replace(old, new)` | in-place swap; `old` is forwarded to `new` afterwards |
| `ctx.on('internal/update', …)` | `ctx.OnConfigUpdate(handler)` | fiber-local; skip `next()` to apply a config in place |
| `internal/commit` | `Context.Internal.Committed` | source config of a saved update; the loader persists it |
| `Service.check` | `Service.CheckAvailable` | |
| traceable proxy (caller attribution) | `IContextScoped.ForContext` | opt-in via `ScopePerCaller` |
| Standard Schema `Config` | `IConfigSchema` (+ `DataAnnotationsSchema<T>`) | sync-only, same as cordis |
| `static inject = ['x']` | `[Inject("x")]` / `[Inject(typeof(T))]` | |
| `ctx.isolate(name)` | `ctx.Isolate(name)` | |
| `ctx.intercept(name, cfg)` | `ctx.Intercept(name, cfg)` | |
| `emit/parallel/serial/bail/waterfall` | `EmitAsync` / `ParallelAsync` / `BailAsync` / `WaterfallAsync` | one key type per mode, so handlers stay typed |
| `internal/*` events | `Context.Internal` (C# events) | synchronous |
| `@cordisjs/plugin-loader` + `-include` | `LoaderService` + `JsonConfigFile` | JSON instead of YAML |

## Usage

### Service

```csharp
[ServiceName("database")]
public sealed class DatabaseService(Context ctx) : Service(ctx, "database"), IAsyncInit
{
    public async ValueTask InitAsync() => await Connect();   // dependents wait for this
    public string? Read(string key) => …;
}
```

### Plugin with dependencies and validated config

```csharp
[Inject("database")]
[Inject("timer")]
public sealed class HeartbeatPlugin
{
    public sealed class Options
    {
        [Range(10, 60_000)] public int IntervalMs { get; set; } = 250;
    }

    public static readonly IConfigSchema Schema = DataAnnotationsSchema<Options>.Instance;

    public HeartbeatPlugin(Context ctx, Options config)
    {
        var db = ctx.Get<DatabaseService>();                 // throws unless declared above
        ctx.Get<TimerService>().Interval(
            () => ctx.Logger.Info(db.Read("greeting")),      // stops when *this* plugin unloads
            TimeSpan.FromMilliseconds(config.IntervalMs));

        ctx.Effect(() =>
        {
            var listener = Subscribe();
            return () => listener.Dispose();                 // released on unload
        }, "subscription");
    }
}
```

### Composition

```csharp
var root = new Context();
await root.Plugin<TimerService>();

var heartbeat = root.Plugin<HeartbeatPlugin>(new HeartbeatPlugin.Options { IntervalMs = 200 });
Console.WriteLine(heartbeat.State);            // Pending - no database yet

await root.Plugin<DatabaseService>();
await heartbeat;                               // Active, started on its own

await heartbeat.UpdateAsync(new HeartbeatPlugin.Options { IntervalMs = 100 }); // unload + reload
root.Registry.Replace(typeof(HeartbeatPlugin), typeof(HeartbeatV2));          // same fiber, new code
await root.Registry.RemoveAsync(typeof(DatabaseService));                      // dependents unload first

root.Isolate("database").Plugin<DatabaseService>();          // second, private instance
root.Intercept("logger", new LoggerConfig { Level = LogLevel.Debug }).Plugin<Noisy>();
await root.DisposeAsync();                                   // whole tree, leaves first
```

### Inline bodies and events

```csharp
static readonly EventKey<int> Tick = new("tick");

var fiber = root.Inject(["database"], ctx =>
{
    ctx.On(Tick, (c, n) => Console.WriteLine(n));   // unregisters itself on unload
});

await root.EmitAsync(Tick, 1);
```

### Declarative tree

`cordis.json`:

```json
[
  { "id": "timer", "name": "TimerService" },
  {
    "id": "prod", "name": "group", "group": true,
    "children": [
      { "id": "db",   "name": "DatabaseService", "isolate": { "database": "true" } },
      { "id": "beat", "name": "HeartbeatPlugin", "config": { "intervalMs": 150 } }
    ]
  }
]
```

```csharp
await root.Plugin<LoaderService>();
await root.Plugin<JsonConfigFile>(new JsonConfigFile.Options { Path = "cordis.json" });
```

The file is watched; edits reconcile the live tree (create / update / dispose per entry) and
`fiber.UpdateAsync(...)` writes config back to disk.

`isolate` maps a service name to a realm label: entries with the same label share a realm, and
`true` gives the entry a realm of its own. Any string, the empty string included, is a label.
Entry ids must not contain `:`, which separates nested ids (`group:child`).

## Design notes

**Why explicit resolution instead of `DispatchProxy`.** cordis leans on `Proxy` for three jobs:
dependency enforcement, caller attribution, and mixins. A faithful reproduction would need a
`DispatchProxy` per service access - reflection-heavy, interface-only, and AOT-hostile.
`ctx.Get<T>()` does the enforcement, `IContextScoped` does the attribution, and mixins are
dropped (extension methods cover that ground in C#).

**Per-caller cloning is opt-in.** `Service.ForContext` returns `this` unless the service sets
`ScopePerCaller`. The clone is shallow, so a service that mutates state after cloning - an event
subscription, a cache - would silently diverge per caller. In JavaScript the same hazard exists
(own-property writes shadow the prototype); making it opt-in turns a silent bug into a decision.
`TimerService` opts in; `LoaderService` does not.

**Threading.** JavaScript is single-threaded; .NET is not. All fiber state transitions are
serialised on one lock per context tree, and no `await` ever runs while it is held - every
transition starts with `await Task.Yield()` so its synchronous prefix ends inside the caller's
lock. (Getting this wrong is a real deadlock; there is a regression test for it.)

**Strict injection.** A plugin context reaches only the services it provides itself and the ones
it declares in `Inject`; it does not inherit its parent fiber's injections, and a provider on the
root context is not reachable undeclared. Upstream cordis is looser on both counts. The stricter
rule is kept on purpose: in C# the declaration is the only place a dependency is visible.

**Teardown order.** A provider's effects are disposed only after every fiber that loaded against
its services has finished unloading, so a dependent can still use a service in its own cleanup.
Within one fiber, effects are disposed one at a time in reverse order (upstream starts them
concurrently).

**Config is resolved per load.** `Fiber.Config` holds the source config; it is validated against
the plugin's schema on every load, so a reload after `Registry.Replace` uses the new schema. While a
fiber is loaded, `UpdateAsync` validates first and throws on a bad config without touching the
running plugin. A failed fiber stays failed through dependency changes and restarts until
`UpdateAsync` recovers it.

**Async effects during load.** An effect started by the plugin body keeps the fiber `Loading`
until it settles, and its failure fails the load. Effects started later - from a timer callback,
say - cannot retroactively fail the fiber; they just log and dispose themselves.

## Not ported

- **HMR.** The .NET analogue is `AssemblyLoadContext` with collectible assemblies plus a
  `FileSystemWatcher` on build output. It fits behind `IPluginResolver`, but unload semantics
  differ enough from Node's module registry that a straight port would be misleading.
  `Registry.Replace`, the in-place swap upstream's HMR reconciles with, is ported.
- **YAML config** (`js-yaml` + the `!!js` expression tag) - JSON only, no extra dependency.
- **`ctx.mixin` / accessors** - prototype ergonomics with no C# analogue worth the machinery.
- **Long stack-trace stitching** (`composeError`). .NET's `ExceptionDispatchInfo` preserves async
  stacks well enough that the JS trick isn't needed.
- **`[Inject]` per-dependency config** is limited to attribute constants; use
  `ctx.Intercept(name, config).Plugin<T>()` for rich objects.

## Upstream

- Original project: <https://github.com/cordiverse/cordis>
- Synced to upstream `a6a38a0` (2026-11-06); see [docs/upstream-sync.md](docs/upstream-sync.md) for
  what was ported and what does not apply
- This port: <https://github.com/smartpcr/Cordis.NET>

Not affiliated with or endorsed by the cordis maintainers. Refer to the upstream repository
for its own license and terms before redistributing anything derived from it.
