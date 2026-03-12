using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


using MouseEventArgs  = System.Windows.Input.MouseEventArgs;
using DragEventArgs   = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;

namespace RemoteX
{
    public partial class MainWindow
    {
        // ── 加载 ──────────────────────────────────────────────────────────────

        private async Task LoadServersAsync()
        {
            _servers = await _serverRepository.LoadAllAsync();
            _serverView = BuildServerView();
            ServerList.ItemsSource = _serverView;
            UpdateServerCount();
            AppLogger.Info($"servers loaded: {_servers.Count}");
        }

        // ── 新增 ──────────────────────────────────────────────────────────────

        private async void AddServerButton_Click(object sender, RoutedEventArgs e)
        {
            var server = new ServerInfo { Port = _appSettings.DefaultPort };
            var dlg    = new ServerEditWindow(server, isNew: true,
                _appSettings.SocksProxies,
                ExistingGroups()) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            // 追加到列表末尾，SortOrder = max + 1
            var maxOrder   = await _serverRepository.GetMaxSortOrderAsync();
            server.SortOrder = maxOrder + 1;

            await _serverRepository.InsertAsync(server);
            AppLogger.Info($"server inserted: {server.Name} ({server.IP}:{server.Port})");

            _servers.Add(server);
            ResetToDefaultSort();
            RefreshServerView();

            // 选中新增的服务器
            ServerList.SelectedItem = server;
            ServerList.ScrollIntoView(server);
        }

        // ── 编辑 ──────────────────────────────────────────────────────────────

        private async void EditServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is not ServerInfo server) return;

            var dlg = new ServerEditWindow(server, isNew: false,
                _appSettings.SocksProxies,
                ExistingGroups()) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            await _serverRepository.UpdateAsync(server);
            AppLogger.Info($"server updated: {server.Name} ({server.IP}:{server.Port})");

            if (_tabSessions.TryGetValue(server.Id, out var tab))
            {
                tab.Server  = server;
                tab.Address = BuildServerAddress(server);
                UpdateTabHeader(tab, server.Name);
                UpdateCurrentTabStatus();
            }

            // INPC 已触发 UI 更新（Name/IP 等），手动 Refresh 保证分组/排序更新
            _serverView?.Refresh();
        }

        // ── 删除 ──────────────────────────────────────────────────────────────

        private async void DeleteServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (ServerList.SelectedItem is not ServerInfo server) return;

            var result = AppMsg.Show(this,
                $"确定要删除服务器「{server.Name}」吗？",
                "删除确认", AppMsgIcon.Question, AppMsgButton.YesNo);
            if (result != AppMsgResult.Yes) return;

            RemoveTabSession(server.Id);
            await _serverRepository.DeleteAsync(server);
            AppLogger.Info($"server deleted: {server.Name} ({server.IP}:{server.Port})");
            _servers.Remove(server);
            _appSettings.RemoveRecentServer(server.Id);
            RefreshServerView();
            RefreshRecentSection();
        }

        // ── 拖拽排序 ──────────────────────────────────────────────────────────

        private async void ServerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 仅在左键双击了列表项时触发连接
            if (e.ChangedButton != MouseButton.Left) return;
            var el = e.OriginalSource as DependencyObject;
            while (el != null)
            {
                if (el is ListBoxItem) break;
                el = VisualTreeHelper.GetParent(el);
            }
            if (el == null) return;
            if (ServerList.SelectedItem is ServerInfo server)
                await ConnectToServerAsync(server);
        }

        private void ServerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void ServerList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (ServerList.SelectedItem is not ServerInfo server) return;

            var pos  = e.GetPosition(null);
            var diff = _dragStartPoint - pos;

            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            // 搜索或过滤时禁用拖拽（避免混乱）
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;

            DragDrop.DoDragDrop(ServerList, server, DragDropEffects.Move);
        }

        private void ServerList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(ServerInfo))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void ServerList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ServerInfo))) return;

            var source = (ServerInfo)e.Data.GetData(typeof(ServerInfo));
            var target = GetDropTarget(e);

            if (target == null || source.Id == target.Id) return;

            var srcIdx = _servers.IndexOf(source);
            var tgtIdx = _servers.IndexOf(target);
            if (srcIdx < 0 || tgtIdx < 0) return;

            // 重排内存列表
            _servers.RemoveAt(srcIdx);
            _servers.Insert(tgtIdx, source);

            // 更新 SortOrder 并持久化
            for (int i = 0; i < _servers.Count; i++)
                _servers[i].SortOrder = i;

            ResetToDefaultSort();
            _serverView?.Refresh();

            var snapshot = _servers.Select(s => (s.Id, s.SortOrder)).ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _serverRepository.BatchUpdateSortOrderAsync(snapshot);
                    AppLogger.Info("drag-drop sort order saved");
                }
                catch (Exception ex) { AppLogger.Error("save sort order failed", ex); }
            });

            ServerList.SelectedItem = source;
        }

        private List<string> ExistingGroups() =>
            _servers.Select(s => s.Group)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Distinct()
                    .OrderBy(g => g)
                    .ToList();

        private ServerInfo? GetDropTarget(DragEventArgs e)
        {
            var pos    = e.GetPosition(ServerList);
            var result = VisualTreeHelper.HitTest(ServerList, pos);
            if (result == null) return null;

            var el = result.VisualHit as DependencyObject;
            while (el != null)
            {
                if (el is ListBoxItem lbi) return lbi.DataContext as ServerInfo;
                el = VisualTreeHelper.GetParent(el);
            }
            return null;
        }
    }
}
