using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Cordis;

public enum FiberState
{
    Pending,
    Loading,
    Active,
    Failed,
    Disposed,
    Unloading,
}

/// <summary>Mutable payload of an <see cref="InternalEvents.Updating"/> notification.</summary>
public sealed class FiberUpdate(Fiber fiber, object? config, bool noSave)
{
    public Fiber Fiber { get; } = fiber;
    public object? Config { get; set; } = config;
    public bool NoSave { get; } = noSave;
}

/// <summary>
/// One live instantiation of a plugin. Owns the effects the plugin created, tracks the
/// identity of every injected provider as an "epoch", and reloads/unloads itself whenever
/// that epoch changes. Transitions are serialised by the inertia lock.
/// </summary>
public sealed class Fiber : IAsyncDisposable
{
    internal const string InactiveEpoch = "\u0000inactive";

    private readonly Dictionary<string, object?> _inject;
    private readonly Dictionary<string, ServiceImpl> _candidates = new(StringComparer.Ordinal);
    private readonly List<EffectHandle> _effects = [];
    private readonly List<Task> _loadTasks = [];
    private readonly EffectHandle? _anchor;
    private string _epoch = InactiveEpoch;
    private Task? _inertia;
    private Exception? _error;
    private object? _instance;

    /// <summary>Root fiber: always active, owns nothing but the top-level effects.</summary>
    internal Fiber(Context root)
    {
        Gate = root.SyncRoot;
        Parent = root;
        Ctx = root;
        Uid = 0;
        _inject = new Dictionary<string, object?>(StringComparer.Ordinal);
        _epoch = string.Empty;
        State = FiberState.Active;
        Store = new Dictionary<string, ServiceImpl>(StringComparer.Ordinal);
    }

    internal Fiber(Context parent, PluginRuntime runtime, object? config, Dictionary<string, object?> inject)
    {
        Gate = parent.SyncRoot;
        Parent = parent;
        Runtime = runtime;
        _inject = inject;
        Uid = parent.Registry.NextUid();
        Ctx = parent.ExtendForFiber(this, inject);

        parent.Internal.RaisePluginChanged(this);

        foreach (var name in _inject.Keys) CheckImpl(name);

        _anchor = parent.Fiber.EffectSync(() =>
        {
            runtime.Attach(this);
            try
            {
                Config = runtime.Schema is not null ? runtime.Schema.Validate(config) : config;
                Refresh();
            }
            catch (Exception error)
            {
                Ctx.Logger.Error(error);
                lock (Gate)
                {
                    _error = error;
                    UpdateState(static () => null);
                }
            }

            return async () =>
            {
                lock (Gate)
                {
                    Uid = null;
                    runtime.Detach(this);
                    if (runtime.FiberCount == 0) parent.Registry.Remove(runtime.Key);
                    SetEpoch(InactiveEpoch);
                }

                parent.Internal.RaisePluginChanged(this);

                while (true)
                {
                    Task? inertia;
                    lock (Gate) inertia = _inertia;
                    if (inertia is null) break;
                    try { await inertia.ConfigureAwait(false); }
                    catch { /* reported by the transition itself */ }
                }

                lock (Gate) State = FiberState.Disposed;
            };
        }, "ctx.Plugin()");
    }

    internal object Gate { get; }

    /// <summary>Incrementing identity; <c>null</c> once disposed. Part of the epoch key.</summary>
    public int? Uid { get; private set; }

    /// <summary>The context this fiber was created from.</summary>
    public Context Parent { get; }

    /// <summary>The context handed to the plugin body.</summary>
    public Context Ctx { get; }

    public PluginRuntime? Runtime { get; }

    public object? Config { get; private set; }

    public FiberState State { get; private set; } = FiberState.Pending;

    public Exception? Error => _error;

    public IReadOnlyDictionary<string, object?> Inject => _inject;

    /// <summary>Providers visible to the plugin body; <c>null</c> while unloaded.</summary>
    internal Dictionary<string, ServiceImpl>? Store { get; private set; }

    public object? Instance => _instance;

    public string Name
    {
        get
        {
            var fiber = this;
            while (fiber.Runtime is null)
            {
                var parent = fiber.Parent.Fiber;
                if (ReferenceEquals(parent, fiber)) return "root";
                fiber = parent;
            }
            return fiber.Runtime.Name;
        }
    }

    public override string ToString() => $"Fiber <{Name}> ({State})";

    // ---------------------------------------------------------------- effects

    public void AssertActive()
    {
        if (Uid is null) throw new InactiveFiberException();
    }

    /// <summary>Acquire a resource; the returned cleanup runs when the fiber unloads.</summary>
    public EffectHandle Effect(Func<Action?> execute, string label = "anonymous")
    {
        return Run(label, handle =>
        {
            var cleanup = execute();
            if (cleanup is not null) handle.Collect(() => { cleanup(); return default; });
            return null;
        });
    }

    /// <summary>Async acquisition. The fiber stays <see cref="FiberState.Loading"/> until it completes.</summary>
    public EffectHandle EffectAsync(Func<ValueTask<Disposer?>> execute, string label = "anonymous")
    {
        return Run(label, Body);

        async Task Body(EffectHandle handle) => handle.Collect(await execute().ConfigureAwait(false));
    }

    /// <summary>
    /// Generator form: yield one disposer per acquired resource. The analogue of cordis'
    /// <c>function*</c> effects. Pass an iterator local function.
    /// </summary>
    public EffectHandle EffectMany(Func<IAsyncEnumerable<Disposer>> execute, string label = "anonymous")
    {
        return Run(label, Body);

        async Task Body(EffectHandle handle)
        {
            await foreach (var disposer in execute().ConfigureAwait(false)) handle.Collect(disposer);
        }
    }

    public EffectHandle Use(IDisposable resource, string? label = null)
        => EffectSync(() => () => { resource.Dispose(); return default; }, label ?? $"use({resource.GetType().Name})");

    public EffectHandle UseAsync(IAsyncDisposable resource, string? label = null)
        => EffectSync(() => resource.DisposeAsync, label ?? $"use({resource.GetType().Name})");

    internal EffectHandle EffectSync(Func<Disposer?> execute, string label)
        => Run(label, handle => { handle.Collect(execute()); return null; });

    /// <summary>Snapshot of the effects currently owned by this fiber.</summary>
    public IReadOnlyList<EffectHandle> Effects
    {
        get { lock (Gate) return [.. _effects]; }
    }

    private EffectHandle Run(string label, Func<EffectHandle, Task?> body)
    {
        AssertActive();
        var handle = new EffectHandle(this, label);
        lock (Gate) _effects.Add(handle);

        Task? task;
        try
        {
            task = body(handle);
        }
        catch
        {
            _ = handle.DisposeAsync();
            throw;
        }

        if (task is null || task.IsCompletedSuccessfully) return handle;

        if (task.IsFaulted)
        {
            _ = handle.DisposeAsync();
            ExceptionDispatchInfo.Capture(task.Exception!.InnerException ?? task.Exception).Throw();
        }

        handle.SetPending(task);

        // An async effect started by the plugin body keeps the fiber LOADING until it settles,
        // and its failure fails the load. Effects started later (from a callback) cannot.
        lock (Gate)
        {
            if (State == FiberState.Loading)
            {
                _loadTasks.Add(task);
                return handle;
            }
        }

        _ = task.ContinueWith(faulted =>
        {
            Ctx.Logger.Error(faulted.Exception!.InnerException ?? faulted.Exception);
            _ = handle.DisposeAsync();
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        return handle;
    }

    internal void Detach(EffectHandle handle)
    {
        lock (Gate) _effects.Remove(handle);
    }

    internal void AttachDisposer(Disposer disposer, string label)
    {
        var handle = new EffectHandle(this, label);
        lock (Gate) _effects.Add(handle);
        handle.Collect(disposer);
    }

    // ------------------------------------------------------- dependency graph

    internal bool TryGetService(string name, out ServiceImpl impl)
    {
        lock (Gate)
        {
            if (Store is not null && Store.TryGetValue(name, out impl!)) return true;
        }
        impl = default!;
        return false;
    }

    internal void AddOwnService(string name, ServiceImpl impl)
    {
        lock (Gate) (Store ??= new Dictionary<string, ServiceImpl>(StringComparer.Ordinal))[name] = impl;
    }

    internal void RemoveOwnService(string name)
    {
        lock (Gate) Store?.Remove(name);
    }

    /// <summary>Re-evaluate whether <paramref name="name"/> currently has a usable provider.</summary>
    internal void CheckImpl(string name)
    {
        var impl = Ctx.Reflect.GetImpl(Ctx, name, strict: true);
        if (impl is null)
        {
            lock (Gate) _candidates.Remove(name);
            return;
        }

        if (impl.Check is not null)
        {
            bool ok;
            try
            {
                ok = impl.Check();
            }
            catch (Exception error)
            {
                impl.Fiber.Ctx.Logger.Error(error);
                ok = false;
            }
            if (!ok)
            {
                lock (Gate) _candidates.Remove(name);
                return;
            }
        }

        lock (Gate) _candidates[name] = impl;
    }

    /// <summary>Recompute the epoch from the identity of every injected provider.</summary>
    internal void Refresh()
    {
        string epoch;
        lock (Gate)
        {
            var builder = new StringBuilder();
            epoch = string.Empty;
            foreach (var name in _inject.Keys)
            {
                if (!_candidates.TryGetValue(name, out var impl))
                {
                    epoch = InactiveEpoch;
                    break;
                }
                builder.Append(':').Append(impl.Fiber.Uid);
            }
            if (epoch != InactiveEpoch) epoch = builder.ToString();
        }
        SetEpoch(epoch);
    }

    private void SetEpoch(string epoch)
    {
        lock (Gate)
        {
            var old = _epoch;
            if (epoch == old) return;
            _epoch = epoch;
            // inertia lock: a transition is already running; it reconciles on completion.
            if (_inertia is not null) return;

            UpdateState(() =>
            {
                if (epoch != InactiveEpoch && old == InactiveEpoch)
                {
                    _inertia = ReloadAsync();
                    return FiberState.Loading;
                }
                _inertia = UnloadAsync();
                return FiberState.Unloading;
            });
        }
    }

    private FiberState ComputeState()
    {
        if (Uid is null) return FiberState.Disposed;
        if (_error is not null) return FiberState.Failed;
        return _epoch != InactiveEpoch ? FiberState.Active : FiberState.Pending;
    }

    private void UpdateState(Func<FiberState?> transition)
    {
        var old = State;
        State = transition() ?? ComputeState();
        if (old == State) return;

        Ctx.Internal.RaiseStatusChanged(this, old);

        // only (de)activation is observable by dependents
        if (old != FiberState.Active && State != FiberState.Active) return;
        var provided = Ctx.Reflect.NamesProvidedBy(this);
        if (provided.Count > 0) Ctx.Reflect.Notify(Ctx, provided);
    }

    private async Task ReloadAsync()
    {
        await Task.Yield(); // mirror cordis: the plugin body never runs inside ctx.Plugin()

        string epoch;
        lock (Gate)
        {
            Store = new Dictionary<string, ServiceImpl>(_candidates, StringComparer.Ordinal);
            epoch = _epoch;
        }

        try
        {
            await ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Ctx.Logger.Error(error);
            lock (Gate)
            {
                _error = error;
                _epoch = InactiveEpoch;
            }
        }

        lock (Gate)
        {
            UpdateState(() =>
            {
                if (_epoch == epoch)
                {
                    _inertia = null;
                    return null;
                }
                _inertia = UnloadAsync();
                return FiberState.Unloading;
            });
        }
    }

    private async Task UnloadAsync()
    {
        // Transitions are started from inside `lock (Gate)`. Yielding first guarantees the
        // synchronous prefix ends there, so no await ever runs while the caller holds the lock.
        await Task.Yield();

        EffectHandle[] effects;
        lock (Gate)
        {
            effects = [.. _effects];
            _effects.Clear();
            _loadTasks.Clear();
        }

        for (var i = effects.Length - 1; i >= 0; i--)
        {
            try { await effects[i].DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Ctx.Logger.Error(error); }
        }

        lock (Gate)
        {
            Store = null;
            _instance = null;
            UpdateState(() =>
            {
                if (_epoch == InactiveEpoch)
                {
                    _inertia = null;
                    return null;
                }
                _inertia = ReloadAsync();
                return FiberState.Loading;
            });
        }
    }

    private async Task ExecuteAsync()
    {
        var instance = await Runtime!.Apply(Ctx, Config).ConfigureAwait(false);
        lock (Gate) _instance = instance;

        switch (instance)
        {
            case IAsyncDisposable asyncDisposable:
                AttachDisposer(asyncDisposable.DisposeAsync, $"dispose({instance.GetType().Name})");
                break;
            case IDisposable disposable:
                AttachDisposer(() => { disposable.Dispose(); return default; }, $"dispose({instance.GetType().Name})");
                break;
        }

        if (instance is IAsyncInit init) await init.InitAsync().ConfigureAwait(false);

        // drain async effects registered during the load (they may register more)
        while (true)
        {
            Task[] pending;
            lock (Gate)
            {
                if (_loadTasks.Count == 0) break;
                pending = [.. _loadTasks];
                _loadTasks.Clear();
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------- public API

    /// <summary>Wait until the fiber settles; rethrows the load error if it failed.</summary>
    public async Task<Fiber> WaitAsync()
    {
        while (true)
        {
            Task? inertia;
            lock (Gate) inertia = _inertia;
            if (inertia is null) break;
            await inertia.ConfigureAwait(false);
        }
        if (_error is not null) ExceptionDispatchInfo.Capture(_error).Throw();
        return this;
    }

    /// <summary><c>await ctx.Plugin(...)</c> resolves once the fiber is active.</summary>
    public TaskAwaiter<Fiber> GetAwaiter() => WaitAsync().GetAwaiter();

    public async Task RestartAsync()
    {
        AssertActive();
        lock (Gate)
        {
            SetEpoch(InactiveEpoch);
            Refresh();
        }
        await WaitAsync().ConfigureAwait(false);
    }

    /// <summary>Replace the config and reload. <paramref name="noSave"/> suppresses loader write-back.</summary>
    public async Task UpdateAsync(object? config, bool noSave = false)
    {
        AssertActive();
        var resolved = Runtime?.Schema is not null ? Runtime.Schema.Validate(config) : config;
        var update = new FiberUpdate(this, resolved, noSave);
        Ctx.Internal.RaiseUpdating(update);
        lock (Gate)
        {
            Config = update.Config;
            _error = null;
        }
        await RestartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_anchor is not null)
        {
            await _anchor.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // root fiber: tear the whole tree down
        EffectHandle[] effects;
        lock (Gate)
        {
            effects = [.. _effects];
            _effects.Clear();
        }
        for (var i = effects.Length - 1; i >= 0; i--)
        {
            try { await effects[i].DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Ctx.Logger.Error(error); }
        }
    }
}
