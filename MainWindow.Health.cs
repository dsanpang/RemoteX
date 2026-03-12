using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.ComponentModel;

namespace RemoteX
{
    public partial class MainWindow
    {
        // 批量健康检测

        private async Task CheckServersHealthAsync(
            IReadOnlyList<ServerInfo> targets, bool reorderAfterCheck,
            CancellationToken ct = default)
        {
            if (targets.Count == 0)
            {
                StatusText.Text = "暂无可检测服务器";
                return;
            }

            if (_isHealthChecking)
            {
                StatusText.Text = "正在检测当前服务器，请稍候...";
                return;
            }

            _isBulkHealthChecking = true;
            var selectedId = ServerList.SelectedItem is ServerInfo selected ? selected.Id : 0;

            try
            {
                foreach (var s in targets)
                {
                    s.HealthState   = ServerHealthState.Checking;
                    s.HealthMessage = "";
                }

                StatusText.Text      = $"正在检测 {targets.Count} 台服务器... （再次点击可取消）";
                DescriptionText.Text = "";

                using var limiter = new SemaphoreSlim(_appSettings.HealthCheckConcurrency);
                var tasks = targets.Select(async server =>
                {
                    bool gotSlot = false;
                    try
                    {
                        await limiter.WaitAsync(ct);
                        gotSlot = true;

                        SocksProxyEntry? proxy = null;
                        var hasProxyRef = HasSocksProxyReference(server);
                        TryGetSocksProxy(server, out proxy);

                        if (hasProxyRef && proxy == null)
                            return (server, reachable: false, message: $"代理配置缺失 ({GetSocksProxyLabel(server)})", skipped: false);

                        var result = await _connectionHealthService.CheckAsync(
                            server.IP, server.Port, _appSettings.HealthCheckTimeoutMs, proxy);
                        return (server, reachable: result.PortReachable, message: result.Message, skipped: false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 在等待信号量时被取消，标记为跳过
                        return (server, reachable: false, message: "", skipped: true);
                    }
                    catch (Exception ex)
                    {
                        return (server, reachable: false, message: $"超时错误 ({ex.Message})", skipped: false);
                    }
                    finally
                    {
                        if (gotSlot) limiter.Release();
                    }
                }).ToList();

                var probeResults = await Task.WhenAll(tasks);
                int online = 0, offline = 0;
                foreach (var item in probeResults)
                {
                    if (item.skipped)
                    {
                        // 被取消跳过的保留 Checking → 重置为 Unknown
                        item.server.HealthState   = ServerHealthState.Unknown;
                        item.server.HealthMessage = "";
                        continue;
                    }
                    item.server.HealthState   = item.reachable
                        ? ServerHealthState.Online
                        : ServerHealthState.Offline;
                    item.server.HealthMessage = item.message;
                    if (item.reachable) online++; else offline++;
                }
                int checked_ = online + offline;

                if (reorderAfterCheck)
                    ApplyHealthSort();
                else
                    _serverView?.Refresh();

                RestoreSelectionByServerId(selectedId);
                UpdateServerCount();

                StatusText.Text = $"检测完成：在线 {online} / 已检 {checked_}";
                DescriptionText.Text = offline == 0
                    ? "所有已检测服务器端口可达"
                    : $"检测完成：{offline} 台离线，共检测 {checked_} 台";
                AppLogger.Info(
                    $"bulk health check: total={probeResults.Length}, checked={checked_}, online={online}, offline={offline}");
            }
            catch (OperationCanceledException)
            {
                // 理论上不应到达（各 task 已内部捕获 OCE），作为保底处理
                foreach (var s in targets.Where(s => s.HealthState == ServerHealthState.Checking))
                {
                    s.HealthState   = ServerHealthState.Unknown;
                    s.HealthMessage = "";
                }
                _serverView?.Refresh();
                StatusText.Text      = "检测已取消";
                DescriptionText.Text = "";
                AppLogger.Info("bulk health check cancelled by user");
            }
            finally
            {
                _isBulkHealthChecking = false;
            }
        }

        /// <summary>
        /// 批量检测后按健康状态排序显示（临时修改 SortDescriptions，不影响 DB 排序）。
        /// </summary>
        private void ApplyHealthSort()
        {
            if (_serverView == null) return;
            _serverView.SortDescriptions.Clear();
            _serverView.SortDescriptions.Add(new SortDescription(
                nameof(ServerInfo.HealthOrder), ListSortDirection.Ascending));
            _serverView.SortDescriptions.Add(new SortDescription(
                nameof(ServerInfo.Name), ListSortDirection.Ascending));
            _serverView.LiveSortingProperties.Clear();
            _serverView.LiveSortingProperties.Add(nameof(ServerInfo.HealthOrder));
            _serverView.IsLiveSorting = true;
        }

        /// <summary>
        /// 恢复为默认的 SortOrder 排序。
        /// </summary>
        private void ResetToDefaultSort()
        {
            if (_serverView == null) return;
            _serverView.SortDescriptions.Clear();
            _serverView.SortDescriptions.Add(new SortDescription(
                nameof(ServerInfo.SortOrder), ListSortDirection.Ascending));
            _serverView.SortDescriptions.Add(new SortDescription(
                nameof(ServerInfo.Name), ListSortDirection.Ascending));
            _serverView.LiveSortingProperties.Clear();
            _serverView.LiveSortingProperties.Add(nameof(ServerInfo.SortOrder));
            _serverView.IsLiveSorting = true;
        }

        // ── Context menu / Button 事件 ────────────────────────────────────────

        private async void MenuConnect_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is ServerInfo server)
                await ConnectToServerAsync(server);
        }

        private async void MenuCheck_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is not ServerInfo server) return;
            await CheckServersHealthAsync(new List<ServerInfo> { server }, reorderAfterCheck: false);
        }

        private void MenuDisconnect_Click(object sender, RoutedEventArgs e)
            => DisconnectButton_Click(sender, e);

        private void MenuEdit_Click(object sender, RoutedEventArgs e)
            => EditServerButton_Click(sender, e);

        private async void MenuClone_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is not ServerInfo source) return;
            var clone = new ServerInfo
            {
                Name = source.Name + " (副本)",
                IP = source.IP,
                Username = source.Username,
                Password = source.Password,
                Description = source.Description,
                Group = source.Group,
                Protocol = source.Protocol,
                SshPrivateKeyPath = source.SshPrivateKeyPath,
                Port = source.Port,
                SocksProxyId = source.SocksProxyId,
                SocksProxyName = source.SocksProxyName
            };
            var dlg = new ServerEditWindow(clone, isNew: true,
                _appSettings.SocksProxies,
                ExistingGroups()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var maxOrder = await _serverRepository.GetMaxSortOrderAsync();
            clone.SortOrder = maxOrder + 1;
            await _serverRepository.InsertAsync(clone);
            _servers.Add(clone);
            ResetToDefaultSort();
            RefreshServerView();
            ServerList.SelectedItem = clone;
            ServerList.ScrollIntoView(clone);
            ShowToast($"已克隆服务器：{clone.Name}");
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
            => DeleteServerButton_Click(sender, e);

        private void MenuCopyIp_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is ServerInfo server)
                TrySetClipboard(server.IP);
        }

        private void MenuCopyAddress_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is ServerInfo server)
                TrySetClipboard(server.AddressDisplay);
        }

        /// <summary>
        /// 在独立 STA 线程上写剪贴板，不阻塞 UI。
        /// WPF 内部会自动重试最多 1 秒以等待剪贴板释放。
        /// </summary>
        private static void TrySetClipboard(string text)
        {
            var t = new System.Threading.Thread(() =>
            {
                try { System.Windows.Clipboard.SetText(text); }
                catch { }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        private async void CheckAllButton_Click(object sender, RoutedEventArgs e)
        {
            // 再次点击时取消正在进行的检测
            if (_isBulkHealthChecking)
            {
                _healthCheckCts?.Cancel();
                return;
            }

            _healthCheckCts?.Dispose();
            _healthCheckCts = new CancellationTokenSource();
            await CheckServersHealthAsync(_servers, reorderAfterCheck: true, _healthCheckCts.Token);
            _healthCheckCts.Dispose();
            _healthCheckCts = null;
        }
    }
}
