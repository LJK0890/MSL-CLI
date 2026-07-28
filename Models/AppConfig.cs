namespace MSL_CLI.Models;

internal class AppConfig
{
    public bool EnableAI { get; set; } = true;

    public Dictionary<string, AIConfig> AIConfigs { get; set; } = new();

    public Dictionary<string, string> ServerPaths { get; set; } = new();
}