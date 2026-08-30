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
  "DefaultAIConfig": "default",                      // 默认 AI 配置名（$ai/$chat/$agent 未指定配置时使用）
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
  }
}
```

**API Key 说明**：当 `UseApiKeyEnv = true` 时，程序会从 `ApiKeyEnv` 指定的环境变量读取密钥，配置文件中无需明文存放密钥。

**DefaultAIConfig**：`$ai` / `$chat` / `$agent` 在未显式指定配置名时，会先使用 `DefaultAIConfig` 指向的配置（若为空则报错“未设置默认AI配置”）。可通过 `$ai default [配置名]` 在运行时查看/修改该值。

也可在运行时用命令查看/修改配置，见下文 [配置命令](#配置命令)。

---

## 命令参考

所有命令均以 `$` 开头。命令通过反射自动注册。

### 通用

| 命令 | 说明 |
| --- | --- |
| `$help` | 显示所有可用命令及其说明 |
| `$exit` | 退出程序（会先停止所有服务器） |
| `$list` | 列出所有已配置的服务器及其状态 |
| `$status [服务器名]` | 查看服务器进程状态（默认全部：PID、内存、CPU 时间） |
| `$reload` | 重新加载配置文件并重建服务器列表（跳过运行中的服务器） |

### 服务器管理

| 命令 | 说明 |
| --- | --- |
| `$run <服务器名>` | 启动指定服务器（若未设高亮则自动切换） |
| `$stop <服务器名> [-f]` | 停止服务器（`-f` 强制终止） |
| `$stopall` | 停止所有运行中的服务器 |
| `$send <服务器名> <命令>` | 向指定服务器发送 Minecraft 命令 |
| `$sendall <命令>` | 向所有运行中的服务器发送命令 |
| `$server <子命令> ...` | 统一服务器管理入口（见下） |
| `$highlight / $hl <服务器名>` | 切换高亮服务器；不带参数显示当前高亮 |
| `$serverargument get\|set ...` | 查看/修改服务器启动参数 |

`$server` 子命令：

```
$server run    <服务器名>             启动服务器
$server stop   <服务器名|all> [-f]   停止服务器
$server send   <服务器名|all> <命令>  发送命令
$server buffer read|update <服务器名> 读取/清空缓冲区
$server config get|set <服务器名>...  查看/修改 server.properties
$server query  <服务器名|all>         查询 Query 信息
$server status [服务器名]             查看进程状态
```

**高亮服务器**：设置高亮后，直接在控制台输入的非 `$` 内容会被转发给该服务器，实现“敲命令直达”。

### 缓冲区 / 配置

| 命令 | 说明 |
| --- | --- |
| `$bufferread / $bufr <服务器名>` | 读取服务器缓冲区（不清空） |
| `$bufferupdate / $bufu <服务器名>` | 读取并清空服务器缓冲区 |
| `$serverconfigget / $scg [服务器名] [键]` | 查看 `server.properties`（可省略服务器名使用高亮） |
| `$serverconfigset / $scs [服务器名] <键> <值>` | 修改 `server.properties`（原子写入） |
| `$serverargument get\|set ...` | 查看/修改启动脚本参数（javapath/jvmargs/jarargs/append） |

### 配置命令

| 命令 | 说明 |
| --- | --- |
| `$appconfigget / $acg [路径]` | 获取应用配置值（点号路径，如 `AIConfigs.default.Url`） |
| `$appconfiggetall / $acga` | 获取全部配置 |
| `$appconfigset / $acs <路径> <值>` | 设置应用配置值并保存 |
| `$printconfig` | 打印当前配置到控制台（调试用） |

### 备份与文件

| 命令 | 说明 |
| --- | --- |
| `$backup <服务器名> [备注]` | 将服务器世界目录打包到 `backups` 目录 |
| `$file <read\|write\|list\|delete> <路径>` | 受控文件操作；支持 `%appdata%`、`%<服务器名>%` 占位符，仅限白名单目录 |

### 检查列表

| 命令 | 说明 |
| --- | --- |
| `$check <子命令> <服务器名> [名称]` | 子命令：`whitelist/wl`、`op`、`banplayer/bp`、`banip/bip`。不带名称列出全部，带名称判断是否在列表中 |

### 系统执行

| 命令 | 说明 |
| --- | --- |
| `$exec <命令/脚本路径> [参数...]` | 执行系统命令或脚本（注意：AI Agent 会在执行前要求确认） |

### AI

| 命令 | 说明 |
| --- | --- |
| `$ai chat [配置名] <消息>` | 与 AI 对话 |
| `$ai agent [配置名] <指令>` | 以智能体模式执行指令（可调用工具操作服务器） |
| `$ai default [配置名]` | 查看当前默认 AI 配置；带参数则设置 `DefaultAIConfig` 并写入配置文件 |
| `$chat [配置名] <消息>` | `$ai chat` 的简写（等价于 `$ai chat`） |
| `$agent [配置名] <指令>` | `$ai agent` 的简写（等价于 `$ai agent`） |

> 说明：`$chat`、`$agent`、`$ai` 共用同一套解析逻辑。未指定配置名（或首词为 `default`）时，会使用 `DefaultAIConfig` 指定的默认配置。

此外，服务器控制台输出中若出现 `$chat` / `$agent` / `@ai ...` 开头的文本，也会被当作 AI 触发命令处理（用于让游戏内玩家触发 AI 回复）。

---

## 架构概览

MSL-CLI 遵循**依赖倒置**原则：

- **Core（领域/端口/用例）**：定义领域模型与抽象接口，不依赖任何外部框架细节。
- **Infrastructure（基础设施）**：实现端口，包括服务器进程管理、配置持久化、日志、AI 服务，以及所有命令。
- **CLI（适配器）**：控制台输入/输出适配器。

依赖注入容器在 `Program.cs` 中组装；`CommandParser` 通过反射扫描所有实现 `ICommand` 的类型并自动注册命令。

AI 智能体（`OpenAiAgentService`）通过 OpenAI 的 Function Calling 能力提供两个工具：

- `execute_command`：执行一条 `$` 开头的命令。
- `sleep`：等待指定秒数。

Agent 依据系统提示词中的“执行 → 等待 → 验证日志（失败重试）”循环协议，自动完成多步操作。

---

## 注意事项

- `$exec`、`$run`、`$stop`、`$stopall`、`$server run/stop` 等破坏性或耗时命令在执行前会要求 AI 确认，防止误操作。
- 备份运行中的服务器可能产生不一致数据，建议先停止服务器再备份。
- `$file` 命令受白名单限制，只能访问服务器目录与 `%APPDATA%\MSL_CLI`。
- 修改 `server.properties` 采用临时文件 + 替换的原子写入方式。
- 停止服务器时，先发送 `stop` 命令并等待最多 30 秒，超时后强制终止。
- 每台服务器拥有**独立的系统进程与日志缓冲**，输出彼此隔离，不会出现多服务器日志混在一起的情况。

---

## License

[MIT](LICENSE.txt) © 2026 Li Jiakun
