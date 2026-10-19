using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ControllerAutoSelect;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

var libraryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(libraryPath, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
await LifecycleTests.RunAsync(args.Length > 0 ? args[0] : Path.GetTempPath());

internal static class LifecycleTests
{
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "ControllerAutoSelectLifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ui = FakeProxy.Create<IUiBuilder>();
        var commands = FakeProxy.Create<ICommandManager>();
        var framework = FakeProxy.Create<IFramework>();
        var pluginInterface = FakeProxy.Create<IDalamudPluginInterface>();
        var pluginLog = FakeProxy.Create<IPluginLog>();
        var handlers = new HashSet<string>();
        var scheduled = 0;

        commands.Proxy.Handler = (method, arguments) => method.Name switch
        {
            "AddHandler" => handlers.Add((string)arguments[0]!),
            "RemoveHandler" => handlers.Remove((string)arguments[0]!),
            _ => null,
        };
        pluginInterface.Proxy.Handler = (method, _) => method.Name switch
        {
            "get_UiBuilder" => ui.Service,
            "get_ConfigDirectory" => new DirectoryInfo(directory),
            "GetPluginConfig" => new Configuration(),
            _ => null,
        };
        framework.Proxy.Handler = (method, arguments) =>
        {
            if (method.Name != "RunOnTick") return null;
            Interlocked.Increment(ref scheduled);
            // Simulate a queued game callback. Never invoke native game APIs in this test.
            return Task.Delay(Timeout.Infinite, (CancellationToken)arguments[3]!);
        };

        var references = new List<WeakReference>();
        for (var i = 0; i < 50; i++)
        {
            var prior = scheduled;
            references.Add(await LoadAndUnload(pluginInterface.Service, commands.Service,
                framework.Service, pluginLog.Service, () => scheduled > prior));
            Check(ui.Proxy.SubscriberCount == 0, "UI handler remained after unload");
            Check(handlers.Count == 0, "Command handler remained after unload");
            Check(!Directory.EnumerateFileSystemEntries(directory).Any(), "Plugin created a debug file");
        }
        Console.WriteLine("PASS 50 complete plugin load/unload cycles remove all handlers and create no debug files");
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Check(references.All(reference => !reference.IsAlive), "Unloaded plugin instance was retained");
        Console.WriteLine("PASS all 50 unloaded plugin instances are collectible while service proxies remain alive");

        handlers.Add("/cas");
        var collision = new Plugin(pluginInterface.Service, commands.Service, framework.Service, pluginLog.Service);
        try
        {
            await collision.LoadAsync(default);
            throw new Exception("Command collision should have failed");
        }
        catch (InvalidOperationException) { }
        await collision.DisposeAsync();
        Check(handlers.Contains("/cas"), "Failed load removed someone else's command");
        Check(ui.Proxy.SubscriberCount == 0, "Failed load left UI handlers");
        Console.WriteLine("PASS failed load cleans up without removing an existing command");
        handlers.Clear();

        var cancelled = new Plugin(pluginInterface.Service, commands.Service, framework.Service, pluginLog.Service);
        try
        {
            await cancelled.LoadAsync(new CancellationToken(true));
            throw new Exception("Cancelled load should throw");
        }
        catch (OperationCanceledException) { }
        await cancelled.DisposeAsync();
        await cancelled.DisposeAsync();
        Check(handlers.Count == 0 && ui.Proxy.SubscriberCount == 0, "Cancelled load leaked handlers");
        Console.WriteLine("PASS cancelled load and repeated disposal clean up safely");
        GC.KeepAlive(ui);
        GC.KeepAlive(commands);
        GC.KeepAlive(pluginInterface);
        GC.KeepAlive(framework);
        Console.WriteLine("4 lifecycle tests passed. Game input and UI rendering still need in-game testing.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndUnload(IDalamudPluginInterface pluginInterface,
        ICommandManager commands, IFramework framework, IPluginLog log, Func<bool> scheduled)
    {
        var plugin = new Plugin(pluginInterface, commands, framework, log);
        await plugin.LoadAsync(default);
        Check(((FakeProxy)(object)pluginInterface.UiBuilder).SubscriberCount == 3,
            "The test must exercise all three live UI subscriptions");
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!scheduled())
        {
            if (DateTime.UtcNow > timeout) throw new Exception("Monitor did not schedule");
            await Task.Delay(1);
        }
        await plugin.DisposeAsync();
        return new WeakReference(plugin);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

public class FakeProxy : DispatchProxy
{
    private readonly Dictionary<string, List<Delegate>> events = new();
    public Func<MethodInfo, object?[], object?>? Handler;
    public int SubscriberCount => events.Values.Sum(list => list.Count);

    public static (T Service, FakeProxy Proxy) Create<T>() where T : class
    {
        var service = DispatchProxy.Create<T, FakeProxy>();
        return (service, (FakeProxy)(object)service);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        var arguments = args ?? [];
        if (method.Name.StartsWith("add_", StringComparison.Ordinal))
        {
            var name = method.Name[4..];
            if (!events.TryGetValue(name, out var subscribers)) events[name] = subscribers = [];
            subscribers.Add((Delegate)arguments[0]!);
            return null;
        }
        if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
        {
            if (events.TryGetValue(method.Name[7..], out var subscribers)) subscribers.Remove((Delegate)arguments[0]!);
            return null;
        }
        return Handler?.Invoke(method, arguments) ??
            (method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
