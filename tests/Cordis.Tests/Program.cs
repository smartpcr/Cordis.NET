using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cordis;
using Cordis.Loading;
using Cordis.Tests;
using Cordis.Timers;

// ------------------------------------------------------------------- tests

Harness.Case("effects dispose in reverse order", async () =>
{
    var root = new Context();
    var log = new List<string>();

    var fiber = root.Apply("p", ctx =>
    {
        ctx.Effect(() => { log.Add("a"); return () => log.Add("~a"); });
        ctx.Effect(() => { log.Add("b"); return () => log.Add("~b"); });
    });

    await fiber;
    Assert.Equal("a,b", string.Join(',', log));

    await fiber.DisposeAsync();
    Assert.Equal("a,b,~b,~a", string.Join(',', log));
});

Harness.Case("generator effects nest and unwind together", async () =>
{
    var root = new Context();
    var log = new List<string>();

    var fiber = root.Apply("p", ctx =>
    {
        ctx.EffectMany(Setup, "resources");

        async IAsyncEnumerable<Disposer> Setup()
        {
            await Task.Yield();
            log.Add("open:1");
            yield return () => { log.Add("close:1"); return default; };
            log.Add("open:2");
            yield return ctx.Effect(() => { log.Add("open:3"); return () => log.Add("close:3"); }).ToDisposer();
        }
    });

    await fiber;
    Assert.Equal("open:1,open:2,open:3", string.Join(',', log));

    await fiber.DisposeAsync();
    Assert.Equal("open:1,open:2,open:3,close:3,close:1", string.Join(',', log));
});

Harness.Case("undeclared service access is rejected", async () =>
{
    var root = new Context();
    root.Provide("secret", 42);

    Exception? captured = null;
    await root.Apply("p", ctx =>
    {
        try { ctx.Get("secret"); }
        catch (Exception error) { captured = error; }
    });

    Assert.True(captured is ServiceNotInjectedException, $"got {captured?.GetType().Name ?? "nothing"}");
    Assert.Equal(42, root.Get("secret"));
});

Harness.Case("dependents load and unload with their provider", async () =>
{
    var root = new Context();
    var loads = 0;
    var unloads = 0;

    var consumer = root.Inject(["counter"], ctx =>
    {
        loads++;
        ctx.Effect(() => () => unloads++);
    });

    await consumer;
    Assert.Equal(FiberState.Pending, consumer.State);
    Assert.Equal(0, loads);

    var provider = await root.Plugin<CounterService>();
    await consumer;
    Assert.Equal(FiberState.Active, consumer.State);
    Assert.Equal(1, loads);

    await provider.DisposeAsync();
    await consumer;
    Assert.Equal(FiberState.Pending, consumer.State);
    Assert.Equal(1, unloads);

    await root.Plugin<CounterService>();
    await consumer;
    Assert.Equal(2, loads);
});

Harness.Case("provider identity change reloads dependents", async () =>
{
    var root = new Context();
    var loads = 0;
    var handle = root.Provide("thing", 1);
    var consumer = root.Inject(["thing"], _ => loads++);
    await consumer;
    Assert.Equal(1, loads);

    await handle.DisposeAsync();
    root.Provide("thing", 2);
    await consumer;
    Assert.Equal(2, loads);
});

Harness.Case("IAsyncInit gates dependents", async () =>
{
    var root = new Context();
    var loaded = false;

    var provider = root.Plugin<GatedService>();
    var consumer = root.Inject(["gated"], _ => loaded = true);

    await consumer;
    Assert.True(!loaded, "dependent ran before init completed");
    Assert.Equal(FiberState.Loading, provider.State);

    GatedService.Ready.SetResult();
    await provider;
    await consumer;
    Assert.True(loaded, "dependent never ran");
});

Harness.Case("isolate creates a private realm", async () =>
{
    var root = new Context();
    root.Provide("db", "shared");

    var isolated = root.Isolate("db");
    isolated.Provide("db", "private");

    string? outer = null;
    string? inner = null;
    await root.Inject(["db"], ctx => outer = (string)ctx.Get("db")!);
    await isolated.Inject(["db"], ctx => inner = (string)ctx.Get("db")!);

    Assert.Equal("shared", outer);
    Assert.Equal("private", inner);
});

Harness.Case("intercept layers config down the chain", async () =>
{
    var root = new Context();
    var scoped = root.Intercept(LoggerService.ServiceName, new LoggerConfig { Level = LogLevel.Debug });
    var deeper = scoped.Intercept(LoggerService.ServiceName, new LoggerConfig { Level = LogLevel.Silent });

    Assert.True(root.GetInterceptConfig<LoggerConfig>(LoggerService.ServiceName) is null);
    Assert.Equal(LogLevel.Debug, scoped.GetInterceptConfig<LoggerConfig>(LoggerService.ServiceName)!.Level);
    Assert.Equal(LogLevel.Silent, deeper.GetInterceptConfig<LoggerConfig>(LoggerService.ServiceName)!.Level);
    Assert.Equal(2, deeper.InterceptChain<LoggerConfig>(LoggerService.ServiceName).Count());
    await Task.CompletedTask;
});

Harness.Case("listeners unregister when their fiber unloads", async () =>
{
    var root = new Context();
    var seen = 0;

    var fiber = root.Apply("p", ctx => ctx.On(TestEvents.Tick, (_, n) => { seen += n; }));
    await fiber;

    await root.EmitAsync(TestEvents.Tick, 2);
    Assert.Equal(2, seen);

    await fiber.DisposeAsync();
    await root.EmitAsync(TestEvents.Tick, 2);
    Assert.Equal(2, seen);
});

Harness.Case("bail returns the first non-null result", async () =>
{
    var root = new Context();
    await root.Apply("a", ctx => ctx.On(TestEvents.Resolve, (_, _) => new ValueTask<string?>((string?)null)));
    await root.Apply("b", ctx => ctx.On(TestEvents.Resolve, (_, input) => new ValueTask<string?>(input.ToUpperInvariant())));
    await root.Apply("c", ctx => ctx.On(TestEvents.Resolve, (_, _) => new ValueTask<string?>("never")));

    Assert.Equal("HI", await root.BailAsync(TestEvents.Resolve, "hi"));
});

Harness.Case("waterfall composes middleware around a terminal", async () =>
{
    var root = new Context();
    await root.Apply("outer", ctx => ctx.On(TestEvents.Transform, async (_, value, next) => "[" + await next(value) + "]"));
    await root.Apply("inner", ctx => ctx.On(TestEvents.Transform, async (_, value, next) => await next(value + "!")));

    var result = await root.WaterfallAsync(TestEvents.Transform, "x", value => new ValueTask<string>(value.ToUpperInvariant()));
    Assert.Equal("[X!]", result);
});

Harness.Case("config is validated before the plugin body runs", async () =>
{
    var root = new Context();
    EchoPlugin.Log.Clear();

    var bad = root.Plugin<EchoPlugin>(new EchoPlugin.Options { Times = 99, Text = "no" });
    await Assert.ThrowsAsync<ConfigValidationException>(() => bad.WaitAsync());
    Assert.Equal(FiberState.Failed, bad.State);
    Assert.Equal(0, EchoPlugin.Log.Count);

    var good = await root.Plugin<EchoPlugin>(new EchoPlugin.Options { Times = 2, Text = "hi" });
    Assert.Equal("+hi hi", EchoPlugin.Log[0]);

    await good.UpdateAsync(new EchoPlugin.Options { Times = 1, Text = "bye" });
    Assert.Equal("-hi hi", EchoPlugin.Log[1]);
    Assert.Equal("+bye", EchoPlugin.Log[2]);
});

Harness.Case("timers are owned by the calling fiber", async () =>
{
    var root = new Context();
    await root.Plugin<TimerService>();

    var ticks = 0;
    var fiber = await root.Inject(["timer"], ctx =>
        ctx.Get<TimerService>().Interval(() => Interlocked.Increment(ref ticks), TimeSpan.FromMilliseconds(10)));

    await Assert.Eventually(() => Volatile.Read(ref ticks) > 2, "interval never fired");
    await fiber.DisposeAsync();

    var settled = Volatile.Read(ref ticks);
    await Task.Delay(60);
    Assert.Equal(settled, Volatile.Read(ref ticks), "timer kept firing after its owner unloaded");
});

Harness.Case("nested plugin trees unload from the leaves up", async () =>
{
    var root = new Context();
    var log = new List<string>();

    var outer = root.Apply("outer", ctx =>
    {
        ctx.Effect(() => () => log.Add("~outer"));
        ctx.Apply("inner", inner =>
        {
            inner.Effect(() => () => log.Add("~inner"));
        });
    });

    await outer;
    await Task.Delay(20); // the inner fiber loads on its own turn
    await outer.DisposeAsync();

    Assert.Equal("~inner,~outer", string.Join(',', log));
});

Harness.Case("loader materialises an entry tree", async () =>
{
    var root = new Context();
    EchoPlugin.Log.Clear();
    var loader = await root.Plugin<LoaderService>();
    var service = root.Get<LoaderService>();

    var entries = new List<EntryOptions>
    {
        new() { Id = "timer", Name = nameof(TimerService) },
        new()
        {
            Id = "group",
            Name = "group",
            Group = true,
            Children =
            [
                new EntryOptions
                {
                    Id = "echo",
                    Name = nameof(EchoPlugin),
                    Config = JsonSerializer.SerializeToElement(new { times = 1, text = "loaded" }),
                },
            ],
        },
    };

    await service.ApplyAsync(entries);
    Assert.True(service.Find("timer")!.IsActive, "timer entry inactive");
    Assert.True(service.Find("group:echo")!.IsActive, "nested entry inactive");
    Assert.Equal("+loaded", EchoPlugin.Log[0]);

    await service.SetDisabledAsync("group:echo", true);
    Assert.Equal("-loaded", EchoPlugin.Log[1]);
    Assert.True(service.Find("group:echo")!.Fiber is null, "disabled entry still has a fiber");

    await service.SetDisabledAsync("group:echo", false);
    Assert.Equal("+loaded", EchoPlugin.Log[2]);

    // removing the group takes its children with it
    await service.ApplyAsync([entries[0]]);
    Assert.Equal("-loaded", EchoPlugin.Log[3]);
    Assert.True(service.Find("group") is null, "group entry survived removal");

    await loader.DisposeAsync();
});

Harness.Case("loader reads and rewrites a json file", async () =>
{
    var directory = Directory.CreateTempSubdirectory("cordis-loader");
    var path = Path.Combine(directory.FullName, "cordis.json");
    EchoPlugin.Log.Clear();

    await File.WriteAllTextAsync(path, """
    [
      { "id": "echo", "name": "EchoPlugin", "config": { "times": 1, "text": "file" } }
    ]
    """);

    var root = new Context();
    await root.Plugin<LoaderService>();
    await root.Plugin<JsonConfigFile>(new JsonConfigFile.Options { Path = path, Watch = false });

    var service = root.Get<LoaderService>();
    Assert.True(service.Find("echo")!.IsActive, "entry from file is not active");
    Assert.Equal("+file", EchoPlugin.Log[0]);

    await service.Find("echo")!.Fiber!.UpdateAsync(new EchoPlugin.Options { Times = 2, Text = "file" });
    await Assert.Eventually(() => File.ReadAllText(path).Contains("\"times\": 2"), "config was not written back");

    await root.DisposeAsync();
    directory.Delete(recursive: true);
});

Harness.Case("a service constructed without a name registers under its own type's name", async () =>
{
    var root = new Context();
    await root.Plugin<AttributeNamedService>();
    await root.Plugin<TypeNamedService>();

    Assert.Equal("attribute-named", root.Get<AttributeNamedService>().Name);
    Assert.Equal(typeof(TypeNamedService).FullName, root.Get<TypeNamedService>().Name);
    Assert.True(!root.Has("Cordis.Context"), "a service was registered under the context's type name");

    var consumer = await root.Plugin<NeedsUnnamedServices>();
    Assert.Equal(FiberState.Active, consumer.State);
    Assert.True(consumer.Instance is NeedsUnnamedServices { First: not null, Second: not null }, "dependencies did not resolve");
});

UpstreamSyncTests.Register();

return await Harness.RunAsync();
