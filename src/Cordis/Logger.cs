namespace Cordis;

public enum LogLevel
{
    Silent = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
}

public sealed record LogMessage(DateTimeOffset Timestamp, string Name, LogLevel Level, string Text, Exception? Error);

public interface ILogExporter
{
    void Export(LogMessage message);
}

public sealed class ConsoleExporter : ILogExporter
{
    public void Export(LogMessage message)
    {
        var writer = message.Level <= LogLevel.Warn ? Console.Error : Console.Out;
        writer.WriteLine($"{message.Timestamp:HH:mm:ss} [{message.Level.ToString()[0]}] {message.Name} {message.Text}");
        if (message.Error is not null) writer.WriteLine(message.Error);
    }
}

/// <summary>Collects messages in memory. Useful in tests.</summary>
public sealed class MemoryExporter : ILogExporter
{
    private readonly List<LogMessage> _messages = [];

    public IReadOnlyList<LogMessage> Messages
    {
        get { lock (_messages) return [.. _messages]; }
    }

    public void Export(LogMessage message)
    {
        lock (_messages) _messages.Add(message);
    }
}

public sealed class LoggerConfig
{
    public LogLevel Level { get; set; } = LogLevel.Info;
}

public sealed class LoggerService
{
    public const string ServiceName = "logger";

    private readonly List<ILogExporter> _exporters = [new ConsoleExporter()];

    public LogLevel Level { get; set; } = LogLevel.Info;

    public IReadOnlyList<ILogExporter> Exporters
    {
        get { lock (_exporters) return [.. _exporters]; }
    }

    public IDisposable AddExporter(ILogExporter exporter)
    {
        lock (_exporters) _exporters.Add(exporter);
        return new Removal(this, exporter);
    }

    public void ClearExporters()
    {
        lock (_exporters) _exporters.Clear();
    }

    public Logger For(Context ctx) => new(this, ctx, ctx.Fiber.Name);

    public Logger For(Context ctx, string name) => new(this, ctx, name);

    internal void Export(LogMessage message)
    {
        foreach (var exporter in Exporters)
        {
            try { exporter.Export(message); }
            catch { /* an exporter must never break the caller */ }
        }
    }

    private sealed class Removal(LoggerService service, ILogExporter exporter) : IDisposable
    {
        public void Dispose()
        {
            lock (service._exporters) service._exporters.Remove(exporter);
        }
    }
}

/// <summary>A named logger bound to a context; the level comes from the context's intercept chain.</summary>
public sealed class Logger(LoggerService service, Context ctx, string name)
{
    public string Name => name;

    private LogLevel Threshold =>
        ctx.GetInterceptConfig<LoggerConfig>(LoggerService.ServiceName)?.Level ?? service.Level;

    public void Error(Exception error) => Write(LogLevel.Error, error.Message, error);

    public void Error(string text) => Write(LogLevel.Error, text, null);

    public void Warn(string text) => Write(LogLevel.Warn, text, null);

    public void Info(string text) => Write(LogLevel.Info, text, null);

    public void Debug(string text) => Write(LogLevel.Debug, text, null);

    private void Write(LogLevel level, string text, Exception? error)
    {
        if (level > Threshold) return;
        service.Export(new LogMessage(DateTimeOffset.Now, name, level, text, error));
    }
}
