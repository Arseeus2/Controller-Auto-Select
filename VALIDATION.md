# Validation — 1.0.0.0

Target: `net10.0-windows`, x64, Dalamud API 15, SDK 15.0.0. Compiled against the currently installed Dalamud 15.0.3.8 development files, commit `f7d06f363955251991222363a7c008b8998b6565`.

## Changes

Updated the release version from `0.2.2.0` to `1.0.0.0`. The **Enable** checkbox and optional **Use now** button are unchanged. Controller identification, ordering, selection policy, configuration format, and one-second scheduling are preserved. The assembly's internal name and configuration version are unchanged, so saved preferences carry over.

Custom debugging features remain removed. Unexpected errors still use Dalamud's normal error log, with repeated identical errors suppressed.

## Checks

- Release build: passed with zero warnings and zero errors.
- 20 selection and scheduler tests passed.
- 4 full-plugin lifecycle tests passed.
- 50 simulated load/unload cycles removed all three UI subscriptions and the command, and created no debug files.
- All 50 unloaded plugin instances were collectible while the simulated services remained alive.
- A separate 60-monitor test confirmed stopped monitors and their captured state were collectible.
- Failed and cancelled loads cleaned up; a command collision preserved the existing command.

The supplied version 0.1 game log showed two reconnects followed by a successful selection on the first attempt and confirmation on the next check. The selection policy and native selection call are preserved in 1.0.0.0. This version still needs the short in-game check in README.md; automated tests do not measure game FPS or prove that no leak can ever occur.

## Runtime and cleanup

The plugin queues one cancellable `IFramework.RunOnTick` callback at a time, with one second between completed checks. It has no per-frame Update subscription, Windows device enumeration, input polling, native hooks, or custom log writer.

The callback reads `InputDeviceManager.PadDevice.GamepadDevices`, preserves its order, and uses `GetActiveDeviceIndex` and `SetActiveDevice`. All native access stays on the framework thread. The plugin owns no controller handles and retains no native pointers.

The native adapter validates device counts and indices, and rechecks instance/product IDs immediately before selecting a device. The policy waits for two presence samples, confirms selection on the following sample, limits attempts to three, and respects later manual changes.

Async unload removes UI/command handlers, clears windows, cancels and awaits scheduled work, and clears pending UI commands. The UI command queue remains capped at 32 entries.

## References

- [Dalamud interaction guidance](https://dalamud.dev/plugin-development/interaction/)
- [PadDeviceInterface](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/System/Input/PadDeviceInterface.cs)
- [Framework scheduling](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Framework.cs)
- [Async plugin lifecycle](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Plugin/IAsyncDalamudPlugin.cs)
