namespace Cordis;

/// <summary>
/// Base class for a plugin that publishes itself as a named service. The instance registers in
/// its own fiber's store, so unloading the plugin removes the service and reloads every dependent.
/// </summary>
public abstract class Service : IContextScoped
{
    protected Service(Context ctx, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(name);
        Ctx = ctx;
        Name = name;
        Ctx.Provide(name, this, CheckAvailable);
    }

    /// <summary>
    /// Registers under the name of the concrete service type: its <see cref="ServiceNameAttribute"/>,
    /// or its full type name. This is the name <c>ctx.Get&lt;T&gt;()</c> and <c>[Inject(typeof(T))]</c>
    /// look up.
    /// </summary>
    protected Service(Context ctx)
    {
        // A constructor initializer cannot see `this`, so the runtime type is read here instead.
        ArgumentNullException.ThrowIfNull(ctx);
        Ctx = ctx;
        Name = ServiceNames.For(GetType());
        Ctx.Provide(Name, this, CheckAvailable);
    }

    /// <summary>The context this instance is bound to. Rebound per caller by <see cref="ForContext"/>.</summary>
    protected Context Ctx { get; private set; }

    public string Name { get; }

    /// <summary>Dynamic availability check; a false result unloads dependents.</summary>
    protected virtual bool CheckAvailable() => true;

    /// <summary>
    /// Opt in to per-caller cloning. Only safe for stateless facades: the clone is shallow, so
    /// any field the service mutates after cloning (including event subscriptions) is a copy and
    /// will not be seen by other callers. Default is <c>false</c> — one shared instance.
    /// </summary>
    protected virtual bool ScopePerCaller => false;

    /// <summary>
    /// Shallow clone bound to the caller's context, so effects and log lines created inside
    /// service methods are attributed to the caller's fiber. Mirrors <c>Object.create(this)</c>.
    /// </summary>
    public virtual object ForContext(Context ctx)
    {
        if (!ScopePerCaller || ReferenceEquals(ctx, Ctx)) return this;
        var clone = (Service)MemberwiseClone();
        clone.Ctx = ctx;
        return clone;
    }
}

/// <summary>Convenience base that derives the service name from <typeparamref name="TSelf"/>.</summary>
public abstract class Service<TSelf>(Context ctx) : Service(ctx, ServiceNames.For<TSelf>())
    where TSelf : Service<TSelf>;
