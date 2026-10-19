using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ControllerAutoSelect;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private const int CommandLimit = 32;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog pluginLog;
    private readonly Configuration config;
    private readonly SelectionPolicy policy = new();
    private readonly ConcurrentQueue<UiCommand> commands = new();
    private readonly WindowSystem windows = new("ControllerAutoSelect");
    private readonly object uiGate = new();
    private readonly SettingsWindow settings;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private ScheduledMonitor? monitor;
    private bool handlerRegistered;
    private ViewState view;
    private string? lastError;
    private string? lastSchedulerError;
    private int commandCount;
    private int disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager,
        IFramework framework, IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.pluginLog = pluginLog;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        view = new(ControllerSnapshot.Empty, config.Enabled, config.InstanceId, config.ProductId,
            config.ControllerName, "Waiting for FFXIV's controller list.");
        settings = new(this) { IsOpen = config.InstanceId == Guid.Empty };
        windows.AddWindow(settings);
    }

    // Dalamud calls DisposeAsync even when asynchronous loading fails or is cancelled.
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!commandManager.AddHandler("/cas", new CommandInfo(OnCommand)
            { HelpMessage = "Open Controller Auto Select." }))
            throw new InvalidOperationException("The command /cas is already in use.");
        handlerRegistered = true;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += OpenSettings;
        monitor = new((action, delay, token) => framework.RunOnTick(action, delay, cancellationToken: token),
            Tick, ReportSchedulerError);
        return Task.CompletedTask;
    }

    private ViewState View => Volatile.Read(ref view);

    private void Draw()
    {
        if (Volatile.Read(ref disposed) != 0 || !settings.IsOpen) return;
        lock (uiGate)
        {
            if (Volatile.Read(ref disposed) == 0 && settings.IsOpen) windows.Draw();
        }
    }

    private void OpenSettings() => settings.IsOpen = true;

    private void OnCommand(string command, string arguments) => OpenSettings();

    private void Enqueue(UiCommand command)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (Interlocked.Increment(ref commandCount) > CommandLimit)
        {
            Interlocked.Decrement(ref commandCount);
            return;
        }
        commands.Enqueue(command);
    }

    // Runs once a second on the game's framework thread, never on every frame.
    private void Tick()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var status = "Waiting for FFXIV's controller list.";
        var snapshot = View.Snapshot;
        try
        {
            while (commands.TryDequeue(out var command))
            {
                Interlocked.Decrement(ref commandCount);
                switch (command.Kind)
                {
                    case CommandKind.Enable:
                        config.Enabled = command.Enabled;
                        policy.Reset();
                        pluginInterface.SavePluginConfig(config);
                        break;
                    case CommandKind.Choose:
                        if (command.Device is not { } device) break;
                        config.InstanceId = device.InstanceId;
                        config.ProductId = device.ProductId;
                        config.ControllerName = device.Name;
                        policy.Reset();
                        pluginInterface.SavePluginConfig(config);
                        break;
                    case CommandKind.UseNow:
                        policy.Reset();
                        break;
                }
            }

            snapshot = GameControllers.Read();
            lastError = null;
            lastSchedulerError = null;
            var decision = policy.Evaluate(snapshot, config.InstanceId, config.ProductId, config.Enabled);
            status = decision.Action switch
            {
                SelectionAction.Off => "Automatic switching is off.",
                SelectionAction.Unconfigured => "Choose a controller below.",
                SelectionAction.Waiting => "Waiting for controller...",
                SelectionAction.Settling => "Controller found. Waiting for it to settle.",
                SelectionAction.Switch => "Switching to your controller...",
                SelectionAction.Active => $"Using {config.ControllerName}.",
                SelectionAction.Ready => "Ready. Will switch when your controller reconnects.",
                SelectionAction.Failed => "Could not switch. Try Use now.",
                _ => "Ready.",
            };

            if (decision.Action == SelectionAction.Switch)
            {
                var target = snapshot.Find(config.InstanceId, config.ProductId)!;
                GameControllers.Select(target);
                // The next scheduled sample confirms whether FFXIV kept the selection.
            }
        }
        catch (Exception ex)
        {
            status = "Controller unavailable. Try again in a moment.";
            var error = $"{ex.GetType().Name}: {ex.Message}";
            if (error != lastError)
            {
                pluginLog.Error(ex, "Controller Auto Select could not read or switch the controller.");
                lastError = error;
            }
        }
        finally
        {
            Volatile.Write(ref view, new(snapshot, config.Enabled, config.InstanceId, config.ProductId,
                config.ControllerName, status));
        }
    }

    private void ReportSchedulerError(Exception ex)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var error = $"{ex.GetType().Name}: {ex.Message}";
        if (error == lastSchedulerError) return;
        lastSchedulerError = error;
        pluginLog.Error(ex, "Controller Auto Select could not schedule a controller check.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Await cancellation asynchronously; never block the framework or render thread.
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi -= OpenSettings;
        if (handlerRegistered) commandManager.RemoveHandler("/cas");
        lock (uiGate) windows.RemoveAllWindows();
        if (monitor is { } worker)
        {
            worker.Stop();
            await worker.Completion.ConfigureAwait(false);
        }
        while (commands.TryDequeue(out _)) Interlocked.Decrement(ref commandCount);
    }

    private enum CommandKind { Enable, Choose, UseNow }
    private sealed record UiCommand(CommandKind Kind, bool Enabled = false, ControllerDevice? Device = null);
    private sealed record ViewState(ControllerSnapshot Snapshot, bool Enabled, Guid InstanceId, Guid ProductId,
        string PreferredName, string Status);

    private sealed class SettingsWindow : Window
    {
        private readonly Plugin owner;
        public SettingsWindow(Plugin owner) : base("Controller Auto Select", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.owner = owner;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(430, 150), MaximumSize = new Vector2(700, 800) };
        }

        public override void Draw()
        {
            var current = owner.View;
            var preferred = current.Snapshot.Find(current.InstanceId, current.ProductId);
            var enabled = current.Enabled;
            if (ImGui.Checkbox("Enable", ref enabled))
                owner.Enqueue(new(CommandKind.Enable, enabled));
            ImGui.TextWrapped(current.Status);
            ImGui.Spacing();
            ImGui.TextUnformatted("Preferred controller");
            var preview = current.InstanceId == Guid.Empty ? "Choose a controller" : current.PreferredName;
            if (current.InstanceId != Guid.Empty && preferred is null)
                preview += " (disconnected)";
            ImGui.SetNextItemWidth(390);
            if (ImGui.BeginCombo("##controller", preview))
            {
                if (current.Snapshot.Devices.Length == 0) ImGui.TextUnformatted("No controllers found by FFXIV.");
                foreach (var device in current.Snapshot.Devices)
                {
                    var label = device.Name;
                    if (current.Snapshot.Devices.Count(d => d.Name == device.Name) > 1)
                        label += $" (device {device.Index + 1})";
                    if (ImGui.Selectable($"{label}##{device.InstanceId:D}",
                        device.InstanceId == current.InstanceId && device.ProductId == current.ProductId))
                        owner.Enqueue(new(CommandKind.Choose, Device: device));
                }
                ImGui.EndCombo();
            }
            ImGui.BeginDisabled(!current.Enabled || preferred is null);
            if (ImGui.Button("Use now")) owner.Enqueue(new(CommandKind.UseNow));
            ImGui.EndDisabled();
        }
    }
}
