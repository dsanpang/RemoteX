# RemoteX

> 面向运维人员的多协议远程连接管理器，支持 RDP / SSH / Telnet，内置 SOCKS5 跳板代理。**v2** 起支持 SOCKS over TLS、端口转发、快捷命令、SFTP 浏览等。

---

## 功能概览

### 连接管理
- 支持 **RDP、SSH、Telnet** 三种协议，可在同一界面多标签并行操作
- 服务器列表支持**分组**、**拖拽排序**、模糊搜索
- 侧边栏显示**最近连接**历史，一键快速重连

### SSH / Telnet 终端
- 内置终端面板，SSH 支持**密码认证**和 **PEM/OpenSSH 私钥**认证
- Telnet 支持**自动登录**（检测 login/password 提示后自动输入）
- 终端字符正确透传，退格、方向键等控制字符均正常工作
- **窗口尺寸同步**：SSH/Telnet 随终端面板缩放同步 NAWS/Window Change
- 连接超时可在设置中配置（3–120 秒）

### SOCKS5 代理（跳板机）
- 可配置多个 SOCKS5 代理（如多机房跳板机），每台服务器单独绑定
- 支持 **SOCKS over TLS**：代理管理内可启用 TLS、证书名称/SNI、可选 SHA256 指纹固定
- 代理配置使用**稳定 ID** 引用，改名不影响已有服务器绑定；密码本地 **DPAPI** 加密存储
- RDP / SSH / Telnet 连接均可**按服务器独立路由**到指定代理
- **存活检测**同样走代理路径；若服务器引用已删除的代理，会明确提示「代理配置缺失」而非静默直连

### 存活检测
- 批量 TCP 端口探测，实时显示在线/离线/检测中状态
- 支持并发检测，并发数和超时均可在设置中调整

### 数据管理
- 服务器信息存储于本地 **SQLite** 数据库
- 支持 **v2 格式导出/导入**：含服务器列表与代理列表，跨机器恢复可一并还原代理配置
- 导出可选 AES 加密，配置文件位于 `%LocalAppData%\RemoteX\`

---

## 截图

> *(可在此处放置截图)*

---

## 快速开始

### 系统要求
- Windows 10 / 11（64 位）
- .NET 8 运行时（选择自包含发布则不需要）

### 下载 & 运行

从 [Releases](https://github.com/dsanpang/RemoteX/releases) 下载最新的 `RemoteX.exe`，双击运行即可，无需安装。

### 从源码编译

```bash
git clone https://github.com/dsanpang/RemoteX.git
cd RemoteX

# 调试运行
dotnet run

# 发布为单文件（推荐）
dotnet publish -c Release -p:PublishProfile=SingleFile
# 输出: bin\Release\net8.0-windows\win-x64\publish\RemoteX.exe
```

---

## SOCKS5 跳板服务端（可选）

项目附带一个配套的高性能 SOCKS5 代理服务端（Go 编写），用于部署在机房跳板机上。

### 编译

```bat
cd SocksServer
build.bat
```

> 需要先安装 [Go 1.21+](https://golang.org/dl/)，编译结果为单文件 `proxy.exe`（无控制台窗口）。

### 安装为 Windows 服务

```bat
# 安装（同时自动添加防火墙入站规则）
proxy.exe install -port 1080 -user admin -pass yourpassword -tls

# 卸载
proxy.exe uninstall

# 前台调试模式
proxy.exe run -port 1080 -user admin -pass yourpassword
```

### 参数说明

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `-port` | `1080` | 监听端口 |
| `-user` | `admin` | SOCKS5 用户名 |
| `-pass` | `changeme` | SOCKS5 密码（**请务必修改**） |
| `-tls` | 关闭 | 启用 TLS 封装（流量看起来像 HTTPS） |
| `-cert` | 自动生成 | TLS 证书文件路径 |
| `-key` | 自动生成 | TLS 私钥文件路径 |
| `-log` | `proxy.log` | 日志文件路径 |
| `-verbose` | 关闭 | 输出详细连接日志 |

### 特性
- 标准 SOCKS5（RFC 1928）+ 用户名/密码认证
- **TLS 自动嗅探**：同一端口同时支持 TLS 加密客户端和裸连接客户端
- 日志自动滚动（超过 10 MB 后备份）
- 以 Windows 服务形式运行（Session 0，无窗口，开机自启）

---

## 在 RemoteX 中配置代理

1. 点击主界面工具栏的 **「代理」** 按钮，打开代理管理窗口
2. 添加跳板机信息（名称、地址、端口、用户名、密码）
3. 若跳板机启用 TLS，勾选「通过 TLS 连接代理服务端」，可选填证书名称（SNI）和 SHA256 指纹固定
4. 在服务器编辑页面的「连接方式」中选择对应代理
5. 点击存活检测，验证经代理的连通性

---

## 项目结构

```
RemoteX/
├── MainWindow.*          # 主窗口（侧边栏、标签页、健康检测、CRUD、端口转发、快捷命令）
├── ServerEditWindow.*    # 服务器新增/编辑弹窗
├── SettingsWindow.*      # 全局设置（含终端连接超时）
├── ProxyManagerWindow.*  # SOCKS5 代理管理（含 TLS 配置）
├── PortForwardEditWindow.*  # 端口转发规则编辑
├── QuickCommandEditWindow.* # 快捷命令编辑
├── SftpBrowserWindow.*  # SFTP 文件浏览
├── TerminalSessionService.cs   # SSH / Telnet 会话（NAWS 窗口同步）
├── RdpSessionService.cs        # RDP 会话逻辑
├── Socks5Helper.cs             # SOCKS5 隧道（含 TLS）统一封装
├── SocksProxyBridge.cs         # 客户端 SOCKS5 本地转发（RDP 用）
├── ConnectionHealthService.cs  # TCP 存活探测（含代理路径）
├── ServerRepository.cs         # SQLite 数据访问
├── ServerExportImport.cs       # v2 导出/导入（服务器+代理）
├── CloudSyncConfig.cs / CloudSyncService.cs  # 云同步配置
├── ZmodemTransfer.cs           # Zmodem 传输
├── AppSettings.cs              # 配置持久化
└── SocksServer/
    ├── main.go           # Go SOCKS5 代理服务端
    ├── build.bat         # 编译脚本
    └── go.mod
```

---

## 技术栈

| 组件 | 技术 |
|------|------|
| UI 框架 | WPF (.NET 8, C#) |
| RDP | AxMSTSCLib（系统内置 mstscax.dll） |
| SSH | SSH.NET |
| 数据库 | Microsoft.Data.Sqlite |
| 日志 | Serilog |
| SOCKS5 服务端 | Go + go-socks5 + kardianos/service |

---

## 版本历史

### v2.1
- 重点修复 `rz/sz` ZMODEM 文件传输，上传与下载恢复可用
- 优化 `rz` 触发文件选择窗口的响应速度，并清理排障阶段的调试日志

### v2.0
- **代理**：稳定 ID 引用、代理密码 DPAPI 加密存储、支持 SOCKS over TLS（证书名称/SNI、SHA256 指纹固定）
- **连接**：Socks5Helper 隧道封装，修复握手后连接被提前关闭；健康检查在代理缺失时明确提示，不再静默直连
- **终端**：SSH/Telnet 窗口尺寸同步（NAWS/Window Change），设置页增加终端连接超时（3–120 秒）
- **导出/导入**：v2 格式含服务器与代理列表，跨机器恢复可一并还原代理配置
- **新增**：端口转发、快捷命令、SFTP 浏览、Zmodem 传输、云同步配置等

### v1.1
- 新增独立代理管理窗口，支持配置多个 SOCKS5 跳板机
- 存活检测支持经代理路由
- 修复 Telnet 退格键在代理模式下失效的问题（禁用 Nagle 算法）
- 修复密码框光标垂直未居中及多余内边距问题
- 所有编辑框获得焦点时自动全选内容
- SOCKS5 服务端重构为 Go 实现，支持 TLS 自动嗅探

### v1.0
- 初始版本：RDP / SSH / Telnet 多标签连接管理
- 服务器分组、拖拽排序、最近连接历史
- 批量存活检测、JSON 导出/导入
- 基础 SOCKS5 代理支持

---

## License

MIT
