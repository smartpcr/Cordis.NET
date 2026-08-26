using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cordis;
using Cordis.Loading;
using Cordis.Timers;

// ------------------------------------------------------------------ services

[ServiceName("database")]
public sealed class DatabaseService(Context ctx) : Service(ctx, "database"), IAsyncInit, IAsyncDisposable
{
    private readonly Dictionary<string, string> _rows = [];

    public async ValueTask InitAsync()
    {
        await Task.Delay(50); // pretend to open a connection
        _rows["greeting"] = "hello from the database";
        Ctx.Logger.Info("connected");
    }

    public string? Read(string key) => _rows.GetValueOrDefault(key);

    public ValueTask DisposeAsync()
    {
        Ctx.Logger.Info("disconnected");
        _rows.Clear();
        return default;
    }
}

// ------------------------------------------------------------------- plugins

[Inject("database")]
[Inject("timer")]
public sealed class HeartbeatPlugin
{
    public sealed class Options
    {
        [Range(10, 60_000)]
        public int IntervalMs { get; set; } = 250;
    }

    public static readonly IConfigSchema Schema = DataAnnotationsSchema<Options>.Instance;

    public HeartbeatPlugin(Context ctx, Options config)
    {
        var database = ctx.Get<DatabaseService>();
        var timer = ctx.Get<TimerService>();

        // The interval is owned by *this* fiber: unloading the plugin stops it.
        timer.Interval(
            () => ctx.Logger.Info($"beat: {database.Read("greeting")}"),
            TimeSpan.FromMilliseconds(config.IntervalMs));

        ctx.Effect(() =>
        {
            ctx.Logger.Info($"heartbeat every {config.IntervalMs}ms");
            return () => ctx.Logger.Info("heartbeat stopped");
        }, "heartbeat");
    }
}

// ---------------------------------------------------------------------- host

public static class Program
{
    public static async Task Main()
    {
        var root = new Context();
        root.Loggers.Level = LogLevel.Debug;

        // 1. imperative composition
        await root.Plugin<TimerService>();
        var database = root.Plugin<DatabaseService>();

        // The heartbeat is registered before the database exists; it stays PENDING and
        // starts by itself once the database finishes InitAsync.
        var heartbeat = root.Plugin<HeartbeatPlugin>(new HeartbeatPlugin.Options { IntervalMs = 200 });
        Console.WriteLine($"heartbeat before database: {heartbeat.State}");

        await database;
        await heartbeat;
        Console.WriteLine($"heartbeat after database:  {heartbeat.State}");

        await Task.Delay(500);

        // 2. live reconfiguration: the plugin unloads and reloads with the new config
        await heartbeat.UpdateAsync(new HeartbeatPlugin.Options { IntervalMs = 100 });
        await Task.Delay(300);

        // 3. pull the dependency: every dependent unloads, then comes back
        await database.DisposeAsync();
        Console.WriteLine($"heartbeat without database: {heartbeat.State}");
        await root.Plugin<DatabaseService>();
        await heartbeat;
        Console.WriteLine($"heartbeat restored:         {heartbeat.State}");
        await Task.Delay(300);

        // 4. declarative composition through the loader
        var configPath = Path.Combine(Path.GetTempPath(), "cordis-sample.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new[]
        {
            new EntryOptions
            {
                Id = "beat2",
                Name = nameof(HeartbeatPlugin),
                Config = JsonSerializer.SerializeToElement(new { intervalMs = 150 }),
            },
        }, LoaderService.JsonOptions));

        await root.Plugin<LoaderService>();
        await root.Plugin<JsonConfigFile>(new JsonConfigFile.Options { Path = configPath, Watch = true });
        Console.WriteLine("loaded from file; edit it and watch it reload");
        await Task.Delay(500);

        await root.DisposeAsync();
        File.Delete(configPath);
    }
}
