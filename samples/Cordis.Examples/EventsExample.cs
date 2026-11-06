namespace Cordis.Examples;

/// <summary>
/// One key type per dispatch mode keeps handlers typed. Listeners belong to the fiber that
/// registered them and disappear when it unloads.
/// </summary>
public static class EventsExample
{
    public sealed record Order(string Id, decimal Total);

    public static readonly EventKey<Order> OrderPlaced = new("order-placed");
    public static readonly BailKey<string, decimal?> PriceLookup = new("price-lookup");
    public static readonly WaterfallKey<string> RenderReceipt = new("render-receipt");

    public static async Task RunAsync()
    {
        var root = new Context();

        // a plugin that reacts to orders, filtering on the payload
        var audit = await root.Apply("audit", ctx =>
        {
            ctx.On(OrderPlaced, (_, order) =>
            {
                if (order.Total < 100) return;
                Console.WriteLine($"  audit: large order {order.Id} ({order.Total})");
            });
        });

        // prepend puts a listener in front of the existing ones
        await root.Apply("metrics", ctx =>
            ctx.On(OrderPlaced, (_, order) => Console.WriteLine($"  metrics: order {order.Id}"), prepend: true));

        Console.WriteLine("  -- emit (sequential)");
        await root.EmitAsync(OrderPlaced, new Order("A1", 250));

        Console.WriteLine("  -- parallel (concurrent, failures aggregated)");
        await root.ParallelAsync(OrderPlaced, new Order("A2", 20));

        // bail: the first non-null answer wins
        await root.Apply("catalog", ctx => ctx.On(PriceLookup, (_, sku) =>
            new ValueTask<decimal?>(sku == "apple" ? 1.5m : null)));
        await root.Apply("fallback", ctx => ctx.On(PriceLookup, (_, _) => new ValueTask<decimal?>(9.99m)));
        Console.WriteLine($"  -- bail: apple = {await root.BailAsync(PriceLookup, "apple")}, pear = {await root.BailAsync(PriceLookup, "pear")}");

        // waterfall: middleware around a terminal
        await root.Apply("frame", ctx => ctx.On(RenderReceipt, async (_, text, next) => $"[{await next(text)}]"));
        await root.Apply("shout", ctx => ctx.On(RenderReceipt, (_, text, next) => next(text.ToUpperInvariant())));
        var receipt = await root.WaterfallAsync(RenderReceipt, "thanks", text => new ValueTask<string>(text + "!"));
        Console.WriteLine($"  -- waterfall: {receipt}");

        // once: removes itself after the first call
        root.Once(OrderPlaced, (_, order) =>
        {
            Console.WriteLine($"  once: first order seen was {order.Id}");
            return default;
        });

        Console.WriteLine("  -- unload audit, emit again (audit is gone, once fires a single time)");
        await audit.DisposeAsync();
        await root.EmitAsync(OrderPlaced, new Order("A3", 500));
        await root.EmitAsync(OrderPlaced, new Order("A4", 500));
    }
}
