# Upstream sync record

Cordis.NET was ported from cordiverse/cordis at `8cc9e33` (2026-08-13), the commit the
[smartpcr/cordis](https://github.com/smartpcr/cordis) fork still points at. This record covers the
85 upstream commits from there to `a6a38a0` (2026-11-06). The next sync starts after `a6a38a0`:

```bash
git log --reverse a6a38a0..origin/main -- packages/core packages/timer packages/loader packages/include
```

## Ported

| Upstream | Change | Where |
| --- | --- | --- |
| `2bca227` #242 | A listener removed during a dispatch is skipped by that dispatch | `Events.cs` (`Hook.Removed`) |
| `5b195b3` #44 | Waterfall `next()` may be called only once | `EventsService.WaterfallAsync` |
| `1a87f46` #205 | Parallel `AggregateError` names the event; members are the original errors | `EventsService.ParallelAsync` |
| `10194de` #98 | A failed fiber does not re-enter its lifecycle on a dependency refresh; only `UpdateAsync` recovers it | `Fiber.SetEpoch` |
| `1431598` #134 | Config is resolved on every load; `Fiber.Config` is the source config | `Fiber.ExecuteAsync` |
| `c261dd2` #189 | Restarts bump a revision in the epoch, so a restart during a transition is not lost | `Fiber.Refresh`, `RestartAsync` |
| `9cad5e8` #196 | A provider's effects are disposed only after every dependent has unloaded | `Fiber.UnloadAsync`, `ServiceStore.Provide` |
| `147e4f2` #191, `e708b90` #277 | Bindings remember their realm; activation notifies only the bindings a fiber provides, each in its own realm | `ServiceImpl.Isolate`, `Fiber.UpdateState` |
| `5dac695` #133 | A disposed fiber settles to `Disposed` and raises `StatusChanged` | `Fiber` anchor disposer |
| `bd427d0` #268 | A generator effect is not advanced once its epoch changes; its last yield is collected, then it is closed | `Fiber.EffectMany`, `EffectHandle.Collect` |
| `7d0a90d` #56 | Fiber-local config-update hooks are effects and do not accumulate across reloads | `Context.OnConfigUpdate` |
| `1431598` #134 | `update()` split into commit (source config, unless `noSave`) and a vetoable update chain | `InternalEvents.Committed`, `ConfigUpdateHandler` |
| `7e9eb09` #206 | An `[Inject(name, Config = ...)]` value sits under every context intercept, taken from the declaring fiber | `Context.InterceptChain` |
| `bc9a344` #209, `4cfd19a` | Lookup stops at an isolation boundary; own provisions are kept apart from the injected view and need no declaration | `ServiceStore.Resolve` |
| `74b7905` #266 | `registry.replace()`: in-place implementation swap, with forwarding | `RegistryService.Replace` |
| `9cad5e8` #196 | Epoch uses the binding's uid, so a new binding from the same fiber still reloads dependents | `ServiceImpl.Uid` |
| `b109a1a` #267 | An empty-string isolate label is a label, not a private realm; JSON `true` means private | `LoaderService.RealmKey`, `IsolateMapConverter` |
| `518b8bc` #172 | Entry ids containing the tree separator are rejected | `LoaderService.Flatten` |
| `7580bfc` #171 | Only a missing file falls back to the initial entries | `JsonConfigFile.ReadIfExistsAsync` |

Each row has a case in `tests/Cordis.Tests/UpstreamSyncTests.cs`. Run against the pre-sync code,
18 of the 19 cases that compile there fail; the exception is `once runs once across concurrent
dispatches`, which is a hardening guard.

## Already equivalent

| Upstream | Why nothing changed |
| --- | --- |
| `0e0cc2c` #216 | `LoaderService.Flatten` already assigns an anonymous id before walking the children |
| `10e1b1f` #161 | `TimerService.Throttle` already ignores calls after disposal |
| `bbc1103` #200 | `JsonConfigFile` resolves `Path` with `Path.GetFullPath`, so absolute paths already work |
| `a6a38a0` #296 | `DisposableList.unshift()`; prepending is a `List.Insert(0, ...)` here |

## Not applicable

- **JavaScript or TypeScript only:** symbol and prototype-named events (#51), the dispatch mode
  reported to `internal/dispatch` (#136, no such event here), deferred access-error stacks (#280),
  `ctx[Context.root]` (#207), unhandled promise rejections (#109), import extensions (#125),
  `exactOptionalPropertyTypes` (#68), void-returning scheduled functions (#163), logging an empty
  `AggregateError` (#222, the C# logger does not flatten aggregates), removal of `@cordisjs/utils` (`5b6c4c8`).
- **Timer:** concurrent reads of the async-iterator form of `interval` (#53); the C# timer has no
  async-iterator form.
- **Loader module resolution:** drive-letter import paths (#212), the resolve helper under a
  blocked rename (#188), removing `cordis:` builtin specifiers (#241), bare specifiers (#123),
  `_initTask` (#166), runtime-shape detection of the internal API (`2df12b5`). The C# loader
  resolves types through `IPluginResolver`.
- **Loader and include features with no C# counterpart:** `!!js` evaluation and injection scopes
  (#179, #245, #246), YAML source locations for validation issues (#210, #239, #247), patch
  provenance (#238), the file/runtime edit journal (#121), cross-tree entry moves (#271, ids are
  path-based here), awaiting nested group reconciliation on a config update (`1b7d0f2`, groups
  carry no config here).
- **Packages that are not ported:** `hmr` (#103, #111, #128, #157, #170, #190, #197, #208, #244,
  #249, #263, #264, #269, #282, `303cfd2`), `fs` and `fs-node` (#265, #285, #290, #293),
  `package-manager` (#283), `create` (#150, #151, #173, #199).
- **Chores, CI, docs and version bumps:** `b3df558`, `ed8a775`, `b912d39`, `f8ea3cd`, `0b96d9f`,
  `01b3962`, `efa855f`, `4c8e6be`, `e09e752`, `6350fb5`, `f009117`.

## Deliberate differences kept

- Plugins must declare every service they read; upstream also lets a child fiber use its parent's
  injections and lets any plugin read services provided on the root context.
- Effects of one fiber are disposed one at a time in reverse order; upstream starts them
  concurrently.
- Upstream's `internal/config` waterfall (a hook to rewrite config resolution) is not exposed;
  resolution is always the plugin's schema.
