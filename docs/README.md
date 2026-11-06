# Cordis.NET documentation

| Document | Read it when you want to |
| --- | --- |
| [Usage guide](usage.md) | build something: plugins, services, config, effects, events, isolation, the loader |
| [Design](design.md) | understand the model: why availability is a value, how fibers react, what the guarantees are |
| [Architecture](architecture.md) | see the components, the object graph, and the main flows end to end |
| [Implementation](implementation.md) | change the library: threading, the .NET constructs behind each concept, extension points |
| [Upstream sync record](upstream-sync.md) | port the next batch of changes from cordiverse/cordis |

Runnable code:

- [`samples/Cordis.Examples`](../samples/Cordis.Examples) - one small example per feature:
  `dotnet run --project samples/Cordis.Examples -- <name>` (no name runs them all).
- [`samples/Cordis.Sample`](../samples/Cordis.Sample) - an end-to-end demo: services, a plugin
  with config, live reconfiguration, and a watched JSON file.
- [`tests/Cordis.Tests`](../tests/Cordis.Tests) - the behavioural tests double as precise
  specifications of edge cases.
