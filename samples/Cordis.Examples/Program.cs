using Cordis.Examples;

// dotnet run --project samples/Cordis.Examples            -> every example
// dotnet run --project samples/Cordis.Examples -- events  -> one example

var examples = new (string Name, string Summary, Func<Task> Run)[]
{
    ("effects", "resource ownership and teardown order", EffectsExample.RunAsync),
    ("services", "dependents follow their provider", ServicesExample.RunAsync),
    ("events", "typed events in four dispatch modes", EventsExample.RunAsync),
    ("isolate", "private realms and intercepted config", IsolateExample.RunAsync),
    ("gate", "turn an external signal into a service", GateExample.RunAsync),
    ("config", "validation, failure, recovery and in-place updates", ConfigExample.RunAsync),
    ("replace", "swap a plugin's implementation in place", ReplaceExample.RunAsync),
    ("loader", "a declarative tree with isolate labels", LoaderExample.RunAsync),
};

var selected = args.Length == 0
    ? examples
    : examples.Where(example => args.Contains(example.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

if (selected.Length == 0)
{
    Console.WriteLine("unknown example; available:");
    foreach (var example in examples) Console.WriteLine($"  {example.Name,-10} {example.Summary}");
    return 1;
}

foreach (var example in selected)
{
    Console.WriteLine($"=== {example.Name}: {example.Summary}");
    await example.Run();
    Console.WriteLine();
}

return 0;
