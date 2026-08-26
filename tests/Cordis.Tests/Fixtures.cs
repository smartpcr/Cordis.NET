using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cordis;
using Cordis.Loading;
using Cordis.Tests;
using Cordis.Timers;

// ---------------------------------------------------------------- fixtures

[ServiceName("counter")]
public sealed class CounterService(Context ctx) : Service(ctx, "counter")
{
    public int Value { get; private set; }

    public void Increase() => Value++;
}

[ServiceName("gated")]
public sealed class GatedService(Context ctx) : Service(ctx, "gated"), IAsyncInit
{
    public static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask InitAsync() => await Ready.Task;
}

public sealed class EchoPlugin(Context ctx, EchoPlugin.Options config)
{
    public sealed class Options
    {
        [Range(1, 10)]
        public int Times { get; set; } = 1;

        public string Text { get; set; } = "";
    }

    public static readonly IConfigSchema Schema = DataAnnotationsSchema<Options>.Instance;

    public static readonly List<string> Log = [];

    private readonly EffectHandle _handle = ctx.Effect(() =>
    {
        var line = string.Join(' ', Enumerable.Repeat(config.Text, config.Times));
        Log.Add($"+{line}");
        return () => Log.Add($"-{line}");
    }, "echo");
}

[Inject("counter")]
public sealed class NeedsCounter(Context ctx)
{
    public static int Loads;

    public int Observed { get; } = Loads++ >= 0 ? ctx.Get<CounterService>().Value : 0;
}

public static class TestEvents
{
    public static readonly EventKey<int> Tick = new("tick");
    public static readonly BailKey<string, string> Resolve = new("resolve");
    public static readonly WaterfallKey<string> Transform = new("transform");
}
