using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Web.WebView2.Core;



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
                AppMsg.Show(this,
                    "终端组件（WebView2）初始化失败，SSH/Telnet 功能不可用。\n" +
                    "请确认已安装 Microsoft Edge WebView2 Runtime。\n\n" + ex.Message,
                    "初始化失败", AppMsgIcon.Warning);
            }
        }

        // ── ZMODEM 文件保存 ───────────────────────────────────────────────────

        private void ZmodemSaveFile(string filename, byte[] data)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "保存接收到的文件",
                FileName = filename
            };
            var ext = Path.GetExtension(filename);
            if (!string.IsNullOrEmpty(ext))
                dlg.Filter = $"*{ext}|*{ext}|所有文件|*.*";
            else
                dlg.Filter = "所有文件|*.*";

            if (dlg.ShowDialog() != true) return;

            try
            {
                File.WriteAllBytes(dlg.FileName, data);
                ShowToast($"文件已保存：{Path.GetFileName(dlg.FileName)}（{data.Length / 1024.0:F1} KB）");
            }
            catch (Exception ex)
            {
                AppMsg.Show(this, $"保存文件失败：{ex.Message}", "ZMODEM", AppMsgIcon.Error);
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

                // sessionId 在 xterm.js 侧是 (int)instanceId，查字典时转回 long
                long tabKey = (long)sessionId;

                if (type == "input")
                {
                    var data = msg.GetProperty("data").GetString() ?? "";
                    if (_tabSessions.TryGetValue(tabKey, out var t) && t is TerminalTabSession term)
                        term.Service.SendInput(data);
                }
                else if (type == "resize")
                {
                    var cols = msg.GetProperty("cols").GetInt32();
                    var rows = msg.GetProperty("rows").GetInt32();
                    if (_tabSessions.TryGetValue(tabKey, out var t) && t is TerminalTabSession term)
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
                        _tabSessions.TryGetValue(tabKey, out var t) && t is TerminalTabSession term)
                    {
                        term.Service.SendInput(text);
                    }
                }
                else if (type == "copy-text")
                {
                    // xterm.js 选中文字后通知 C# 写入系统剪贴板
                    var text = msg.GetProperty("data").GetString() ?? "";
                    if (!string.IsNullOrEmpty(text))
                        Dispatcher.Invoke(() => TrySetClipboard(text));
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

        private void EnsureTerminalViewport(long instanceId, bool activateSession)
        {
            if (!_webViewReady) return;
            _ = EnsureTerminalViewportAsync(instanceId, activateSession);
        }

        private async Task EnsureTerminalViewportAsync(long instanceId, bool activateSession)
        {
            try
            {
                ApplyTerminalViewport(instanceId, activateSession);
                await Dispatcher.InvokeAsync(
                    () => ApplyTerminalViewport(instanceId, false),
                    System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(60);
                await Dispatcher.InvokeAsync(
                    () => ApplyTerminalViewport(instanceId, false),
                    System.Windows.Threading.DispatcherPriority.Input);
                await Task.Delay(120);
                await Dispatcher.InvokeAsync(
                    () => ApplyTerminalViewport(instanceId, false),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"terminal viewport sync ignored: {ex.Message}");
            }
        }

        private void ApplyTerminalViewport(long instanceId, bool activateSession)
        {
            if (!_webViewReady || TerminalWebView.Visibility != Visibility.Visible)
                return;
            if (!TryGetLiveTabSession(instanceId, out var current) || current is not TerminalTabSession)
                return;

            bool shouldFocus = _activeTabId == instanceId || (_splitMode && _secondaryTabId == instanceId);
            if (!shouldFocus)
                return;

            try { TerminalWebView.UpdateLayout(); } catch { }

            try
            {
                TerminalWebView.Focus();
                System.Windows.Input.Keyboard.Focus(TerminalWebView);
            }
            catch { }

            if (activateSession)
                SendWebViewMessage(new { type = "activate", sessionId = (int)instanceId });

            SendWebViewMessage(new { type = "focus", sessionId = (int)instanceId });
        }

        // ── 建立 SSH / Telnet 连接 ────────────────────────────────────────────

        internal async Task ConnectTerminalAsync(ServerInfo server, bool forceNew = false)
        {
            // 不强制新建时：若已有连接中的同一服务器，切换过去
            if (!forceNew && TryGetSessionForServer(server.Id, out var existingId, out var existing) && existing != null)
            {
                if (existing is TerminalTabSession { IsConnected: true })
                {
                    SwitchToSession(existingId);
                    return;
                }
                RemoveTabSession(existingId);
            }

            if (!_webViewReady)
            {
                AppMsg.Show(this, "终端组件尚未就绪，请稍后再试", "提示", AppMsgIcon.Info);
                return;
            }

            // 同步超时配置
            TerminalSessionService.ConnectTimeoutSeconds = _appSettings.TerminalConnectTimeoutSec;

            var service    = new TerminalSessionService();
            var instanceId = System.Threading.Interlocked.Increment(ref _nextSessionInstanceId);
            var tab        = new TerminalTabSession
            {
                Server     = server,
                Address    = BuildServerAddress(server),
                InstanceId = instanceId,
                Service    = service
            };

            SocksProxyEntry? socks = null;
            if (TryGetSocksProxy(server, out socks) && socks != null)
            {
                AppLogger.Info($"terminal via socks [{socks.Name}] {server.IP}:{server.Port}");
            }

            // 以 instanceId 为键存入，允许同一服务器有多个会话
            _tabSessions[instanceId] = tab;
            AddStatusTabButton(tab);
            RefreshTabLabelsForServer(server.Id);
            SwitchToSession(instanceId);

            StatusText.Text      = $"正在连接到 {server.Name}...";
            DescriptionText.Text = server.Description ?? "";
            Title = $"RemoteX — {server.Name}";

            // 在 xterm.js 中创建对应终端实例（sessionId = instanceId）
            SendWebViewMessage(new { type = "create", sessionId = (int)instanceId });
            EnsureTerminalViewport(instanceId, activateSession: true);
            UpdateTabDot(tab, null);

            // 数据接收：SSH/Telnet → xterm.js
            service.DataReceived += data =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (!TryGetLiveTabSession(instanceId, out var current) || !ReferenceEquals(current, tab))
                        return;
                    SendWebViewMessage(new { type = "data", sessionId = (int)instanceId, data });
                });

            // 连接成功回调
            service.Connected += () => Dispatcher.BeginInvoke(() =>
            {
                if (!TryGetLiveTabSession(instanceId, out var current) || !ReferenceEquals(current, tab))
                    return;
                AppLogger.Info($"terminal connected: {tab.Address}");
                UpdateTabDot(tab, true);
                StatusText.Text = $"已连接  {server.Name}";
                _appSettings.AddRecentServer(server.Id);
                RefreshRecentSection();
                if (_activeTabId == instanceId)
                    EnsureTerminalViewport(instanceId, activateSession: false);
            });

            // 断开回调
            service.Disconnected += () => Dispatcher.BeginInvoke(() =>
            {
                if (!TryGetLiveTabSession(instanceId, out var current) || !ReferenceEquals(current, tab))
                    return;
                AppLogger.Info($"terminal disconnected: {tab.Address}");
                UpdateTabDot(tab, false);
                if (_activeTabId == instanceId)
                    StatusText.Text = $"已断开  {tab.Server.Name}";
            });

            // ── ZMODEM 回调 ──────────────────────────────────────────────────
            // 进度文字 → StatusText
            service.ZmodemStatus = msg => Dispatcher.BeginInvoke(() =>
            {
                if (_activeTabId == instanceId) StatusText.Text = msg;
            });

            // 接收完成 → 弹 SaveFileDialog 保存
            // 使用 Invoke（同步）：ZMODEM 协议已结束，阻塞 SSH 读线程等待用户确认路径，确保对话框在前台出现
            service.ZmodemFileReceived = (filename, data) =>
                Dispatcher.Invoke(() => ZmodemSaveFile(filename, data));

            // 需要上传 → 同步弹 OpenFileDialog，优先让文件窗口立刻出现；
            // 文件内容读取放到后台线程，避免选中文件后卡住 UI。
            service.ZmodemRequestUpload = () =>
            {
                try
                {
                    string? filePath = Dispatcher.Invoke(() =>
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择要上传的文件" };
                        return dlg.ShowDialog() == true ? dlg.FileName : null;
                    });

                    if (string.IsNullOrEmpty(filePath))
                        return Task.FromCanceled<(string, byte[])>(new System.Threading.CancellationToken(true));

                    return Task.Run(() =>
                    {
                        var bytes = File.ReadAllBytes(filePath);
                        return (System.IO.Path.GetFileName(filePath), bytes);
                    });
                }
                catch (Exception ex)
                {
                    return Task.FromException<(string, byte[])>(ex);
                }
            };

            try
            {
                await service.ConnectAsync(server, socks);
            }
            catch (Exception ex)
            {
                AppLogger.Error("terminal connect failed", ex);
                AppMsg.Show(this,
                    $"连接失败：{tab.Server.Name}\n\n{ex.Message}",
                    "连接错误", AppMsgIcon.Error);
                RemoveTabSession(instanceId);
            }
        }
    }
}
