namespace Cordis;

/// <summary>Fire-and-forget or awaited notification. Handlers: <c>Func&lt;Context, TArgs, ValueTask&gt;</c>.</summary>
public sealed class EventKey<TArgs>(string name)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>First non-null result wins. Handlers: <c>Func&lt;Context, TArgs, ValueTask&lt;TResult?&gt;&gt;</c>.</summary>
public sealed class BailKey<TArgs, TResult>(string name)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>Middleware chain over a value. Handlers receive <c>next</c>.</summary>
public sealed class WaterfallKey<TArgs>(string name)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

internal sealed class Hook(Context ctx, Delegate callback, bool global)
{
    public Context Ctx { get; } = ctx;
    public Delegate Callback { get; } = callback;
    public bool Global { get; } = global;
}

public sealed class EventsService(Context root)
{
    private readonly Dictionary<object, List<Hook>> _hooks = new(ReferenceEqualityComparer.Instance);

    // ------------------------------------------------------------ registration

    public EffectHandle On<TArgs>(
        Context ctx, EventKey<TArgs> key, Func<Context, TArgs, ValueTask> handler,
        bool prepend = false, bool global = false)
        => Register(ctx, key, handler, prepend, global);

    public EffectHandle On<TArgs>(
        Context ctx, EventKey<TArgs> key, Action<Context, TArgs> handler,
        bool prepend = false, bool global = false)
        => Register(ctx, key, (Func<Context, TArgs, ValueTask>)((c, a) => { handler(c, a); return default; }), prepend, global);

    public EffectHandle On<TArgs, TResult>(
        Context ctx, BailKey<TArgs, TResult> key, Func<Context, TArgs, ValueTask<TResult?>> handler,
        bool prepend = false, bool global = false)
        => Register(ctx, key, handler, prepend, global);

    public EffectHandle On<TArgs>(
        Context ctx, WaterfallKey<TArgs> key,
        Func<Context, TArgs, Func<TArgs, ValueTask<TArgs>>, ValueTask<TArgs>> handler,
        bool prepend = false, bool global = false)
        => Register(ctx, key, handler, prepend, global);

    public EffectHandle Once<TArgs>(Context ctx, EventKey<TArgs> key, Func<Context, TArgs, ValueTask> handler)
    {
        EffectHandle? handle = null;
        handle = On(ctx, key, async (c, a) =>
        {
            if (handle is not null) await handle.DisposeAsync().ConfigureAwait(false);
            await handler(c, a).ConfigureAwait(false);
        });
        return handle;
    }

    private EffectHandle Register(Context ctx, object key, Delegate handler, bool prepend, bool global)
    {
        return ctx.Fiber.EffectSync(() =>
        {
            var hook = new Hook(ctx, handler, global);
            lock (root.SyncRoot)
            {
                if (!_hooks.TryGetValue(key, out var list)) _hooks[key] = list = [];
                if (prepend) list.Insert(0, hook); else list.Add(hook);
            }
            return () =>
            {
                lock (root.SyncRoot)
                {
                    if (_hooks.TryGetValue(key, out var list)) list.Remove(hook);
                }
                return default;
            };
        }, $"ctx.On({key})");
    }

    private List<Hook> Snapshot(object key, Func<Context, bool>? filter)
    {
        lock (root.SyncRoot)
        {
            if (!_hooks.TryGetValue(key, out var list)) return [];
            if (filter is null) return [.. list];
            return [.. list.Where(hook => hook.Global || filter(hook.Ctx))];
        }
    }

    // -------------------------------------------------------------- dispatch

    /// <summary>Sequential dispatch; the first failure propagates.</summary>
    public async ValueTask EmitAsync<TArgs>(EventKey<TArgs> key, TArgs args, Func<Context, bool>? filter = null)
    {
        foreach (var hook in Snapshot(key, filter))
        {
            await ((Func<Context, TArgs, ValueTask>)hook.Callback)(hook.Ctx, args).ConfigureAwait(false);
        }
    }

    /// <summary>Concurrent dispatch; failures are aggregated.</summary>
    public async ValueTask ParallelAsync<TArgs>(EventKey<TArgs> key, TArgs args, Func<Context, bool>? filter = null)
    {
        var tasks = Snapshot(key, filter)
            .Select(hook => ((Func<Context, TArgs, ValueTask>)hook.Callback)(hook.Ctx, args).AsTask())
            .ToArray();
        if (tasks.Length == 0) return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            var errors = tasks.Where(t => t.IsFaulted).Select(t => (Exception)t.Exception!).ToArray();
            throw new AggregateError(errors);
        }
    }

    /// <summary>Returns the first non-null result.</summary>
    public async ValueTask<TResult?> BailAsync<TArgs, TResult>(
        BailKey<TArgs, TResult> key, TArgs args, Func<Context, bool>? filter = null)
    {
        foreach (var hook in Snapshot(key, filter))
        {
            var result = await ((Func<Context, TArgs, ValueTask<TResult?>>)hook.Callback)(hook.Ctx, args).ConfigureAwait(false);
            if (result is not null) return result;
        }
        return default;
    }

    /// <summary>Middleware chain terminated by <paramref name="terminal"/>.</summary>
    public ValueTask<TArgs> WaterfallAsync<TArgs>(
        WaterfallKey<TArgs> key, TArgs args, Func<TArgs, ValueTask<TArgs>> terminal, Func<Context, bool>? filter = null)
    {
        var hooks = Snapshot(key, filter);
        var index = 0;
        return Next(args);

        ValueTask<TArgs> Next(TArgs value)
        {
            if (index >= hooks.Count) return terminal(value);
            var hook = hooks[index++];
            var callback = (Func<Context, TArgs, Func<TArgs, ValueTask<TArgs>>, ValueTask<TArgs>>)hook.Callback;
            return callback(hook.Ctx, value, Next);
        }
    }
}

public sealed class AggregateError(IReadOnlyList<Exception> errors)
    : AggregateException("one or more event handlers failed", errors);

/// <summary>
/// Synchronous lifecycle notifications used by the framework and by the loader.
/// Analogue of cordis' <c>internal/*</c> events.
/// </summary>
public sealed class InternalEvents
{
    /// <summary>A fiber was created (Uid set) or disposed (Uid null).</summary>
    public event Action<Fiber>? PluginChanged;

    public event Action<Fiber, FiberState>? StatusChanged;

    public event Action<string, object?>? ServiceChanged;

    /// <summary>Raised before a fiber applies a new config; handlers may rewrite or persist it.</summary>
    public event Action<FiberUpdate>? Updating;

    internal void RaisePluginChanged(Fiber fiber) => PluginChanged?.Invoke(fiber);

    internal void RaiseStatusChanged(Fiber fiber, FiberState old) => StatusChanged?.Invoke(fiber, old);

    internal void RaiseServiceChanged(string name, object? value) => ServiceChanged?.Invoke(name, value);

    internal void RaiseUpdating(FiberUpdate update) => Updating?.Invoke(update);
}
