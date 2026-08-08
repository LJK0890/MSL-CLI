namespace MSL_CLI.Commands;

/// <summary>
/// 标记命令类的特性，用于自动注册。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class CommandAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; set; } = string.Empty;

    public CommandAttribute(string name)
    {
        Name = name;
    }
}