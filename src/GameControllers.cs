using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace ControllerAutoSelect;

internal static unsafe class GameControllers
{
    private const int MaximumDevices = 64;

    // Call only on the framework thread. The game owns every pointer; retain none of them.
    private static PadDevice* GetPad()
    {
        var manager = InputDeviceManager.Instance();
        if (manager == null || manager->PadDevice == null)
            throw new InvalidOperationException("FFXIV's controller list is not ready.");
        return manager->PadDevice;
    }

    public static ControllerSnapshot Read()
    {
        var pad = GetPad();
        var count = pad->GamepadDevices.Count;
        if (count is < 0 or > MaximumDevices || pad->GetNumberOfDevices() != count)
            throw new InvalidOperationException("FFXIV's controller list is changing or has an unexpected layout.");

        var active = pad->GetActiveDeviceIndex();
        if (count == 0) active = -1;
        if (active < -1 || active >= count)
            throw new InvalidOperationException("FFXIV reported an invalid active controller index.");

        var devices = new ControllerDevice[count];
        for (var i = 0; i < count; i++)
        {
            ref var info = ref pad->GamepadDevices[i].DeviceInfo;
            var name = info.InstanceNameString;
            if (string.IsNullOrWhiteSpace(name)) name = info.ProductNameString;
            if (string.IsNullOrWhiteSpace(name)) name = $"Controller {i + 1}";
            devices[i] = new(i, info.Instance, info.Product, name);
        }

        return new(devices, active);
    }

    public static void Select(ControllerDevice expected)
    {
        var pad = GetPad();
        var count = pad->GamepadDevices.Count;
        if (count is < 1 or > MaximumDevices || count != pad->GetNumberOfDevices() ||
            expected.Index < 0 || expected.Index >= count)
            throw new InvalidOperationException("The controller list changed. Waiting for the next check.");

        // Recheck identity at the point of use, rather than trusting an earlier dropdown index.
        ref var info = ref pad->GamepadDevices[expected.Index].DeviceInfo;
        if (info.Instance != expected.InstanceId || info.Product != expected.ProductId)
            throw new InvalidOperationException("The controller moved in the list. Waiting for the next check.");

        // The same client entry point used to select an active gamepad. No hooks or raw writes.
        pad->SetActiveDevice(expected.Index);
    }
}
