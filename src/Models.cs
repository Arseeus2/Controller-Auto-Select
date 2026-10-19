namespace ControllerAutoSelect;

internal sealed record ControllerDevice(int Index, Guid InstanceId, Guid ProductId, string Name);

internal sealed record ControllerSnapshot(ControllerDevice[] Devices, int ActiveIndex)
{
    public static readonly ControllerSnapshot Empty = new([], -1);

    public ControllerDevice? Find(Guid instanceId, Guid productId)
    {
        foreach (var device in Devices)
            if (device.InstanceId == instanceId && device.ProductId == productId)
                return device;
        return null;
    }
}

internal enum SelectionAction { None, Waiting, Settling, Switch, Active, Ready, Failed, Off, Unconfigured }

internal readonly record struct SelectionDecision(SelectionAction Action, int Index = -1, int Attempt = 0);

// Pure state machine. No pointers, timers, subscriptions, or game dependencies.
internal sealed class SelectionPolicy
{
    public const int MaxAttempts = 3;
    private Guid lastInstance;
    private Guid lastProduct;
    private bool wasEnabled;
    private int stableSamples;
    private int attempts;
    private bool settled;

    public void Reset()
    {
        stableSamples = 0;
        attempts = 0;
        settled = false;
    }

    public SelectionDecision Evaluate(ControllerSnapshot snapshot, Guid instance, Guid product, bool enabled)
    {
        if (lastInstance != instance || lastProduct != product || wasEnabled != enabled)
        {
            Reset();
            lastInstance = instance;
            lastProduct = product;
            wasEnabled = enabled;
        }

        if (!enabled) return new(SelectionAction.Off);
        if (instance == Guid.Empty) return new(SelectionAction.Unconfigured);

        var device = snapshot.Find(instance, product);
        if (device is null)
        {
            Reset();
            return new(SelectionAction.Waiting);
        }

        // Confirm presence on two separate scheduled samples before changing the device.
        stableSamples = Math.Min(2, stableSamples + 1);
        if (stableSamples < 2) return new(SelectionAction.Settling);

        if (snapshot.ActiveIndex == device.Index)
        {
            settled = true;
            return new(SelectionAction.Active);
        }

        // Once confirmed, respect a later manual selection until the preferred pad reconnects.
        if (settled) return new(SelectionAction.Ready);
        if (attempts >= MaxAttempts) return new(SelectionAction.Failed);
        return new(SelectionAction.Switch, device.Index, ++attempts);
    }
}
