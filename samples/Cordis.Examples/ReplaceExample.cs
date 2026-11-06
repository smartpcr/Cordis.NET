namespace Cordis.Examples;

/// <summary>
/// <c>Registry.Replace</c> swaps the implementation behind every fiber of a plugin in place: the
/// fibers keep their position, config and identity, take the new plugin's dependencies, and reload.
/// The old plugin forwards to the new one afterwards. This is the primitive a hot-reload host builds on.
/// </summary>
public static class ReplaceExample
{
    public sealed class Options
    {
        public string Name { get; set; } = "world";
    }

    public sealed class GreeterV1
    {
        public GreeterV1(Context ctx, Options config)
        {
            Console.WriteLine($"  v1: hello, {config.Name}");
            ctx.Effect(() => () => Console.WriteLine("  v1: unloaded"));
        }
    }

    [Inject("clock")]
    public sealed class GreeterV2
    {
        public GreeterV2(Context ctx, Options config)
        {
            Console.WriteLine($"  v2: good {ctx.Get("clock")}, {config.Name}");
            ctx.Effect(() => () => Console.WriteLine("  v2: unloaded"));
        }
    }

    public static async Task RunAsync()
    {
        var root = new Context();
        root.Internal.Replaced += fiber => Console.WriteLine($"  replaced: fiber #{fiber.Uid} now runs {fiber.Runtime!.Name}");

        var greeter = await root.Plugin<GreeterV1>(new Options { Name = "Ada" });

        Console.WriteLine("  -- replace v1 with v2 (v2 needs a clock that is not there yet)");
        root.Registry.Replace(typeof(GreeterV1), typeof(GreeterV2));
        await greeter;
        Console.WriteLine($"  greeter: {greeter.State}");

        root.Provide("clock", "morning");
        await Task.Delay(20);
        await greeter;
        Console.WriteLine($"  greeter: {greeter.State}");

        Console.WriteLine("  -- the old type now forwards to the new one");
        var another = await root.Plugin<GreeterV1>(new Options { Name = "Grace" });
        Console.WriteLine($"  another runs {another.Runtime!.Name}");

        Console.WriteLine("  -- switch back");
        root.Registry.Replace(typeof(GreeterV2), typeof(GreeterV1));
        await greeter;
        await another;

        await root.DisposeAsync();
    }
}
