using Timer = System.Threading.Timer;

namespace Cordis.Timers;

/// <summary>
/// Timers whose lifetime is bound to the <em>caller's</em> fiber, not to the timer plugin's.
/// This is what <see cref="IContextScoped"/> buys: <c>ctx.Get&lt;TimerService&gt;()</c> hands back a
/// clone bound to <c>ctx</c>, so every effect below lands on the caller.
/// </summary>
[ServiceName("timer")]
public sealed class TimerService(Context ctx) : Service(ctx, "timer")
{
    /// <summary>Stateless facade: safe to hand each caller its own bound clone.</summary>
    protected override bool ScopePerCaller => true;

    /// <summary>Run <paramref name="callback"/> once. Cancelled if the caller unloads first.</summary>
    public EffectHandle Timeout(Action callback, TimeSpan delay)
    {
        var slot = new EffectHandle[1];
        slot[0] = Ctx.Effect(() =>
        {
            var timer = new Timer(_ =>
            {
                var handle = Volatile.Read(ref slot[0]);
                if (handle is not null) _ = handle.DisposeAsync();
                callback();
            }, null, delay, System.Threading.Timeout.InfiniteTimeSpan);
            return timer.Dispose;
        }, "ctx.Timeout()");
        return slot[0];
    }

    /// <summary>Repeat <paramref name="callback"/> until the returned effect or the fiber is disposed.</summary>
    public EffectHandle Interval(Action callback, TimeSpan period)
    {
        return Ctx.Effect(() =>
        {
            var timer = new Timer(_ => callback(), null, period, period);
            return timer.Dispose;
        }, "ctx.Interval()");
    }

    /// <summary>Awaitable delay that faults if the caller's fiber unloads while waiting.</summary>
    public Task Delay(TimeSpan delay)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = Ctx.Effect(() =>
        {
            var timer = new Timer(_ => completion.TrySetResult(), null, delay, System.Threading.Timeout.InfiniteTimeSpan);
            return () =>
            {
                timer.Dispose();
                completion.TrySetException(new InactiveFiberException());
            };
        }, "ctx.Delay()");

        return completion.Task.ContinueWith(task =>
        {
            _ = handle.DisposeAsync();
            return task;
        }, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Trailing-edge debounce. The returned delegate is inert once the fiber unloads.</summary>
    public Action<T> Debounce<T>(Action<T> callback, TimeSpan delay)
    {
        var gate = new object();
        Timer? pending = null;
        var alive = true;

        Ctx.Effect(() => () =>
        {
            lock (gate)
            {
                alive = false;
                pending?.Dispose();
                pending = null;
            }
        }, "ctx.Debounce()");

        return argument =>
        {
            lock (gate)
            {
                if (!alive) return;
                pending?.Dispose();
                pending = new Timer(_ => callback(argument), null, delay, System.Threading.Timeout.InfiniteTimeSpan);
            }
        };
    }

    /// <summary>Leading-edge throttle.</summary>
    public Action Throttle(Action callback, TimeSpan period)
    {
        var gate = new object();
        var last = DateTimeOffset.MinValue;
        var alive = true;

        Ctx.Effect(() => () => { lock (gate) alive = false; }, "ctx.Throttle()");

        return () =>
        {
            lock (gate)
            {
                if (!alive) return;
                var now = DateTimeOffset.UtcNow;
                if (now - last < period) return;
                last = now;
            }
            callback();
        };
    }
}
