using System.Runtime.CompilerServices;
using ControllerAutoSelect;

var passed = 0;
var a = new ControllerDevice(0, Guid.NewGuid(), Guid.NewGuid(), "Same name");
var b = new ControllerDevice(1, Guid.NewGuid(), a.ProductId, "Same name");
var bothBActive = new ControllerSnapshot([a, b], 1);
SelectionDecision Evaluate(SelectionPolicy policy, ControllerSnapshot state, bool enabled = true) =>
    policy.Evaluate(state, a.InstanceId, a.ProductId, enabled);
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
}
void Test(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS {name}");
}
async Task TestAsync(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

Test("No saved controller does nothing", () =>
    Check(new SelectionPolicy().Evaluate(bothBActive, Guid.Empty, Guid.Empty, true).Action == SelectionAction.Unconfigured, "Must not guess"));
Test("Disabled does nothing", () =>
    Check(Evaluate(new(), bothBActive, false).Action == SelectionAction.Off, "Disabled"));
Test("Missing preferred device waits", () =>
    Check(Evaluate(new(), new([b], 1)).Action == SelectionAction.Waiting, "Missing device"));
Test("Two observations are required", () =>
{
    var p = new SelectionPolicy();
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Settling, "First observation");
    var next = Evaluate(p, bothBActive);
    Check(next.Action == SelectionAction.Switch && next.Index == 0 && next.Attempt == 1, "Second observation");
});
Test("Already active never issues a switch", () =>
{
    var p = new SelectionPolicy();
    var state = new ControllerSnapshot([a, b], 0);
    Evaluate(p, state);
    Check(Evaluate(p, state).Action == SelectionAction.Active, "Already active");
});
Test("Switch confirmation ends retries", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, bothBActive);
    Evaluate(p, bothBActive);
    Check(Evaluate(p, new([a, b], 0)).Action == SelectionAction.Active, "Confirmation");
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Ready, "Respect manual change after confirmation");
});
Test("Retries stop after three attempts", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, bothBActive);
    for (var i = 1; i <= 3; i++)
    {
        var d = Evaluate(p, bothBActive);
        Check(d.Action == SelectionAction.Switch && d.Attempt == i, "Retry count");
    }
    for (var i = 0; i < 100_000; i++)
        Check(Evaluate(p, bothBActive).Action == SelectionAction.Failed, "Retries must stay stopped");
});
Test("Disconnect and reconnect rearm switching", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, new([a, b], 0));
    Evaluate(p, new([a, b], 0));
    Check(Evaluate(p, new([b], 1)).Action == SelectionAction.Waiting, "Disconnect");
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Settling, "Reconnect settle");
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Switch, "Reconnect select");
});
Test("Use now rearms a failed attempt", () =>
{
    var p = new SelectionPolicy();
    for (var i = 0; i < 5; i++) Evaluate(p, bothBActive);
    p.Reset();
    Evaluate(p, bothBActive);
    Check(Evaluate(p, bothBActive).Attempt == 1, "Reset attempt count");
});
Test("Same name never matches a different instance", () =>
{
    var p = new SelectionPolicy();
    for (var i = 0; i < 4; i++)
        Check(Evaluate(p, new([b], 1)).Action == SelectionAction.Waiting, "Name is not identity");
});
Test("Changed product ID does not match", () =>
    Check(Evaluate(new(), new([a with { ProductId = Guid.NewGuid() }], -1)).Action == SelectionAction.Waiting, "Product guard"));
Test("Device reordering uses current index", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, bothBActive);
    var d = Evaluate(p, new([b with { Index = 0 }, a with { Index = 1 }], 0));
    Check(d.Action == SelectionAction.Switch && d.Index == 1, "Stale index");
});
Test("Changing the saved controller resets retry state", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, bothBActive);
    Evaluate(p, bothBActive);
    Check(p.Evaluate(bothBActive, b.InstanceId, b.ProductId, true).Action == SelectionAction.Settling, "Changed choice");
});
Test("Disabling and enabling rearms switching", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, new([a, b], 0));
    Evaluate(p, new([a, b], 0));
    Evaluate(p, bothBActive, false);
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Settling, "Enable settle");
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Switch, "Enable switch");
});
Test("Brief connection does not switch", () =>
{
    var p = new SelectionPolicy();
    Evaluate(p, bothBActive);
    Evaluate(p, ControllerSnapshot.Empty);
    Check(Evaluate(p, bothBActive).Action == SelectionAction.Settling, "Consecutive samples");
});

await TestAsync("Scheduled checks never overlap", async () =>
{
    var inFlight = 0;
    var count = 0;
    var overlap = false;
    TimeSpan observedDelay = default;
    var monitor = new ScheduledMonitor(async (tick, delay, token) =>
    {
        observedDelay = delay;
        if (Interlocked.Increment(ref inFlight) != 1) overlap = true;
        try { await Task.Delay(2, token); tick(); }
        finally { Interlocked.Decrement(ref inFlight); }
    }, () => Interlocked.Increment(ref count), _ => { });
    try
    {
        while (Volatile.Read(ref count) < 5) await Task.Delay(2);
    }
    finally { monitor.Stop(); }
    await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Check(!overlap, "Only one queued callback");
    Check(observedDelay == TimeSpan.FromSeconds(1), "Production interval is one second");
    var stoppedAt = count;
    await Task.Delay(20);
    Check(count == stoppedAt, "No callbacks after stop");
    monitor.Stop();
});
await TestAsync("Unload cancels a pending scheduled callback", async () =>
{
    var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var count = 0;
    var monitor = new ScheduledMonitor(async (tick, _, token) =>
    {
        scheduled.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        tick();
    }, () => count++, _ => { });
    await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    monitor.Stop();
    await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Check(count == 0, "Cancelled callback must not execute");
});
await TestAsync("Scheduler recovers after a managed error", async () =>
{
    var calls = 0;
    var errors = 0;
    var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new ScheduledMonitor(async (tick, _, token) =>
    {
        if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("test");
        await Task.Delay(2, token);
        tick();
    }, () => ran.TrySetResult(), _ => Interlocked.Increment(ref errors));
    await ran.Task.WaitAsync(TimeSpan.FromSeconds(3));
    monitor.Stop();
    await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Check(errors == 1, "Error observed");
});
await TestAsync("Framework shutdown cancels without a busy retry loop", async () =>
{
    var calls = 0;
    var monitor = new ScheduledMonitor((_, _, _) =>
    {
        calls++;
        return Task.FromCanceled(new CancellationToken(true));
    }, () => throw new Exception("Must not run"), _ => { });
    await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    monitor.Stop();
    Check(calls == 1, "Shutdown must stop scheduling");
});
await TestAsync("Stopped monitors and captured state are collectible", async () =>
{
    var references = new List<WeakReference>();
    for (var i = 0; i < 60; i++) references.AddRange(await CreateAndStopMonitor());
    for (var i = 0; i < 3; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(10);
    }
    Check(references.All(reference => !reference.IsAlive), "A stopped monitor retained state");
});

Console.WriteLine($"{passed} tests passed.");

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task<WeakReference[]> CreateAndStopMonitor()
{
    var payload = new byte[128 * 1024];
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var monitor = new ScheduledMonitor(async (tick, _, token) =>
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
        tick();
    }, () => GC.KeepAlive(payload), _ => { });
    await started.Task;
    var references = new[] { new WeakReference(monitor), new WeakReference(payload) };
    monitor.Stop();
    await monitor.Completion;
    return references;
}
