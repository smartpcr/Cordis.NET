namespace Cordis;

/// <summary>A cleanup callback. The analogue of cordis' <c>Disposable</c>.</summary>
public delegate ValueTask Disposer();

/// <summary>
/// A unit of resource ownership attached to a <see cref="Fiber"/>. Disposing it releases every
/// resource collected during execution, in reverse order, including nested effects.
/// </summary>
public sealed class EffectHandle : IAsyncDisposable
{
    private readonly List<Disposer> _disposers = [];
    private readonly List<EffectHandle> _children = [];
    private Task? _pending;
    private bool _alive = true;

    internal EffectHandle(Fiber fiber, string label)
    {
        Fiber = fiber;
        Label = label;
    }

    public string Label { get; }

    public Fiber Fiber { get; }

    public bool IsAlive => _alive;

    /// <summary>Nested effects that were collected by this one.</summary>
    public IReadOnlyList<EffectHandle> Children => _children;

    internal void Collect(Disposer? disposer)
    {
        if (disposer is null) return;
        lock (Fiber.Gate)
        {
            if (!_alive)
            {
                _ = disposer();
                return;
            }
            _disposers.Add(disposer);
            // A disposer that *is* another handle's DisposeAsync means the child was adopted:
            // detach it from the fiber so it is not disposed twice.
            if (disposer.Target is EffectHandle child && disposer.Method.Name == nameof(DisposeAsync))
            {
                Fiber.Detach(child);
                _children.Add(child);
            }
        }
    }

    internal void SetPending(Task task) => _pending = task;

    /// <summary>Hands ownership of this effect to an enclosing effect.</summary>
    public Disposer ToDisposer() => DisposeAsync;

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (Fiber.Gate)
        {
            if (!_alive) return;
            _alive = false;
            pending = _pending;
        }

        Fiber.Detach(this);

        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch { /* already reported by the runner */ }
        }

        Disposer[] disposers;
        lock (Fiber.Gate)
        {
            disposers = [.. _disposers];
            _disposers.Clear();
        }

        for (var i = disposers.Length - 1; i >= 0; i--)
        {
            try { await disposers[i]().ConfigureAwait(false); }
            catch (Exception error) { Fiber.Ctx.Logger.Error(error); }
        }
    }

    public override string ToString() => $"Effect <{Label}>";
}
