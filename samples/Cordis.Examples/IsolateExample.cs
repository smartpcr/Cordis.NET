namespace Cordis.Examples;

/// <summary>
/// <c>Isolate</c> gives a subtree its own realm for one service name; <c>Intercept</c> layers config
/// for a subtree. Both are cheap child contexts, not copies.
/// </summary>
public static class IsolateExample
{
    [Inject("store")]
    public sealed class Reporter
    {
        public Reporter(Context ctx)
        {
            ctx.Logger.Debug("debug lines show only where the logger level is intercepted");
            Console.WriteLine($"  {ctx.Fiber.Name} reads store = {ctx.Get("store")}");
        }
    }

    public static async Task RunAsync()
    {
        var root = new Context();
        root.Loggers.ClearExporters();
        root.Loggers.AddExporter(new PrefixExporter());

        root.Provide("store", "shared-store");

        // tenant A and B each get a private "store"; everything else is still shared
        var tenantA = root.Isolate("store");
        tenantA.Provide("store", "tenant-a-store");
        var tenantB = root.Isolate("store");
        tenantB.Provide("store", "tenant-b-store");

        await root.Plugin<Reporter>();
        await tenantA.Plugin<Reporter>();
        await tenantB.Plugin<Reporter>();

        // a shared realm: two subtrees that pass the same key see the same binding
        var analytics = new IsolateKey("store");
        root.Isolate("store", analytics).Provide("store", "analytics-store");
        await root.Isolate("store", analytics).Plugin<Reporter>();

        // intercept: debug logging for one subtree only
        Console.WriteLine("  -- logger intercepted to Debug for one subtree");
        await root.Intercept(LoggerService.ServiceName, new LoggerConfig { Level = LogLevel.Debug })
            .Plugin<Reporter>();
    }

    private sealed class PrefixExporter : ILogExporter
    {
        public void Export(LogMessage message) => Console.WriteLine($"  [{message.Level}] {message.Name}: {message.Text}");
    }
}
