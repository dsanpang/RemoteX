using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Web.WebView2.Core;

using MessageBox = System.Windows.MessageBox;

namespace RemoteX
{
    public partial class MainWindow
    {
        // ── WebView2 初始化 ───────────────────────────────────────────────────

        private bool _webViewReady;

        private async Task InitTerminalWebViewAsync()
        {
            try
            {
                await App.ExtractionTask.ConfigureAwait(true);
                // 使用 %LocalAppData%\RemoteX\WebView2，避免在程序目录生成 exe.WebView2
                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppPaths.WebView2UserDataFolder,
                    options: null).ConfigureAwait(true);
                await TerminalWebView.EnsureCoreWebView2Async(env);

                var wv        = TerminalWebView.CoreWebView2;
                var assetsDir = AppPaths.AssetsDir;

                wv.SetVirtualHostNameToFolderMapping(
                    "local.rdpmanager",
                    assetsDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                wv.WebMessageReceived += OnTerminalWebMessageReceived;

                // 状态栏不需要；右键菜单由 xterm.js 自行实现
                wv.Settings.IsStatusBarEnabled = false;
                // 允许 xterm.js 通过 JS 写入剪贴板（复制）
                wv.Settings.AreDefaultContextMenusEnabled = false;

                // 授予剪贴板读取权限（用于粘贴）
                wv.PermissionRequested += (_, args) =>
                {
                    if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                        args.State = CoreWebView2PermissionState.Allow;
                };

                wv.Navigate("https://local.rdpmanager/terminal.html");
                _webViewReady = true;
                AppLogger.Info("terminal webview initialized");
            }
            catch (Exception ex)
            {
                AppLogger.Error("webview2 init failed", ex);
                MessageBox.Show(
                    "终端组件（WebView2）初始化失败，SSH/Telnet 功能不可用。\n" +
                    "请确认已安装 Microsoft Edge WebView2 Runtime。\n\n" + ex.Message,
                    "初始化失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ── WebView2 消息处理（JS → C#）──────────────────────────────────────

        private void OnTerminalWebMessageReceived(
            object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json      = e.TryGetWebMessageAsString();
                var msg       = JsonSerializer.Deserialize<JsonElement>(json);
                var type      = msg.GetProperty("type").GetString();
                var sessionId = msg.GetProperty("sessionId").GetInt32();

                if (type == "input")
                {
                    var data = msg.GetProperty("data").GetString() ?? "";
                    if (_tabSessions.TryGetValue(sessionId, out var t) && t is TerminalTabSession term)
                        term.Service.SendInput(data);
                }
                else if (type == "resize")
                {
                    var cols = msg.GetProperty("cols").GetInt32();
                    var rows = msg.GetProperty("rows").GetInt32();
                    if (_tabSessions.TryGetValue(sessionId, out var t) && t is TerminalTabSession term)
                        term.Service.Resize(cols, rows);
                }
                else if (type == "paste-request")
                {
                    // 通过 C# 侧读取剪贴板并发送到终端（避免 JS 剪贴板权限问题）
                    var text = Dispatcher.Invoke(() =>
                        System.Windows.Clipboard.ContainsText()
                            ? System.Windows.Clipboard.GetText()
                            : "");
                    if (!string.IsNullOrEmpty(text) &&
                        _tabSessions.TryGetValue(sessionId, out var t) && t is TerminalTabSession term)
                    {
                        term.Service.SendInput(text);
                    }
                }
                else if (type == "copy-text")
                {
                    // xterm.js 选中文字后通知 C# 写入系统剪贴板
                    var text = msg.GetProperty("data").GetString() ?? "";
                    if (!string.IsNullOrEmpty(text))
                        Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("terminal web message error", ex);
            }
        }

        // ── C# → WebView2 消息发送 ────────────────────────────────────────────

        internal void SendWebViewMessage(object message)
        {
            if (!_webViewReady) return;
            try
            {
                var json = JsonSerializer.Serialize(message);
                TerminalWebView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                AppLogger.Error("webview post message error", ex);
            }
        }

        // ── 建立 SSH / Telnet 连接 ────────────────────────────────────────────

        internal async Task ConnectTerminalAsync(ServerInfo server)
        {
            // 若已有连接中的同一服务器，切换过去
            if (_tabSessions.TryGetValue(server.Id, out var existing))
            {
                if (existing is TerminalTabSession { IsConnected: true })
                {
                    SwitchToSession(server.Id);
                    return;
                }
                RemoveTabSession(server.Id);
            }

            if (!_webViewReady)
            {
                MessageBox.Show(
                    "终端组件尚未就绪，请稍后再试",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 同步超时配置
            TerminalSessionService.ConnectTimeoutSeconds = _appSettings.TerminalConnectTimeoutSec;

            var service = new TerminalSessionService();
            var tab     = new TerminalTabSession
            {
                Server  = server,
                Address = BuildServerAddress(server),
                Service = service
            };

            ServerInfo connectTarget = server;
            if (TryGetSocksProxy(server, out var socks) && socks != null)
            {
                var bridge = new SocksProxyBridge();
                var localPort = bridge.Start(
                    socks.Host, socks.Port,
                    string.IsNullOrWhiteSpace(socks.Username) ? null : socks.Username,
                    string.IsNullOrWhiteSpace(socks.Password) ? null : socks.Password,
                    server.IP, server.Port);
                tab.SocksBridge = bridge;
                connectTarget = new ServerInfo
                {
                    Id = server.Id,
                    Name = server.Name,
                    IP = "127.0.0.1",
                    Port = localPort,
                    Username = server.Username,
                    Password = server.Password,
                    Protocol = server.Protocol,
                    SshPrivateKeyPath = server.SshPrivateKeyPath,
                    Description = server.Description,
                    Group = server.Group
                };
                AppLogger.Info($"terminal via socks [{socks.Name}] 127.0.0.1:{localPort} -> {server.IP}:{server.Port}");
            }

            _tabSessions[server.Id] = tab;
            AddStatusTabButton(tab);
            SwitchToSession(server.Id);

            StatusText.Text      = $"正在连接到 {tab.Address}...";
            DescriptionText.Text = server.Description ?? "";
            Title = $"RemoteX — {server.Name}  ({tab.Address})";

            // 在 xterm.js 中创建对应终端实例
            SendWebViewMessage(new { type = "create",   sessionId = server.Id });
            SendWebViewMessage(new { type = "activate", sessionId = server.Id });
            UpdateTabDot(tab, null);

            // 数据接收：SSH/Telnet → xterm.js
            service.DataReceived += data =>
                Dispatcher.BeginInvoke(() =>
                    SendWebViewMessage(new { type = "data", sessionId = server.Id, data }));

            // 连接成功回调
            service.Connected += () => Dispatcher.BeginInvoke(() =>
            {
                if (!_tabSessions.ContainsKey(server.Id)) return;
                AppLogger.Info($"terminal connected: {tab.Address}");
                UpdateTabDot(tab, true);
                StatusText.Text = $"已连接  {tab.Address}";
                _appSettings.AddRecentServer(server.Id);
                RefreshRecentSection();
            });

            // 断开回调
            service.Disconnected += () => Dispatcher.BeginInvoke(() =>
            {
                if (!_tabSessions.ContainsKey(server.Id)) return;
                AppLogger.Info($"terminal disconnected: {tab.Address}");
                UpdateTabDot(tab, false);
                if (_activeServerId == server.Id)
                    StatusText.Text = $"已断开  {tab.Address}";
            });

            try
            {
                await service.ConnectAsync(connectTarget);
            }
            catch (Exception ex)
            {
                AppLogger.Error("terminal connect failed", ex);
                MessageBox.Show(
                    $"连接失败：\n{tab.Address}\n\n{ex.Message}",
                    "连接错误", MessageBoxButton.OK, MessageBoxImage.Error);
                RemoveTabSession(server.Id);
            }
        }
    }
}
