using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Cordis;

public interface IPlugin
{
    string? PluginName => null;

    IEnumerable<string> Inject => [];

    IConfigSchema? ConfigSchema => null;

    ValueTask ApplyAsync(Context ctx);
}

public interface IPlugin<in TConfig> : IPlugin
{
    ValueTask ApplyAsync(Context ctx, TConfig config);

    ValueTask IPlugin.ApplyAsync(Context ctx) => ApplyAsync(ctx, default!);
}

/// <summary>
/// A plugin registration. One runtime per plugin identity (type, instance or delegate);
/// one <see cref="Fiber"/> per load.
/// </summary>
public sealed class PluginRuntime(
    object key,
    string name,
    Func<Context, object?, ValueTask<object?>> apply,
    IConfigSchema? schema)
{
    private readonly List<Fiber> _fibers = [];

    public object Key { get; } = key;

    public string Name { get; } = name;

    public IConfigSchema? Schema { get; } = schema;

    internal Func<Context, object?, ValueTask<object?>> Apply { get; } = apply;

    public IReadOnlyList<Fiber> Fibers
    {
        get { lock (_fibers) return [.. _fibers]; }
    }

    public int FiberCount
    {
        get { lock (_fibers) return _fibers.Count; }
    }

    internal void Attach(Fiber fiber) { lock (_fibers) _fibers.Add(fiber); }

    internal void Detach(Fiber fiber) { lock (_fibers) _fibers.Remove(fiber); }
}

public sealed class RegistryService(Context root)
{
    private readonly Dictionary<object, PluginRuntime> _runtimes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _aliases = new(ReferenceEqualityComparer.Instance);
    private int _counter;

    internal int NextUid() => Interlocked.Increment(ref _counter);

    public int Count
    {
        get { lock (root.SyncRoot) return _runtimes.Count; }
    }

    public IReadOnlyList<PluginRuntime> Runtimes
    {
        get { lock (root.SyncRoot) return [.. _runtimes.Values]; }
    }

    public bool Has(object key)
    {
        lock (root.SyncRoot) return _runtimes.ContainsKey(Forward(key));
    }

    public PluginRuntime? Get(object key)
    {
        lock (root.SyncRoot) return _runtimes.GetValueOrDefault(Forward(key));
    }

    /// <summary>Follow the replacements recorded by <see cref="Replace"/> to the current plugin.</summary>
    public object Forward(object plugin)
    {
        lock (root.SyncRoot)
        {
            while (_aliases.TryGetValue(plugin, out var next)) plugin = next;
            return plugin;
        }
    }

    internal PluginRuntime GetOrAdd(object key, Func<PluginRuntime> factory)
    {
        lock (root.SyncRoot)
        {
            if (_runtimes.TryGetValue(key, out var runtime)) return runtime;
            runtime = factory();
            _runtimes[key] = runtime;
            return runtime;
        }
    }

    /// <summary>Drop a runtime whose last fiber is gone, unless its key has been taken over since.</summary>
    internal void Unregister(PluginRuntime runtime)
    {
        lock (root.SyncRoot)
        {
            if (_runtimes.TryGetValue(runtime.Key, out var current) && ReferenceEquals(current, runtime))
            {
                _runtimes.Remove(runtime.Key);
            }
        }
    }

    /// <summary>Unload every fiber of a plugin. Analogue of <c>registry.delete()</c>.</summary>
    public async Task RemoveAsync(object key)
    {
        PluginRuntime? runtime;
        lock (root.SyncRoot)
        {
            if (!_runtimes.Remove(Forward(key), out runtime)) return;
        }
        foreach (var fiber in runtime.Fibers) await fiber.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Swap the implementation behind every fiber of <paramref name="oldPlugin"/> in place: each
    /// fiber keeps its parent, its config and its identity, takes the dependencies of
    /// <paramref name="newPlugin"/>, and reloads (validating its config with the new schema).
    /// <paramref name="oldPlugin"/> is forwarded to <paramref name="newPlugin"/> from now on, so a
    /// later <c>ctx.Plugin(oldPlugin)</c> creates a fiber of the new one. Plugins are a
    /// <see cref="Type"/> or an <see cref="IPlugin"/> instance. Analogue of <c>registry.replace()</c>.
    /// </summary>
    /// <returns>The fibers that were moved.</returns>
    public IReadOnlyList<Fiber> Replace(object oldPlugin, object newPlugin)
    {
        PluginFactory.EnsurePlugin(oldPlugin);
        PluginFactory.EnsurePlugin(newPlugin);

        lock (root.SyncRoot)
        {
            var oldKey = Forward(oldPlugin);

            // the new plugin is an implementation of its own again
            _aliases.Remove(newPlugin);
            _runtimes.TryGetValue(oldKey, out var runtime);
            _runtimes.TryGetValue(newPlugin, out var target);
            if (!ReferenceEquals(oldKey, newPlugin))
            {
                _runtimes.Remove(oldKey);
                _aliases[oldKey] = newPlugin;
            }

            IReadOnlyList<Fiber> fibers = runtime?.Fibers ?? [];
            if (fibers.Count == 0) return fibers;

            if (target is null)
            {
                target = PluginFactory.ForKey(newPlugin);
                _runtimes[newPlugin] = target;
            }

            foreach (var fiber in fibers) fiber.Replace(target, PluginFactory.InjectFor(newPlugin));
            return fibers;
        }
    }
}

internal static class PluginFactory
{
    /// <summary>Runtime for a <see cref="Type"/> or <see cref="IPlugin"/> key.</summary>
    public static PluginRuntime ForKey(object key) => key switch
    {
        Type type => ForType(type),
        IPlugin plugin => ForInstance(plugin),
        _ => throw InvalidPlugin(key),
    };

    /// <summary>Dependencies of a <see cref="Type"/> or <see cref="IPlugin"/> key.</summary>
    public static Dictionary<string, object?> InjectFor(object key)
    {
        switch (key)
        {
            case Type type:
                return InjectOf(type);
            case IPlugin plugin:
                var inject = InjectOf(plugin.Inject);
                foreach (var (name, value) in InjectOf(plugin.GetType())) inject[name] = value;
                return inject;
            default:
                throw InvalidPlugin(key);
        }
    }

    public static void EnsurePlugin(object key)
    {
        if (key is not (Type or IPlugin)) throw InvalidPlugin(key);
    }

    private static CordisException InvalidPlugin(object? key)
        => new($"invalid plugin, expect a Type or an IPlugin, received {key?.GetType().Name ?? "null"}");

    public static PluginRuntime ForType(Type type)
    {
        var schema = FindSchema(type);
        var name = type.Name;
        return new PluginRuntime(type, name, (ctx, config) =>
        {
            var ctor = SelectConstructor(type, config is not null);
            var parameters = ctor.GetParameters();
            var args = parameters.Length switch
            {
                0 => [],
                1 => [ctx],
                _ => new object?[] { ctx, config ?? DefaultFor(parameters[1]) },
            };
            try
            {
                return new ValueTask<object?>(ctor.Invoke(args));
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }, schema);
    }

    public static PluginRuntime ForInstance(IPlugin plugin)
    {
        var name = plugin.PluginName ?? plugin.GetType().Name;
        return new PluginRuntime(plugin, name, async (ctx, config) =>
        {
            await InvokeAsync(plugin, ctx, config).ConfigureAwait(false);
            return null;
        }, plugin.ConfigSchema);
    }

    public static PluginRuntime ForDelegate(object key, string name, Func<Context, ValueTask> apply)
        => new(key, name, async (ctx, _) =>
        {
            await apply(ctx).ConfigureAwait(false);
            return null;
        }, null);

    private static ValueTask InvokeAsync(IPlugin plugin, Context ctx, object? config)
    {
        var closed = plugin.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPlugin<>));
        if (closed is null || config is null) return plugin.ApplyAsync(ctx);

        var method = closed.GetMethod(nameof(IPlugin<object>.ApplyAsync), [typeof(Context), closed.GetGenericArguments()[0]])!;
        try
        {
            return (ValueTask)method.Invoke(plugin, [ctx, config])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    /// <summary>Reads <c>[Inject]</c> attributes declared on a plugin type (inherited).</summary>
    public static Dictionary<string, object?> InjectOf(Type type)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in type.GetCustomAttributes<InjectAttribute>(inherit: true))
        {
            result[attribute.Name] = attribute.Config;
        }
        return result;
    }

    public static Dictionary<string, object?> InjectOf(IEnumerable<string> names)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in names) result[name] = null;
        return result;
    }

    /// <summary>Looks for <c>public static IConfigSchema Schema { get; }</c> on the plugin type.</summary>
    private static IConfigSchema? FindSchema(Type type)
    {
        var property = type.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property is not null && typeof(IConfigSchema).IsAssignableFrom(property.PropertyType))
        {
            return (IConfigSchema?)property.GetValue(null);
        }
        var field = type.GetField("Schema", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field is not null && typeof(IConfigSchema).IsAssignableFrom(field.FieldType))
        {
            return (IConfigSchema?)field.GetValue(null);
        }
        return null;
    }

    private static ConstructorInfo SelectConstructor(Type type, bool hasConfig)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var withConfig = ctors.FirstOrDefault(c =>
            c.GetParameters().Length == 2 && c.GetParameters()[0].ParameterType == typeof(Context));
        var ctxOnly = ctors.FirstOrDefault(c =>
            c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(Context));

        var selected = hasConfig ? withConfig ?? ctxOnly : ctxOnly ?? withConfig;
        selected ??= ctors.FirstOrDefault(c => c.GetParameters().Length == 0);

        return selected ?? throw new CordisException(
            $"plugin type {type.Name} needs a constructor taking (Context) or (Context, TConfig)");
    }

    private static object? DefaultFor(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        var type = parameter.ParameterType;
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.GetConstructor(Type.EmptyTypes) is not null) return Activator.CreateInstance(type);
        return null;
    }
}
