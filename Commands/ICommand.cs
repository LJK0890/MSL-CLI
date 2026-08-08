using MSL_CLI.IO;

namespace MSL_CLI.Commands;

/// <summary>
/// 所有命令必须实现的接口。
/// </summary>
public interface ICommand
{
    int Execute(CommandArgs args, bool capture = false);
}