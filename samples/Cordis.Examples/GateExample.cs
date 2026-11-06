namespace Cordis.Examples;

/// <summary>
/// A fiber's lifecycle follows service availability only. To make a plugin run only while an
/// external condition holds, a small gate plugin turns the condition into a service.
/// </summary>
public static class GateExample
{
    public static readonly EventKey<bool> NetworkChanged = new("network-changed");

    public static async Task RunAsync()
    {
        var root = new Context();

        // the gate: provides "network" while the signal says it is up
        await root.Apply("network-gate", ctx =>
        {
            EffectHandle? online = null;
            ctx.On(NetworkChanged, async (_, up) =>
            {
                if (up && online is null)
                {
                    online = ctx.Provide("network", new object());
                }
                else if (!up && online is not null)
                {
                    var leaving = online;
                    online = null;
                    await leaving.DisposeAsync(); // returns once every dependent has unloaded
                }
            });
        });

        // runs only while the network is up, and cleans up when it drops
        var sync = root.Inject(["network"], ctx =>
        {
            Console.WriteLine("  sync: started");
            ctx.Effect(() => () => Console.WriteLine("  sync: stopped"));
        });

        await sync;
        Console.WriteLine($"  network down: {sync.State}");

        await root.EmitAsync(NetworkChanged, true);
        await sync;
        Console.WriteLine($"  network up:   {sync.State}");

        await root.EmitAsync(NetworkChanged, false);
        Console.WriteLine($"  network down: {sync.State}");

        await root.EmitAsync(NetworkChanged, true);
        await sync;
        Console.WriteLine($"  network up:   {sync.State}");

        await root.DisposeAsync();
    }
}
