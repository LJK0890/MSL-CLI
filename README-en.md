# MSL-CLI

A command-line tool for managing multiple Minecraft servers, with built-in AI chat and agent capabilities.

MSL-CLI is organized using **Clean Architecture / Ports & Adapters** and assembled with dependency injection (DI). It manages multiple servers through one entry point (start, stop, send commands, query status, batch stop/broadcast), backs up world files, reads server log buffers, and lets an AI (OpenAI-compatible API) help you operate your servers.

- **Stack**: C# / .NET 10.0, Microsoft.Extensions.DependencyInjection, OpenAI SDK 2.12.0, McQuery.Net 2.0.0, System.Configuration.ConfigurationManager 10.0.10
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
│   │   ├── AppConfig.cs           # App config (AI configs + server paths + locks/hidden + permissions)
│   │   ├── AIConfig.cs            # AI service connection config (env-var ApiKey)
│   │   ├── AgentPermissions.cs    # Agent command authorization (allow-list / always-ask)
│   │   ├── ServerProperties.cs    # server.properties read/write
│   │   ├── ServerStatus.cs        # Stopped/Starting/Running/Stopping
│   │   ├── CommandArgs.cs         # Command argument context
│   │   ├── CommandResult.cs       # Command execution result
│   │   ├── CommandInvocationValidator.cs # Pre-flight validation of agent commands
│   │   ├── MinecraftChatParser.cs # In-game chat line / AI trigger parsing
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
│   ├── ServerBuffer.cs            # Output buffer
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
      "ChatPrompt": "...",                           // chat system prompt (empty = built-in default, may contain {commandList})
      "EnableAgent": true,                           // enable agent
      "MaxIterations": 16,                           // max agent iterations
      "MaxContextTokens": 32000,                     // agent context auto-compression threshold (estimated tokens, <=0 disables)
      "ContextCompressPrompt": "",                   // compression prompt template (empty = built-in default, may contain {transcript})
      "AgentPrompt": "..."                           // agent system prompt
    }
  },
  "ServerPaths": {                                   // server registry: name -> path
    "survival": "D:\\Minecraft\\survival"
  },
  "AgentPermissions": {                              // agent command authorization
    "AllowList": [ "$server buf update survival" ],   // always-allowed commands ("*" suffix = prefix match)
    "AlwaysAskCommands": [ "$app exec", "$server del" ]   // confirmed on every execution (command name or "command + action" scope)
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
- `$app exec` and `$server del` are a **floor** for per-execution confirmation: even if they are manually removed from `AlwaysAskCommands`, they are restored on load.

**DefaultAIConfig**: When `$ai` does not specify a config name, it uses the config pointed to by `DefaultAIConfig` (an error "未设置默认AI配置" is shown if it is empty). View or change it at runtime with `$ai default [config]`.

You can also inspect/modify configuration at runtime — see [Command Reference](#command-reference).

---

## Command Reference

All commands are prefixed with `$` and auto-registered via reflection. There are **only 6 top-level commands**; everything else is a subcommand/action.

| Command | Description |
| --- | --- |
| `$ai chat\|agent [config] <message/instruction>` | Chat / agent execution; `default`/`add`/`rm`/`cfg`/`perm` manage every AI-related setting |
| `$app exec\|cfg\|exit\|reload\|ptcfg ...` | High-risk / high-privilege operations (running system commands, rewriting any config), and the secondary path to any config |
| `$file read\|write\|list\|delete <path> [content]` | Sandboxed file ops, whitelist directories only |
| `$help [command]` | No argument: list all commands with descriptions; with a name: that command's detailed usage |
| `$list` | **List command names only** (no descriptions) |
| `$server <action> ...` | Server management (including `hl` highlight and `lk`/`ulk` lock, below) |

Use `$list` for a quick name dump and `$help <command>` for one command's detailed usage.

### `$ai`

```
$ai chat [config] <message>          Plain chat (runs no commands)
$ai agent [config] <instruction>     Agent execution (tools, gated by authorization)
$ai default [config]                 View / set the default AI config
$ai add <config> <Url> <Model> [ApiKeyEnv]   Add an AI instance
$ai rm <config>                      Delete an AI instance
$ai cfg get [path] | getall | set <path> <value> | rm <path>   Read/write all AI config
$ai perm [get|add|rm|ask|noask] [scope]                        Agent command authorization
```

**Full AI configuration surface** (`$ai` covers every AI-related setting; only the rest needs `$app cfg`):

| Path | Meaning |
| --- | --- |
| `EnableAI` | whether AI is enabled |
| `DefaultAIConfig` | default instance name (validated against existing instances) |
| `AIConfigs.<name>.<field>` | instance fields, shorthand `<name>.<field>`; fields include `Url`/`Model`/`ApiKey`/`ApiKeyEnv`/`UseApiKeyEnv`/`EnableChat`/`EnableAgent`/`MaxIterations`/`MaxContextTokens`/`ChatPrompt`/`AgentPrompt`/`ContextCompressPrompt` |
| `AgentPermissions.AllowList` / `AlwaysAskCommands` | authorization lists, maintained with `$ai perm` (`cfg set` points you there) |

```bash
$ai cfg get                              # AI overview (instances, key source, prompts, authorization)
$ai cfg getall                           # AI-related config as JSON (keys are never printed in plaintext)
$ai cfg get ds.MaxIterations             # read one field (shorthand)
$ai cfg set ds.MaxIterations 32          # write one field (takes effect immediately)
$ai cfg set DefaultAIConfig local        # switch the default instance
$ai cfg rm local                         # delete an instance (same as $ai rm local)
$ai perm                                 # show AllowList / AlwaysAskCommands
$ai perm add "$server ck"                # add to the allow-list (floor entries are refused)
$ai perm rm  "$server ck"                # remove from the allow-list
$ai perm ask "$app cfg set"              # require confirmation every time
$ai perm noask "$ai agent"               # stop requiring it (built-in floor entries cannot be removed)
```

**Adding/removing AI instances** (saved and hot-reloaded into the running AI service; `$app reload` refreshes it too):

```bash
$ai add ds https://api.deepseek.com/v1 deepseek-chat DEEPSEEK_API_KEY
$ai add local https://localhost:11434/v1 qwen2.5:14b        # no env var: set the key later
$ai cfg set local.ApiKey sk-xxx
$ai default ds
$ai rm local
```

- The name must be unique, free of whitespace and dots (a dot is the config path separator), and must not be a reserved subcommand (`chat`/`agent`/`default`/`add`/`rm`/`remove`/`del`/`delete`, ...); a config literally named `default` would be read as "use the default config".
- `Url` must be an absolute `http/https` URL. When `ApiKeyEnv` is omitted the instance is treated as using a literal key, and the command tells you to set it with `$ai cfg set <name>.ApiKey <key>` (stored in plaintext) or to use an environment variable instead.
- New instances enable chat and agent by default; if no default AI config exists yet, the new one becomes the default.
- Deleting the current default clears `DefaultAIConfig` (with a hint to pick a new one); deleting the last instance warns that `$ai` becomes unusable.

### `$app`

`$app` is the entry point for **high-risk / high-privilege operations**, and the "secondary path" to any configuration: day-to-day AI settings go through `$ai cfg` and server settings through `$server`; what lives here is what those do not cover, or what needs greater privilege.

```
$app exec <command/script path> [args...]   Run a system command or script (an agent must confirm every time)
$app cfg get [path]          Read any app config; with no path, print the whole JSON
$app cfg getall              Print the entire config
$app cfg set <path> <value>  Write any config (a missing dictionary key in the path is created)
$app cfg rm <path>           Delete a dictionary entry, e.g. ServerPaths.tga; properties cannot be removed
$app exit                    Exit the program (stops all servers first)
$app reload                  Reload config and rebuild the server list and AI instances
$app ptcfg                   Print the current config (debug)
```

> `$exec` is **no longer a top-level command**; it moved to `$app exec`. Running arbitrary system commands and rewriting arbitrary configuration are both high-privilege operations, so they live together under `$app` — and `$app exec` still requires human confirmation on every agent call (a built-in floor entry).

Dot paths (`$app cfg` reaches everything; prefer `$ai cfg` for AI): `ServerPaths.tga`, `AgentPermissions.AllowList`, `AIConfigs.default.Url`.

### `$server`

```
$server add <name> <path>                              register a new server (path must exist)
$server rm <name>                                      drop the config reference, keep the directory
$server del <name> confirm                             drop the reference and delete the whole directory (irreversible)
$server lk <name> | ulk <name>                         lock / unlock (while locked: run/stop/send/rm/del are refused)
$server hd <name> | uhd <name>                         hide / show that server's output on the console
$server hl [name]                                      view / switch the highlighted server
$server [server] file <flag> <args...>                 file operations inside the server directory
      -r  <path>            read a file
      -w  <path> <content>  write (overwrite, creates directories)
      -a  <path> <content>  append one line (creates the file)
      -rm <path>            delete a file or empty directory
      -cp <src> <dst>       copy a file/directory (refuses an existing target)
      -mv <src> <dst>       move / rename (refuses an existing target)
      -ls [path]            list a directory (defaults to the server root)
$server cfg get|getall|set|rm [server] [key] [value]   server.properties
$server arg get|set|rm <server> <arg> [value...]       launch arguments
$server ck wl|op|bp|bip <server|all> [name]            list checks
$server buf read|update <server>                       read / read-and-clear the output buffer
$server ls                                             list all servers with status (highlight/lock/hidden marks)
$server bp <server> [remark]                           back up the world directory
$server query <server|all>                             query server Query info
$server status [server|all]                            show process status
$server stop <server|all> [-f]                         stop a server
$server run <server>                                   start a server
$server send <server|all> <command>                    send a Minecraft command
```

**File operations are built into `$server file`**: `$server [server] file <flag> <args...>`.

- With the server name omitted the **highlighted server** is used (same as `cfg`/`arg`); `$server <server> file ...` selects one explicitly.
- Flags: `-r` read, `-w` overwrite, `-a` append one line, `-rm` delete (file or empty directory), `-cp` copy, `-mv` move/rename, `-ls` list.
- Paths are resolved **relative to that server's directory** (an absolute path inside it also works) and are escape-checked: `..\..\x` or `C:\Windows\...` are refused, so nothing outside the server directory can be read or written — stricter than the `$file` whitelist.
- `-w`/`-a` create missing parent directories; `-a` inserts a newline first when the file does not end with one, so each append gets its own line; `-cp`/`-mv` **refuse to overwrite** an existing target; `-rm` only removes empty directories.
- It shares the same implementation as `$file`, so messages and behaviour match. `$file` remains available for whitelisted locations such as `%appdata%`.
- Permission scopes are per flag: `$server file read`, `$server file write`, `$server file append`, `$server file delete`, `$server file copy`, `$server file move`, `$server file list`.

```bash
$server hl yz                        # the name may now be omitted
$server file -ls                     # list the server root
$server file -r  banned-ips.json
$server file -w  ops.json ["Steve","Alex"]
$server file -a  banned-ips.txt 10.0.0.1
$server file -cp ops.json ops.json.bak
$server file -mv config/old.yml config/new.yml
$server file -rm plugins/old.jar
$server yz file -r server.properties  # explicit server also works
```

**Highlighted server**: `$server hl <name>` sets the highlight (the former `$hl` command is now part of `$server`); with no argument it prints the current one. When the server name is omitted, actions such as `cfg`/`arg get`/`ck`/`file` fall back to the highlighted server. `query` and `status` treat an omitted server name as **all servers** (`all`), not as the highlighted one.

**Locking a server** (`$server lk|ulk`):

- The lock lives in the config's `LockedServers` list: it takes effect **immediately and survives restarts**.
- It blocks exactly five operations: `run`, `stop`, `send`, `rm` and `del` (including being skipped by `$server stop all` / `send all` broadcasts).
- **Everything else still works**: `cfg`/`arg`/`ck`/`buf`/`bp`/`query`/`status`/`ls`/`hl`/`hd` and file read/write.
- `stop`/`send` targeting `all` **skip** locked servers and report which ones; `query all`/`status all`/`ck all`/`arg get all` still include them.
- Forwarding plain console text to a locked *highlighted* server is refused as well (it is a `send`).
- Config normalization drops lock entries for servers that no longer exist, so a stale entry cannot accidentally lock a re-registered server of the same name.

**Hiding output** (`$server hd|uhd`):

- While hidden, the server's **process output is no longer written to the console or the log**, but it still goes into the output buffer — `$server buf read` and the AI agent keep working unchanged.
- Only process output is affected; the tool's own lifecycle messages (`正在启动`/`已启动`/`进程退出`) remain visible.
- The toggle takes effect immediately and persists in the config's `HiddenServers` list; `$server ls` shows an `[输出已隐藏]` mark.

```bash
$server lk  survival          # lock: run/stop/send/rm/del are refused
$server run survival          # -> 服务器 'survival' 已锁定：run/stop/send/rm/del 一律拒绝，$server ulk survival 可解锁
$server buf read survival     # read-only operations are unaffected
$server hd  survival          # stop the console spam from that server
$server uhd survival          # show it again
$server ulk survival          # unlock
```

**Registering and deleting servers** (all three reload the registry immediately, no restart needed):

- `add` writes `name -> path` into `ServerPaths`. The name must be unique and is not allowed to be the reserved word `all`; the path must already exist and must not be used by another server (registering the same directory twice would spawn two processes fighting over one world, so it is refused).
- `rm` drops only the config reference; the directory and its files are untouched and can be re-added with `add` at any time.
- `del` drops the reference **and recursively deletes the whole server directory** — irreversible. It only acts when `confirm` is given explicitly; without it, the command just prints the path that would be deleted and suggests a backup. It refuses when the server is running (stop it first), for drive roots, for symlinks/junctions, and for a path that is an **ancestor of another configured server** (which would take that server down with it). The reference is removed and the registry reloaded first (releasing process handles), then the directory is deleted; if deletion fails, the message explains how to re-add the reference.

```bash
$server add survival D:\Minecraft\survival
$server rm  survival                       # reference only; the directory stays
$server del survival                       # preview what would be deleted, changes nothing
$server del survival confirm               # actually delete reference + directory
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

How `javaPath` is read and written:

- Reading skips directive/comment lines (`@echo off`, `REM`, `set`, `pause`, `cd`, shebangs) and takes the line that actually launches Java (preferring one with `-jar` or `@xxx_args.txt`), so `java`, `"C:\Program Files\...\java.exe"` and even an **unquoted path containing spaces** written by an older version all parse correctly.
- Writing **quotes the path automatically when it contains spaces**, so the next read cannot be cut short at the space. This was the cause of "whatever I write comes back as `java`": a quoted path was written back unquoted, the old parser could no longer recognize the launch line, and it fell back to the default value and overwrote the script.
- If the script exists but no launch line can be recognized, the file is **left untouched** and a warning is logged (defaults are only generated when the script does not exist), so a custom launch script is never clobbered. Use `$server arg set <server> javaPath <path>` to rebuild it explicitly.

**Highlight server**: use `$server hl <server>` to highlight one, after which non-`$` input typed at the console is forwarded to it; if that server is locked, forwarding is refused and the unlock command is suggested.

> Note: the command set is organized into **6 top-level entry points** (`$ai`, `$app`, `$file`, `$help`, `$list`, `$server`); everything else is a subcommand/action. The old top-level entries (`$exit`, `$reload`, `$printconfig`, `$run`, `$stop`, `$stopall`, `$send`, `$sendall`, `$query`, `$status`, `$backup`, `$check`, `$highlight`, `$serverconfig`, `$serverargument`, `$bufferread`, `$bufferupdate`, `$appconfig*`, `$serverconfig*`) were all removed.
>
> The `$chat` / `$agent` shorthand aliases are gone too — use `$ai chat [config] <message>` and `$ai agent [config] <instruction>`.

**Accepted synonyms** (kept for convenience; the docs and examples always use the canonical form on the left):

| Canonical | Synonyms |
| --- | --- |
| `$server rm` / `del` / `lk` / `ulk` / `hd` / `uhd` | `remove` / `delete` / `lock` / `unlock` / `hide` / `show` |
| `$server cfg rm`, `$server arg rm` | `remove` |
| `$server ck wl` / `bp` / `bip` | `whitelist` / `banplayer` / `banip` |
| `$ai rm`, `$ai cfg rm`, `$ai perm rm` | `remove` (`$ai` also accepts `del`, `delete`) |
| `$app cfg rm` | `remove` |

Authorization scopes **normalize** these synonyms to the canonical action (`$server delete` becomes `$server del`), so an alias cannot bypass a `AlwaysAskCommands` floor entry.


### In-game AI trigger

A player can trigger the AI by sending any of these chat forms:

```
$chat [config] <message>            reply in chat mode
$agent [config] <instruction>       run in agent mode (may call tools)
@ai <config> chat|agent <content>   explicitly pick config and mode
```

When the config name is omitted, `DefaultAIConfig` is used. The reply is sent back to the requesting player via `tellraw` and also printed to the console. The first word is only treated as a config name when it **really is a registered AI config**, so `$chat hello world` sends the whole line as the message instead of looking up a config named `hello`.

Recognition is based on the Minecraft server chat line format:

```
[21:08:29] [Server thread/INFO] [net.minecraft.server.MinecraftServer/]: <Sparky_0890> 点任务啊
```

The `<player> message` part is anchored at the end of the line, so log prefixes such as `[21:08:29]` and `[Server thread/INFO]` are never mistaken for angle-bracket content. The split point is the log prefix terminator `]: ` (**first** occurrence), so a `: ` inside the chat body cannot change the player name — a player cannot impersonate someone else by writing `hi: <Alex> $agent ...`. **A chat body containing more than one angle-bracket pair is not forwarded to the AI** — the player chat is still recognized, it simply does not trigger an AI command, so e.g. `<a> <b>` is treated as plain chat.

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
命令 '$check' 不存在，未申请授权也未执行。可用命令: $ai $app $file $help $list $server
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
| `$server add yz D:\srv` / `$server rm yz` | `$server add` / `$server rm` |
| `$server del yz confirm` (alias `delete`) / `$server rm yz` (alias `remove`) | `$server del` / `$server rm` (aliases are normalized so they cannot bypass forced asking) |
| `$app cfg set X Y` | `$app cfg set` |
| `$app exec whoami` | `$app exec` (read-only commands stop at the command level) |

Approving `$server ck op` therefore covers only `$server ck op`: a different server or player no longer prompts, while `$server ck wl`, `$server ck bip` and `$server cfg get` each still need their own approval. That scope string is what gets written to the allow-list, and session passes are keyed the same way.

A broader entry covers narrower ones: writing `$server ck` by hand covers `op`/`wl`/`bp`/`bip`, and `$server` covers every action of that command. For backward compatibility, entries containing `*` match as a whole-string prefix, and entries containing a space (e.g. `$server cfg get`) match when the current call starts with them.

> Security note: the scope stops at the verb. To keep one action confirmed every time, add its scope string to `AgentPermissions.AlwaysAskCommands` (`$app exec` and `$server del` by default).

A player answer must correspond to the outstanding prompt: only the player who was asked, on that server, can answer with `y`/`yes`, `a`/`always` or `n`/`no`; any other chat text is not consumed as an answer. A player can have only one pending prompt at a time — a second request is refused outright. Waiting longer than 120 seconds counts as a refusal. `$app exec` still requires confirmation every single time, and an operator choosing `a` never writes it to the allow-list.

In agent mode the instruction is prefixed with a source label that determines its authority:

| Source label | Authority |
| --- | --- |
| `[来自控制台]` | the local console operator, with **full authority**: no operator check and no refusal on "dangerous operation / insufficient permission" grounds — the instruction is carried out (commands still pass the authorization gate) |
| `[来自服务器 'X' 的玩家 'Y']` | an in-game player: `$server ck op X Y` must confirm they are an operator first, otherwise the request is politely refused |

The label is separated from the instruction body by a space, and the prompts use exactly these formats.

---

## Architecture Overview

MSL-CLI follows the **dependency inversion** principle:

- **Core (domain/ports/use cases)**: defines domain models and abstract interfaces, independent of external framework details.
- **Infrastructure**: implements the ports — server process management, config persistence, logging, AI service, and all commands.
- **CLI (adapters)**: console input/output adapters.

The DI container is wired in `Program.cs`; `CommandParser` scans for all types implementing `ICommand` via reflection and registers them automatically.

### Command layout

Commands live in `Infrastructure/Commands/`, each file owning one responsibility — no command reaches into another's implementation, and none is buried in the server layer:

- **Each of the 6 top-level commands has its own file**: `AICommand.cs`, `AppCommand.cs`, `FileCommand.cs`, `HelpCommand.cs`, `ListCommand.cs`, `ServerCommand.cs`.
- `$server` is an action dispatcher (`ServerCommand.cs`). `cfg`/`arg`/`ck`/`buf` go to dedicated handlers — `ServerConfigHandler.cs`, `ServerArgsHandler.cs`, `ServerCheckHandler.cs`, `ServerBufferHandler.cs`; lifecycle actions live in `ServerLifecycleHandler.cs` (`add`/`rm`/`del`) and `ServerLockHandler.cs` (`lk`/`ulk`/`hd`/`uhd`), while `ls`/`query`/`status`/`stop`/`run`/`send` are implemented in `ServerCommand.cs` and `ServerActionHandlers.cs` and `bp` in `BackupCommand.cs`. Shared target resolution is in `ServerTargetResolver.cs`.
- File operations inside a server directory live in `ServerFileHandler.cs` + `FileOperations.cs` (the same read/write implementation that `$file` uses).
- Reading a server's list files (`ops.json`, `whitelist.json`, `banned-players.json`, `banned-ips.json`) lives in `$server ck` (`ServerCheckHandler.cs`). The server port exposes no `GetOps`/`IsOp` helpers.
- App-config dot-path reading/writing lives in `$app cfg` (`AppConfigPath.cs` + `AppCommand.cs`).
- **There are no subcommand-level entry points any more**: `$serverconfig*`, `$serverargument`, `$bufferread`, `$bufferupdate`, `$appconfig*`, `$run`, `$stop`, `$send` and friends were all removed; they are reachable only as a top-level command plus an action.

### System prompts

Both `$ai chat` and `$ai agent` send the model a system prompt first, and the two are independent:

- **Chat** uses `AIConfigs.<name>.ChatPrompt` (empty falls back to the built-in default). It only shapes the answer's scope and tone: the model may answer questions and give advice but **cannot run commands**, and points to `$ai agent` when real action is needed. The request carries exactly two messages — system prompt plus user message — and no tools.
- **Agent** uses `AgentPrompt`, which carries the authorization rules and the "execute → wait → verify" loop, and mounts the three tools.
- Both templates support the `{commandList}` placeholder, replaced by the name-sorted command summary (the built-in defaults already contain it). Use `$app cfg get AIConfigs.<name>.ChatPrompt` to read the current value; `$app cfg` marks each prompt field as "(内置默认)" (built-in) or "(已自定义)" (customized).
- **The agent prompt derives authority from the source label**: `[来自控制台]` is a full-authority operator instruction (no operator check, no refusal on "dangerous / insufficient permission" grounds), while `[来自服务器 'X' 的玩家 'Y']` must first pass `$server ck op`. See [Agent command authorization](#agent-command-authorization) below.
- A prompt identical to the built-in default is **not written** to `config.json`, so revised built-in prompts take effect after an upgrade and the file stays free of large default blobs; only customized content is persisted. Prompts are read from the in-memory AI config **on every request**: changes made with `$ai cfg set` (hot-reloaded on save) or `$app reload` apply immediately; only editing the file directly (`$app cfg set AIConfigs...` or hand-editing) requires another `$app reload` or a restart.

The AI agent (`OpenAiAgentService`) exposes three tools via OpenAI Function Calling:

- `request_permission`: ask the local operator to authorize a command before it runs, returning the decision as text.
- `execute_command`: run a `$`-prefixed command — **only after** it is authorized (allow-list hit or a granted request).
- `sleep`: wait a number of seconds.

Following the "execute → wait → verify log (retry on failure)" loop from the system prompt, the Agent can complete multi-step operations automatically.

### Automatic context compression

A multi-step agent task accumulates every tool call and result in its conversation history, and long runs (large log dumps from `$server buf update`, for example) can easily overflow the model window. The context length is therefore estimated before every request:

- The threshold comes from `AIConfigs.<name>.MaxContextTokens` (default `32000`; **a value <= 0 disables it**).
- When the estimate exceeds the threshold and the history part is long enough, the same model first compresses the earlier execution record into a summary, that summary replaces the history, and only then is the request sent (the log shows `上下文已压缩: before -> after 估算 tokens`).
- The summary is produced from the `ContextCompressPrompt` template (empty = built-in default; `{transcript}` in the template is replaced with the execution record). The summary is capped at roughly 1/8 of the threshold, so the result stays well below the trigger and compression does not repeat every iteration.
- If the excess comes mainly from the system prompt or the instruction itself (history below 1/4 of the threshold), compression would not help, so a single warning is logged and the loop continues. If the summarization call fails or returns empty text, it falls back to tail truncation rather than failing the task.
- Length estimation needs no tokenizer: CJK characters count as ~1 token each, other characters as ~1 per 4 characters, plus a fixed per-message and tool-definition overhead.

### Agent command authorization

Every command the agent runs is gated, so an unconfirmed command can never execute:

1. `execute_command` first checks whether the command has a session pass (from a prior `request_permission`) or matches the persisted allow-list.
2. If not, it asks the local operator inline, so skipping `request_permission` cannot bypass the check.
3. Requests that do **not** originate from the console (for example in-game player triggers) are never prompted; they are refused, because only the local operator may authorize commands.

The prompt offers `y` (allow once), `a` (always allow, persisted to `AgentPermissions.AllowList`), and `n` (deny); anything unrecognized is treated as a denial.

`$app exec` and `$server del` are on the built-in `AlwaysAskCommands` list: each requires confirmation on **every** execution and can never be added to the allow-list. Both are command + action scopes, so they cover only that action (`$app exec` does not affect `$app cfg`, `$server del` does not affect `$server run`). Even with `$app` or `$server` in the `AllowList`, an agent's `$app exec` and `$server del` still ask every single time, and aliases (`$server delete`) normalize to the same scope so they cannot slip through. Use `$ai perm` to review and maintain both lists.

Console reading is arbitrated by a single input pump: `ConsoleInputReader` keeps reading lines, and while a prompt is pending the line is delivered to the asking component instead of being dispatched as a console command. `IInputReader.ReadLineAsync` is the entry point any component uses to ask the operator a question.

---

## Notes

- Destructive or long-running commands (`$app exec`, `$server run`, `$server stop`, ...) require AI confirmation before execution. Agent-issued commands are always gated: they run only when allowed by `AgentPermissions.AllowList` or explicitly confirmed, and `$app exec` and `$server del` are confirmed every single time.
- `$server del <name> confirm` **recursively deletes the whole server directory and cannot be undone**: back up first with `$server bp <name>`, or use `$server rm <name>` to drop only the reference.
- Backing up a running server can produce inconsistent data; stop the server first when possible.
- The `$file` command is whitelist-restricted to server directories and `%APPDATA%\MSL_CLI`.
- `server.properties` writes use an atomic temp-file + replace strategy.
- On stop, a `stop` command is sent and the process is awaited up to 30 seconds before force-killing.
- Each server has its **own process and log buffer**, so their outputs are isolated and never mixed together.

---

## License

[MIT](LICENSE.txt) © 2026 Li Jiakun
