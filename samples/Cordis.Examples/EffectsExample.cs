namespace Cordis.Examples;

/// <summary>
/// Everything a plugin acquires through <c>ctx</c> is owned by its fiber and released, in reverse
/// order, when the fiber unloads: sync effects, async effects, generator effects, disposables and
/// child plugins.
/// </summary>
public static class EffectsExample
{
    public static async Task RunAsync()
    {
        var root = new Context();

        var fiber = root.Apply("owner", ctx =>
        {
            // 1. sync: acquire, return the cleanup
            ctx.Effect(() =>
            {
                Console.WriteLine("  open  socket");
                return () => Console.WriteLine("  close socket");
            }, "socket");

            // 2. async: the fiber stays Loading until it completes
            ctx.EffectAsync(async () =>
            {
                await Task.Delay(20);
                Console.WriteLine("  open  connection (async)");
                return () => { Console.WriteLine("  close connection"); return default; };
            }, "connection");

            // 3. generator: one disposer per acquired resource, unwound together
            ctx.EffectMany(Resources, "resources");

            // 4. any IDisposable / IAsyncDisposable
            ctx.Use(new Noisy("lease"));

            // 5. a child plugin is an effect too, so it unloads before its parent
            ctx.Apply("child", child => child.Effect(() =>
            {
                Console.WriteLine("  start child");
                return () => Console.WriteLine("  stop  child");
            }));

            async IAsyncEnumerable<Disposer> Resources()
            {
                Console.WriteLine("  open  file A");
                yield return () => { Console.WriteLine("  close file A"); return default; };
                await Task.Yield();
                Console.WriteLine("  open  file B");
                yield return () => { Console.WriteLine("  close file B"); return default; };
            }
        });

        await fiber;
        await Task.Delay(50); // the child loads on its own turn
        Console.WriteLine($"  state: {fiber.State}, effects: {string.Join(", ", fiber.Effects.Select(e => e.Label))}");

        Console.WriteLine("  -- dispose");
        await fiber.DisposeAsync();
        Console.WriteLine($"  state: {fiber.State}");
    }

    private sealed class Noisy(string name) : IDisposable
    {
        public void Dispose() => Console.WriteLine($"  dispose {name}");
    }
}
