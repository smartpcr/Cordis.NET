using System.Text.Json;
using Cordis.Loading;

namespace Cordis.Tests;

/// <summary>
/// Behaviour ported from cordiverse/cordis after the 8cc9e33 fork point. Each case names the
/// upstream change it mirrors.
/// </summary>
public static class UpstreamSyncTests
{
    public static void Register()
    {
        // ------------------------------------------------------------- events

        Harness.Case("a listener disposed earlier in the same dispatch is skipped (#242)", async () =>
        {
            var root = new Context();
            var log = new List<string>();
            EffectHandle? second = null;

            root.On(TestEvents.Tick, (_, _) =>
            {
                log.Add("first");
                _ = second!.DisposeAsync();
            });
            second = root.On(TestEvents.Tick, (_, _) => log.Add("second"));

            await root.EmitAsync(TestEvents.Tick, 1);
            await root.EmitAsync(TestEvents.Tick, 1);
            Assert.Equal("first,first", string.Join(',', log));
        });

        Harness.Case("waterfall rejects a second next() (#44)", async () =>
        {
            var root = new Context();
            root.On(TestEvents.Transform, async (_, value, next) =>
            {
                await next(value);
                return await next(value);
            });

            var error = await Assert.ThrowsAsync<CordisException>(async () =>
                await root.WaterfallAsync(TestEvents.Transform, "x", v => new ValueTask<string>(v)));
            Assert.True(error.Message.Contains("next()"), error.Message);
        });

        Harness.Case("waterfall continues past a listener removed mid-dispatch (#242)", async () =>
        {
            var root = new Context();
            EffectHandle? middle = null;
            root.On(TestEvents.Transform, async (_, value, next) =>
            {
                await middle!.DisposeAsync();
                return await next(value + "a");
            });
            middle = root.On(TestEvents.Transform, (_, value, next) => next(value + "b"));
            root.On(TestEvents.Transform, (_, value, next) => next(value + "c"));

            var result = await root.WaterfallAsync(TestEvents.Transform, "", v => new ValueTask<string>(v));
            Assert.Equal("ac", result);
        });

        Harness.Case("parallel failures name the event and keep the original errors (#205)", async () =>
        {
            var root = new Context();
            root.On(TestEvents.Tick, (Func<Context, int, ValueTask>)((_, _) => throw new InvalidOperationException("boom")));
            root.On(TestEvents.Tick, (_, _) => { });

            var error = await Assert.ThrowsAsync<AggregateError>(async () => await root.ParallelAsync(TestEvents.Tick, 1));
            Assert.True(error.Message.Contains("\"tick\""), error.Message);
            Assert.True(error.InnerExceptions.Single() is InvalidOperationException { Message: "boom" },
                error.InnerExceptions.Single().GetType().Name);
        });

        Harness.Case("once runs once across concurrent dispatches", async () =>
        {
            var root = new Context();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = 0;
            root.Once(TestEvents.Tick, async (_, _) =>
            {
                Interlocked.Increment(ref runs);
                await gate.Task;
            });

            var first = root.EmitAsync(TestEvents.Tick, 1).AsTask();
            var second = root.EmitAsync(TestEvents.Tick, 1).AsTask();
            gate.SetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(1, runs);
        });

        // ------------------------------------------------------------- fibers

        Harness.Case("a failed fiber does not re-enter its lifecycle on a dependency refresh (#98)", async () =>
        {
            var root = new Context();
            var loads = 0;
            var handle = root.Provide("dep", 1);
            var fiber = root.Inject(["dep"], _ =>
            {
                loads++;
                throw new InvalidOperationException("broken");
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fiber.WaitAsync());
            Assert.Equal(FiberState.Failed, fiber.State);

            await handle.DisposeAsync();
            root.Provide("dep", 2); // a new binding would previously reload the failed fiber
            await Task.Delay(30);
            Assert.Equal(1, loads);
            Assert.Equal(FiberState.Failed, fiber.State);

            await Assert.ThrowsAsync<InvalidOperationException>(() => fiber.UpdateAsync(null));
            Assert.Equal(2, loads, "UpdateAsync did not recover the failed fiber");
        });

        Harness.Case("config is resolved on every load (#134)", async () =>
        {
            var root = new Context();
            var validations = 0;
            var schema = new DelegateSchema<string>(input =>
            {
                validations++;
                return (string?)input ?? "default";
            });
            var seen = new List<string>();
            var fiber = await root.Plugin(new SchemaPlugin(schema, seen), "a");
            Assert.Equal(1, validations);

            await fiber.RestartAsync();
            Assert.Equal(2, validations);
            Assert.Equal("a,a", string.Join(',', seen));
            Assert.Equal("a", fiber.Config, "Config holds the source config");
        });

        Harness.Case("a restart issued while loading is not lost (#189)", async () =>
        {
            var root = new Context();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loads = 0;
            var fiber = root.Apply("slow", ctx =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                {
                    ctx.EffectAsync(async () => { await gate.Task; return null; });
                }
            });

            await Assert.Eventually(() => fiber.State == FiberState.Loading && Volatile.Read(ref loads) == 1);
            var restart = fiber.RestartAsync();
            gate.SetResult();
            await restart;
            Assert.Equal(2, loads);
            Assert.Equal(FiberState.Active, fiber.State);
        });

        Harness.Case("a provider disposes after its dependents (#196)", async () =>
        {
            var root = new Context();
            var log = new List<string>();

            var provider = await root.Apply("provider", ctx =>
            {
                ctx.Provide("svc", "value");
                ctx.Effect(() => () => { lock (log) log.Add("~provider"); });
            });
            var consumer = await root.Inject(["svc"], ctx =>
            {
                ctx.Effect(() => () => { lock (log) log.Add($"~consumer:{ctx.Get("svc")}"); });
            });

            await provider.DisposeAsync();
            Assert.Equal("~consumer:value,~provider", string.Join(',', log));
            Assert.Equal(FiberState.Pending, consumer.State);
        });

        Harness.Case("a dependent that is itself disposing still holds back its provider (#196)", async () =>
        {
            var root = new Context();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new List<string>();

            var provider = await root.Apply("provider", ctx =>
            {
                ctx.Provide("svc", "value");
                ctx.Effect(() => () => { lock (log) log.Add("~provider"); });
            });
            var consumer = await root.Inject(["svc"], ctx =>
            {
                ctx.Fiber.UseAsync(new AsyncCleanup(async () =>
                {
                    await gate.Task;
                    lock (log) log.Add($"~consumer:{ctx.Get("svc")}");
                }));
            });

            var consumerGone = consumer.DisposeAsync().AsTask(); // leaves the registry, still draining
            var providerGone = provider.DisposeAsync().AsTask();
            await Task.Delay(30);
            Assert.Equal(0, log.Count);
            gate.SetResult();
            await Task.WhenAll(consumerGone, providerGone);
            Assert.Equal("~consumer:value,~provider", string.Join(',', log));
        });

        Harness.Case("disposing a pending or failed fiber settles to Disposed and reports it (#133)", async () =>
        {
            var root = new Context();
            var reported = new List<string>();
            root.Internal.StatusChanged += (fiber, _) =>
            {
                if (fiber.State == FiberState.Disposed) lock (reported) reported.Add(fiber.Name);
            };

            var pending = root.Apply("pending", _ => { }, "missing");
            await pending;
            var failed = root.Apply("failed", _ => throw new InvalidOperationException());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync());

            await pending.DisposeAsync();
            await failed.DisposeAsync();
            Assert.Equal(FiberState.Disposed, pending.State);
            Assert.Equal(FiberState.Disposed, failed.State);
            Assert.Equal("pending,failed", string.Join(',', reported));
        });

        Harness.Case("a generator effect stops advancing once its epoch changes (#268)", async () =>
        {
            var root = new Context();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new List<string>();
            var loads = 0;

            var fiber = root.Apply("gen", ctx =>
            {
                var load = Interlocked.Increment(ref loads);
                ctx.EffectMany(Body);

                async IAsyncEnumerable<Disposer> Body()
                {
                    log.Add($"load{load}");
                    if (load > 1) yield break;
                    try
                    {
                        yield return () => { log.Add("close1"); return default; };
                        await gate.Task;
                        yield return () => { log.Add("close2"); return default; };
                        log.Add("outdated continuation");
                    }
                    finally
                    {
                        log.Add("finally");
                    }
                }
            });

            await Assert.Eventually(() => log.Count == 1);
            var restart = fiber.RestartAsync();
            gate.SetResult();
            await restart;
            Assert.Equal("load1,finally,close2,close1,load2", string.Join(',', log));
            Assert.Equal(FiberState.Active, fiber.State);
        });

        // ------------------------------------------------------------- updates

        Harness.Case("a config-update handler can apply the config in place (#56)", async () =>
        {
            var root = new Context();
            var loads = 0;
            var applied = new List<object?>();
            var fiber = await root.Plugin(new HookPlugin(ctx =>
            {
                loads++;
                ctx.OnConfigUpdate((config, _) => { applied.Add(config); return default; });
            }), "one");

            await fiber.UpdateAsync("two");
            Assert.Equal(1, loads, "the handler vetoed the restart");
            Assert.Equal("two", fiber.Config);

            // handlers are effects: a reload replaces them instead of adding to them
            await fiber.RestartAsync();
            await fiber.UpdateAsync("three");
            Assert.Equal(2, loads);
            Assert.Equal("two,three", string.Join(',', applied));
        });

        Harness.Case("UpdateAsync commits the source config unless noSave, and keeps a running plugin on a bad config", async () =>
        {
            var root = new Context();
            var committed = new List<string>();
            root.Internal.Committed += (_, config) => committed.Add(config is EchoPlugin.Options o ? $"{o.Times}" : "?");
            EchoPlugin.Log.Clear();

            var fiber = await root.Plugin<EchoPlugin>(new EchoPlugin.Options { Times = 1, Text = "x" });
            await Assert.ThrowsAsync<ConfigValidationException>(() =>
                fiber.UpdateAsync(new EchoPlugin.Options { Times = 99, Text = "x" }));
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.Equal(1, ((EchoPlugin.Options)fiber.Config!).Times);
            Assert.Equal(0, committed.Count, "an invalid config was committed");

            await fiber.UpdateAsync(new EchoPlugin.Options { Times = 2, Text = "x" });
            await fiber.UpdateAsync(new EchoPlugin.Options { Times = 3, Text = "x" }, noSave: true);
            Assert.Equal("2", string.Join(',', committed));
            Assert.Equal("+x x x", EchoPlugin.Log[^1]);
        });

        // ------------------------------------------------------------- services

        Harness.Case("injected config sits under context intercepts (#206)", async () =>
        {
            var root = new Context();
            root.Provide("opts", new object());

            var plain = await root.Plugin<OptsPlugin>();
            var intercepted = await root.Intercept("opts", "context").Plugin<OptsPlugin>();
            Assert.Equal("injected", ((OptsPlugin)plain.Instance!).Seen);
            Assert.Equal("context", ((OptsPlugin)intercepted.Instance!).Seen);
            await Assert.Eventually(() => ((OptsPlugin)plain.Instance!).SeenByChild is not null, "the child never loaded");
            Assert.Equal("injected", ((OptsPlugin)plain.Instance!).SeenByChild, "a child takes the declaring fiber's config");
        });

        Harness.Case("a plugin reaches its own provisions without declaring them", async () =>
        {
            var root = new Context();
            object? seen = null;
            await root.Apply("self", ctx =>
            {
                ctx.Provide("mine", 7);
                seen = ctx.Get("mine");
            });
            Assert.Equal(7, seen);
        });

        Harness.Case("service lookup stops at an isolation boundary (#209)", async () =>
        {
            var root = new Context();
            root.Provide("db", "outer");
            Exception? error = null;
            object? direct = null;
            await root.Inject(["db"], ctx =>
            {
                direct = ctx.Get("db");
                try { ctx.Isolate("db").Get("db"); }
                catch (Exception e) { error = e; }
            });
            Assert.Equal("outer", direct);
            Assert.True(error is ServiceUnavailableException, $"got {error?.GetType().Name ?? "the outer binding"}");
        });

        Harness.Case("a service provided into an isolate notifies dependents in that realm (#191, #277)", async () =>
        {
            var root = new Context();
            var realm = new IsolateKey("db");
            var log = new List<string>();

            var provider = root.Apply("provider", ctx =>
            {
                ctx.Isolate("db", realm).Provide("db", "private");
                ctx.Effect(() => () => { lock (log) log.Add("~provider"); });
            });
            var consumer = root.Isolate("db", realm).Inject(["db"], ctx =>
            {
                lock (log) log.Add($"+consumer:{ctx.Get("db")}");
                ctx.Effect(() => () => { lock (log) log.Add("~consumer"); });
            });

            await provider;
            await Assert.Eventually(() => consumer.State == FiberState.Active, "the consumer never saw the isolated provider");
            await provider.DisposeAsync();
            Assert.Equal("+consumer:private,~consumer,~provider", string.Join(',', log));
        });

        Harness.Case("a binding swapped while a dependent loads reloads it (per-binding uid in the epoch)", async () =>
        {
            var root = new Context();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new List<object?>();
            var first = root.Provide("thing", 1);

            var consumer = root.Inject(["thing"], ctx =>
            {
                lock (seen) seen.Add(ctx.Get("thing"));
                if (seen.Count == 1) ctx.EffectAsync(async () => { await gate.Task; return null; });
            });
            await Assert.Eventually(() => consumer.State == FiberState.Loading && seen.Count == 1);

            // same providing fiber (the root), new binding: the epoch must still change
            var retiring = first.DisposeAsync().AsTask();
            root.Provide("thing", 2);
            gate.SetResult();
            await retiring;
            await Assert.Eventually(() => seen.Count == 2, "the dependent kept the retired binding");
            Assert.Equal("1,2", string.Join(',', seen));
        });

        // ------------------------------------------------------------- registry

        Harness.Case("Registry.Replace swaps the implementation behind a fiber in place (#266)", async () =>
        {
            var root = new Context();
            var replaced = new List<Fiber>();
            root.Internal.Replaced += replaced.Add;

            var fiber = await root.Plugin<ReplaceV1>("cfg");
            var moved = root.Registry.Replace(typeof(ReplaceV1), typeof(ReplaceV2));
            await fiber;

            Assert.Equal(1, moved.Count);
            Assert.True(ReferenceEquals(moved[0], fiber) && ReferenceEquals(replaced.Single(), fiber), "a new fiber was created");
            Assert.True(fiber.Instance is ReplaceV2 { Config: "cfg" }, $"instance is {fiber.Instance?.GetType().Name}");
            Assert.True(ReferenceEquals(root.Registry.Get(typeof(ReplaceV1)), fiber.Runtime), "the old plugin does not forward");

            // the old plugin is forwarded to the new one
            var later = await root.Plugin<ReplaceV1>("later");
            Assert.True(later.Instance is ReplaceV2, "ctx.Plugin(old) was not forwarded");

            // switching back makes the old plugin an implementation of its own again
            root.Registry.Replace(typeof(ReplaceV2), typeof(ReplaceV1));
            await fiber;
            Assert.True(fiber.Instance is ReplaceV1, "could not switch back");
        });

        Harness.Case("Registry.Replace takes the dependencies of the new plugin (#266)", async () =>
        {
            var root = new Context();
            var fiber = await root.Plugin<ReplaceV1>("cfg");
            root.Registry.Replace(typeof(ReplaceV1), typeof(ReplaceNeedsCounter));
            await fiber;
            Assert.Equal(FiberState.Pending, fiber.State);

            await root.Plugin<CounterService>();
            await Assert.Eventually(() => fiber.State == FiberState.Active, "the replaced fiber never picked up its new dependency");
        });

        // ------------------------------------------------------------- loader

        Harness.Case("loader: an empty isolate label is a shared label, true is private (#267)", async () =>
        {
            var root = new Context();
            await root.Plugin<LoaderService>();
            var loader = root.Get<LoaderService>();

            var entries = JsonSerializer.Deserialize<List<EntryOptions>>("""
            [
              { "id": "a", "name": "group", "group": true, "isolate": { "db": "" } },
              { "id": "b", "name": "group", "group": true, "isolate": { "db": "" } },
              { "id": "c", "name": "group", "group": true, "isolate": { "db": true } },
              { "id": "d", "name": "group", "group": true, "isolate": { "db": true } }
            ]
            """, LoaderService.JsonOptions)!;
            await loader.ApplyAsync(entries);

            Context Scope(string id) => loader.Find(id)!.Scope!;
            Assert.True(Scope("a").Has("db") == Scope("b").Has("db"));
            Scope("a").Provide("db", "shared");
            Assert.True(Scope("b").Has("db"), "two entries with the label \"\" do not share a realm");
            Assert.True(!Scope("c").Has("db") && !root.Has("db"), "the empty label leaked out of its realm");
            Scope("c").Provide("db", "private");
            Assert.True(!Scope("d").Has("db"), "two private realms are shared");

            var written = JsonSerializer.Serialize(loader.Snapshot(), LoaderService.JsonOptions);
            Assert.True(written.Contains("\"db\": true") && written.Contains("\"db\": \"\""), written);
        });

        Harness.Case("loader: an entry id containing the separator is rejected (#172)", async () =>
        {
            var root = new Context();
            await root.Plugin<LoaderService>();
            var loader = root.Get<LoaderService>();
            var error = await Assert.ThrowsAsync<CordisException>(() =>
                loader.ApplyAsync([new EntryOptions { Id = "a:b", Name = "group", Group = true }]));
            Assert.True(error.Message.Contains("a:b"), error.Message);
        });
    }

    private sealed class AsyncCleanup(Func<Task> cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await cleanup();
    }
}

public sealed class SchemaPlugin(IConfigSchema schema, List<string> seen) : IPlugin<string>
{
    public IConfigSchema ConfigSchema => schema;

    public ValueTask ApplyAsync(Context ctx, string config)
    {
        seen.Add(config);
        return default;
    }
}

public sealed class HookPlugin(Action<Context> body) : IPlugin
{
    public ValueTask ApplyAsync(Context ctx)
    {
        body(ctx);
        return default;
    }
}

[Inject("opts", Config = "injected")]
public sealed class OptsPlugin
{
    public OptsPlugin(Context ctx)
    {
        Seen = ctx.GetInterceptConfig<string>("opts");
        ctx.Apply("child", child => SeenByChild = child.GetInterceptConfig<string>("opts"));
    }

    public string? Seen { get; }

    public string? SeenByChild { get; private set; }
}

public sealed class ReplaceV1(Context ctx, string config)
{
    public Context Ctx { get; } = ctx;
    public string Config { get; } = config;
}

public sealed class ReplaceV2(Context ctx, string config)
{
    public Context Ctx { get; } = ctx;
    public string Config { get; } = config;
}

[Inject("counter")]
public sealed class ReplaceNeedsCounter(Context ctx, string config)
{
    public Context Ctx { get; } = ctx;
    public string Config { get; } = config;
}
