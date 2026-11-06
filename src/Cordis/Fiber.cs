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

/// <summary>
/// Intercepts a <see cref="Fiber.UpdateAsync"/> of the fiber it was registered on. Receives the
/// validated config and a <c>next</c> that restarts the fiber; a handler that applies the config
/// in place returns without calling <c>next</c>. Analogue of a fiber-local <c>internal/update</c>.
/// </summary>
public delegate ValueTask ConfigUpdateHandler(object? resolvedConfig, Func<ValueTask> next);

/// <summary>
/// One live instantiation of a plugin. Owns the effects the plugin created, tracks the
/// identity of every injected provider as an "epoch", and reloads/unloads itself whenever
/// that epoch changes. Transitions are serialised by the inertia lock.
/// </summary>
public sealed class Fiber : IAsyncDisposable
{
    internal const string InactiveEpoch = "\u0000inactive";

    private readonly Dictionary<string, ServiceImpl> _candidates = new(StringComparer.Ordinal);
    private readonly List<EffectHandle> _effects = [];
    private readonly List<Task> _loadTasks = [];
    private readonly List<UpdateHook> _updateHooks = [];
    private readonly EffectHandle? _anchor;
    private Dictionary<string, object?> _inject;
    private string _epoch = InactiveEpoch;
    private int _revision;
    private Task? _inertia;
    private Exception? _error;
    private object? _instance;
    private TaskCompletionSource? _releaseSignal;

    /// <summary>Root fiber: always active, owns nothing but the top-level effects.</summary>
    internal Fiber(Context root)
    {
        Gate = root.SyncRoot;
        Parent = root;
        Ctx = root;
        Uid = 0;
        _inject = new Dictionary<string, object?>(StringComparer.Ordinal);
        _epoch = "0";
        State = FiberState.Active;
        Committed = new Dictionary<string, ServiceImpl>(StringComparer.Ordinal);
    }

    internal Fiber(Context parent, PluginRuntime runtime, object? config, Dictionary<string, object?> inject)
    {
        Gate = parent.SyncRoot;
        Parent = parent;
        Runtime = runtime;
        Config = config;
        _inject = inject;
        Uid = parent.Registry.NextUid();
        Ctx = parent.ExtendForFiber(this);

        parent.Internal.RaisePluginChanged(this);

        foreach (var name in _inject.Keys) CheckImpl(name);

        _anchor = parent.Fiber.EffectSync(() =>
        {
            runtime.Attach(this);
            try
            {
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
                    // the fiber may have been moved to another runtime by Registry.Replace
                    var current = Runtime!;
                    current.Detach(this);
                    if (current.FiberCount == 0) parent.Registry.Unregister(current);
                    SetEpoch(InactiveEpoch);
                }

                parent.Internal.RaisePluginChanged(this);

                // the transitions never fault: reload and unload log their own errors
                while (true)
                {
                    Task? inertia;
                    lock (Gate) inertia = _inertia;
                    if (inertia is null) break;
                    await inertia.ConfigureAwait(false);
                }

                // SetEpoch above is a no-op for a fiber that never loaded or that failed, so the
                // state is re-derived here; with Uid gone it settles to Disposed.
                lock (Gate) UpdateState(static () => null);
            };
        }, "ctx.Plugin()");
    }

    internal object Gate { get; }

    /// <summary>Incrementing identity; <c>null</c> once disposed.</summary>
    public int? Uid { get; private set; }

    /// <summary>The context this fiber was created from.</summary>
    public Context Parent { get; }

    /// <summary>The context handed to the plugin body.</summary>
    public Context Ctx { get; }

    /// <summary>The implementation behind this fiber. Changes when <see cref="RegistryService.Replace"/> moves it.</summary>
    public PluginRuntime? Runtime { get; private set; }

    /// <summary>
    /// The source config, as given to <c>ctx.Plugin()</c> or <see cref="UpdateAsync"/>. It is
    /// validated against the plugin's schema on every load, so it is never stale against a
    /// replaced schema.
    /// </summary>
    public object? Config { get; private set; }

    public FiberState State { get; private set; } = FiberState.Pending;

    public Exception? Error => _error;

    public IReadOnlyDictionary<string, object?> Inject => _inject;

    /// <summary>Injected providers committed by the current load; <c>null</c> while unloaded.</summary>
    internal Dictionary<string, ServiceImpl>? Committed { get; private set; }

    /// <summary>Services this fiber provides itself, including retired ones still draining. Guarded by <see cref="Gate"/>.</summary>
    internal HashSet<ServiceImpl> Provisions { get; } = [];

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
    /// <para>
    /// Once the effect is disposed or the fiber's epoch moves on (an update, a lost dependency),
    /// the iterator is not advanced again: the disposer it yields last is still collected, and the
    /// iterator is then closed, so its <c>finally</c> blocks run before the collected disposers.
    /// </para>
    /// </summary>
    public EffectHandle EffectMany(Func<IAsyncEnumerable<Disposer>> execute, string label = "anonymous")
    {
        return Run(label, Body);

        async Task Body(EffectHandle handle)
        {
            string epoch;
            lock (Gate) epoch = _epoch;
            await foreach (var disposer in execute().ConfigureAwait(false))
            {
                handle.Collect(disposer);
                if (!handle.IsAlive || EpochChanged(epoch)) break;
            }
        }
    }

    public EffectHandle Use(IDisposable resource, string? label = null)
        => EffectSync(() => () => { resource.Dispose(); return default; }, label ?? $"use({resource.GetType().Name})");

    public EffectHandle UseAsync(IAsyncDisposable resource, string? label = null)
        => EffectSync(() => resource.DisposeAsync, label ?? $"use({resource.GetType().Name})");

    internal EffectHandle EffectSync(Func<Disposer?> execute, string label)
        => Run(label, handle => { handle.Collect(execute()); return null; });

    /// <summary>
    /// Register a <see cref="ConfigUpdateHandler"/> on this fiber. It is an effect: it goes away
    /// when the fiber unloads, so a plugin that registers one on every load does not accumulate them.
    /// </summary>
    public EffectHandle OnConfigUpdate(ConfigUpdateHandler handler, bool prepend = false)
    {
        return EffectSync(() =>
        {
            var hook = new UpdateHook(handler);
            lock (Gate)
            {
                if (prepend) _updateHooks.Insert(0, hook); else _updateHooks.Add(hook);
            }
            return () =>
            {
                hook.Removed = true;
                lock (Gate) _updateHooks.Remove(hook);
                return default;
            };
        }, "ctx.OnConfigUpdate()");
    }

    /// <summary>Snapshot of the effects currently owned by this fiber.</summary>
    public IReadOnlyList<EffectHandle> Effects
    {
        get { lock (Gate) return [.. _effects]; }
    }

    private bool EpochChanged(string epoch)
    {
        lock (Gate) return _epoch != epoch;
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
            if (Committed is not null && Committed.TryGetValue(name, out impl!)) return true;
        }
        impl = default!;
        return false;
    }

    /// <summary>A service this fiber provides itself in the realm <paramref name="key"/>; the live binding wins.</summary>
    internal bool TryGetProvision(string name, IsolateKey key, out ServiceImpl impl)
    {
        lock (Gate)
        {
            ServiceImpl? found = null;
            foreach (var provision in Provisions)
            {
                if (provision.Name != name || !ReferenceEquals(provision.Isolate, key)) continue;
                found = provision;
                if (Ctx.Reflect.IsCurrent(provision)) break;
            }
            impl = found!;
            return found is not null;
        }
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
                impl.Ctx.Logger.Error(error);
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

    /// <summary>
    /// Recompute the epoch: the reload revision followed by the identity of every injected
    /// binding. A new binding, a lost one, or a restart all change it.
    /// </summary>
    internal void Refresh()
    {
        lock (Gate)
        {
            if (Uid is null) return;
            var builder = new StringBuilder().Append(_revision);
            foreach (var name in _inject.Keys)
            {
                if (!_candidates.TryGetValue(name, out var impl))
                {
                    SetEpoch(InactiveEpoch);
                    return;
                }
                builder.Append(':').Append(impl.Uid);
            }
            SetEpoch(builder.ToString());
        }
    }

    private void SetEpoch(string epoch)
    {
        lock (Gate)
        {
            var old = _epoch;
            if (epoch == old) return;
            // a failed fiber only recovers through UpdateAsync, which clears the error
            if (_error is not null) return;
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

        // only (de)activation is observable by dependents, and only the bindings this fiber
        // still holds in the store; each is notified in its own realm
        if (old != FiberState.Active && State != FiberState.Active) return;
        var provided = Provisions.Where(Ctx.Reflect.IsCurrent).ToArray();
        if (provided.Length > 0) Ctx.Reflect.Notify(provided);
    }

    private async Task ReloadAsync()
    {
        await Task.Yield(); // mirror cordis: the plugin body never runs inside ctx.Plugin()

        string epoch;
        lock (Gate)
        {
            Committed = new Dictionary<string, ServiceImpl>(_candidates, StringComparer.Ordinal);
            foreach (var impl in Committed.Values)
            {
                if (!ReferenceEquals(impl.Fiber, this)) impl.Dependents.Add(this);
            }
            epoch = _epoch;
        }

        try
        {
            // an update that arrived while this load was queued supersedes it
            if (!EpochChanged(epoch)) await ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Ctx.Logger.Error(error);
            lock (Gate)
            {
                // a superseded load still gets cleaned up, but cannot fail the newer target
                if (_epoch == epoch)
                {
                    _error = error;
                    _epoch = InactiveEpoch;
                }
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

        // Dependents were notified when this fiber left ACTIVE, but may still use its services
        // during their own teardown, so this fiber's effects wait until all of them have unloaded.
        while (true)
        {
            Task wait;
            lock (Gate)
            {
                if (!Provisions.Any(impl => impl.Dependents.Count > 0))
                {
                    _releaseSignal = null;
                    break;
                }
                _releaseSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _releaseSignal.Task;
            }
            await wait.ConfigureAwait(false);
        }

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
            // release the providers waiting for this fiber to unload
            foreach (var impl in Committed?.Values ?? Enumerable.Empty<ServiceImpl>())
            {
                if (!impl.Dependents.Remove(this) || impl.Dependents.Count > 0) continue;
                impl.Release?.TrySetResult();
                impl.Fiber.SignalRelease();
            }

            Committed = null;
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

    /// <summary>Wake an unload waiting for dependents. Caller holds <see cref="Gate"/>.</summary>
    private void SignalRelease()
    {
        var signal = _releaseSignal;
        _releaseSignal = null;
        signal?.TrySetResult();
    }

    private object? ResolveConfig(object? config)
    {
        var schema = Runtime?.Schema;
        return schema is not null ? schema.Validate(config) : config;
    }

    private async Task ExecuteAsync()
    {
        PluginRuntime runtime;
        object? source;
        lock (Gate)
        {
            runtime = Runtime!;
            source = Config;
        }

        // resolved on every load, so a reload after Replace validates with the new schema
        var resolved = ResolveConfig(source);
        var instance = await runtime.Apply(Ctx, resolved).ConfigureAwait(false);
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

    /// <summary>Move this fiber to <paramref name="runtime"/> in place. See <see cref="RegistryService.Replace"/>.</summary>
    internal void Replace(PluginRuntime runtime, Dictionary<string, object?> inject)
    {
        lock (Gate)
        {
            if (Uid is null) return;
            if (!ReferenceEquals(runtime, Runtime))
            {
                Runtime!.Detach(this);
                runtime.Attach(this);
                Runtime = runtime;
            }
            _inject = inject;
            Ctx.Internal.RaiseReplaced(this);

            _candidates.Clear();
            foreach (var name in _inject.Keys) CheckImpl(name);
            _error = null;
            _revision++;
            Refresh();
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

    /// <summary>
    /// Reload with the same config. Bumps the revision rather than going through an inactive
    /// epoch, so a restart issued during a transition is not lost. A failed fiber stays failed;
    /// use <see cref="UpdateAsync"/> to recover it.
    /// </summary>
    public async Task RestartAsync()
    {
        AssertActive();
        lock (Gate)
        {
            _revision++;
            Refresh();
        }
        await WaitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Replace the config and reload.
    /// <list type="bullet">
    /// <item>While the fiber is loaded, the new config is validated first: an invalid one throws
    /// and leaves the running plugin untouched. The handlers registered with
    /// <see cref="OnConfigUpdate"/> then see the validated config and may apply it in place.</item>
    /// <item>Unless <paramref name="noSave"/> is set, <see cref="InternalEvents.Committed"/> is raised
    /// with the config as given, which is what the loader writes back.</item>
    /// <item>A failed fiber is recovered: the error is cleared and the fiber loads again.</item>
    /// </list>
    /// </summary>
    public async Task UpdateAsync(object? config, bool noSave = false)
    {
        AssertActive();
        bool loaded;
        lock (Gate) loaded = _epoch != InactiveEpoch;
        var resolved = loaded ? ResolveConfig(config) : null;

        if (!noSave) Ctx.Internal.RaiseCommitted(this, config);

        UpdateHook[] hooks;
        lock (Gate)
        {
            Config = config;
            _error = null;
            hooks = loaded ? [.. _updateHooks] : [];
        }

        var index = 0;
        await Next().ConfigureAwait(false);

        ValueTask Next()
        {
            while (index < hooks.Length && hooks[index].Removed) index++;
            if (index >= hooks.Length) return new ValueTask(RestartAsync());
            var hook = hooks[index++];
            var called = 0;
            return hook.Handler(resolved, () =>
            {
                if (Interlocked.Exchange(ref called, 1) == 1) throw new CordisException("next() called multiple times");
                return Next();
            });
        }
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

    private sealed class UpdateHook(ConfigUpdateHandler handler)
    {
        private volatile bool _removed;

        public ConfigUpdateHandler Handler { get; } = handler;

        public bool Removed
        {
            get => _removed;
            set => _removed = value;
        }
    }
}
