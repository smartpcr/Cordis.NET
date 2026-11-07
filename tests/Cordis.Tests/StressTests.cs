using Cordis.Loading;

namespace Cordis.Tests;

/// <summary>
/// Concurrency stress. JavaScript gives upstream cordis run-to-completion for free; these cases
/// hammer the same paths from many threads to check that the tree lock and the fiber transitions
/// keep every acquisition paired with exactly one release.
/// </summary>
public static class StressTests
{
    private const int Threads = 8;

    public static void Register()
    {
        Harness.Case("stress: effects created while a fiber is disposed are all released", async () =>
        {
            var leaked = 0;
            for (var round = 0; round < 200; round++)
            {
                var root = new Context();
                var balance = 0;
                var ready = new TaskCompletionSource<Context>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fiber = root.Apply("busy", ready.SetResult);
                var ctx = await ready.Task;

                using var start = new Barrier(Threads + 1);
                var hammers = Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    while (true)
                    {
                        try
                        {
                            ctx.Effect(() =>
                            {
                                Interlocked.Increment(ref balance);
                                return () => Interlocked.Decrement(ref balance);
                            });
                        }
                        catch (InactiveFiberException)
                        {
                            return;
                        }
                    }
                })).ToArray();

                start.SignalAndWait();
                await Task.Yield();
                await fiber.DisposeAsync();
                await Task.WhenAll(hammers);
                leaked += Volatile.Read(ref balance);
            }
            Assert.Equal(0, leaked, $"{leaked} effects were acquired but never released");
        });

        Harness.Case("stress: effects created by a callback while a fiber is pending are released on dispose", async () =>
        {
            var root = new Context();
            var balance = 0;
            var provide = root.Provide("dep", 1);
            Context? captured = null;
            var fiber = await root.Inject(["dep"], ctx => captured = ctx);

            await provide.DisposeAsync();          // the fiber unloads to Pending
            await fiber;
            Assert.Equal(FiberState.Pending, fiber.State);

            // a callback that outlived the load (a timer, a socket handler) still holds the context
            for (var i = 0; i < 10; i++)
            {
                captured!.Effect(() =>
                {
                    Interlocked.Increment(ref balance);
                    return () => Interlocked.Decrement(ref balance);
                });
            }

            await fiber.DisposeAsync();
            Assert.Equal(0, balance, $"{balance} effects outlived the disposed fiber");
            Assert.Equal(FiberState.Disposed, fiber.State);
        });

        Harness.Case("stress: the root context is inert after disposal and leaks nothing", async () =>
        {
            for (var round = 0; round < 100; round++)
            {
                var root = new Context();
                var balance = 0;
                using var start = new Barrier(Threads + 1);
                var hammers = Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    for (var i = 0; i < 2_000; i++)
                    {
                        try
                        {
                            root.Effect(() =>
                            {
                                Interlocked.Increment(ref balance);
                                return () => Interlocked.Decrement(ref balance);
                            });
                        }
                        catch (InactiveFiberException)
                        {
                            return;
                        }
                    }
                })).ToArray();

                start.SignalAndWait();
                await root.DisposeAsync();
                await Task.WhenAll(hammers);
                Assert.Equal(0, Volatile.Read(ref balance), $"round {round}: {balance} root effects leaked");
            }
        });

        Harness.Case("stress: concurrent provide and withdraw keeps every dependent consistent", async () =>
        {
            var root = new Context();
            root.Loggers.ClearExporters();
            var loads = 0;
            var unloads = 0;
            var consumers = Enumerable.Range(0, 16).Select(i => root.Inject([$"svc{i % 4}"], ctx =>
            {
                Interlocked.Increment(ref loads);
                ctx.Effect(() => () => Interlocked.Increment(ref unloads));
            })).ToArray();

            using var start = new Barrier(Threads);
            var churn = Enumerable.Range(0, Threads).Select(t => Task.Run(async () =>
            {
                start.SignalAndWait();
                for (var i = 0; i < 200; i++)
                {
                    var name = $"svc{(t + i) % 4}";
                    EffectHandle handle;
                    try
                    {
                        handle = root.Provide(name, i);
                    }
                    catch (CordisException)
                    {
                        continue; // another thread holds this name right now
                    }
                    if (i % 3 == 0) await Task.Yield();
                    await handle.DisposeAsync();
                }
            })).ToArray();
            await Task.WhenAll(churn);

            foreach (var consumer in consumers) await consumer;
            Assert.True(consumers.All(c => c.State == FiberState.Pending),
                string.Join(',', consumers.Select(c => c.State)));
            Assert.Equal(loads, unloads, $"{loads} loads but {unloads} unloads");
        });

        Harness.Case("stress: concurrent listener churn and dispatch never fails", async () =>
        {
            var root = new Context();
            var key = new EventKey<int>("stress");
            var running = true;
            var delivered = 0;

            var churn = Enumerable.Range(0, Threads / 2).Select(_ => Task.Run(async () =>
            {
                while (Volatile.Read(ref running))
                {
                    var handle = root.On(key, (_, _) => { Interlocked.Increment(ref delivered); });
                    await handle.DisposeAsync();
                }
            })).ToArray();
            var emitters = Enumerable.Range(0, Threads / 2).Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 5_000; i++)
                {
                    await root.EmitAsync(key, i);
                    if (i % 2 == 0) await root.ParallelAsync(key, i);
                }
            })).ToArray();

            await Task.WhenAll(emitters);
            Volatile.Write(ref running, false);
            await Task.WhenAll(churn);
            Assert.True(delivered >= 0);
        });

        Harness.Case("stress: concurrent loader edits converge to the requested tree", async () =>
        {
            var root = new Context();
            root.Loggers.ClearExporters();
            var logs = new MemoryExporter();
            root.Loggers.AddExporter(logs);
            CountedPlugin.Reset();
            await root.Plugin<LoaderService>();
            var loader = root.Get<LoaderService>();
            List<EntryOptions> Tree() => Enumerable.Range(0, 8)
                .Select(i => new EntryOptions { Id = $"e{i}", Name = nameof(CountedPlugin) })
                .ToList();
            await loader.ApplyAsync(Tree());

            using var start = new Barrier(Threads);
            var editors = Enumerable.Range(0, Threads).Select(t => Task.Run(async () =>
            {
                start.SignalAndWait();
                var random = new Random(t);
                for (var i = 0; i < 25; i++)
                {
                    if (i % 5 == 0) await loader.ApplyAsync(Tree());
                    else await loader.SetDisabledAsync($"e{random.Next(8)}", random.Next(2) == 0);
                }
            })).ToArray();
            await Task.WhenAll(editors);

            await loader.ApplyAsync(Tree());
            await loader.WaitAllAsync();
            Assert.Equal(8, CountedPlugin.Live, $"{CountedPlugin.Live} live instances for 8 entries; logged: " +
                string.Join(", ", logs.Messages.Where(m => m.Error is not null).GroupBy(m => m.Error!.GetType().Name).Select(g => $"{g.Key} x{g.Count()}")));
            Assert.Equal(8, root.Registry.Get(typeof(CountedPlugin))!.FiberCount);

            await root.DisposeAsync();
            Assert.Equal(0, CountedPlugin.Live, $"{CountedPlugin.Live} instances survived disposal");
        });
    }
}

/// <summary>
/// Counts live loads across threads. The count is taken inside the effect: a body that runs while
/// its fiber is being disposed has the effect refused (<see cref="InactiveFiberException"/>), and a
/// side effect taken outside it would never be undone.
/// </summary>
public sealed class CountedPlugin
{
    private static int _live;

    public static int Live => Volatile.Read(ref _live);

    public static void Reset() => Volatile.Write(ref _live, 0);

    public CountedPlugin(Context ctx)
    {
        ctx.Effect(() =>
        {
            Interlocked.Increment(ref _live);
            return () => Interlocked.Decrement(ref _live);
        });
    }
}
