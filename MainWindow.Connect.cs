using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace RemoteX
{
    public partial class MainWindow
    {
        // ── 连接入口（协议路由） ──────────────────────────────────────────────

        private async Task ConnectToServerAsync(ServerInfo server)
        {
            if (_isHealthChecking)
            {
                StatusText.Text = "正在进行连通性检查，请稍候...";
                return;
            }

            if (_isBulkHealthChecking)
            {
                StatusText.Text = "正在批量检测，请稍候...";
                return;
            }

            _isHealthChecking = true;
            try
            {
                var healthy = await CheckServerHealthBeforeConnectAsync(server);
                if (!healthy) return;
            }
            finally
            {
                _isHealthChecking = false;
            }

            if (server.Protocol != ServerProtocol.RDP)
            {
                await ConnectTerminalAsync(server);
                return;
            }

            // ── RDP 连接 ───────────────────────────────────────────────────────

            if (TryGetSessionForServer(server.Id, out var existingId, out var existing) && existing != null)
            {
                if (existing.IsConnected)
                {
                    SwitchToSession(existingId);
                    UpdateCurrentTabStatus();
                    return;
                }

                AppLogger.Info($"session disconnected, recreating rdp control: {server.Name}");
                RemoveTabSession(existingId);
            }

            var tab = CreateTabSession(server);
            _tabSessions[tab.InstanceId] = tab;
            BeginConnect(tab, server);
        }

        // ── 连通性预检 ────────────────────────────────────────────────────────

        private bool TryGetSocksProxy(ServerInfo server, out SocksProxyEntry? proxy)
        {
            proxy = null;
            if (!HasSocksProxyReference(server) || _appSettings.SocksProxies == null) return false;

            if (!string.IsNullOrWhiteSpace(server.SocksProxyId))
            {
                proxy = _appSettings.SocksProxies.FirstOrDefault(p =>
                    string.Equals(p.Id, server.SocksProxyId, StringComparison.OrdinalIgnoreCase));
            }

            if (proxy == null && !string.IsNullOrWhiteSpace(server.SocksProxyName))
            {
                proxy = _appSettings.SocksProxies.FirstOrDefault(p =>
                    string.Equals(p.Name, server.SocksProxyName, StringComparison.OrdinalIgnoreCase));
            }

            if (proxy != null)
            {
                if (!string.Equals(server.SocksProxyId, proxy.Id, StringComparison.OrdinalIgnoreCase))
                    server.SocksProxyId = proxy.Id;
                if (!string.Equals(server.SocksProxyName, proxy.Name, StringComparison.Ordinal))
                    server.SocksProxyName = proxy.Name;
                return true;
            }

            return false;
        }

        private static bool HasSocksProxyReference(ServerInfo server)
            => !string.IsNullOrWhiteSpace(server.SocksProxyId) ||
               !string.IsNullOrWhiteSpace(server.SocksProxyName);

        private static string GetSocksProxyLabel(ServerInfo server)
            => !string.IsNullOrWhiteSpace(server.SocksProxyName)
                ? server.SocksProxyName
                : server.SocksProxyId;

        private async Task<bool> CheckServerHealthBeforeConnectAsync(ServerInfo server)
        {
            var address = BuildServerAddress(server);

            if (TryGetSocksProxy(server, out var socksEntry) && socksEntry != null)
            {
                StatusText.Text = $"正在检测经 SOCKS 代理「{socksEntry.Name}」到目标的连通性...";
                var health = await _connectionHealthService.CheckAsync(
                    server.IP, server.Port, _appSettings.HealthCheckTimeoutMs, socksEntry);
                if (!health.PortReachable)
                {
                    StatusText.Text = $"代理路径不可达：{health.Message}";
                    var result = AppMsg.Show(this,
                        $"经 SOCKS 代理「{socksEntry.Name}」访问目标失败：\n{health.Message}\n\n是否仍尝试连接？",
                        "代理路径不可达", AppMsgIcon.Warning, AppMsgButton.YesNo);
                    if (result != AppMsgResult.Yes) return false;
                }
                StatusText.Text = $"正在建立连接（经 {socksEntry.Name}）...";
                return true;
            }

            if (HasSocksProxyReference(server))
            {
                StatusText.Text = "该服务器选择了 SOCKS 代理，但该代理未在设置中找到";
                var result = AppMsg.Show(this,
                    $"该服务器选择的代理「{GetSocksProxyLabel(server)}」在设置中不存在。\n\n请先在设置中添加对应机房的 SOCKS 代理，或将该服务器改为「直连」。\n\n是否仍尝试直连？",
                    "SOCKS 代理未配置", AppMsgIcon.Warning, AppMsgButton.YesNo);
                if (result != AppMsgResult.Yes) return false;
            }

            StatusText.Text = $"正在检测 {server.Name} 的连通性...";
            var directHealth = await _connectionHealthService.CheckAsync(
                server.IP, server.Port, _appSettings.HealthCheckTimeoutMs);

            AppLogger.Info($"health check for {address}: tcp={directHealth.PortReachable}, detail={directHealth.Message}");

            if (directHealth.PortReachable)
            {
                StatusText.Text = $"端口可达，正在建立 {server.Name} 的连接...";
                return true;
            }

            StatusText.Text      = $"无法连接 {server.Name}";
            DescriptionText.Text = directHealth.Message;

            var yesNo = AppMsg.Show(this,
                $"连接前检查失败：{server.Name}\n\n{directHealth.Message}\n\n是否仍尝试连接？",
                "连通性检查", AppMsgIcon.Warning, AppMsgButton.YesNo);
            return yesNo == AppMsgResult.Yes;
        }

        // ── 发起 RDP 连接 ─────────────────────────────────────────────────────

        private void BeginConnect(RdpTabSession tab, ServerInfo server)
        {
            try
            {
                var address = BuildServerAddress(server);
                AppLogger.Info($"begin rdp connect: {server.Name} ({address})");
                tab.Server  = server;
                tab.Address = address;

                string? connectOverride = null;
                if (TryGetSocksProxy(server, out var socks) && socks != null)
                {
                    var bridge = new SocksProxyBridge();
                    var localPort = bridge.Start(socks, server.IP, server.Port);
                    tab.SocksBridge = bridge;
                    connectOverride = $"127.0.0.1:{localPort}";
                    AppLogger.Info($"socks bridge [{socks.Name}] 127.0.0.1:{localPort} -> {server.IP}:{server.Port}");
                }

                var (_, _, logW, logH) = GetTargetDesktopSize();
                tab.LastDisplayWidth  = logW;
                tab.LastDisplayHeight = logH;
                _syncDisplaySettingsRequested = false;
                _lastWindowState = WindowState;

                SwitchToSession(tab.InstanceId);
                StatusText.Text      = $"正在连接到 {server.Name}...";
                DescriptionText.Text = server.Description ?? "";
                Title = $"RemoteX — {server.Name}";

                UpdateTabDot(tab, null);
                var authLevel = Math.Clamp(_appSettings.RdpAuthLevel, 0, 2);
                tab.Session.Connect(server, logW, logH, authLevel, connectOverride);

                StartConnectTimeout(tab.InstanceId);
            }
            catch (Exception ex)
            {
                AppLogger.Error("rdp connect failed", ex);
                AppMsg.Show(this, "连接失败: " + ex.Message, "错误", AppMsgIcon.Error);
                RemoveTabSession(tab.InstanceId);
            }
        }

        // ── 连接超时监控 ──────────────────────────────────────────────────────

        private void StartConnectTimeout(long instanceId)
        {
            var timeoutMs = _appSettings.ConnectTimeoutMs;
            if (timeoutMs <= 0) return;

            var cts = new CancellationTokenSource();

            if (_connectTimeouts.TryGetValue(instanceId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            _connectTimeouts[instanceId] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeoutMs, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!TryGetLiveTabSession(instanceId, out var tab) || tab == null) return;
                    if (tab.IsConnected) return;

                    AppLogger.Warn($"connect timeout ({timeoutMs} ms): {tab.Address}");
                    StatusText.Text = $"连接超时 {tab.Server.Name}";
                    ShowToast($"连接超时（{timeoutMs / 1000} 秒）\n{tab.Server.Name}");
                    RemoveTabSession(instanceId);
                });
            });
        }

        private void CancelConnectTimeout(long instanceId)
        {
            if (_connectTimeouts.TryGetValue(instanceId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _connectTimeouts.Remove(instanceId);
            }
        }

        private void DisposeAllConnectTimeouts()
        {
            foreach (var cts in _connectTimeouts.Values)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch { }
            }
            _connectTimeouts.Clear();
        }

        // ── RDP 连接事件回调 ──────────────────────────────────────────────────

        private void OnTabConnected(long instanceId)
        {
            if (!TryGetLiveTabSession(instanceId, out var tab) || tab == null) return;
            AppLogger.Info($"rdp connected: {tab.Address}");

            CancelConnectTimeout(instanceId);

            UpdateTabDot(tab, true);
            UpdateCurrentTabStatus();

            _appSettings.AddRecentServer(tab.Server.Id);
            RefreshRecentSection();

            _syncDisplaySettingsRequested = true;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void OnTabDisconnected(long instanceId)
        {
            if (!TryGetLiveTabSession(instanceId, out var tab) || tab == null) return;
            AppLogger.Info($"rdp disconnected: {tab.Address}");
            CancelConnectTimeout(instanceId);
            UpdateTabDot(tab, false);
            UpdateCurrentTabStatus();
        }

        // ── 断开按钮 ──────────────────────────────────────────────────────────

        private void DisconnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AppLogger.Info("disconnect requested by user");
            _syncDisplaySettingsRequested = false;
            _resizeTimer.Stop();

            if (_activeTabId > 0 && _tabSessions.ContainsKey(_activeTabId))
                RequestRemoveTabSession(_activeTabId);
            else
                UpdateDisconnectedState();
        }

        // ── 超时 CTS 字典 ─────────────────────────────────────────────────────
        private readonly Dictionary<long, CancellationTokenSource> _connectTimeouts = new();
    }
}
