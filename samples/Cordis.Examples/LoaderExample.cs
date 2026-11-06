using System.Text.Json;
using Cordis.Loading;

namespace Cordis.Examples;

/// <summary>
/// The loader turns a JSON entry list into a live plugin tree and reconciles it on every change:
/// new entries are created, changed configs are applied, removed entries are disposed.
/// </summary>
public static class LoaderExample
{
    [ServiceName("store")]
    public sealed class Store(Context ctx) : Service(ctx, "store")
    {
        public string Id { get; } = Guid.NewGuid().ToString("n")[..4];
    }

    [Inject("store")]
    public sealed class Worker
    {
        public sealed class Options
        {
            public string Queue { get; set; } = "default";
        }

        public Worker(Context ctx, Options config)
        {
            Console.WriteLine($"  worker '{config.Queue}' uses store {ctx.Get<Store>().Id}");
            ctx.Effect(() => () => Console.WriteLine($"  worker '{config.Queue}' stopped"));
        }
    }

    public static async Task RunAsync()
    {
        var root = new Context();
        root.Loggers.ClearExporters();
        await root.Plugin<LoaderService>();
        var loader = root.Get<LoaderService>();
        loader.Resolver = new TypePluginResolver(typeof(LoaderExample).Assembly);

        // "isolate": the label "orders" is shared by both groups below; true would be private
        const string tree = """
        [
          { "id": "store", "name": "Store" },
          { "id": "orders", "name": "group", "group": true, "isolate": { "store": "orders" },
            "children": [
              { "id": "store",  "name": "Store" },
              { "id": "intake", "name": "Worker", "config": { "queue": "intake" } }
            ] },
          { "id": "billing", "name": "group", "group": true, "isolate": { "store": "orders" },
            "children": [
              { "id": "invoice", "name": "Worker", "config": { "queue": "invoice" } }
            ] },
          { "id": "audit", "name": "Worker", "config": { "queue": "audit" } }
        ]
        """;

        await loader.ApplyAsync(Parse(tree));
        Console.WriteLine($"  entries: {string.Join(", ", loader.Entries.Select(e => e.Id).Order())}");

        Console.WriteLine("  -- change one config, drop one entry");
        var edited = tree
            .Replace("\"queue\": \"audit\"", "\"queue\": \"audit-v2\"")
            .Replace("""
                  { "id": "invoice", "name": "Worker", "config": { "queue": "invoice" } }
            """, "");
        await loader.ApplyAsync(Parse(edited));

        Console.WriteLine("  -- disable an entry");
        await loader.SetDisabledAsync("orders:intake", true);

        await root.DisposeAsync();
    }

    private static List<EntryOptions> Parse(string json)
        => JsonSerializer.Deserialize<List<EntryOptions>>(json, LoaderService.JsonOptions)!;
}
