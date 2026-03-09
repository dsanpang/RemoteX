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
            IReadOnlyList<ServerInfo> targets, bool reorderAfterCheck)
        {
            if (targets.Count == 0)
            {
                StatusText.Text = "暂无可检测服务器";
                return;
            }

            if (_isBulkHealthChecking)
            {
            StatusText.Text = "正在检测中，请稍候...";
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
                // INPC 已触发 UI 更新，无需手动 Refresh

                StatusText.Text      = $"正在检测 {targets.Count} 台服务器端口 ...";
                DescriptionText.Text = "";

                using var limiter = new SemaphoreSlim(_appSettings.HealthCheckConcurrency);
                var tasks = targets.Select(async server =>
                {
                    await limiter.WaitAsync();
                    try
                    {
                        var result = await _connectionHealthService.CheckAsync(
                            server.IP, server.Port, _appSettings.HealthCheckTimeoutMs);
                        return (server, reachable: result.PortReachable, message: result.Message);
                    }
                    catch (Exception ex)
                    {
            return (server, reachable: false, message: $"超时错误 ({ex.Message})");
                    }
                    finally
                    {
                        limiter.Release();
                    }
                }).ToList();

                var probeResults = await Task.WhenAll(tasks);
                foreach (var item in probeResults)
                {
                    item.server.HealthState   = item.reachable
                        ? ServerHealthState.Online
                        : ServerHealthState.Offline;
                    item.server.HealthMessage = item.message;
                }

                var online  = probeResults.Count(r => r.reachable);
                var offline = probeResults.Length - online;

                if (reorderAfterCheck)
                    ApplyHealthSort();
                else
                    _serverView?.Refresh();

                RestoreSelectionByServerId(selectedId);
                UpdateServerCount();

                StatusText.Text = $"检测完成：在线 {online} / 总计 {probeResults.Length}";
                DescriptionText.Text = offline == 0
                    ? "所有服务器端口可达"
                : $"检测完成：{offline} 台离线，共检测 {probeResults.Length} 台";
                AppLogger.Info(
                    $"bulk health check: total={probeResults.Length}, online={online}, offline={offline}");
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
                SocksProxyName = source.SocksProxyName
            };
            var dlg = new ServerEditWindow(clone, isNew: true, _appSettings.SocksProxies?.Select(p => p.Name).ToList()) { Owner = this };
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
                System.Windows.Clipboard.SetText(server.IP);
        }

        private void MenuCopyAddress_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is ServerInfo server)
                System.Windows.Clipboard.SetText(server.AddressDisplay);
        }

        private async void CheckAllButton_Click(object sender, RoutedEventArgs e)
            => await CheckServersHealthAsync(_servers, reorderAfterCheck: true);
    }
}
