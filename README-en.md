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
  "DefaultAIConfig": "default",                      // default AI config name (used when $ai/$chat/$agent omit one)
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
  }
}
```

**API Key**: When `UseApiKeyEnv = true`, the key is read from the environment variable named by `ApiKeyEnv`, so no plaintext key needs to be stored in the file.

**DefaultAIConfig**: When `$ai` / `$chat` / `$agent` do not specify a config name, they use the config pointed to by `DefaultAIConfig` (an error "未设置默认AI配置" is shown if it is empty). View or change it at runtime with `$ai default [config]`.

You can also inspect/modify configuration at runtime — see [Configuration Commands](#configuration-commands).

---

## Command Reference

All commands are prefixed with `$` and auto-registered via reflection.

### General

| Command | Description |
| --- | --- |
| `$help` | Show all available commands and their descriptions |
| `$exit` | Exit the program (stops all servers first) |
| `$list` | List all configured servers and their status |
| `$status [server]` | Show process status (default: all — PID, memory, CPU time) |
| `$reload` | Reload config and rebuild the server list (skips running servers) |

### Server Management

| Command | Description |
| --- | --- |
| `$run <server>` | Start the given server (auto-highlights if none set) |
| `$stop <server> [-f]` | Stop a server (`-f` force-kills) |
| `$stopall` | Stop all running servers |
| `$send <server> <command>` | Send a Minecraft command to a server |
| `$sendall <command>` | Send a command to all running servers |
| `$server <subcommand> ...` | Unified server management entry (see below) |
| `$highlight / $hl <server>` | Switch the highlighted server; no arg shows the current one |
| `$serverargument get\|set ...` | View/modify server launch arguments |

`$server` subcommands:

```
$server run    <server|all> [-f]     Start a server
$server stop   <server|all> [-f]     Stop a server
$server send   <server|all> <cmd>    Send a command
$server buffer read|update <server>  Read/clear the buffer
$server config get|set <server>...   View/modify server.properties
$server query  <server|all>          Query server Query info
$server status [server]              Show process status
```

**Highlight server**: once a server is highlighted, non-`$` input typed at the console is forwarded to it.

### Buffer / Config

| Command | Description |
| --- | --- |
| `$bufferread / $bufr <server>` | Read the server buffer (without clearing) |
| `$bufferupdate / $bufu <server>` | Read and clear the server buffer |
| `$serverconfigget / $scg [server] [key]` | View `server.properties` (server name optional — uses highlight) |
| `$serverconfigset / $scs [server] <key> <value>` | Modify `server.properties` (atomic write) |
| `$serverargument get\|set ...` | View/modify launch script args (javapath/jvmargs/jarargs/append) |

### Configuration Commands

| Command | Description |
| --- | --- |
| `$appconfigget / $acg [path]` | Get an app config value (dot path, e.g. `AIConfigs.default.Url`) |
| `$appconfiggetall / $acga` | Get the entire config |
| `$appconfigset / $acs <path> <value>` | Set an app config value and save |
| `$printconfig` | Print the current config to the console (debug) |

### Backup & Files

| Command | Description |
| --- | --- |
| `$backup <server> [remark]` | Zip a server's world directory into `backups` |
| `$file <read\|write\|list\|delete> <path>` | Sandboxed file ops; supports `%appdata%`, `%<server>%` placeholders; whitelist only |

### Checks

| Command | Description |
| --- | --- |
| `$check <subcommand> <server> [name]` | Subcommands: `whitelist/wl`, `op`, `banplayer/bp`, `banip/bip`. Without a name, lists all; with a name, checks membership |

### System Execution

| Command | Description |
| --- | --- |
| `$exec <command/script path> [args...]` | Run a system command or script (AI Agent must confirm before executing) |

### AI

| Command | Description |
| --- | --- |
| `$ai chat [config] <message>` | Chat with the AI |
| `$ai agent [config] <instruction>` | Run an instruction in Agent mode (may call tools to operate servers) |
| `$ai default [config]` | Show the current default AI config; with an argument, set `DefaultAIConfig` and persist it |
| `$chat [config] <message>` | Shorthand for `$ai chat` |
| `$agent [config] <instruction>` | Shorthand for `$ai agent` |

> Note: `$chat`, `$agent` and `$ai` share the same parsing logic. When no config name is given (or the first token is `default`), the config pointed to by `DefaultAIConfig` is used.

Additionally, if server console output contains text starting with `$chat`, `$agent`, or `@ai ...`, it is treated as an AI trigger (allowing in-game players to trigger AI replies).

---

## Architecture Overview

MSL-CLI follows the **dependency inversion** principle:

- **Core (domain/ports/use cases)**: defines domain models and abstract interfaces, independent of external framework details.
- **Infrastructure**: implements the ports — server process management, config persistence, logging, AI service, and all commands.
- **CLI (adapters)**: console input/output adapters.

The DI container is wired in `Program.cs`; `CommandParser` scans for all types implementing `ICommand` via reflection and registers them automatically.

The AI agent (`OpenAiAgentService`) exposes two tools via OpenAI Function Calling:

- `execute_command`: run a `$`-prefixed command.
- `sleep`: wait a number of seconds.

Following the "execute → wait → verify log (retry on failure)" loop from the system prompt, the Agent can complete multi-step operations automatically.

---

## Notes

- Destructive or long-running commands (`$exec`, `$run`, `$stop`, `$stopall`, `$server run/stop`, ...) require AI confirmation before execution.
- Backing up a running server can produce inconsistent data; stop the server first when possible.
- The `$file` command is whitelist-restricted to server directories and `%APPDATA%\MSL_CLI`.
- `server.properties` writes use an atomic temp-file + replace strategy.
- On stop, a `stop` command is sent and the process is awaited up to 30 seconds before force-killing.
- Each server has its **own process and log buffer**, so their outputs are isolated and never mixed together.

---

## License

[MIT](LICENSE.txt) © 2026 Li Jiakun
