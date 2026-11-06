using System.ComponentModel.DataAnnotations;

namespace Cordis.Examples;

/// <summary>
/// Config is validated on every load. A running plugin rejects a bad update without stopping;
/// a failed plugin waits for an update to recover. A plugin can also apply a config in place
/// instead of restarting.
/// </summary>
public static class ConfigExample
{
    public sealed class PollerOptions
    {
        [Range(10, 10_000)]
        public int IntervalMs { get; set; } = 100;

        [Required, MinLength(1)]
        public string Target { get; set; } = "localhost";
    }

    /// <summary>Restarts on every config change (the default).</summary>
    public sealed class Poller
    {
        public static readonly IConfigSchema Schema = DataAnnotationsSchema<PollerOptions>.Instance;

        public Poller(Context ctx, PollerOptions config)
        {
            Console.WriteLine($"  poller loaded: {config.Target} every {config.IntervalMs}ms");
            ctx.Effect(() => () => Console.WriteLine($"  poller unloaded ({config.Target})"));
        }
    }

    /// <summary>Applies a new log prefix in place: no unload, no reload.</summary>
    public sealed class Banner : IPlugin<string>
    {
        public string Prefix { get; private set; } = "";

        public ValueTask ApplyAsync(Context ctx, string config)
        {
            Prefix = config;
            Console.WriteLine($"  banner loaded with prefix '{Prefix}'");
            ctx.OnConfigUpdate((resolved, next) =>
            {
                // call next() instead to fall back to a restart
                Prefix = (string)resolved!;
                Console.WriteLine($"  banner updated in place to '{Prefix}'");
                return default;
            });
            return default;
        }
    }

    public static async Task RunAsync()
    {
        var root = new Context();
        root.Loggers.ClearExporters(); // keep the output to what the example prints
        root.Internal.Committed += (fiber, config) => Console.WriteLine($"  committed config for {fiber.Name}");

        var poller = await root.Plugin<Poller>(new PollerOptions { Target = "db-1" });

        Console.WriteLine("  -- valid update: unload + reload");
        await poller.UpdateAsync(new PollerOptions { Target = "db-2", IntervalMs = 500 });

        Console.WriteLine("  -- invalid update: rejected, the plugin keeps running");
        try
        {
            await poller.UpdateAsync(new PollerOptions { Target = "db-3", IntervalMs = 1 });
        }
        catch (ConfigValidationException error)
        {
            Console.WriteLine($"  rejected: {error.Issues[0]}");
        }
        Console.WriteLine($"  poller: {poller.State}, target still {((PollerOptions)poller.Config!).Target}");

        Console.WriteLine("  -- a plugin created with a bad config fails, then an update recovers it");
        var broken = root.Plugin<Poller>(new PollerOptions { Target = "" });
        try { await broken; } catch (ConfigValidationException) { }
        Console.WriteLine($"  broken: {broken.State}");
        await broken.UpdateAsync(new PollerOptions { Target = "db-4" });
        Console.WriteLine($"  broken: {broken.State}");

        Console.WriteLine("  -- in-place update (noSave: not committed)");
        var banner = await root.Plugin(new Banner(), "v1");
        await banner.UpdateAsync("v2", noSave: true);
        Console.WriteLine($"  banner: {banner.State}");

        await root.DisposeAsync();
    }
}
