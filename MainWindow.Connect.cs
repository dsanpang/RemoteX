using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using MessageBox = System.Windows.MessageBox;

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

            if (_tabSessions.TryGetValue(server.Id, out var existing))
            {
                if (existing.IsConnected)
                {
                    SwitchToSession(server.Id);
                    UpdateCurrentTabStatus();
                    return;
                }

                AppLogger.Info($"session disconnected, recreating rdp control: {server.Name}");
                RemoveTabSession(server.Id);
            }

            var tab = CreateTabSession(server);
            _tabSessions[server.Id] = tab;
            BeginConnect(tab, server);
        }

        // ── 连通性预检 ────────────────────────────────────────────────────────

        private bool TryGetSocksProxy(ServerInfo server, out SocksProxyEntry? proxy)
        {
            proxy = null;
            if (string.IsNullOrWhiteSpace(server.SocksProxyName) || _appSettings.SocksProxies == null) return false;
            proxy = _appSettings.SocksProxies.FirstOrDefault(p => string.Equals(p.Name, server.SocksProxyName, StringComparison.OrdinalIgnoreCase));
            return proxy != null;
        }

        private async Task<bool> CheckServerHealthBeforeConnectAsync(ServerInfo server)
        {
            var address = BuildServerAddress(server);

            if (TryGetSocksProxy(server, out var socksEntry) && socksEntry != null)
            {
                StatusText.Text = $"正在检测 SOCKS 代理「{socksEntry.Name}」{socksEntry.Host}:{socksEntry.Port}...";
                var health = await _connectionHealthService.CheckAsync(
                    socksEntry.Host, socksEntry.Port, _appSettings.HealthCheckTimeoutMs);
                if (!health.PortReachable)
                {
                    StatusText.Text = $"SOCKS 代理不可达：{health.Message}";
                    var result = MessageBox.Show(
                        $"SOCKS 代理「{socksEntry.Name}」{socksEntry.Host}:{socksEntry.Port} 无法连接：\n{health.Message}\n\n是否仍尝试连接？",
                        "SOCKS 不可达",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes) return false;
                }
                StatusText.Text = $"正在建立到 {address} 的连接（经 {socksEntry.Name}）...";
                return true;
            }

            if (!string.IsNullOrWhiteSpace(server.SocksProxyName))
            {
                StatusText.Text = "该服务器选择了 SOCKS 代理，但该代理未在设置中找到";
                var result = MessageBox.Show(
                    $"该服务器选择的代理「{server.SocksProxyName}」在设置中不存在。\n\n请先在设置中添加对应机房的 SOCKS 代理，或将该服务器改为「直连」。\n\n是否仍尝试直连？",
                    "SOCKS 代理未配置",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return false;
            }

            StatusText.Text = $"正在检测 {address} 的连通性...";
            var directHealth = await _connectionHealthService.CheckAsync(
                server.IP, server.Port, _appSettings.HealthCheckTimeoutMs);

            AppLogger.Info($"health check for {address}: tcp={directHealth.PortReachable}, detail={directHealth.Message}");

            if (directHealth.PortReachable)
            {
                StatusText.Text = $"端口可达，正在建立 {address} 的连接...";
                return true;
            }

            StatusText.Text      = $"无法连接 {address}";
            DescriptionText.Text = directHealth.Message;

            var yesNo = MessageBox.Show(
                $"连接前检查失败：\n{address}\n\n{directHealth.Message}\n\n是否仍尝试连接？",
                "连通性检查",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return yesNo == MessageBoxResult.Yes;
        }

        // 发起 RDP 连接（含超时监控）

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
                    var localPort = bridge.Start(
                        socks.Host, socks.Port,
                        string.IsNullOrWhiteSpace(socks.Username) ? null : socks.Username,
                        string.IsNullOrWhiteSpace(socks.Password) ? null : socks.Password,
                        server.IP, server.Port);
                    tab.SocksBridge = bridge;
                    connectOverride = $"127.0.0.1:{localPort}";
                    AppLogger.Info($"socks bridge [{socks.Name}] 127.0.0.1:{localPort} -> {server.IP}:{server.Port}");
                }

                var (_, _, logW, logH) = GetTargetDesktopSize();
                tab.LastDisplayWidth  = logW;
                tab.LastDisplayHeight = logH;
                _syncDisplaySettingsRequested = false;
                _lastWindowState = WindowState;

                SwitchToSession(server.Id);
                StatusText.Text      = $"正在连接到 {address}...";
                DescriptionText.Text = server.Description ?? "";
                Title = $"RemoteX — {server.Name}  ({address})";

                UpdateTabDot(tab, null);
                var authLevel = Math.Clamp(_appSettings.RdpAuthLevel, 0, 2);
                tab.Session.Connect(server, logW, logH, authLevel, connectOverride);

                StartConnectTimeout(server.Id);
            }
            catch (Exception ex)
            {
                AppLogger.Error("rdp connect failed", ex);
                MessageBox.Show("连接失败: " + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                RemoveTabSession(server.Id);
            }
        }

        // ── 连接超时监控 ──────────────────────────────────────────────────────

        private void StartConnectTimeout(int serverId)
        {
            var timeoutMs = _appSettings.ConnectTimeoutMs;
            if (timeoutMs <= 0) return;

            var cts = new CancellationTokenSource();

            if (_connectTimeouts.TryGetValue(serverId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            _connectTimeouts[serverId] = cts;

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
                    if (!_tabSessions.TryGetValue(serverId, out var tab)) return;
                    if (tab.IsConnected) return;

                    AppLogger.Warn($"connect timeout ({timeoutMs} ms): {tab.Address}");
                    StatusText.Text = $"连接超时 {tab.Address}";
                    ShowToast($"连接超时（{timeoutMs / 1000} 秒）\n{tab.Address}");
                    RemoveTabSession(serverId);
                });
            });
        }

        private void CancelConnectTimeout(int serverId)
        {
            if (_connectTimeouts.TryGetValue(serverId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _connectTimeouts.Remove(serverId);
            }
        }

        // ── RDP 连接事件回调 ──────────────────────────────────────────────────

        private void OnTabConnected(int serverId)
        {
            if (!_tabSessions.TryGetValue(serverId, out var tab)) return;
            AppLogger.Info($"rdp connected: {tab.Address}");

            CancelConnectTimeout(serverId);

            UpdateTabDot(tab, true);
            UpdateCurrentTabStatus();

            _appSettings.AddRecentServer(serverId);
            RefreshRecentSection();

            _syncDisplaySettingsRequested = true;
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void OnTabDisconnected(int serverId)
        {
            if (!_tabSessions.TryGetValue(serverId, out var tab)) return;
            AppLogger.Info($"rdp disconnected: {tab.Address}");
            CancelConnectTimeout(serverId);
            UpdateTabDot(tab, false);
            UpdateCurrentTabStatus();
        }

        // ── 断开按钮 ──────────────────────────────────────────────────────────

        private void DisconnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            AppLogger.Info("disconnect requested by user");
            _syncDisplaySettingsRequested = false;
            _resizeTimer.Stop();

            if (_activeServerId > 0 && _tabSessions.ContainsKey(_activeServerId))
                RequestRemoveTabSession(_activeServerId);
            else
                UpdateDisconnectedState();
        }

        // ── 超时 CTS 字典 ─────────────────────────────────────────────────────
        private readonly Dictionary<int, CancellationTokenSource> _connectTimeouts = new();
    }
}
