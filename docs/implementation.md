# Implementation notes (.NET specifics)

This document is for people changing the library. It explains how each upstream concept is
expressed in C#, why, and what to keep intact.

## From JavaScript to C#: the mapping

| Upstream construct (TypeScript) | C# construct | Where |
| --- | --- | --- |
| `Proxy` get-trap on `ctx` | explicit `ctx.Get<T>()` / `ServiceStore.Resolve` | `ServiceStore.cs` |
| `Object.create(parent)` chains for isolate and intercept tables | `ChainMap<T>`: lookups fall through to the parent, writes stay local | `Primitives.cs` |
| `Symbol(name)` realm keys | `IsolateKey`, compared by reference | `Primitives.cs` |
| traceable proxy (caller attribution) | `IContextScoped.ForContext`, `Service.ScopePerCaller` + `MemberwiseClone` | `Service.cs` |
| `ctx.effect(() => dispose)` | `Effect(Func<Action?>)` | `Fiber.cs` |
| `ctx.effect(async () => dispose)` | `EffectAsync(Func<ValueTask<Disposer?>>)` | `Fiber.cs` |
| `ctx.effect(function* () { yield ... })` | `EffectMany(Func<IAsyncEnumerable<Disposer>>)` | `Fiber.cs` |
| `Disposable` | `delegate ValueTask Disposer()` | `Effects.cs` |
| `static inject = [...]` | `[Inject("name")]`, `[Inject(typeof(T))]`, `IPlugin.Inject` | `Primitives.cs`, `Registry.cs` |
| `Service.init` | `IAsyncInit.InitAsync` | `Primitives.cs` |
| Standard Schema `Config` | `IConfigSchema` | `Config.cs` |
| `emit` / `parallel` / `bail` / `waterfall` | one key type per mode: `EventKey<T>`, `BailKey<T, R>`, `WaterfallKey<T>` | `Events.cs` |
| `internal/*` events | C# `event`s on `InternalEvents` | `Events.cs` |
| fiber-local `internal/update` | `ConfigUpdateHandler`, `Fiber.OnConfigUpdate` | `Fiber.cs` |
| `await ctx.plugin(...)` (thenable fiber) | `Fiber.GetAwaiter()` | `Fiber.cs` |

## Explicit resolution instead of proxies

Upstream uses `Proxy` for three jobs: enforcing declared dependencies, attributing effects to the
caller, and mixins. Reproducing it would mean a `DispatchProxy` per service access: reflection
heavy, interface only, and hostile to trimming and AOT.

- **Enforcement** is `ServiceStore.Resolve`. For a plugin context it checks, in order: the
  fiber's own provisions in the caller's realm; that the name is declared (else
  `ServiceNotInjectedException`); that the caller's context has not re-isolated the name below the
  fiber (else `ServiceUnavailableException`); then the binding the fiber committed at load. The
  root context resolves without checks.
- **Attribution** is `IContextScoped`. `Resolve` passes every value through `ForContext(ctx)`.
  `Service.ForContext` returns `this` unless `ScopePerCaller` is overridden to `true`, in which case
  it returns a `MemberwiseClone` with `Ctx` rebound. The clone is shallow, so only stateless
  facades should opt in; `TimerService` does, `LoaderService` does not.
- **Mixins** are dropped; extension methods on `Context` cover the same ground.

## The fiber state machine

State lives in a handful of fields on `Fiber`, all guarded by `Gate` (the tree lock):

| Field | Meaning |
| --- | --- |
| `_epoch` | the target: `"<revision>:<uid>:<uid>..."` or `InactiveEpoch` |
| `_candidates` | the binding currently seen for each injected name |
| `Committed` | the bindings the running load was started with; `null` while unloaded |
| `_inertia` | the running transition (`ReloadAsync` or `UnloadAsync`), or `null` |
| `_revision` | bumped by `RestartAsync`, `UpdateAsync` and `Replace`, so a restart always changes the epoch |
| `_error` | the load failure; while set, `SetEpoch` ignores changes |
| `Provisions` | bindings this fiber provides, including retired ones still waiting for dependents |

The algorithm is upstream's "inertia lock": `SetEpoch` records the new target and, only if no
transition is running, starts one. Every transition, when it finishes, compares the target with
what it achieved and starts the next transition if they differ. A burst of changes therefore costs
at most one extra transition, and the fiber always converges on the latest target.

Epochs use the **binding** uid (`ServiceImpl.Uid`), not the provider fiber's uid. A service
withdrawn and re-provided by the same fiber is a different binding, so a dependent that loaded
against the old one is reloaded instead of silently keeping a retired value.

## Threading

JavaScript is single threaded; .NET is not. The rules:

1. **One lock per tree.** `Context.SyncRoot` is created by the root and shared by every context,
   fiber, store, registry and event service of that tree. It is a plain `Monitor`, so it is
   reentrant: a notification that triggers a refresh that triggers a state change all run under
   one acquisition.
2. **Never await while holding it.** A transition is started inside `lock (Gate)` and its first
   statement is `await Task.Yield()`. The synchronous part of the async method ends at that yield,
   while the caller still holds the lock; everything after it runs on the thread pool, outside.
   Starting a transition without the yield runs plugin code under the lock, which deadlocks as
   soon as that code awaits something that needs the lock.
3. **Events raised under the lock are synchronous.** `StatusChanged`, `ServiceChanged`,
   `Committed` and `Replaced` are raised inside the lock. Handlers must be quick and must not block
   on other fibers.
4. **Signals use `TaskCompletionSource` with `RunContinuationsAsynchronously`.** A provider waiting
   for its dependents awaits a TCS that the last dependent completes; running continuations
   asynchronously keeps the completing thread (which holds the lock) from executing the
   provider's teardown inline.

## Effects

`EffectHandle` holds a list of `Disposer`s and an optional pending task.

- `Effect` runs the body synchronously and collects its cleanup.
- `EffectAsync` and `EffectMany` may leave a pending task. If the fiber is `Loading`, the task joins
  `_loadTasks`; the load waits for it and fails if it faults. Started later, a faulted task is only
  logged and its handle disposed.
- `EffectMany` checks after every yield whether the handle is still alive and the epoch unchanged.
  If not, it stops: the disposer just yielded is still collected, the enumerator is disposed (its
  `finally` blocks run), and the collected disposers run afterwards, newest first.
- Disposal sets `_alive = false`, awaits the pending task, then takes a snapshot of the disposers
  and runs them in reverse. A disposer collected while the drain waits joins the snapshot; one
  collected after it runs immediately.
- **Adoption.** Yielding `child.ToDisposer()` from a generator effect moves the child effect under
  the parent: `Collect` recognises a disposer whose target is an `EffectHandle` and whose method is
  `DisposeAsync`, and detaches the child from the fiber so it is disposed once.

## Plugin construction (`PluginFactory`)

Reflection based, resolved per plugin type:

- **Constructor:** `(Context, TConfig)` is preferred when a config is present, `(Context)` when it
  is not; either falls back to the other, then to a parameterless constructor. With no config and a
  two-parameter constructor, the parameter's default value, `default(T)`, or `new T()` is passed.
- **Schema:** a public static property or field named `Schema` of type `IConfigSchema`. For
  `IPlugin`, `ConfigSchema`.
- **Dependencies:** `[Inject]` attributes, inherited. `Config` on the attribute is the
  per-dependency intercept, limited to attribute constants.
- **Return value:** the constructed instance. If it is `IAsyncDisposable` or `IDisposable` it is
  disposed on unload; if it is `IAsyncInit`, `InitAsync` gates the load.
- `TargetInvocationException` is unwrapped with `ExceptionDispatchInfo`, so a plugin's own exception
  and stack surface unchanged.

Trimming and AOT: constructor and attribute discovery use reflection on the plugin types. An AOT
host must root those types (for example with `DynamicDependency` or a rooted assembly). Nothing in
the core needs runtime code generation.

## Services and the store

- `Service(ctx)` names the service after the concrete type (`ServiceNames.For(GetType())`), read in
  the constructor body because a constructor initializer cannot see `this`. `Service(ctx, name)`
  and `Service<TSelf>` name it explicitly.

- `ServiceStore._store` maps an `IsolateKey` to the one live binding in that realm. Providing into a
  realm that already has a binding throws.
- `Context.KeyFor(name)` walks the context's isolate `ChainMap`; a name never isolated resolves to a
  key created lazily on the root, so every unisolated context shares it.
- A binding remembers its realm (`ServiceImpl.Isolate`). Notifications match dependents by that key,
  so a service provided into an isolated child context notifies consumers in that realm, not in the
  providing fiber's own realm.
- `Notify` scans every fiber of every runtime. There is no reverse index from names to fibers;
  notifications are rare and fibers are few, and an index would have to follow fiber creation,
  disposal and `Replace`.

## Events

- `EventsService._hooks` is a `Dictionary<object, List<Hook>>` keyed by the key object (reference
  equality), so two keys with the same name are different events.
- Dispatch takes a snapshot of the list under the lock and invokes outside it. `Hook.Removed`
  (volatile) is set on disposal, and every dispatch mode skips removed hooks, so a listener
  removed mid-dispatch does not run.
- Waterfall `next` is guarded with `Interlocked.Exchange`; a second call throws.
- `ParallelAsync` collects failures into `AggregateError` with the event's name in the message; each
  member is the handler's original exception.

## Config schemas

`IConfigSchema.Validate` is synchronous by design (as upstream): validation runs on the load path,
and an async validator would let a slow check hold a transition.

- `DataAnnotationsSchema<T>`: `null` becomes `new T()`; a `T` is validated with
  `Validator.TryValidateObject(validateAllProperties: true)`; anything else is rejected.
- `DelegateSchema<T>`: a function, plus an optional `Simplify` used by the loader when it writes a
  committed config back.

## Logger

`LoggerService` is not a store service; every context reaches it as `ctx.Logger`. The level for a
context is the first `LoggerConfig` in `ctx.InterceptChain<LoggerConfig>("logger")`, falling back to
`LoggerService.Level`. Exporters are plain `ILogExporter`s and are isolated from each other: an
exporter that throws is ignored.

## Loader

- `EntryOptions` is a POCO bound with `System.Text.Json` (`LoaderService.JsonOptions`: web defaults,
  case-insensitive, nulls omitted). `Isolate` has its own converter so `true` reads and writes as a
  JSON boolean.
- Entry ids are paths joined with `:`; an id containing `:` is rejected.
- Realm labels map to `IsolateKey`s held by the loader. Private realms and shared labels use separate
  key spaces.
- `JsonConfigFile` debounces `FileSystemWatcher` events, ignores a reread of the text it wrote last,
  and treats only `FileNotFoundException` / `DirectoryNotFoundException` as "missing".

## Known limitations

- A fiber's effects are disposed sequentially, so one slow async cleanup delays the cleanups
  registered before it.
- `Notify` is linear in the number of fibers.
- `[Inject]` per-dependency config is limited to attribute constants; use
  `ctx.Intercept(name, config)` for objects.

## Working on the code

- Build and test: `dotnet build Cordis.sln`, `dotnet run --project tests/Cordis.Tests`.
- Tests are cases registered with `Harness.Case(name, body)`; each runs with a 10 second timeout.
  Prefer `Assert.Eventually` to fixed sleeps.
- For a behaviour change ported from upstream, add a case that fails on the old code, and record the
  commit in [upstream-sync.md](upstream-sync.md).
