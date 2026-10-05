# MSL-CLI

一个用于管理多个 Minecraft 服务器的命令行工具，内置 AI 对话与智能体（Agent）能力。

MSL-CLI 采用 **整洁架构（Clean Architecture）/ 端口-适配器（Ports & Adapters）** 组织代码，通过依赖注入（DI）组装模块。它可以同时启动/停止多台服务器、向服务器发送命令、查询运行状态、备份世界文件、读取服务器日志缓冲区，还可以让 AI（OpenAI 兼容接口）辅助管理与服务器交互。

- **技术栈**：C# / .NET 10.0、Microsoft.Extensions.DependencyInjection、OpenAI SDK、McQuery.Net
- **协议**：MIT License

> 其他语言版本：English — [README-en.md](README-en.md)

---

## 目录

- [功能特性](#功能特性)
- [项目结构](#项目结构)
- [环境要求](#环境要求)
- [构建与运行](#构建与运行)
- [配置说明](#配置说明)
- [命令参考](#命令参考)
- [架构概览](#架构概览)
- [注意事项](#注意事项)

---

## 功能特性

- **多服务器管理**：同时管理多台 Minecraft 服务器，支持启动、停止、发送命令、查询状态。
- **高亮服务器（Highlight）**：把输入转发到某一台被高亮的服务器，方便直接敲游戏内命令。
- **服务器配置管理**：读取/修改 `server.properties`，解析并持久化启动脚本参数（`run.bat` / `run.sh`、`user_jvm_args.txt`）。
- **日志缓冲**：捕获服务器标准输出，可读取或“读取并清空”缓冲区，供诊断和 AI 使用。
- **世界备份**：将服务器的世界目录打包为 zip 到 `backups` 目录。
- **AI 对话 / 智能体**：接入 OpenAI 兼容接口；提供统一的 `$ai` 命令入口（`chat`/`agent`/`default`），支持 `DefaultAIConfig` 指定默认 AI 配置；Agent 模式可通过“执行命令 → 等待 → 验证日志”的循环自动管理服务器。
- **权限与检查**：查询/校验白名单、OP、封禁玩家、封禁 IP 列表。
- **受控文件操作**：仅允许在配置的服务器目录与 `%APPDATA%\MSL_CLI` 内进行文件读写。
- **彩色控制台输出 + 文件日志**：日志同时输出到控制台（支持 ANSI 颜色）与 `%APPDATA%\MSL_CLI\Log-*.txt`。

---

## 项目结构

```
MSL-CLI/
├── Program.cs                     # 入口：DI 容器组装、输入监听、生命周期
├── MSL-CLI.csproj
├── MSL-CLI.slnx
├── Core/                          # 领域与端口层
│   ├── Domain/                    # 领域模型
│   │   ├── AppConfig.cs           # 应用配置（AI 配置 + 服务器路径）
│   │   ├── AIConfig.cs            # 单个 AI 服务的连接配置（含环境变量 ApiKey）
│   │   ├── ServerProperties.cs    # server.properties 读写
│   │   ├── ServerStatus.cs        # Stopped/Starting/Running/Stopping
│   │   ├── CommandArgs.cs         # 命令参数上下文
│   │   ├── CommandResult.cs       # 命令执行结果
│   │   └── LogLevel.cs            # 日志级别
│   ├── Ports/                     # 端口（接口）定义
│   │   ├── ICommand / ICommandExecutor / ICommandParser
│   │   ├── IServer / IServerRegistry / IServerProcess / IServerBuffer
│   │   ├── IInputReader / IOutputWriter
│   │   ├── IConfigurationStore / IAgentService
│   │   └── ...
│   └── UseCases/                  # 用例
│       ├── CommandExecutor.cs     # 命令分发执行
│       └── ServerOrchestrator.cs  # 服务器编排
├── Infrastructure/                # 基础设施层（适配器、命令实现）
│   ├── ServerManager.cs           # IServer 实现：进程生命周期、输出缓冲、AI 触发
│   ├── ServerProcess.cs           # 系统进程封装
│   ├── ServerRegistry.cs          # 服务器注册表
│   ├── ServerArgument.cs          # 启动脚本参数解析与持久化
│   ├── CommandParser.cs           # 命令反射注册
│   ├── FileConfigurationStore.cs  # JSON 配置读写
│   ├── CompositeOutputWriter.cs   # 多路输出（控制台 + 文件）
│   ├── FileOutputWriter.cs        # 文件日志
│   ├── AgentService .cs           # OpenAI 对话/智能体服务
│   ├── AgentPermissionGateway.cs  # 代理命令授权（向操作员提问）
│   └── Commands/                  # 所有 $ 开头的命令实现
└── CLI/                           # CLI 适配器（控制台 IO）
    ├── ConsoleOutputWriter.cs
    └── ConsoleInputReader.cs
```

---

## 环境要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) 或更高版本
- 每台服务器需要一个可用的 Java 运行环境（通过 `run.bat` / `run.sh` 启动）
- （可选）OpenAI 兼容的 API 接口，用于 AI 对话 / 智能体功能

---

## 构建与运行

```bash
# 构建
dotnet build

# 运行（Release 模式）
dotnet run -c Release
```

启动后进入交互式命令行。输入以 `$` 开头的命令执行管理操作；否则，输入内容会被当作命令发送给当前“高亮”的服务器（若已设置）。

---

## 配置说明

配置文件存放在 `%APPDATA%\MSL_CLI\config.json`（首次可从程序目录下的 `config.default.json` 加载）。主要结构如下：

```jsonc
{
  "EnableAI": true,                                  // 是否启用 AI
  "DefaultAIConfig": "default",                      // 默认 AI 配置名（$ai 未指定配置时使用）
  "AIConfigs": {                                     // AI 服务配置（可按需定义多个）
    "default": {
      "Url": "https://api.openai.com/v1",
      "Model": "gpt-4o",
      "UseApiKeyEnv": true,                          // 是否从环境变量读取 ApiKey
      "ApiKey": "",                                  // 直接填写密钥（当 UseApiKeyEnv=false）
      "ApiKeyEnv": "OPENAI_API_KEY",                 // 环境变量名
      "EnableChat": true,                            // 启用对话
      "EnableAgent": true,                           // 启用智能体
      "MaxIterations": 16,                           // Agent 最大迭代次数
      "AgentPrompt": "..."                           // Agent 系统提示词
    }
  },
  "ServerPaths": {                                   // 服务器注册表：名称 -> 路径
    "survival": "D:\\Minecraft\\survival"
  },
  "AgentPermissions": {                              // 代理命令授权
    "AllowList": [ "$server buf update survival" ],   // 总是允许的命令（以 * 结尾表示前缀匹配）
    "AlwaysAskCommands": [ "$exec" ]                 // 每次执行都必须确认的命令
  }
}
```

**API Key 说明**：当 `UseApiKeyEnv = true` 时，程序会从 `ApiKeyEnv` 指定的环境变量读取密钥，配置文件中无需明文存放密钥。

**配置持久化与恢复**：

- 配置优先从 `%APPDATA%\MSL_CLI\config.json` 读取；不存在或解析失败时回退到程序目录下的 `config.default.json`，两者都不可用时使用程序内置默认值。
- 加载时会做**规范化**：`AIConfigs`、`ServerPaths`、`AgentPermissions` 及其子集合为 `null` 时自动补齐，字典中值为 `null` 的条目会被剔除，避免损坏字段导致启动崩溃。
- 保存采用「先备份 → 写临时文件 → 原子替换」：覆盖前会把原文件复制为 `config.json.bak`，写入失败会清理 `.tmp` 残留。
- 配置解析失败时会在控制台与日志中报错（而不是静默丢弃），并且**不会**在加载阶段改写原文件。
- 如需回滚到上一次保存前的状态，把 `config.json.bak` 覆盖回 `config.json` 即可；程序内也可调用 `FileConfigurationStore.RestoreFromBackup()`（会把损坏文件另存为 `config.json.corrupt` 便于排查）。
- `$exec` 是强制逐次确认的**底线**：即使手工把它从 `AlwaysAskCommands` 中删除，加载时也会自动补回。

**DefaultAIConfig**：`$ai` 在未显式指定配置名时，会先使用 `DefaultAIConfig` 指向的配置（若为空则报错“未设置默认AI配置”）。可通过 `$ai default [配置名]` 在运行时查看/修改该值。

也可在运行时用命令查看/修改配置，见下文 [配置命令](#配置命令)。

---

## 命令参考

所有命令均以 `$` 开头，通过反射自动注册。**只有 8 个顶层命令**，其余功能都是子命令/动作。

| 命令 | 说明 |
| --- | --- |
| `$ai chat\|agent [配置名] <消息/指令>` | AI 对话 / 代理执行；`default [配置名]` 查看或设置默认 AI 配置 |
| `$app cfg\|exit\|reload\|ptcfg ...` | 应用级操作（见下） |
| `$exec <命令/脚本路径> [参数...]` | 执行系统命令或脚本（AI Agent 调用前必须确认） |
| `$file read\|write\|list\|delete <路径> [内容]` | 受控文件操作，仅限白名单目录 |
| `$help [命令名]` | 不带参数列出全部命令与说明；带命令名输出该命令详细用法 |
| `$hl <服务器名>` | 切换高亮服务器；不带参数显示当前高亮 |
| `$list` | **只列出全部命令名**（不含说明） |
| `$server <动作> ...` | 服务器管理（见下） |

用 `$list` 快速查看命令名，用 `$help <命令名>` 查看某条命令的详细用法。

### `$app`

```
$app cfg get [路径]          读取应用配置；省略路径输出全部 JSON
$app cfg getall              输出全部配置
$app cfg set <路径> <值>     写入配置（路径中间的字典键会自动创建）
$app cfg rm <路径>           删除字典条目，如 ServerPaths.tga；属性不可删除，改用 set
$app exit                    退出程序（先停止所有服务器）
$app reload                  重新加载配置文件并重建服务器列表
$app ptcfg                   打印当前配置（调试用）
```

点号路径示例：`AIConfigs.default.Url`、`ServerPaths.tga`、`AgentPermissions.AllowList`。

### `$server`

```
$server cfg get|getall|set|rm [服务器名] [键] [值]   服务器配置 server.properties
$server arg get|set|rm <服务器名> <参数> [值...]     启动参数
$server ck wl|op|bp|bip <服务器名|all> [名称]        名单检查
$server buf read|update <服务器名>                   读取 / 读取并清空输出缓冲区
$server ls                                           列出所有服务器及状态
$server bp <服务器名> [备注]                         备份世界目录
$server query <服务器名|all>                         查询 Query 信息
$server status [服务器名|all]                        查看进程状态
$server stop <服务器名|all> [-f]                     停止服务器
$server run <服务器名>                               启动服务器
$server send <服务器名|all> <命令>                   发送 Minecraft 命令
```

`$server cfg` 省略服务器名时使用高亮服务器；`cfg rm` 会保留注释行与其他键。

**`$server ck` 子命令**：`wl`（白名单）、`op`（管理员）、`bp`（封禁玩家）、`bip`（封禁 IP）。不带名称列出全部，带名称判断是否在列表中。

**`$server arg` 启动参数模型**（`ServerArgument`）：

| 参数 | 类型 | 权限 | 说明 |
| --- | --- | --- | --- |
| `javaPath` | String | 可读写，**不可删除** | Java 可执行文件路径，为空会被拒绝 |
| `jvmArgs` | List | 可读写，**可删除**（单个/全部） | 写入 `user_jvm_args.txt` |
| `jarArgs` | String | 可读写，**不可删除** | 支持 `server.jar`、`@libraries/.../win_args.txt`、`-jar server.jar` |
| `appendArgs` | List | 可读写，**可删除**（单个/全部） | 如 `nogui` |
| `javaArgs` / `all` | — | 只读 | 聚合展示 |

```bash
$server arg get yz jvmArgs                 # 带索引列出（[0] -Xms8G ...）
$server arg get yz jvmArgs element 1       # 读取第 1 个元素
$server arg set yz jvmArgs -Xms4G -Xmx4G   # 整体替换列表
$server arg set yz javaPath C:\Java\bin\java.exe
$server arg rm  yz jvmArgs -Xmx4G          # 删除单个元素
$server arg rm  yz jvmArgs all             # 清空整个列表
$server arg rm  yz javaPath                # 被拒绝（javaPath 不可删除）
```

启动脚本按 `{javaPath} @user_jvm_args.txt {jarArgs} {appendArgs}` 重建，与 Forge / NeoForge 官方脚本格式一致；内容未变化时不会重写文件。

**高亮服务器**：设置高亮后，直接在控制台输入的非 `$` 内容会被转发给该服务器，实现“敲命令直达”。


> 说明：命令已整理为 **8 个顶层入口**（`$ai`、`$app`、`$exec`、`$file`、`$help`、`$hl`、`$list`、`$server`），其余功能都是子命令/动作。旧的顶层入口（`$exit`、`$reload`、`$printconfig`、`$run`、`$stop`、`$stopall`、`$send`、`$sendall`、`$query`、`$status`、`$backup`、`$check`、`$highlight`、`$serverconfig`、`$serverargument`、`$bufferread`、`$bufferupdate`、`$appconfig*`、`$serverconfig*`）已全部删除。
>
> `$chat` / `$agent` 等简写别名同样已移除，请使用 `$ai chat [配置名] <消息>` 与 `$ai agent [配置名] <指令>`。

此外，服务器控制台输出中若出现 `@ai <配置名> chat|agent <内容>` 形式的文本，会被识别为 AI 触发命令（用于让游戏内玩家触发 AI 回复）。该识别逻辑目前仍处于停用状态（`ServerManager` 中相关调用被注释，且后续处理会抛出 `NotImplementedException`）。

---

## 架构概览

MSL-CLI 遵循**依赖倒置**原则：

- **Core（领域/端口/用例）**：定义领域模型与抽象接口，不依赖任何外部框架细节。
- **Infrastructure（基础设施）**：实现端口，包括服务器进程管理、配置持久化、日志、AI 服务，以及所有命令。
- **CLI（适配器）**：控制台输入/输出适配器。

依赖注入容器在 `Program.cs` 中组装；`CommandParser` 通过反射扫描所有实现 `ICommand` 的类型并自动注册命令。

### 命令组织方式

命令集中在 `Infrastructure/Commands/`，每个文件负责一块职责，命令之间不互相渗透实现，也不存在多层穿透：

- **8 个顶层命令各占一个文件**：`AICommand.cs`、`AppCommand.cs`、`ExecCommand.cs`、`FileCommand.cs`、`HelpCommand.cs`、`HighlightCommand.cs`、`ListCommand.cs`、`ServerCommand.cs`。
- `$server` 是动作分发器（`ServerCommand.cs`），把 `cfg`/`arg`/`ck`/`buf` 交给各自独立的处理器：`ServerConfigHandler.cs`、`ServerArgsHandler.cs`、`ServerCheckHandler.cs`、`ServerBufferHandler.cs`；其余动作（`ls`/`bp`/`query`/`status`/`stop`/`run`/`send`）在 `ServerCommand.cs` 与 `ServerActionHandlers.cs` 中实现。共享的目标服务器解析在 `ServerTargetResolver.cs`。
- 服务器名单文件（`ops.json`、`whitelist.json`、`banned-players.json`、`banned-ips.json`）的读取位于 `$server ck`（`ServerCheckHandler.cs`）；服务器端口不暴露 `GetOps`/`IsOp` 之类的转发方法。
- 应用配置的点号路径读写位于 `$app cfg`（`AppConfigPath.cs` + `AppCommand.cs`）。
- **不再有子命令级的独立入口**：`$serverconfig*`、`$serverargument`、`$bufferread`、`$bufferupdate`、`$appconfig*`、`$run`、`$stop`、`$send` 等都已删除，只能通过对应顶层命令 + 动作调用。

AI 智能体（`OpenAiAgentService`）通过 OpenAI 的 Function Calling 能力提供三个工具：

- `request_permission`：执行前向本地操作员申请授权，并把裁决结果作为文本回传。
- `execute_command`：执行一条 `$` 开头的命令——**仅在已获授权**（命中允许列表或已获批）时才会真正执行。
- `sleep`：等待指定秒数。

Agent 依据系统提示词中的“执行 → 等待 → 验证日志（失败重试）”循环协议，自动完成多步操作。

### 代理命令授权

Agent 执行的每条命令都经过授权闸门，未经确认的命令不可能被执行：

1. `execute_command` 会先检查该命令是否持有会话内一次性放行（由先前的 `request_permission` 发放）或命中持久化允许列表。
2. 若都未命中，会**就地**向本地操作员提问，因此模型跳过 `request_permission` 也无法绕过检查。
3. 非控制台来源的请求（例如游戏内玩家触发）不会弹出提问，而是直接拒绝——只有本地操作员才有权授权命令。

提问支持 `y`（仅本次允许）、`a`（总是允许，写入 `AgentPermissions.AllowList` 持久化）、`n`（拒绝）；无法识别的作答一律按拒绝处理。

`$exec` 位于内置的 `AlwaysAskCommands` 列表中：**每次执行都必须单独确认**，且永远不会被写入允许列表。

控制台读取由单一输入泵仲裁：`ConsoleInputReader` 持续读取输入行，在提问挂起期间该行会交给提问方，而不会作为控制台命令派发。任何组件需要向操作员提问时，都通过 `IInputReader.ReadLineAsync` 发起。

---

## 注意事项

- `$exec`、`$server run`、`$server stop` 等破坏性或耗时命令在执行前会要求 AI 确认，防止误操作。代理发起的命令一律经过授权闸门：只有命中 `AgentPermissions.AllowList` 或经操作员确认后才会执行，其中 `$exec` 每次都必须确认。
- 备份运行中的服务器可能产生不一致数据，建议先停止服务器再备份。
- `$file` 命令受白名单限制，只能访问服务器目录与 `%APPDATA%\MSL_CLI`。
- 修改 `server.properties` 采用临时文件 + 替换的原子写入方式。
- 停止服务器时，先发送 `stop` 命令并等待最多 30 秒，超时后强制终止。
- 每台服务器拥有**独立的系统进程与日志缓冲**，输出彼此隔离，不会出现多服务器日志混在一起的情况。

---

## License

[MIT](LICENSE.txt) © 2026 Li Jiakun
