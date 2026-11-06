namespace Cordis.Examples;

/// <summary>
/// A consumer declared before its provider waits, starts when the provider appears, and unloads
/// before the provider cleans up. Swapping the provider reloads the consumer against the new one.
/// </summary>
public static class ServicesExample
{
    [ServiceName("cache")]
    public sealed class CacheService(Context ctx) : Service(ctx, "cache"), IAsyncInit, IDisposable
    {
        private readonly Dictionary<string, string> _items = [];

        public string Generation { get; } = Guid.NewGuid().ToString("n")[..4];

        // dependents wait for this to finish
        public async ValueTask InitAsync()
        {
            await Task.Delay(20);
            Console.WriteLine($"  cache {Generation} ready");
        }

        public void Put(string key, string value) => _items[key] = value;

        public string? Get(string key) => _items.GetValueOrDefault(key);

        public void Dispose() => Console.WriteLine($"  cache {Generation} disposed");
    }

    [Inject("cache")]
    public sealed class Warmer
    {
        public Warmer(Context ctx)
        {
            var cache = ctx.Get<CacheService>();
            cache.Put("greeting", "hello");
            Console.WriteLine($"  warmer loaded against cache {cache.Generation}");

            // still usable here: the provider waits for this cleanup before disposing
            ctx.Effect(() => () => Console.WriteLine($"  warmer unloading, cache still reads '{cache.Get("greeting")}'"));
        }
    }

    public static async Task RunAsync()
    {
        var root = new Context();

        var warmer = root.Plugin<Warmer>();
        await warmer;
        Console.WriteLine($"  warmer without cache: {warmer.State}");

        var cache = await root.Plugin<CacheService>();
        await warmer;
        Console.WriteLine($"  warmer with cache:    {warmer.State}");

        Console.WriteLine("  -- restart the cache (new instance, same fiber)");
        await cache.RestartAsync();
        await warmer;

        Console.WriteLine("  -- remove the cache");
        await root.Registry.RemoveAsync(typeof(CacheService));
        Console.WriteLine($"  warmer: {warmer.State}");

        await root.DisposeAsync();
    }
}
