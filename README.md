# RemoteX

![RemoteX Logo](app.ico) 

**RemoteX** 是一款基于 .NET 8 和 WPF 构建的轻量级、现代化的多协议远程连接管理器。它旨在为网络工程师和软件开发人员提供一个统一的入口，通过单一界面管理 RDP、SSH 和 Telnet 连接。

---

## 🚀 主要特性 (Key Features)

* **多协议支持**：集成 Microsoft RDP 控件，并结合 `xterm.js` 提供流畅的 SSH 和 Telnet 终端体验。
* **SOCKS 代理集成**：内置 `SocksProxyBridge`，支持通过代理服务器建立远程连接，适应复杂的内网渗透或跨网段运维场景。
* **现代化 UI/UX**：采用 WPF 开发，支持深色模式，拥有响应式的侧边栏管理和多标签页（Tab）切换功能。
* **凭据安全**：通过 `CredentialProtector` 实现连接凭据的加密存储，确保服务器信息安全。
* **健康监测**：内置 `ConnectionHealthService`，实时监测远程节点的在线状态。

## 🛠️ 技术栈 (Tech Stack)

* **Framework**: .NET 8.0 (Windows)
* **UI**: WPF (Windows Presentation Foundation)
* **RDP**: AxMSTSCLib / MSTSCLib (Microsoft Terminal Services Client)
* **Terminal**: xterm.js (via WebView2/HTML Integration)
* **Communication**: C# Socket Programming for Socks Proxy Support

## 📂 项目结构 (Project Structure)

* `MainWindow.*.cs`: 采用分部类（Partial Class）管理 UI 逻辑，包含侧边栏、标签页、CRUD 操作等。
* `SocksServer/`: 独立的 SOCKS 代理服务端组件。
* `Assets/`: 包含终端渲染所需的 HTML/JS 静态资源。
* `ServerRepository.cs`: 负责服务器配置的持久化存储。

## ⚙️ 快速开始 (Quick Start)

### 环境要求
1.  **Windows 10/11**
2.  **Visual Studio 2022** (安装有 .NET 桌面开发工作负载)
3.  **.NET 8.0 SDK**

### 编译与运行
1.  克隆仓库：
    ```bash
    git clone [https://github.com/dsanpang/RemoteX.git](https://github.com/dsanpang/RemoteX.git)
    ```
2.  使用 Visual Studio 打开 `MyRdpManager.sln`。
3.  由于项目依赖 `MSTSCLib.dll`，请确保系统已安装远程桌面连接工具。
4.  直接运行 (F5)。

## 📸 界面预览 (Screenshots)


## 📜 许可证 (License)

本项目采用 [MIT License](LICENSE) 开源。

---

**Author**: dsanpang (大三胖)
