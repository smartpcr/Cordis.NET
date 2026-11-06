using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Cordis.Loading;

/// <summary>
/// Backs a <see cref="LoaderService"/> with a JSON file: reads it, watches it, and writes
/// config changes back. Analogue of <c>@cordisjs/plugin-include</c>.
/// </summary>
[Inject("loader")]
public sealed class JsonConfigFile : IAsyncInit, IAsyncDisposable
{
    public sealed class Options
    {
        [Required]
        public string Path { get; set; } = "cordis.json";

        public bool Watch { get; set; } = true;

        /// <summary>Written out when the file does not exist yet.</summary>
        public List<EntryOptions>? Initial { get; set; }

        public int DebounceMs { get; set; } = 100;
    }

    public static readonly IConfigSchema Schema = DataAnnotationsSchema<Options>.Instance;

    private readonly Context _ctx;
    private readonly Options _config;
    private readonly LoaderService _loader;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private string _lastWritten = "";
    private CancellationTokenSource? _debounce;

    public JsonConfigFile(Context ctx, Options config)
    {
        _ctx = ctx;
        _config = config;
        _loader = ctx.Get<LoaderService>();
        FullPath = System.IO.Path.GetFullPath(config.Path);
    }

    public string FullPath { get; }

    public async ValueTask InitAsync()
    {
        _loader.ConfigChanged += OnConfigChanged;
        _ctx.Effect(() => () => _loader.ConfigChanged -= OnConfigChanged, "loader/config-changed");

        await ReloadAsync().ConfigureAwait(false);
        if (_config.Watch) StartWatching();
    }

    public async Task ReloadAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            List<EntryOptions> entries;
            var text = await ReadIfExistsAsync().ConfigureAwait(false);
            if (text is not null)
            {
                if (text == _lastWritten) return;
                entries = JsonSerializer.Deserialize<List<EntryOptions>>(text, LoaderService.JsonOptions) ?? [];
            }
            else
            {
                entries = _config.Initial ?? [];
                await WriteAsync(entries).ConfigureAwait(false);
            }

            await _loader.ApplyAsync(entries).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Only a file that does not exist counts as missing. Any other read failure (access denied,
    /// a sharing violation) propagates, so the initial entries never overwrite a file that exists.
    /// </summary>
    private async Task<string?> ReadIfExistsAsync()
    {
        try
        {
            return await File.ReadAllTextAsync(FullPath).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task SaveAsync() => await WriteAsync(_loader.Snapshot()).ConfigureAwait(false);

    private async Task WriteAsync(IReadOnlyList<EntryOptions> entries)
    {
        var text = JsonSerializer.Serialize(entries, LoaderService.JsonOptions);
        _lastWritten = text;
        await File.WriteAllTextAsync(FullPath, text).ConfigureAwait(false);
    }

    private void StartWatching()
    {
        _ctx.Effect(() =>
        {
            var directory = System.IO.Path.GetDirectoryName(FullPath);
            var watcher = new FileSystemWatcher(
                string.IsNullOrEmpty(directory) ? "." : directory,
                System.IO.Path.GetFileName(FullPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            return watcher.Dispose;
        }, "loader/watch");
    }

    private void OnFileEvent(object sender, FileSystemEventArgs args)
    {
        var previous = Interlocked.Exchange(ref _debounce, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var token = _debounce!.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_config.DebounceMs, token).ConfigureAwait(false);
                await ReloadAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _ctx.Logger.Error(error); }
        }, CancellationToken.None);
    }

    private void OnConfigChanged() => _ = SaveAsync();

    public async ValueTask DisposeAsync()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _mutex.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
