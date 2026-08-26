using System.Collections.Concurrent;
using System.Reflection;

namespace Cordis;

/// <summary>Reference-identity token. The C# analogue of <c>Symbol(name)</c>.</summary>
public sealed class IsolateKey(string name)
{
    public string Name { get; } = name;
    public override string ToString() => $"Symbol({Name})";
}

/// <summary>
/// A dictionary that falls through to its parent on lookup and writes locally.
/// The C# analogue of a JavaScript prototype chain (<c>Object.create(parent)</c>).
/// </summary>
public sealed class ChainMap<TValue>(ChainMap<TValue>? parent = null)
{
    private readonly Dictionary<string, TValue> _own = new(StringComparer.Ordinal);

    public ChainMap<TValue>? Parent { get; } = parent;

    public bool TryGet(string key, out TValue value)
    {
        for (var map = this; map is not null; map = map.Parent)
        {
            if (map._own.TryGetValue(key, out value!)) return true;
        }
        value = default!;
        return false;
    }

    public TValue? Get(string key) => TryGet(key, out var value) ? value : default;

    public bool HasOwn(string key) => _own.ContainsKey(key);

    public void Set(string key, TValue value) => _own[key] = value;

    /// <summary>All values bound to <paramref name="key"/>, nearest first.</summary>
    public IEnumerable<TValue> Chain(string key)
    {
        for (var map = this; map is not null; map = map.Parent)
        {
            if (map._own.TryGetValue(key, out var value)) yield return value;
        }
    }

    public ChainMap<TValue> Extend() => new(this);
}

public class CordisException(string message) : Exception(message);

public sealed class InactiveFiberException()
    : CordisException("cannot create effect on inactive context");

public sealed class ServiceNotInjectedException(string name)
    : CordisException($"cannot access service \"{name}\" without declaring it in inject");

public sealed class ServiceUnavailableException(string name)
    : CordisException($"service \"{name}\" is not available in this context");

/// <summary>Post-construction hook. The analogue of cordis' <c>Service.init</c>.</summary>
public interface IAsyncInit
{
    ValueTask InitAsync();
}

/// <summary>
/// Opt-in caller attribution. Replaces cordis' traceable <c>Proxy</c>: a service returns a
/// shallow clone bound to the calling context so effects it creates are owned by the caller.
/// </summary>
public interface IContextScoped
{
    object ForContext(Context ctx);
}

/// <summary>Overrides the string key a type is registered/resolved under.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class ServiceNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Declares a dependency of a plugin type. Analogue of <c>static inject = [...]</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class InjectAttribute : Attribute
{
    public InjectAttribute(string name) => Name = name;
    public InjectAttribute(Type serviceType) => Name = ServiceNames.For(serviceType);

    public string Name { get; }

    /// <summary>Optional per-dependency interception config pushed onto the child context.</summary>
    public object? Config { get; set; }
}

public static class ServiceNames
{
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    public static string For<T>() => For(typeof(T));

    public static string For(Type type) => Cache.GetOrAdd(type, static t =>
        t.GetCustomAttribute<ServiceNameAttribute>(inherit: false)?.Name ?? t.FullName ?? t.Name);
}
