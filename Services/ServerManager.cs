using MSL_CLI.Models;

namespace MSL_CLI.Services;

internal class ServerManager
{
    private readonly GlobalManager _globalManager;
    private ServerConfig serverConfig;
    private string serverPath;
    public ServerManager(GlobalManager globalManager,string filePath)
    {
        _globalManager = globalManager;
        serverPath = filePath;
        serverConfig = new ServerConfig(filePath);
    }
}