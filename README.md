# MSL-CLI

一个用于管理多个 Minecraft 服务器的命令行工具，内置 AI 对话与智能体（Agent）能力。

A command-line tool for managing multiple Minecraft servers, with built-in AI chat and agent capabilities.

提供统一 AI 命令入口 `$ai`（`chat` / `agent` / `default`），并支持用 `DefaultAIConfig` 指定默认 AI 配置。
Provides a unified AI entry `$ai` (`chat` / `agent` / `default`) and a `DefaultAIConfig` for the default AI config.

---

## 📖 文档 / Documentation

- **简体中文** → [README-ch.md](README-ch.md)
- **English** → [README-en.md](README-en.md)

---

**技术栈 / Stack**：C# / .NET 10.0 · Microsoft.Extensions.DependencyInjection 9.0.0 · System.Configuration.ConfigurationManager 10.0.10（Query 协议与 OpenAI 兼容客户端为内置实现 / Query protocol and OpenAI-compatible client are built in）

**License**：[MIT](LICENSE.txt)
