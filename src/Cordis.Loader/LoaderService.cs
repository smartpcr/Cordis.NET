using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Loading;

/// <summary>Declarative description of one plugin instance in the config file.</summary>
public sealed class EntryOptions
{
    public string Id { get; set; } = "";

    /// <summary>Plugin identifier handed to the <see cref="IPluginResolver"/>.</summary>
    public string Name { get; set; } = "";

    public JsonElement? Config { get; set; }

    /// <summary>A group is a context node: it loads no plugin but scopes its children.</summary>
    public bool Group { get; set; }

    public bool Disabled { get; set; }

    public string[]? Inject { get; set; }

    /// <summary>
    /// Service name to realm label. Entries with the same label share a realm; <c>true</c> (or the
    /// string <c>"true"</c>) gives the entry a realm of its own. Any string, including the empty
    /// string, is a label.
    /// </summary>
    [JsonConverter(typeof(IsolateMapConverter))]
    public Dictionary<string, string>? Isolate { get; set; }

    /// <summary>Separator of nested entry ids; an id must not contain it.</summary>
    public const char Separator = ':';

    public List<EntryOptions>? Children { get; set; }

    public EntryOptions Clone() => new()
    {
        Id = Id,
        Name = Name,
        Config = Config,
        Group = Group,
        Disabled = Disabled,
        Inject = Inject?.ToArray(),
        Isolate = Isolate is null ? null : new Dictionary<string, string>(Isolate),
        Children = Children?.Select(child => child.Clone()).ToList(),
    };

    public bool SameShape(EntryOptions other)
        => Name == other.Name
        && Group == other.Group
        && Disabled == other.Disabled
        && (Inject ?? []).SequenceEqual(other.Inject ?? [])
        && IsolateEquals(Isolate, other.Isolate);

    private static bool IsolateEquals(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        if (left is null || left.Count == 0) return right is null || right.Count == 0;
        if (right is null || left.Count != right.Count) return false;
        return left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}

public interface IPluginResolver
{
    Type? Resolve(string name);
}

/// <summary>Resolves assembly-qualified or short type names against the loaded assemblies.</summary>
public sealed class TypePluginResolver : IPluginResolver
{
    private readonly List<Assembly> _assemblies = [];

    public TypePluginResolver(params Assembly[] assemblies) => _assemblies.AddRange(assemblies);

    public IPluginResolver Add(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    public Type? Resolve(string name)
    {
        var type = Type.GetType(name, throwOnError: false);
        if (type is not null) return type;

        var candidates = _assemblies.Count > 0 ? _assemblies : [.. AppDomain.CurrentDomain.GetAssemblies()];
        foreach (var assembly in candidates)
        {
            type = assembly.GetType(name, throwOnError: false);
            if (type is not null) return type;
            type = assembly.GetTypes().FirstOrDefault(candidate => candidate.Name == name);
            if (type is not null) return type;
        }
        return null;
    }
}

public sealed class Entry(string id)
{
    public string Id { get; } = id;

    public EntryOptions Options { get; internal set; } = new() { Id = id };

    public Fiber? Fiber { get; internal set; }

    /// <summary>Context children of a group entry are created on.</summary>
    public Context? Scope { get; internal set; }

    public bool IsActive => Fiber is { State: FiberState.Active };

    public override string ToString() => $"Entry {Id} ({Options.Name})";
}

/// <summary>
/// Turns a declarative entry list into a live plugin tree and keeps them in sync.
/// Analogue of <c>@cordisjs/plugin-loader</c>.
/// </summary>
[ServiceName("loader")]
public sealed class LoaderService : Service
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Fiber, Entry> _byFiber = [];
    private readonly Dictionary<(bool Private, string Label), IsolateKey> _realms = [];
    private List<EntryOptions> _current = [];

    // Serialises structural edits. Each reconciliation reads the live table, awaits plugin
    // disposal and updates in between, and writes the table back, so two of them must not
    // interleave. Held only while reconciling, not while waiting for plugins to settle.
    private readonly SemaphoreSlim _edits = new(1, 1);

    public LoaderService(Context ctx) : base(ctx, "loader")
    {
        Resolver = new TypePluginResolver();
        ctx.Internal.Committed += OnCommitted;
        ctx.Effect(() => () => ctx.Internal.Committed -= OnCommitted, "loader/committed");
    }

    public IPluginResolver Resolver { get; set; }

    /// <summary>Raised after a fiber writes its config back, so a file source can persist it.</summary>
    public event Action? ConfigChanged;

    public IReadOnlyCollection<Entry> Entries
    {
        get { lock (_entries) return [.. _entries.Values]; }
    }

    public Entry? Find(string id)
    {
        lock (_entries) return _entries.GetValueOrDefault(id);
    }

    /// <summary>Current entry tree, as it would be written back to disk.</summary>
    public IReadOnlyList<EntryOptions> Snapshot()
    {
        lock (_entries) return _current.Select(options => options.Clone()).ToList();
    }

    /// <summary>
    /// Diff the desired tree against the live one and reconcile, then wait for the entries to
    /// settle. Safe to call concurrently: edits are applied one at a time. Do not call it from the
    /// load path of a plugin this loader manages; the edit in progress waits for that load.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<EntryOptions> desired)
    {
        await _edits.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReconcileAsync(desired).ConfigureAwait(false);
        }
        finally
        {
            _edits.Release();
        }
        await WaitAllAsync().ConfigureAwait(false);
    }

    /// <summary>Reconcile the live entries with <paramref name="desired"/>. Caller holds <see cref="_edits"/>.</summary>
    private async Task ReconcileAsync(IReadOnlyList<EntryOptions> desired)
    {
        var flattened = new Dictionary<string, (EntryOptions Options, string? ParentId)>(StringComparer.Ordinal);
        Flatten(desired, null, flattened);

        string[] removed;
        lock (_entries)
        {
            removed = [.. _entries.Keys.Where(id => !flattened.ContainsKey(id))];
        }

        foreach (var id in removed) await RemoveAsync(id).ConfigureAwait(false);

        foreach (var (id, item) in flattened.OrderBy(pair => Depth(pair.Value.ParentId)))
        {
            await UpsertAsync(id, item.Options, item.ParentId).ConfigureAwait(false);
        }

        lock (_entries) _current = desired.Select(options => options.Clone()).ToList();

        int Depth(string? parentId)
        {
            var depth = 0;
            while (parentId is not null && flattened.TryGetValue(parentId, out var parent))
            {
                depth++;
                parentId = parent.ParentId;
            }
            return depth;
        }
    }

    public async Task WaitAllAsync()
    {
        foreach (var entry in Entries)
        {
            if (entry.Fiber is null) continue;
            try { await entry.Fiber.WaitAsync().ConfigureAwait(false); }
            catch (Exception error) { Ctx.Logger.Error(error); }
        }
    }

    /// <summary>Enable or disable one entry. Safe to call concurrently with other edits.</summary>
    public async Task SetDisabledAsync(string id, bool disabled)
    {
        await _edits.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = Find(id) ?? throw new CordisException($"unknown entry \"{id}\"");
            var options = entry.Options.Clone();
            options.Disabled = disabled;
            List<EntryOptions> desired;
            lock (_entries)
            {
                Patch(_current, id, options);
                desired = _current.Select(item => item.Clone()).ToList();
            }
            await ReconcileAsync(desired).ConfigureAwait(false);
        }
        finally
        {
            _edits.Release();
        }
        await WaitAllAsync().ConfigureAwait(false);
        ConfigChanged?.Invoke();
    }

    // ---------------------------------------------------------------- internals

    private static void Flatten(
        IReadOnlyList<EntryOptions> entries,
        string? parentId,
        Dictionary<string, (EntryOptions, string?)> sink)
    {
        foreach (var options in entries)
        {
            if (string.IsNullOrEmpty(options.Id))
            {
                options.Id = Guid.NewGuid().ToString("n")[..8];
            }
            else if (options.Id.Contains(EntryOptions.Separator))
            {
                // such an id could never be found again by its path
                throw new CordisException($"entry id must not contain \"{EntryOptions.Separator}\": {options.Id}");
            }
            var id = parentId is null ? options.Id : $"{parentId}{EntryOptions.Separator}{options.Id}";
            sink[id] = (options, parentId);
            if (options.Children is { Count: > 0 }) Flatten(options.Children, id, sink);
        }
    }

    private static bool Patch(List<EntryOptions> entries, string id, EntryOptions replacement)
    {
        var segments = id.Split(EntryOptions.Separator);
        var cursor = entries;
        for (var i = 0; i < segments.Length; i++)
        {
            var index = cursor.FindIndex(options => options.Id == segments[i]);
            if (index < 0) return false;
            if (i == segments.Length - 1)
            {
                replacement.Children = cursor[index].Children;
                cursor[index] = replacement;
                return true;
            }
            cursor = cursor[index].Children ?? [];
        }
        return false;
    }

    private async Task UpsertAsync(string id, EntryOptions options, string? parentId)
    {
        Entry? entry;
        lock (_entries) entry = _entries.GetValueOrDefault(id);

        if (entry is not null && entry.Options.SameShape(options))
        {
            var configChanged = !JsonEquals(entry.Options.Config, options.Config);
            entry.Options = options.Clone();
            if (configChanged && entry.Fiber is not null && !options.Group)
            {
                var type = Resolve(options.Name);
                await entry.Fiber.UpdateAsync(BindConfig(type, options.Config), noSave: true).ConfigureAwait(false);
            }
            return;
        }

        if (entry is not null) await RemoveAsync(id).ConfigureAwait(false);

        entry = new Entry(id) { Options = options.Clone() };
        lock (_entries) _entries[id] = entry;

        if (options.Disabled) return;

        var scope = ScopeOf(parentId);
        foreach (var (name, realm) in options.Isolate ?? [])
        {
            scope = scope.Isolate(name, RealmKey(realm, id));
        }

        if (options.Group)
        {
            entry.Fiber = scope.Apply($"group:{options.Id}", _ => { }, options.Inject ?? []);
        }
        else
        {
            var type = Resolve(options.Name);
            entry.Fiber = scope.Plugin(type, BindConfig(type, options.Config), options.Inject);
        }

        entry.Scope = entry.Fiber.Ctx;
        lock (_entries) _byFiber[entry.Fiber] = entry;
    }

    private async Task RemoveAsync(string id)
    {
        Entry? entry;
        lock (_entries)
        {
            if (!_entries.Remove(id, out entry)) return;
            if (entry.Fiber is not null) _byFiber.Remove(entry.Fiber);
        }
        if (entry.Fiber is not null) await entry.Fiber.DisposeAsync().ConfigureAwait(false);
    }

    private Context ScopeOf(string? parentId)
    {
        if (parentId is null) return Ctx;
        var parent = Find(parentId);
        return parent?.Scope ?? Ctx;
    }

    private IsolateKey RealmKey(string realm, string entryId)
    {
        // "true" means a realm private to this entry; every other string, "" included, is a shared
        // label. The two live in separate key spaces, so no label can collide with a private realm.
        var slot = realm == IsolateMapConverter.Private ? (true, entryId) : (false, realm);
        lock (_realms)
        {
            if (!_realms.TryGetValue(slot, out var key))
            {
                _realms[slot] = key = new IsolateKey(slot.Item1 ? $"{entryId}#private" : realm);
            }
            return key;
        }
    }

    private Type Resolve(string name)
        => Resolver.Resolve(name) ?? throw new CordisException($"cannot resolve plugin \"{name}\"");

    /// <summary>Persist a config committed through <see cref="Fiber.UpdateAsync"/>, as the caller gave it.</summary>
    private void OnCommitted(Fiber fiber, object? config)
    {
        Entry? entry;
        lock (_entries)
        {
            if (!_byFiber.TryGetValue(fiber, out entry)) return;
        }
        var schema = fiber.Runtime?.Schema;
        var simplified = schema is null ? config : schema.Simplify(config);
        var options = entry.Options.Clone();
        options.Config = simplified is null
            ? null
            : JsonSerializer.SerializeToElement(simplified, simplified.GetType(), JsonOptions);
        lock (_entries)
        {
            entry.Options = options;
            Patch(_current, entry.Id, options.Clone());
        }
        ConfigChanged?.Invoke();
    }

    private static bool JsonEquals(JsonElement? left, JsonElement? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Value.GetRawText() == right.Value.GetRawText();
    }

    /// <summary>
    /// Binds raw JSON to the plugin's config type, discovered from its static
    /// <c>Schema</c> property or from the second constructor parameter.
    /// </summary>
    internal static object? BindConfig(Type pluginType, JsonElement? json)
    {
        if (json is null) return null;
        var configType = ConfigTypeOf(pluginType);
        return configType is null ? null : json.Value.Deserialize(configType, JsonOptions);
    }

    internal static Type? ConfigTypeOf(Type pluginType)
    {
        var property = pluginType.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property?.GetValue(null) is IConfigSchema fromProperty) return fromProperty.ConfigType;

        var field = pluginType.GetField("Schema", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field?.GetValue(null) is IConfigSchema fromField) return fromField.ConfigType;

        var ctor = pluginType.GetConstructors()
            .FirstOrDefault(candidate => candidate.GetParameters() is { Length: 2 } parameters
                && parameters[0].ParameterType == typeof(Context));
        return ctor?.GetParameters()[1].ParameterType;
    }
}

/// <summary>
/// Reads an isolate map whose values are a realm label (any string) or <c>true</c> for a private
/// realm, and writes <c>true</c> back for the latter.
/// </summary>
public sealed class IsolateMapConverter : JsonConverter<Dictionary<string, string>>
{
    public const string Private = "true";

    public override Dictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("isolate must be an object");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            result[name] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString()!,
                JsonTokenType.True => Private,
                _ => throw new JsonException($"isolate.{name} must be a label or true"),
            };
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, label) in value)
        {
            if (label == Private) writer.WriteBoolean(name, true);
            else writer.WriteString(name, label);
        }
        writer.WriteEndObject();
    }
}
