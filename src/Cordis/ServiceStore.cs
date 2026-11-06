namespace Cordis;

/// <summary>
/// One binding of a service name to a value, created by <see cref="ServiceStore.Provide"/>.
/// A binding is retired when its provide effect is disposed; it is never reused.
/// </summary>
public sealed class ServiceImpl
{
    internal ServiceImpl(int uid, string name, Context ctx, IsolateKey isolate, object? value, Func<bool>? check)
    {
        Uid = uid;
        Name = name;
        Ctx = ctx;
        Isolate = isolate;
        Value = value;
        Check = check;
    }

    /// <summary>Unique per binding. Part of every dependent's epoch, so a new binding reloads dependents.</summary>
    public int Uid { get; }

    public string Name { get; }

    /// <summary>The context the service was provided from.</summary>
    public Context Ctx { get; }

    /// <summary>The providing fiber.</summary>
    public Fiber Fiber => Ctx.Fiber;

    /// <summary>The realm this binding lives in.</summary>
    public IsolateKey Isolate { get; }

    public object? Value { get; internal set; }

    public Func<bool>? Check { get; }

    /// <summary>Fibers that loaded against this binding and have not finished unloading. Guarded by the tree lock.</summary>
    internal HashSet<Fiber> Dependents { get; } = [];

    /// <summary>Completed when the last dependent releases a retired binding. Guarded by the tree lock.</summary>
    internal TaskCompletionSource? Release { get; set; }

    public override string ToString() => $"ServiceImpl <{Name}#{Uid}>";
}

/// <summary>
/// The registry of service implementations, keyed by isolate realm. Replaces cordis'
/// <c>ReflectService</c>: instead of a <c>Proxy</c> get-trap, resolution is explicit and stops at
/// a realm boundary.
/// </summary>
public sealed class ServiceStore(Context root)
{
    private readonly Dictionary<IsolateKey, ServiceImpl> _store = [];
    private int _counter;

    /// <summary>Register a provider owned by the calling fiber. Unregistered on fiber unload.</summary>
    public EffectHandle Provide(Context ctx, string name, object? value = null, Func<bool>? check = null)
    {
        return ctx.Fiber.EffectSync(() =>
        {
            var key = ctx.KeyFor(name);
            ServiceImpl impl;
            lock (root.SyncRoot)
            {
                if (_store.TryGetValue(key, out var existing))
                {
                    throw new CordisException($"service \"{name}\" is already registered at <{existing.Fiber.Name}>");
                }
                impl = new ServiceImpl(++_counter, name, ctx, key, value, check);
                _store[key] = impl;
                ctx.Fiber.Provisions.Add(impl);
                if (ctx.Fiber.State == FiberState.Active) Notify([impl]);
            }

            return async () =>
            {
                Task? release = null;
                lock (root.SyncRoot)
                {
                    if (_store.TryGetValue(impl.Isolate, out var current) && ReferenceEquals(current, impl))
                    {
                        _store.Remove(impl.Isolate);
                    }
                    Notify([impl]);

                    // A retired binding gains no new dependents. Wait only for the fibers that loaded
                    // against it, including ones that already left the registry while disposing.
                    if (impl.Dependents.Count > 0)
                    {
                        impl.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        release = impl.Release.Task;
                    }
                }

                if (release is not null) await release.ConfigureAwait(false);

                // the provider keeps access to its own service until every dependent is gone
                lock (root.SyncRoot)
                {
                    impl.Release = null;
                    ctx.Fiber.Provisions.Remove(impl);
                }
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
            Notify([impl]);
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
    /// Resolve a service for <paramref name="ctx"/>. Plugin contexts reach the services they
    /// provide themselves (in the same realm) and the services they declared in <c>Inject</c>;
    /// the root context is unrestricted.
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

        var key = ctx.KeyFor(name);

        // own provisions come first and are kept apart from the injected view
        if (fiber.TryGetProvision(name, key, out var own)) return Scope(ctx, own.Value);

        if (!fiber.Inject.ContainsKey(name)) throw new ServiceNotInjectedException(name);

        // `ctx` isolates `name` again below the fiber: the injected binding belongs to another realm
        if (!ReferenceEquals(fiber.Ctx.KeyFor(name), key))
        {
            return required ? throw new ServiceUnavailableException(name) : null;
        }

        if (fiber.TryGetService(name, out var injected)) return Scope(ctx, injected.Value);
        return required ? throw new ServiceUnavailableException(name) : null;
    }

    private static object? Scope(Context ctx, object? value)
        => value is IContextScoped scoped ? scoped.ForContext(ctx) : value;

    internal bool IsCurrent(ServiceImpl impl)
    {
        lock (root.SyncRoot)
        {
            return _store.TryGetValue(impl.Isolate, out var current) && ReferenceEquals(current, impl);
        }
    }

    /// <summary>
    /// Re-evaluate every fiber that injects one of the given bindings' names from the same realm.
    /// Matching is by the binding's own realm, which survives the provider context being re-isolated.
    /// </summary>
    internal void Notify(IReadOnlyList<ServiceImpl> impls)
    {
        lock (root.SyncRoot)
        {
            foreach (var runtime in root.Registry.Runtimes)
            {
                foreach (var fiber in runtime.Fibers)
                {
                    var touched = false;
                    foreach (var impl in impls)
                    {
                        if (!fiber.Inject.ContainsKey(impl.Name)) continue;
                        if (!ReferenceEquals(fiber.Ctx.KeyFor(impl.Name), impl.Isolate)) continue;
                        touched = true;
                        fiber.CheckImpl(impl.Name);
                    }
                    if (touched) fiber.Refresh();
                }
            }

            foreach (var impl in impls)
            {
                var value = _store.TryGetValue(impl.Isolate, out var current) ? current.Value : null;
                root.Internal.RaiseServiceChanged(impl.Name, value);
            }
        }
    }
}
