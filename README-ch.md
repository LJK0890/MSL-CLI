# MSL-CLI

一个用于管理多个 Minecraft 服务器的命令行工具，内置 AI 对话与智能体（Agent）能力。

MSL-CLI 采用 **整洁架构（Clean Architecture）/ 端口-适配器（Ports & Adapters）** 组织代码，通过依赖注入（DI）组装模块。它可以统一管理多台服务器（启动、停止、发送命令、查询状态、批量停止/广播）、备份世界文件、读取服务器日志缓冲区，还可以让 AI（OpenAI 兼容接口）辅助管理与服务器交互。

- **技术栈**：C# / .NET 10.0、Microsoft.Extensions.DependencyInjection 9.0.0（Query 协议与 OpenAI 兼容客户端均为内置实现，无其他第三方包）
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
- [游戏内 AI 触发](#游戏内-ai-触发)
- [玩家请求的授权路由](#玩家请求的授权路由)
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
│   │   ├── AppConfig.cs           # 应用配置（AI 配置 + 服务器路径 + 锁定/隐藏/授权）
│   │   ├── AIConfig.cs            # 单个 AI 服务的连接配置（含环境变量 ApiKey）
│   │   ├── AgentPermissions.cs    # 代理命令授权（允许列表 / 强制询问）
│   │   ├── ServerProperties.cs    # server.properties 读写
│   │   ├── ServerStatus.cs        # Stopped/Starting/Running/Stopping
│   │   ├── CommandArgs.cs         # 命令参数上下文
│   │   ├── CommandResult.cs       # 命令执行结果
│   │   ├── CommandInvocationValidator.cs # 代理命令的调用前校验
│   │   ├── MinecraftChatParser.cs # 游戏内聊天行 / AI 触发指令解析
│   │   └── LogLevel.cs            # 日志级别
│   ├── Ports/                     # 端口（接口）定义
│   │   ├── ICommand / ICommandExecutor / ICommandParser
│   │   ├── IServer / IServerRegistry / IServerProcess
│   │   ├── IInputReader / IOutputWriter
│   │   ├── IConfigurationStore / IAgentService
│   │   └── ...
│   └── UseCases/                  # 用例
│       └── CommandExecutor.cs     # 命令分发执行
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
│   ├── OpenAi/                    # 内置 OpenAI 兼容客户端（Chat Completions + 工具调用）
│   │   └── OpenAiChatClient.cs
│   ├── Query/                     # 内置 Minecraft Query 协议客户端（UDP 握手 + 完整状态）
│   │   └── MinecraftQueryClient.cs
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
      "ChatPrompt": "...",                           // 对话系统提示词（留空用内置默认，可含 {commandList}）
      "EnableAgent": true,                           // 启用智能体
      "MaxIterations": 16,                           // Agent 最大迭代次数
      "MaxContextTokens": 32000,                     // Agent 上下文自动压缩阈值（估算 token 数，<=0 关闭）
      "ContextCompressPrompt": "",                   // 压缩提示词模板（留空用内置默认，可含 {transcript}）
      "AgentPrompt": "..."                           // Agent 系统提示词
    }
  },
  "ServerPaths": {                                   // 服务器注册表：名称 -> 路径
    "survival": "D:\\Minecraft\\survival"
  },
  "AgentPermissions": {                              // 代理命令授权
    "AllowList": [ "$server buf update survival" ],   // 总是允许的命令（以 * 结尾表示前缀匹配）
    "AlwaysAskCommands": [ "$app exec", "$server del" ]   // 每次执行都必须确认（命令名或“命令+动作”范围）
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
- `$app exec` 与 `$server del` 是强制逐次确认的**底线**：即使手工把它们从 `AlwaysAskCommands` 中删除，加载时也会自动补回。

**DefaultAIConfig**：`$ai` 在未显式指定配置名时，会先使用 `DefaultAIConfig` 指向的配置（若为空则报错“未设置默认AI配置”）。可通过 `$ai default [配置名]` 在运行时查看/修改该值。

也可在运行时用命令查看/修改配置，见下文 [命令参考](#命令参考)。

---

## 命令参考

所有命令均以 `$` 开头，通过反射自动注册。**只有 6 个顶层命令**，其余功能都是子命令/动作。

| 命令 | 说明 |
| --- | --- |
| `$ai chat\|agent [配置名] <消息/指令>` | AI 对话 / 代理执行；`default`/`add`/`rm`/`cfg`/`perm` 管理全部 AI 相关配置 |
| `$app exec\|cfg\|exit\|reload\|ptcfg ...` | 高危 / 大权限操作（执行系统命令、改写任意配置等），也是访问任意配置的第二路径 |
| `$file read\|write\|list\|delete <路径> [内容]` | 受控文件操作，仅限白名单目录 |
| `$help [命令名]` | 不带参数列出全部命令与说明；带命令名输出该命令详细用法 |
| `$list` | **只列出全部命令名**（不含说明） |
| `$server <动作> ...` | 服务器管理（含 `hl` 高亮、`lk`/`ulk` 锁定，见下） |

用 `$list` 快速查看命令名，用 `$help <命令名>` 查看某条命令的详细用法。

### `$ai`

```
$ai chat [配置名] <消息>              普通对话（不执行命令）
$ai agent [配置名] <指令>             代理执行（可调用工具，受授权闸门约束）
$ai default [配置名]                  查看 / 设置默认 AI 配置
$ai add <配置名> <Url> <Model> [ApiKeyEnv]   新增 AI 实例
$ai rm <配置名>                       删除 AI 实例
$ai cfg get [路径] | getall | set <路径> <值> | rm <路径>   AI 相关配置的统一读写
$ai perm [get|add|rm|ask|noask] [范围]                      代理命令授权
```

**AI 配置的完整管理面**（`$ai` 覆盖全部 AI 相关配置，其余才需要走 `$app cfg`）：

| 路径 | 说明 |
| --- | --- |
| `EnableAI` | 是否启用 AI |
| `DefaultAIConfig` | 默认实例名（写入时会校验实例存在） |
| `AIConfigs.<配置名>.<字段>` | 实例字段，支持简写 `<配置名>.<字段>`；字段含 `Url`/`Model`/`ApiKey`/`ApiKeyEnv`/`UseApiKeyEnv`/`EnableChat`/`EnableAgent`/`MaxIterations`/`MaxContextTokens`/`ChatPrompt`/`AgentPrompt`/`ContextCompressPrompt` |
| `AgentPermissions.AllowList` / `AlwaysAskCommands` | 代理授权列表，用 `$ai perm` 维护（`cfg set` 会提示改用 perm） |

```bash
$ai cfg get                             # AI 配置总览（实例、密钥来源、提示词、授权）
$ai cfg getall                           # AI 相关配置 JSON（密钥只显示是否已设置）
$ai cfg get ds.MaxIterations             # 读取单个字段（简写形式）
$ai cfg set ds.MaxIterations 32          # 写入单个字段（即时生效）
$ai cfg set DefaultAIConfig local        # 切换默认实例
$ai cfg rm local                         # 删除实例（等价 $ai rm local）
$ai perm                                 # 查看 AllowList / AlwaysAskCommands
$ai perm add "$server ck"                # 加入允许列表（底线命令会被拒绝）
$ai perm rm  "$server ck"                # 从允许列表移除
$ai perm ask "$app cfg set"              # 加入强制逐次询问
$ai perm noask "$ai agent"               # 移出强制询问（内置底线不可移除）
```

**AI 实例增删**（保存后立即热更新到运行中的 AI 服务，`$app reload` 同样会刷新）：

```bash
$ai add ds https://api.deepseek.com/v1 deepseek-chat DEEPSEEK_API_KEY
$ai add local https://localhost:11434/v1 qwen2.5:14b        # 不写环境变量时稍后用 cfg set 补密钥
$ai cfg set local.ApiKey sk-xxx
$ai default ds
$ai rm local
```

- 配置名不能重复、不能含空白或点号（点号是配置路径分隔符），也不能用保留子命令名（`chat`/`agent`/`default`/`add`/`rm`/`remove`/`del`/`delete`，其中 `default` 会被当作“使用默认配置”处理）。
- `Url` 必须是 `http/https` 绝对地址；`ApiKeyEnv` 省略时该实例按“直接填写密钥”处理，命令会提示你用 `$ai cfg set <名称>.ApiKey <密钥>` 补上（明文存盘），或改用环境变量形式（`$ai cfg set <名称>.ApiKeyEnv <环境变量名>`）。
- 新增的实例默认启用对话与代理；若此时还没有默认 AI 配置，会自动把它设为默认。
- 删除的若正是默认配置，`DefaultAIConfig` 会一并清空并提示重新指定；删到 0 个实例时会提醒 `$ai` 将不可用。

### `$app`

`$app` 是**高危 / 大权限命令**的入口，也是访问任意配置的“第二路径”：日常的 AI 配置走 `$ai cfg`、服务器配置走 `$server`，这里放的是它们覆盖不到、或权限更大的操作。

```
$app exec <命令/脚本路径> [参数...]   执行系统命令或脚本（代理调用时每次都要确认）
$app cfg get [路径]          读取任意应用配置；省略路径输出全部 JSON
$app cfg getall              输出全部配置
$app cfg set <路径> <值>     写入任意配置（路径中间的字典键会自动创建）
$app cfg rm <路径>           删除字典条目，如 ServerPaths.tga；属性不可删除，改用 set
$app exit                    退出程序（先停止所有服务器）
$app reload                  重新加载配置文件并重建服务器列表与 AI 实例
$app ptcfg                   打印当前配置（调试用）
```

> `$exec` 自本版本起**不再作为顶层命令**，已并入 `$app exec`：执行任意系统命令与改写任意配置同属高危大权限操作，集中到 `$app` 下，代理调用 `$app exec` 时仍然每次都必须人工确认（内置底线）。

点号路径示例（`$app cfg` 可访问全部配置；AI 相关优先用 `$ai cfg`）：`ServerPaths.tga`、`AgentPermissions.AllowList`、`AIConfigs.default.Url`。

### `$server`

```
$server add <名称> <路径>                           登记新服务器（路径须已存在，支持环境变量）
$server rm <名称>                                   只移除配置引用，保留服务器目录
$server del <名称> confirm                          移除引用并删除整个服务器目录（不可恢复）
$server lk <名称> | ulk <名称>                      锁定 / 解锁服务器（锁定后 run/stop/send/rm/del 被拒）
$server hd <名称> | uhd <名称>                      隐藏 / 显示该服务器在控制台的输出
$server hl [名称]                                   查看 / 切换高亮服务器
$server [服务器名] file <选项> <参数...>            服务器目录内的文件操作
      -r  <路径>            读取文件内容
      -w  <路径> <内容>     写入（覆盖，自动创建目录）
      -a  <路径> <内容>     追加一行（文件不存在时创建）
      -rm <路径>            删除文件或空目录
      -cp <源> <目标>       复制文件/目录（目标已存在时拒绝）
      -mv <源> <目标>       移动 / 重命名（目标已存在时拒绝）
      -ls [路径]            列出目录（默认服务器根目录）
$server cfg get|getall|set|rm [服务器名] [键] [值]   服务器配置 server.properties
$server arg get|set|rm <服务器名> <参数> [值...]     启动参数
$server ck wl|op|bp|bip <服务器名|all> [名称]        名单检查
$server buf read|update <服务器名>                   读取 / 读取并清空输出缓冲区
$server ls                                           列出所有服务器及状态（含高亮/锁定/隐藏标记）
$server bp <服务器名> [备注]                         备份世界目录
$server query <服务器名|all>                         查询 Query 信息
$server status [服务器名|all]                        查看进程状态
$server stop <服务器名|all> [-f]                     停止服务器
$server run <服务器名>                               启动服务器
$server send <服务器名|all> <命令>                   发送 Minecraft 命令
```

**文件操作内建在 `$server file` 下**：`$server [服务器名] file <选项> <参数...>`。

- 省略服务器名时使用**高亮服务器**（与 `cfg`/`arg` 等一致）；也可写成 `$server <服务器名> file ...` 显式指定。
- 选项：`-r` 读取、`-w` 覆盖写入、`-a` 追加一行、`-rm` 删除（文件或空目录）、`-cp` 复制、`-mv` 移动/重命名、`-ls` 列目录。
- 路径**相对该服务器目录**解析（也接受目录内的绝对路径），并做越界校验：`..\..\x`、`C:\Windows\...` 会被直接拒绝，因此**不可能读写到服务器目录之外**——比 `$file` 的白名单机制更严格。
- `-w`/`-a` 自动创建缺失的父目录；`-a` 在文件末尾无换行时先补一个换行，保证每次追加独占一行；`-cp`/`-mv` 目标已存在时**拒绝覆盖**；`-rm` 只允许删除空目录。
- 与 `$file` 共用同一套读写实现，提示与行为一致；`$file` 仍然保留（可访问 `%appdata%` 等白名单目录）。
- 授权范围按选项区分：`$server file read`、`$server file write`、`$server file append`、`$server file delete`、`$server file copy`、`$server file move`、`$server file list`。

```bash
$server hl yz                        # 之后可省略服务器名
$server file -ls                     # 列出服务器根目录
$server file -r  banned-ips.json
$server file -w  ops.json ["Steve","Alex"]
$server file -a  banned-ips.txt 10.0.0.1
$server file -cp ops.json ops.json.bak
$server file -mv config/old.yml config/new.yml
$server file -rm plugins/old.jar
$server yz file -r server.properties  # 也可显式指定服务器
```

**高亮服务器**：`$server hl <名称>` 设置高亮（原 `$hl` 命令已并入 `$server`），不带参数显示当前高亮；省略服务器名时，`cfg`/`arg get`/`ck`/`file` 等动作会回退到高亮服务器。`query` 与 `status` 省略服务器名时表示**全部服务器**（等价于 `all`），不使用高亮。

**锁定服务器**（`$server lk|ulk`）：

- 锁定状态保存在配置的 `LockedServers` 列表中，**立即生效且重启后仍然有效**。
- 锁定只禁止这五类操作：`run`、`stop`、`send`、`rm`、`del`（含 `$server stop all` / `send all` 广播时的跳过）。
- **其余操作不受影响**：`cfg`/`arg`/`ck`/`buf`/`bp`/`query`/`status`/`ls`/`hl`/`hd` 以及文件读写照常可用。
- 目标是 `all` 的 `stop`/`send` 会**跳过**锁定服务器并提示跳过了哪些；`query all`/`status all`/`ck all`/`arg get all` 仍会包含它们。
- 控制台直接把文本发给“高亮且已锁定”的服务器同样被拒绝（等价于 `send`）。
- 配置规范化会清理指向已不存在服务器的锁定项，避免旧条目把重新登记的同名服务器意外锁住。

**隐藏输出**（`$server hd|uhd`）：

- 隐藏后该服务器的**进程输出不再写到控制台与日志**，但仍会进入输出缓冲区——`$server buf read` 与 AI 代理读取日志完全不受影响。
- 只影响进程输出；`正在启动`/`已启动`/`进程退出` 这类工具自身的生命周期提示仍然可见。
- 开关立即生效并持久化在配置的 `HiddenServers` 列表中；`$server ls` 会显示 `[输出已隐藏]` 标记。

```bash
$server lk  survival          # 锁定：run/stop/send/rm/del 被拒
$server run survival          # → 服务器 'survival' 已锁定：run/stop/send/rm/del 一律拒绝，$server ulk survival 可解锁
$server buf read survival     # 查看类操作不受影响
$server hd  survival          # 控制台不再刷该服务器的日志
$server uhd survival          # 恢复显示
$server ulk survival          # 解锁
```

**服务器登记与删除**（三者都会立即重载注册表，无需重启）：

- `add`：把“名称 → 路径”写入 `ServerPaths`。名称不能重复、不能是保留字 `all`，路径必须已存在且未被其他服务器占用（同一目录登记两次会产生两个进程抢同一份存档，直接拒绝）。
- `rm`：只删除配置引用，目录与文件原样保留，随时可用 `add` 加回来。
- `del`：删除引用**并递归删除整个服务器目录**，不可恢复。必须显式带上 `confirm` 才会真正执行；不带 `confirm` 时只打印将要删除的路径并提示先备份。以下情况一律拒绝：服务器正在运行（先 `$server stop`）、驱动器根目录、符号链接/联接点、以及**作为其他已配置服务器上级目录**的路径（避免连带删除其他服务器）。删除顺序是先摘除引用并重载（释放进程句柄），再删目录；若目录删除失败，会提示如何用 `add` 把引用加回来。

```bash
$server add survival D:\Minecraft\survival
$server rm  survival                       # 只摘引用，目录还在
$server del survival                       # 预告要删什么，不动手
$server del survival confirm               # 真正删除引用 + 目录
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

`javaPath` 的读写约定：

- 读取时会跳过 `@echo off`、`REM`、`set`、`pause`、`cd`、shebang 等指令行，取真正启动 Java 的那一行（优先含 `-jar` 或 `@xxx_args.txt` 的行），因此 `java`、`"C:\Program Files\...\java.exe"`、甚至旧版本写出的**未加引号含空格路径**都能正确解析。
- 写回时路径**含空格会自动加引号**，保证下次读取不会被空格截断（这也是此前“无论写什么都被改回 `java`”的原因：加引号的路径被无引号写回后，旧解析逻辑再也认不出启动行，于是回退成默认值并覆盖脚本）。
- 若脚本存在但识别不出启动行，**保持原文件不动**并输出告警（只有脚本不存在时才生成默认脚本），避免覆盖自定义启动脚本。此时可用 `$server arg set <服务器> javaPath <路径>` 显式重建。

**高亮服务器**：用 `$server hl <服务器名>` 设置高亮后，直接在控制台输入的非 `$` 内容会被转发给该服务器，实现“敲命令直达”；若该服务器已锁定，转发会被拒绝并提示解锁命令。


> 说明：命令已整理为 **6 个顶层入口**（`$ai`、`$app`、`$file`、`$help`、`$list`、`$server`），其余功能都是子命令/动作。旧的顶层入口（`$exit`、`$reload`、`$printconfig`、`$run`、`$stop`、`$stopall`、`$send`、`$sendall`、`$query`、`$status`、`$backup`、`$check`、`$highlight`、`$serverconfig`、`$serverargument`、`$bufferread`、`$bufferupdate`、`$appconfig*`、`$serverconfig*`）已全部删除。
>
> `$chat` / `$agent` 等简写别名同样已移除，请使用 `$ai chat [配置名] <消息>` 与 `$ai agent [配置名] <指令>`。

**允许的同义词**（为方便输入而保留，文档与示例统一使用左列的规范写法）：

| 规范写法 | 同义词 |
| --- | --- |
| `$server rm` / `del` / `lk` / `ulk` / `hd` / `uhd` | `remove` / `delete` / `lock` / `unlock` / `hide` / `show` |
| `$server cfg rm`、`$server arg rm` | `remove` |
| `$server ck wl` / `bp` / `bip` | `whitelist` / `banplayer` / `banip` |
| `$ai rm`、`$ai cfg rm`、`$ai perm rm` | `remove`（`$ai` 还接受 `del`、`delete`） |
| `$app cfg rm` | `remove` |

授权范围会把这些同义词**归一化**到规范动作（如 `$server delete` → `$server del`），因此无法用别名绕过 `AlwaysAskCommands` 里的强制询问条目。

### 游戏内 AI 触发

玩家在服务器里发送以下任一形式的聊天即可触发 AI：

```
$chat [配置名] <消息>          以对话模式回复
$agent [配置名] <指令>         以代理模式执行（可调用工具）
@ai <配置名> chat|agent <内容>  显式指定配置与模式
```

省略配置名时使用 `DefaultAIConfig`。回复通过 `tellraw` 发回提出请求的玩家，同时打印到控制台。首个词只有在**确实是已登记的 AI 配置名**时才会被当作配置名，因此 `$chat 你好 世界` 会整句作为消息发送，而不是把「你好」当成配置名。

识别依据是 Minecraft 服务端的聊天行格式：

```
[21:08:29] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: <Sparky_0890> 点任务啊
```

解析时只在行尾锚定 `<玩家名> 消息`，因此 `[21:08:29]`、`[Server thread/INFO]` 这类日志前缀里的方括号不会被误当成尖括号内容。切分点是日志前缀的结束标记 `]: `（取**第一次**出现），所以聊天正文里出现 `: ` 也不会改变玩家名——玩家无法通过在聊天里写 `hi: <Alex> $agent ...` 来冒用他人身份。**聊天正文中出现多于一对尖括号时不转发给 AI**——玩家聊天本身仍能被正常识别，只是不会触发 AI 命令，例如 `<a> <b>` 会被当作普通聊天。

### 玩家请求的授权路由

代理在执行命令前会调用授权网关，网关按请求来源自动决定向谁询问：

| 请求来源 | 行为 |
| --- | --- |
| 本地控制台 | 在控制台弹出提问，等待操作员输入 y / a / n |
| 游戏内玩家、**非管理员** | **直接拒绝**，不产生任何提问 |
| 游戏内玩家、**管理员** | 用 `tellraw` 把提问发到游戏内，等待该玩家在聊天中作答 y / a / n |

判定管理员的方式是读取该服务器目录下的 `ops.json`（与 `$server ck op` 同一个数据源）；文件不存在或不可读时按“非管理员”处理（失败关闭）。

**先校验命令与参数，再申请授权**：代理调用 `request_permission` 或 `execute_command` 时，会先检查命令名是否已注册，再通过命令自身的参数校验钩子（`IArgValidatingCommand`）检查子命令/动作是否合法。不通过的直接拒绝回传，**不弹授权提问、不执行、也不会被写进白名单**。

```
命令 '$check' 不存在，未申请授权也未执行。可用命令: $ai $app $file $help $list $server
$server cfg 未知子动作 'bogus'，可用: get、getall、set、rm、remove。可先用 $help $server 查看用法。
```

这样做有两个好处：不会为你根本执行不了的命令浪费一次确认；也不会让无效命令（例如重构前的 `$check`）沉淀到 `AgentPermissions.AllowList` 里。目前实现该钩子的命令为 `$server`、`$app`、`$ai`、`$file`、`$help`。

**顺序很关键**：管理员校验发生在白名单判定**之前**。也就是说玩家一旦被取消管理员，即使命令早就在 `AllowList` 里也会被直接拒绝，不会凭历史白名单继续授权。

**白名单按“命令 + 子动作”匹配，不匹配参数**：把命令按「去除首尾空白 + 合并内部连续空白」规范化后，取到**动词那一层**作为授权范围（忽略大小写）。

| 调用 | 授权范围 |
| --- | --- |
| `$server ck op yz Alice` | `$server ck op` |
| `$server cfg get yz` / `$server cfg set yz k v` | `$server cfg get` / `$server cfg set` |
| `$server buf read yz` | `$server buf read` |
| `$server ls` / `$server run yz` / `$server stop all` | `$server ls` / `$server run` / `$server stop` |
| `$server add yz D:\srv` / `$server rm yz` | `$server add` / `$server rm` |
| `$server del yz confirm`（别名 `delete`）/ `$server rm yz`（别名 `remove`） | `$server del` / `$server rm`（别名会归一化，防止绕过强制询问） |
| `$app cfg set X Y` | `$app cfg set` |
| `$app exec whoami` | `$app exec`（只读命令到命令级） |

因此批准一次 `$server ck op` 只覆盖 `$server ck op`：换服务器名/玩家名不再询问，但 `$server ck wl`、`$server ck bip`、`$server cfg get` 仍需各自授权。写入白名单的也是这个范围字符串；会话内一次性放行同样按范围记录。

上层条目会向下覆盖：手工写 `$server ck` 可一次覆盖 `op`/`wl`/`bp`/`bip`，写 `$server` 可覆盖该命令的全部动作。为兼容历史配置，条目里带 `*` 的按整串前缀匹配，带空格的完整命令（如 `$server cfg get`）按「本次调用以它为前缀」匹配。

> 安全提示：范围到动词为止。若希望某个动作始终单独确认，把对应的范围字符串加入 `AgentPermissions.AlwaysAskCommands`（默认 `$app exec` 与 `$server del`）。

玩家的作答必须与提问精确对应：只有发起该提问的那名玩家在其所在服务器发送 `y`/`yes`、`a`/`always`、`n`/`no` 才算有效作答，其余聊天文本不会被当作答消费。同一玩家同时只能有一个待确认的提问，重复请求会被直接拒绝。等待超过 120 秒视为拒绝。`$app exec` 依然每次都要确认，管理员选择 `a` 也不会被写入允许列表。

代理模式下，指令开头会带上来源标注，代理据此决定权限：

| 来源标注 | 权限 |
| --- | --- |
| `[来自控制台]` | 本地控制台操作员，**拥有完全权限**：不做管理员校验，也不以“危险操作/权限不足”为由拒绝，直接按要求执行（命令仍旧经过授权闸门确认） |
| `[来自服务器 'X' 的玩家 'Y']` | 游戏内玩家：先执行 `$server ck op X Y` 判断其是否为管理员，不是则礼貌拒绝 |

标注与指令正文之间以空格分隔，提示词中的格式与这里完全一致（`[来自控制台]` / `[来自服务器 'X' 的玩家 'Y']`）。

---

## 架构概览

MSL-CLI 遵循**依赖倒置**原则：

- **Core（领域/端口/用例）**：定义领域模型与抽象接口，不依赖任何外部框架细节。
- **Infrastructure（基础设施）**：实现端口，包括服务器进程管理、配置持久化、日志、AI 服务，以及所有命令。
- **CLI（适配器）**：控制台输入/输出适配器。

依赖注入容器在 `Program.cs` 中组装；`CommandParser` 通过反射扫描所有实现 `ICommand` 的类型并自动注册命令。

### 命令组织方式

命令集中在 `Infrastructure/Commands/`，每个文件负责一块职责，命令之间不互相渗透实现，也不存在多层穿透：

- **6 个顶层命令各占一个文件**：`AICommand.cs`、`AppCommand.cs`、`FileCommand.cs`、`HelpCommand.cs`、`ListCommand.cs`、`ServerCommand.cs`。
- `$server` 是动作分发器（`ServerCommand.cs`），把 `cfg`/`arg`/`ck`/`buf` 交给各自独立的处理器：`ServerConfigHandler.cs`、`ServerArgsHandler.cs`、`ServerCheckHandler.cs`、`ServerBufferHandler.cs`；生命周期动作在 `ServerLifecycleHandler.cs`（`add`/`rm`/`del`）、`ServerLockHandler.cs`（`lk`/`ulk`/`hd`/`uhd`）中实现；`ls`/`query`/`status`/`stop`/`run`/`send` 在 `ServerCommand.cs` 与 `ServerActionHandlers.cs` 中实现，`bp` 在 `BackupCommand.cs` 中实现。共享的目标服务器解析在 `ServerTargetResolver.cs`。
- 服务器目录内的文件操作在 `ServerFileHandler.cs` + `FileOperations.cs`（与 `$file` 共用同一套读写实现）。
- 服务器名单文件（`ops.json`、`whitelist.json`、`banned-players.json`、`banned-ips.json`）的读取位于 `$server ck`（`ServerCheckHandler.cs`）；服务器端口不暴露 `GetOps`/`IsOp` 之类的转发方法。
- 应用配置的点号路径读写位于 `$app cfg`（`AppConfigPath.cs` + `AppCommand.cs`）。
- **不再有子命令级的独立入口**：`$serverconfig*`、`$serverargument`、`$bufferread`、`$bufferupdate`、`$appconfig*`、`$run`、`$stop`、`$send` 等都已删除，只能通过对应顶层命令 + 动作调用。

### 系统提示词

`$ai chat` 与 `$ai agent` 都会先给模型一条系统提示词，二者互不影响：

- **对话**使用 `AIConfigs.<名称>.ChatPrompt`（留空回退内置默认）。它只约束回答范围与风格：可以回答问题、给出建议，但**不能执行命令**，需要实际动手时会提示改用 `$ai agent`。请求体只有「系统提示词 + 用户消息」两条消息，不挂载任何工具。
- **智能体**使用 `AgentPrompt`，其中包含授权规则与“执行 → 等待 → 验证”循环协议，并挂载三个工具。
- 两个模板都支持 `{commandList}` 占位符，会替换为按名称排序的命令摘要（内置默认提示词已包含该占位符）。用 `$app cfg get AIConfigs.<名称>.ChatPrompt` 可查看当前值，`$app cfg` 中提示词字段以“(内置默认)/(已自定义)”标示来源。
- **智能体提示词按来源标注区分权限**：`[来自控制台]` 视为拥有完全权限的操作员指令（不做管理员校验、不以“危险/无权限”为由拒绝），`[来自服务器 'X' 的玩家 'Y']` 必须先 `$server ck op` 校验。详见下文[代理命令授权](#代理命令授权)。
- 与内置默认完全一致的提示词**不会写入** `config.json`，因此升级后修订过的内置提示词会自动生效，配置文件也不会被大段默认文本占满；只有自定义内容才落盘。提示词在**每次请求时**从内存中的 AI 配置读取：用 `$ai cfg set`（保存后自动热更新）或 `$app reload` 修改即时生效；只有绕过 `$ai` 直接改配置文件（`$app cfg set AIConfigs...` 或手工编辑）时才需要再执行 `$app reload` 或重启程序。

AI 智能体（`OpenAiAgentService`）通过 OpenAI 的 Function Calling 能力提供三个工具：

- `request_permission`：执行前向本地操作员申请授权，并把裁决结果作为文本回传。
- `execute_command`：执行一条 `$` 开头的命令——**仅在已获授权**（命中允许列表或已获批）时才会真正执行。
- `sleep`：等待指定秒数。

Agent 依据系统提示词中的“执行 → 等待 → 验证日志（失败重试）”循环协议，自动完成多步操作。

### 上下文自动压缩

Agent 的多步任务会把每轮的工具调用与返回累积进对话历史，长时间运行（尤其是 `$server buf update` 之类的大段日志）很容易撑爆模型窗口。为此每轮请求前都会估算上下文长度：

- 阈值由 `AIConfigs.<名称>.MaxContextTokens` 读取（默认 `32000`，**小于等于 0 表示关闭**）。
- 估算值超过阈值且历史部分足够长时，会先调用同一个模型把此前的执行记录压缩成一段摘要，再用摘要替换历史，然后才发起本轮请求（日志中会输出“上下文已压缩: 前 → 后 估算 tokens”）。
- 摘要由 `ContextCompressPrompt` 模板生成（留空使用内置默认提示词，模板中的 `{transcript}` 会替换为执行记录）；摘要长度上限约为阈值的 1/8，压缩后远低于触发条件，因此不会每轮都重复压缩。
- 若超长主要来自系统提示词或指令本身（历史占比不足阈值的 1/4），压缩收益有限，此时只记录一条告警并继续；若模型压缩调用失败或返回空文本，会自动退化为“保留记录尾部并截断”，不会中断任务。
- 长度估算不依赖分词器：中日韩字符按 1 字符 ≈ 1 token、其余字符按 4 字符 ≈ 1 token，并额外计入每条消息与工具定义的开销。

### 代理命令授权

Agent 执行的每条命令都经过授权闸门，未经确认的命令不可能被执行：

1. `execute_command` 会先检查该命令是否持有会话内一次性放行（由先前的 `request_permission` 发放）或命中持久化允许列表。
2. 若都未命中，会**就地**向本地操作员提问，因此模型跳过 `request_permission` 也无法绕过检查。
3. 非控制台来源的请求（例如游戏内玩家触发）不会弹出提问，而是直接拒绝——只有本地操作员才有权授权命令。

提问支持 `y`（仅本次允许）、`a`（总是允许，写入 `AgentPermissions.AllowList` 持久化）、`n`（拒绝）；无法识别的作答一律按拒绝处理。

`$app exec` 与 `$server del` 位于内置的 `AlwaysAskCommands` 列表中：**每次执行都必须单独确认**，且永远不会被写入允许列表。二者的区别是条目粒度——两者都是“命令 + 动作”范围，只覆盖该动作（`$app exec` 不影响 `$app cfg`，`$server del` 不影响 `$server run`）；因此即使 `AllowList` 里写着 `$app` 或 `$server`，代理的 `$app exec`、`$server del` 仍然必须逐次询问，别名（`$server delete`）也会被归一化成同一范围，无法绕过。授权列表可用 `$ai perm` 查看与维护。

控制台读取由单一输入泵仲裁：`ConsoleInputReader` 持续读取输入行，在提问挂起期间该行会交给提问方，而不会作为控制台命令派发。任何组件需要向操作员提问时，都通过 `IInputReader.ReadLineAsync` 发起。

---

## 注意事项

- `$app exec`、`$server run`、`$server stop` 等破坏性或耗时命令在执行前会要求 AI 确认，防止误操作。代理发起的命令一律经过授权闸门：只有命中 `AgentPermissions.AllowList` 或经操作员确认后才会执行，其中 `$app exec` 与 `$server del` 每次都必须确认。
- `$server del <名称> confirm` 会**递归删除整个服务器目录且不可恢复**：请先 `$server bp <名称>` 备份，或先用 `$server rm <名称>` 只移除引用。
- 备份运行中的服务器可能产生不一致数据，建议先停止服务器再备份。
- `$file` 命令受白名单限制，只能访问服务器目录与 `%APPDATA%\MSL_CLI`。
- 修改 `server.properties` 采用临时文件 + 替换的原子写入方式。
- 停止服务器时，先发送 `stop` 命令并等待最多 30 秒，超时后强制终止。
- 每台服务器拥有**独立的系统进程与日志缓冲**，输出彼此隔离，不会出现多服务器日志混在一起的情况。

---

## License

[MIT](LICENSE.txt) © 2026 Li Jiakun
