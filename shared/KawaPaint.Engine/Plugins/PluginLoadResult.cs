// KawaPaint — per-plugin outcome of a load attempt. Lives in Engine (not App, despite being purely
// a reporting DTO) because PluginManager.LoadFrom returns it and Engine cannot reference App.

namespace KawaPaint.Engine.Plugins;

public enum PluginStatus
{
    Loaded,
    Disabled,
    Failed
}

public sealed record PluginLoadResult(string Id, string? Name, string? Version, PluginStatus Status, string? Error, string? FolderPath);
