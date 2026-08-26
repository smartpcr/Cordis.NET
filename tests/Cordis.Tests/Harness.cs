namespace Cordis.Tests;

public static class Harness
{
    private static readonly List<(string Name, Func<Task> Body)> Cases = [];

    public static void Case(string name, Func<Task> body) => Cases.Add((name, body));

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        foreach (var (name, body) in Cases)
        {
            try
            {
                await body().WaitAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {error.GetType().Name}: {error.Message}");
                if (error.StackTrace is { } stack)
                {
                    Console.WriteLine(string.Join('\n', stack.Split('\n').Take(3).Select(line => "        " + line.Trim())));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? $"{Cases.Count} passed" : $"{Cases.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"expected <{expected}>, got <{actual}>");
        }
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> body) where TException : Exception
    {
        try
        {
            await body();
        }
        catch (TException error)
        {
            return error;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"expected {typeof(TException).Name}, got {error.GetType().Name}");
        }
        throw new InvalidOperationException($"expected {typeof(TException).Name}, nothing thrown");
    }

    /// <summary>Spin until a condition holds, so tests never depend on a fixed sleep.</summary>
    public static async Task Eventually(Func<bool> condition, string message = "condition never held", int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        throw new InvalidOperationException(message);
    }
}
