namespace Cordis;

public sealed class ServiceImpl(string name, Fiber fiber, object? value, Func<bool>? check)
{
    public string Name { get; } = name;
    public Fiber Fiber { get; } = fiber;
    public object? Value { get; internal set; } = value;
    public Func<bool>? Check { get; } = check;
}

/// <summary>
/// The registry of service implementations, keyed by isolate realm. Replaces cordis'
/// <c>ReflectService</c>: instead of a <c>Proxy</c> get-trap, resolution is an explicit
/// walk up the fiber chain that stops at a realm boundary.
/// </summary>
public sealed class ServiceStore(Context root)
{
    private readonly Dictionary<IsolateKey, ServiceImpl> _store = [];

    /// <summary>Register a provider owned by the calling fiber. Unregistered on fiber unload.</summary>
    public EffectHandle Provide(Context ctx, string name, object? value = null, Func<bool>? check = null)
    {
        return ctx.Fiber.EffectSync(() =>
        {
            var key = ctx.KeyFor(name);
            lock (root.SyncRoot)
            {
                if (_store.TryGetValue(key, out var existing))
                {
                    throw new CordisException($"service \"{name}\" is already registered at <{existing.Fiber.Name}>");
                }
                var impl = new ServiceImpl(name, ctx.Fiber, value, check);
                _store[key] = impl;
                ctx.Fiber.AddOwnService(name, impl);
                if (ctx.Fiber.State == FiberState.Active) Notify(ctx, [name]);
            }

            return async () =>
            {
                IReadOnlyList<Fiber> affected;
                lock (root.SyncRoot)
                {
                    _store.Remove(key);
                    affected = Notify(ctx, [name]);
                }
                foreach (var fiber in affected)
                {
                    try { await fiber.WaitAsync().ConfigureAwait(false); }
                    catch { /* dependent failures are logged by their own fiber */ }
                }
                ctx.Fiber.RemoveOwnService(name);
            };
        }, $"ctx.Provide(\"{name}\")");
    }

    /// <summary>Swap the value behind an already-provided name (must be the owning fiber).</summary>
    public void Set(Context ctx, string name, object? value)
    {
        lock (root.SyncRoot)
        {
            var key = ctx.KeyFor(name);
            if (!_store.TryGetValue(key, out var impl))
            {
                throw new CordisException($"cannot set service \"{name}\" without Provide");
            }
            if (!ReferenceEquals(impl.Fiber, ctx.Fiber))
            {
                throw new CordisException($"cannot set service \"{name}\" owned by <{impl.Fiber.Name}>");
            }
            impl.Value = value;
            Notify(ctx, [name]);
        }
    }

    internal ServiceImpl? GetImpl(Context ctx, string name, bool strict = true)
    {
        lock (root.SyncRoot)
        {
            var key = ctx.KeyFor(name);
            if (!_store.TryGetValue(key, out var impl)) return null;
            if (strict && impl.Fiber.State != FiberState.Active) return null;
            return impl;
        }
    }

    /// <summary>
    /// Resolve a service for <paramref name="ctx"/>. Plugin contexts may only reach services they
    /// declared in <c>Inject</c>; the root context is unrestricted.
    /// </summary>
    public object? Resolve(Context ctx, string name, bool required = true)
    {
        var fiber = ctx.Fiber;

        if (fiber.Runtime is null)
        {
            var impl = GetImpl(ctx, name);
            if (impl is not null) return Scope(ctx, impl.Value);
            return required ? throw new ServiceUnavailableException(name) : null;
        }

        if (!fiber.Inject.ContainsKey(name)) throw new ServiceNotInjectedException(name);

        var key = ctx.KeyFor(name);
        while (true)
        {
            if (fiber.TryGetService(name, out var impl)) return Scope(ctx, impl.Value);
            if (fiber.Inject.ContainsKey(name)) break;              // declared but not loaded
            if (fiber.Runtime is null) break;                       // reached the root
            if (!ReferenceEquals(fiber.Parent.KeyFor(name), key)) break; // realm boundary
            fiber = fiber.Parent.Fiber;
        }

        return required ? throw new ServiceUnavailableException(name) : null;
    }

    private static object? Scope(Context ctx, object? value)
        => value is IContextScoped scoped ? scoped.ForContext(ctx) : value;

    internal IReadOnlyList<string> NamesProvidedBy(Fiber fiber)
    {
        lock (root.SyncRoot)
        {
            List<string>? names = null;
            foreach (var impl in _store.Values)
            {
                if (!ReferenceEquals(impl.Fiber, fiber)) continue;
                (names ??= []).Add(impl.Name);
            }
            return (IReadOnlyList<string>?)names ?? [];
        }
    }

    /// <summary>Re-evaluate every fiber that injects one of <paramref name="names"/>.</summary>
    internal IReadOnlyList<Fiber> Notify(Context ctx, IReadOnlyList<string> names)
    {
        var affected = new List<Fiber>();

        foreach (var runtime in root.Registry.Runtimes)
        {
            foreach (var fiber in runtime.Fibers)
            {
                var touched = false;
                foreach (var name in names)
                {
                    if (!fiber.Inject.ContainsKey(name)) continue;
                    if (!ReferenceEquals(fiber.Ctx.KeyFor(name), ctx.KeyFor(name))) continue;
                    touched = true;
                    fiber.CheckImpl(name);
                }
                if (!touched) continue;
                fiber.Refresh();
                affected.Add(fiber);
            }
        }

        foreach (var name in names)
        {
            root.Internal.RaiseServiceChanged(name, GetImpl(ctx, name, strict: false)?.Value);
        }

        return affected;
    }
}
