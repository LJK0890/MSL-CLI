using MSL_CLI.Core.Domain;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 配置存储接口，定义应用配置的加载与保存能力。
/// </summary>
public interface IConfigurationStore
{
    /// <summary>
    /// 加载应用配置。
    /// </summary>
    /// <returns>加载得到的应用配置对象。</returns>
    AppConfig LoadConfig();
    /// <summary>
    /// 保存应用配置。
    /// </summary>
    /// <param name="config">要保存的应用配置对象。</param>
    void SaveConfig(AppConfig config);
}
