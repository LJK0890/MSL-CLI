# MSL-CLI

A command-line tool for managing multiple Minecraft servers, with built-in AI chat and agent capabilities.

MSL-CLI is organized using **Clean Architecture / Ports & Adapters** and assembled with dependency injection (DI). It can start/stop multiple servers, send commands, query status, back up world files, read server log buffers, and let an AI (OpenAI-compatible API) help you operate your servers.

- **Stack**: C# / .NET 10.0, Microsoft.Extensions.DependencyInjection, OpenAI SDK, McQuery.Net
- **License**: MIT

> 其他语言版本：简体中文 — [README-ch.md](README-ch.md)

---

## Table of Contents

- [Features](#features)
- [Project Structure](#project-structure)
- [Requirements](#requirements)
- [Build & Run](#build--run)
- [Configuration](#configuration)
- [Command Reference](#command-reference)
- [Architecture Overview](#architecture-overview)
- [Notes](#notes)

---

## Features

- **Multi-server management**: Manage multiple Minecraft servers at once — start, stop, send commands, and query status.
- **Highlight server**: Route typed input to a selected "highlighted" server so you can issue in-game commands directly.
- **Server configuration**: Read/modify `server.properties`, parse and persist launcher arguments (`run.bat` / `run.sh`, `user_jvm_args.txt`).
- **Log buffering**: Capture server stdout and read or "read-and-clear" the buffer for diagnostics and AI use.
- **World backup**: Package a server's world directory into a zip under `backups`.
- **AI chat / agent**: Connects to an OpenAI-compatible API; provides a unified `$ai` entry (`chat`/`agent`/`default`) with `DefaultAIConfig` to select the default AI config; Agent mode auto-manages servers via an "execute → wait → verify log" loop.
- **Access checks**: Query/validate whitelist, OP, banned-player, and banned-IP lists.
- **Sandboxed file operations**: File access is restricted to configured server directories and `%APPDATA%\MSL_CLI`.
- **Colored console + file logging**: Logs go to both the console (ANSI colors) and `%APPDATA%\MSL_CLI\Log-*.txt`.

---

## Project Structure

```
MSL-CLI/
├── Program.cs                     # Entry point: DI wiring, input listening, lifecycle
├── MSL-CLI.csproj
├── MSL-CLI.slnx
├── Core/                          # Domain & Ports layer
│   ├── Domain/                    # Domain models
│   │   ├── AppConfig.cs           # App config (AI configs + server paths)
│   │   ├── AIConfig.cs            # AI service connection config (env-var ApiKey)
│   │   ├── ServerProperties.cs    # server.properties read/write
│   │   ├── ServerStatus.cs        # Stopped/Starting/Running/Stopping
│   │   ├── CommandArgs.cs         # Command argument context
│   │   ├── CommandResult.cs       # Command execution result
│   │   └── LogLevel.cs            # Log levels
│   ├── Ports/                     # Port (interface) definitions
│   │   ├── ICommand / ICommandExecutor / ICommandParser
│   │   ├── IServer / IServerRegistry / IServerProcess / IServerBuffer
│   │   ├── IInputReader / IOutputWriter
│   │   ├── IConfigurationStore / IAgentService
│   │   └── ...
│   └── UseCases/                  # Use cases
│       ├── CommandExecutor.cs     # Command dispatch & execution
│       └── ServerOrchestrator.cs  # Server orchestration
├── Infrastructure/                # Infrastructure layer (adapters & command implementations)
│   ├── ServerManager.cs           # IServer impl: process lifecycle, output buffer, AI trigger
│   ├── ServerProcess.cs           # System process wrapper
│   ├── ServerRegistry.cs          # Server registry
│   ├── ServerArgument.cs          # Launcher argument parsing & persistence
│   ├── CommandParser.cs           # Reflection-based command registration
│   ├── FileConfigurationStore.cs  # JSON config persistence
│   ├── CompositeOutputWriter.cs   # Multiplexed output (console + file)
│   ├── FileOutputWriter.cs        # File logging
│   ├── AgentService .cs           # OpenAI chat / agent service
│   ├── AgentPermissionGateway.cs  # Operator authorization for agent commands
│   └── Commands/                  # All `$`-prefixed command implementations
└── CLI/                           # CLI adapters (console IO)
    ├── ConsoleOutputWriter.cs
    └── ConsoleInputReader.cs
```

---

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later
- A working Java runtime for each server (launched via `run.bat` / `run.sh`)
- (Optional) An OpenAI-compatible API for the AI chat / agent features

---

## Build & Run

```bash
# Build
dotnet build

# Run (Release mode)
dotnet run -c Release
```

After startup you enter an interactive prompt. Type a `$`-prefixed command for management operations; any other input is sent as a command to the currently highlighted server (if one is set).

---

## Configuration

Configuration lives in `%APPDATA%\MSL_CLI\config.json` (on first run it may load from `config.default.json` next to the executable). Main structure:

```jsonc
{
  "EnableAI": true,                                  // whether AI is enabled
  "DefaultAIConfig": "default",                      // default AI config name (used when $ai omits one)
  "AIConfigs": {                                     // AI service configs (you may define several)
    "default": {
      "Url": "https://api.openai.com/v1",
      "Model": "gpt-4o",
      "UseApiKeyEnv": true,                          // read ApiKey from an env var
      "ApiKey": "",                                  // literal key (when UseApiKeyEnv=false)
      "ApiKeyEnv": "OPENAI_API_KEY",                 // env var name
      "EnableChat": true,                            // enable chat
      "EnableAgent": true,                           // enable agent
      "MaxIterations": 16,                           // max agent iterations
      "AgentPrompt": "..."                           // agent system prompt
    }
  },
  "ServerPaths": {                                   // server registry: name -> path
    "survival": "D:\\Minecraft\\survival"
  },
  "AgentPermissions": {                              // agent command authorization
    "AllowList": [ "$server buf update survival" ],   // always-allowed commands ("*" suffix = prefix match)
    "AlwaysAskCommands": [ "$exec" ]                 // must be confirmed on every execution
  }
}
```

**API Key**: When `UseApiKeyEnv = true`, the key is read from the environment variable named by `ApiKeyEnv`, so no plaintext key needs to be stored in the file.

**Config persistence & recovery**:

- Config is read from `%APPDATA%\MSL_CLI\config.json` first; on absence or parse failure it falls back to `config.default.json` next to the executable, then to the built-in defaults.
- Loading **normalizes** the result: `null` collections (`AIConfigs`, `ServerPaths`, `AgentPermissions` and their sub-lists) are repaired and `null` dictionary entries are dropped, so a damaged field cannot crash startup.
- Saving uses backup → temp file → atomic replace: the previous file is copied to `config.json.bak` before being overwritten, and a failed write cleans up its `.tmp` leftover.
- A parse failure is reported to the console and log instead of being silently swallowed, and the original file is **not** rewritten during loading.
- To roll back, copy `config.json.bak` over `config.json`; `FileConfigurationStore.RestoreFromBackup()` does the same in-process and preserves the damaged file as `config.json.corrupt`.
- `$exec` is a **floor** for per-execution confirmation: even if it is manually removed from `AlwaysAskCommands`, it is restored on load.

**DefaultAIConfig**: When `$ai` does not specify a config name, it uses the config pointed to by `DefaultAIConfig` (an error "未设置默认AI配置" is shown if it is empty). View or change it at runtime with `$ai default [config]`.

You can also inspect/modify configuration at runtime — see [Configuration Commands](#configuration-commands).

---

## Command Reference

All commands are prefixed with `$` and auto-registered via reflection. There are **only 8 top-level commands**; everything else is a subcommand/action.

| Command | Description |
| --- | --- |
| `$ai chat\|agent [config] <message/instruction>` | Chat / agent execution; `default [config]` views or sets the default AI config |
| `$app cfg\|exit\|reload\|ptcfg ...` | Application-level operations (below) |
| `$exec <command/script path> [args...]` | Run a system command or script (an AI Agent must confirm first) |
| `$file read\|write\|list\|delete <path> [content]` | Sandboxed file ops, whitelist directories only |
| `$help [command]` | No argument: list all commands with descriptions; with a name: that command's detailed usage |
| `$hl <server>` | Switch the highlighted server; no argument shows the current one |
| `$list` | **List command names only** (no descriptions) |
| `$server <action> ...` | Server management (below) |

Use `$list` for a quick name dump and `$help <command>` for one command's detailed usage.

### `$app`

```
$app cfg get [path]          Read app config; with no path, print the whole JSON
$app cfg getall              Print the entire config
$app cfg set <path> <value>  Write config (a missing dictionary key in the path is created)
$app cfg rm <path>           Delete a dictionary entry, e.g. ServerPaths.tga; properties cannot be removed
$app exit                    Exit the program (stops all servers first)
$app reload                  Reload config and rebuild the server list
$app ptcfg                   Print the current config (debug)
```

Dot paths look like `AIConfigs.default.Url`, `ServerPaths.tga`, `AgentPermissions.AllowList`.

### `$server`

```
$server cfg get|getall|set|rm [server] [key] [value]   server.properties
$server arg get|set|rm <server> <arg> [value...]       launch arguments
$server ck wl|op|bp|bip <server|all> [name]            list checks
$server buf read|update <server>                       read / read-and-clear the output buffer
$server ls                                             list all servers and their status
$server bp <server> [remark]                           back up the world directory
$server query <server|all>                             query server Query info
$server status [server|all]                            show process status
$server stop <server|all> [-f]                         stop a server
$server run <server>                                   start a server
$server send <server|all> <command>                    send a Minecraft command
```

`$server cfg` falls back to the highlighted server when the server name is omitted; `cfg rm` preserves comments and other keys.

**`$server ck` subcommands**: `wl` (whitelist), `op` (operators), `bp` (banned players), `bip` (banned IPs). Without a name it lists everything; with a name it checks membership.

**`$server arg` launch argument model** (`ServerArgument`):

| Argument | Type | Permissions | Notes |
| --- | --- | --- | --- |
| `javaPath` | String | read/write, **not deletable** | Java executable path; empty values are rejected |
| `jvmArgs` | List | read/write/delete (single or all) | persisted to `user_jvm_args.txt` |
| `jarArgs` | String | read/write, **not deletable** | accepts `server.jar`, `@libraries/.../win_args.txt`, `-jar server.jar` |
| `appendArgs` | List | read/write/delete (single or all) | e.g. `nogui` |
| `javaArgs` / `all` | — | read-only | aggregate view |

```bash
$server arg get yz jvmArgs                 # indexed listing ([0] -Xms8G ...)
$server arg get yz jvmArgs element 1       # read one element
$server arg set yz jvmArgs -Xms4G -Xmx4G   # replace the whole list
$server arg set yz javaPath C:\Java\bin\java.exe
$server arg rm  yz jvmArgs -Xmx4G          # delete one element
$server arg rm  yz jvmArgs all             # clear the list
$server arg rm  yz javaPath                # rejected (javaPath is not deletable)
```

The launch script is rebuilt as `{javaPath} @user_jvm_args.txt {jarArgs} {appendArgs}`, matching the official Forge / NeoForge layout, and files are left untouched when nothing changed.

**Highlight server**: once a server is highlighted, non-`$` input typed at the console is forwarded to it.

> Note: the command set is organized into **8 top-level entry points** (`$ai`, `$app`, `$exec`, `$file`, `$help`, `$hl`, `$list`, `$server`); everything else is a subcommand/action. The old top-level entries (`$exit`, `$reload`, `$printconfig`, `$run`, `$stop`, `$stopall`, `$send`, `$sendall`, `$query`, `$status`, `$backup`, `$check`, `$highlight`, `$serverconfig`, `$serverargument`, `$bufferread`, `$bufferupdate`, `$appconfig*`, `$serverconfig*`) were all removed.
>
> The `$chat` / `$agent` shorthand aliases are gone too — use `$ai chat [config] <message>` and `$ai agent [config] <instruction>`.


### In-game AI trigger

A player can trigger the AI by sending any of these chat forms:

```
$chat [config] <message>            reply in chat mode
$agent [config] <instruction>       run in agent mode (may call tools)
@ai <config> chat|agent <content>   explicitly pick config and mode
```

When the config name is omitted, `DefaultAIConfig` is used. The reply is sent back to the requesting player via `tellraw` and also printed to the console.

Recognition is based on the Minecraft server chat line format:

```
[21:08:29] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: <Sparky_0890> 点任务啊
```

The `<player> message` part is anchored at the end of the line, so log prefixes such as `[21:08:29]` and `[Server thread/INFO]` are never mistaken for angle-bracket content. **A chat body containing more than one angle-bracket pair is not forwarded to the AI** — the player chat is still recognized, it simply does not trigger an AI command, so e.g. `<a> <b>` is treated as plain chat.

### Authorization routing for player requests

Before running a command the agent asks the permission gateway, which routes the prompt based on where the request came from:

| Request origin | Behaviour |
| --- | --- |
| Local console | Prompts on the console and waits for the operator to type y / a / n |
| In-game player, **not an operator** | **Refused immediately**, no prompt at all |
| In-game player, **operator** | Sends the prompt in-game with `tellraw` and waits for that player to answer y / a / n in chat |

Operator status is read from `ops.json` in that server's directory (the same source `$server ck op` uses); if the file is missing or unreadable the player is treated as a non-operator (fail closed).

**Validate the command and its arguments before asking for authorization**: when the agent calls `request_permission` or `execute_command`, the command name is first checked against the registered commands, then the command's own argument hook (`IArgValidatingCommand`) checks the subcommand/action. A failure is refused outright — **no prompt, no execution, and nothing written to the allow-list**.

```
命令 '$check' 不存在，未申请授权也未执行。可用命令: $ai $app $exec $file $help $hl $list $server
$server cfg 未知子动作 'bogus'，可用: get、getall、set、rm、remove。可先用 $help $server 查看用法。
```

This avoids spending an approval on something that cannot run, and stops invalid commands (such as a pre-refactor `$check`) from accumulating in `AgentPermissions.AllowList`. The hook is currently implemented by `$server`, `$app`, `$ai`, `$file` and `$help`.

**Order matters**: the operator check runs *before* the allow-list lookup. So once a player is de-opped, even a command already in `AllowList` is refused — a historic allow-list entry cannot keep granting authority.

**The allow-list matches "command + sub-action", not arguments**: commands are normalized (trimmed, runs of internal whitespace collapsed) and the **verb level** becomes the authorization scope, case-insensitively.

| Call | Scope |
| --- | --- |
| `$server ck op yz Alice` | `$server ck op` |
| `$server cfg get yz` / `$server cfg set yz k v` | `$server cfg get` / `$server cfg set` |
| `$server buf read yz` | `$server buf read` |
| `$server ls` / `$server run yz` / `$server stop all` | `$server ls` / `$server run` / `$server stop` |
| `$app cfg set X Y` | `$app cfg set` |
| `$exec whoami` | `$exec` (read-only commands stop at the command level) |

Approving `$server ck op` therefore covers only `$server ck op`: a different server or player no longer prompts, while `$server ck wl`, `$server ck bip` and `$server cfg get` each still need their own approval. That scope string is what gets written to the allow-list, and session passes are keyed the same way.

A broader entry covers narrower ones: writing `$server ck` by hand covers `op`/`wl`/`bp`/`bip`, and `$server` covers every action of that command. For backward compatibility, entries containing `*` match as a whole-string prefix, and entries containing a space (e.g. `$server cfg get`) match when the current call starts with them.

> Security note: the scope stops at the verb. To keep one action confirmed every time, add its scope string to `AgentPermissions.AlwaysAskCommands` (which contains only `$exec` by default).

A player answer must correspond to the outstanding prompt: only the player who was asked, on that server, can answer with `y`/`yes`, `a`/`always` or `n`/`no`; any other chat text is not consumed as an answer. A player can have only one pending prompt at a time — a second request is refused outright. Waiting longer than 120 seconds counts as a refusal. `$exec` still requires confirmation every single time, and an operator choosing `a` never writes it to the allow-list.

In agent mode the player message is prefixed with `[来自服务器 'X' 的玩家 'Y']` so the agent can run `$server ck op <server> <player>` to check whether the sender is an operator.

---

## Architecture Overview

MSL-CLI follows the **dependency inversion** principle:

- **Core (domain/ports/use cases)**: defines domain models and abstract interfaces, independent of external framework details.
- **Infrastructure**: implements the ports — server process management, config persistence, logging, AI service, and all commands.
- **CLI (adapters)**: console input/output adapters.

The DI container is wired in `Program.cs`; `CommandParser` scans for all types implementing `ICommand` via reflection and registers them automatically.

### Command layout

Commands live in `Infrastructure/Commands/`, each file owning one responsibility — no command reaches into another's implementation, and none is buried in the server layer:

- **Each of the 8 top-level commands has its own file**: `AICommand.cs`, `AppCommand.cs`, `ExecCommand.cs`, `FileCommand.cs`, `HelpCommand.cs`, `HighlightCommand.cs`, `ListCommand.cs`, `ServerCommand.cs`.
- `$server` is an action dispatcher (`ServerCommand.cs`). `cfg`/`arg`/`ck`/`buf` go to dedicated handlers — `ServerConfigHandler.cs`, `ServerArgsHandler.cs`, `ServerCheckHandler.cs`, `ServerBufferHandler.cs` — while `ls`/`bp`/`query`/`status`/`stop`/`run`/`send` are implemented in `ServerCommand.cs` and `ServerActionHandlers.cs`. Shared target resolution is in `ServerTargetResolver.cs`.
- Reading a server's list files (`ops.json`, `whitelist.json`, `banned-players.json`, `banned-ips.json`) lives in `$server ck` (`ServerCheckHandler.cs`). The server port exposes no `GetOps`/`IsOp` helpers.
- App-config dot-path reading/writing lives in `$app cfg` (`AppConfigPath.cs` + `AppCommand.cs`).
- **There are no subcommand-level entry points any more**: `$serverconfig*`, `$serverargument`, `$bufferread`, `$bufferupdate`, `$appconfig*`, `$run`, `$stop`, `$send` and friends were all removed; they are reachable only as a top-level command plus an action.

The AI agent (`OpenAiAgentService`) exposes three tools via OpenAI Function Calling:

- `request_permission`: ask the local operator to authorize a command before it runs, returning the decision as text.
- `execute_command`: run a `$`-prefixed command — **only after** it is authorized (allow-list hit or a granted request).
- `sleep`: wait a number of seconds.

Following the "execute → wait → verify log (retry on failure)" loop from the system prompt, the Agent can complete multi-step operations automatically.

### Agent command authorization

Every command the agent runs is gated, so an unconfirmed command can never execute:

1. `execute_command` first checks whether the command has a session pass (from a prior `request_permission`) or matches the persisted allow-list.
2. If not, it asks the local operator inline, so skipping `request_permission` cannot bypass the check.
3. Requests that do **not** originate from the console (for example in-game player triggers) are never prompted; they are refused, because only the local operator may authorize commands.

The prompt offers `y` (allow once), `a` (always allow, persisted to `AgentPermissions.AllowList`), and `n` (deny); anything unrecognized is treated as a denial.

`$exec` is on the built-in `AlwaysAskCommands` list: it requires confirmation on **every** execution and can never be added to the allow-list.

Console reading is arbitrated by a single input pump: `ConsoleInputReader` keeps reading lines, and while a prompt is pending the line is delivered to the asking component instead of being dispatched as a console command. `IInputReader.ReadLineAsync` is the entry point any component uses to ask the operator a question.

---

## Notes

- Destructive or long-running commands (`$exec`, `$server run`, `$server stop`, ...) require AI confirmation before execution. Agent-issued commands are always gated: they run only when allowed by `AgentPermissions.AllowList` or explicitly confirmed, and `$exec` is confirmed every single time.
- Backing up a running server can produce inconsistent data; stop the server first when possible.
- The `$file` command is whitelist-restricted to server directories and `%APPDATA%\MSL_CLI`.
- `server.properties` writes use an atomic temp-file + replace strategy.
- On stop, a `stop` command is sent and the process is awaited up to 30 seconds before force-killing.
- Each server has its **own process and log buffer**, so their outputs are isolated and never mixed together.

---

## License

[MIT](LICENSE.txt) © 2026 Li Jiakun
