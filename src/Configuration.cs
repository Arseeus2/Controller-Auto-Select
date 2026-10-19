using Dalamud.Configuration;

namespace ControllerAutoSelect;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public Guid InstanceId { get; set; }
    public Guid ProductId { get; set; }
    public string ControllerName { get; set; } = "Choose a controller";
}
