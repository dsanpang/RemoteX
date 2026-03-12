using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Color      = System.Windows.Media.Color;

using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using Button     = System.Windows.Controls.Button;

namespace RemoteX
{
    public partial class MainWindow
    {
        // ── 导出 ──────────────────────────────────────────────────────────────

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_servers.Count == 0)
            {
                AppMsg.Show(this, "暂无服务器可导出", "提示", AppMsgIcon.Info);
                return;
            }

            var encrypt = AppMsg.Show(this,
                "是否对备份文件加密？\n\n" +
                "点击「是」→ 输入导出密码（跨机器迁移可用）\n" +
                "【注意】导出文件包含加密密码（若设置了迁移密码则加密），\n" +
                "【建议】请保存到安全位置，注意安全",
                "导出选项", AppMsgIcon.Question, AppMsgButton.YesNoCancel);

            if (encrypt == AppMsgResult.Cancel) return;

            string? password = null;
            if (encrypt == AppMsgResult.Yes)
            {
                password = PromptPassword("设置密码", "请设置备份文件的加密密码（可留空）");
                if (password == null) return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title            = "导出服务器列表",
                Filter           = "RDP 备份文件 (*.rdpbak)|*.rdpbak|所有文件 (*.*)|*.*",
                DefaultExt       = ".rdpbak",
                FileName         = $"MyRdpManager_{DateTime.Now:yyyyMMdd}",
                InitialDirectory = string.IsNullOrWhiteSpace(_appSettings.LastExportDirectory)
                                   ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                                   : _appSettings.LastExportDirectory
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                ServerExportImport.Export(_servers, _appSettings.SocksProxies, dlg.FileName, password);
                _appSettings.LastExportDirectory = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
                _appSettings.Save();
                AppMsg.Show(this,
                    $"成功导出 {_servers.Count} 台服务器、{_appSettings.SocksProxies.Count} 个代理到：\n{dlg.FileName}",
                    "导出成功", AppMsgIcon.Success);
            }
            catch (Exception ex)
            {
                AppLogger.Error("export failed", ex);
                AppMsg.Show(this, $"导出失败：{ex.Message}", "错误", AppMsgIcon.Error);
            }
        }

        // ── 导入 ──────────────────────────────────────────────────────────────

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title            = "导入服务器列表",
                Filter           = "RDP 备份文件 (*.rdpbak)|*.rdpbak|所有文件 (*.*)|*.*",
                InitialDirectory = string.IsNullOrWhiteSpace(_appSettings.LastImportDirectory)
                                   ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                                   : _appSettings.LastImportDirectory
            };

            if (dlg.ShowDialog() != true) return;

            _appSettings.LastImportDirectory = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
            _appSettings.Save();

            ImportResult importedResult;
            try
            {
                importedResult = ServerExportImport.Import(dlg.FileName, null);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("加密"))
            {
                var password = PromptPassword("输入密码", "此备份文件已加密，请输入解密密码");
                if (password == null) return;
                try
                {
                    importedResult = ServerExportImport.Import(dlg.FileName, password);
                }
                catch (Exception ex2)
                {
                    AppMsg.Show(this, $"导入失败：{ex2.Message}", "错误", AppMsgIcon.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                AppMsg.Show(this, $"导入失败：{ex.Message}", "错误", AppMsgIcon.Error);
                return;
            }

            await ApplyImportAsync(importedResult);
        }

        // ── 云同步 ──────────────────────────────────────────────────────────────

        private async void CloudUploadButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _appSettings.CloudSync;
            if (!cfg.Enabled)
            {
                AppMsg.Show(this, "请先在设置中启用并配置云同步。", "云同步未启用", AppMsgIcon.Info);
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.SyncPassword))
            {
                AppMsg.Show(this, "同步密码不能为空，请在设置中配置同步密码。", "同步密码未设置", AppMsgIcon.Warning);
                return;
            }

            var confirm = AppMsg.Show(this,
                $"将本地 {_servers.Count} 台服务器、{_appSettings.SocksProxies.Count} 个代理上传到云端。\n\n" +
                "此操作会覆盖云端已有数据，是否继续？",
                "确认上传", AppMsgIcon.Question, AppMsgButton.YesNo);
            if (confirm != AppMsgResult.Yes) return;

            SyncMenu_CloudUpload.IsEnabled   = false;
            SyncMenu_CloudDownload.IsEnabled = false;
            StatusText.Text = "正在上传到云端…";
            try
            {
                var svc = new CloudSyncService(cfg);
                await svc.UploadAsync(_servers, _appSettings.SocksProxies);
                cfg.LastSyncUtc = DateTime.UtcNow.ToString("O");
                _appSettings.Save();
                StatusText.Text = $"云同步上传成功  {DateTime.Now:HH:mm:ss}";
                AppLogger.Info($"cloud sync upload: {_servers.Count} servers");
                AppMsg.Show(this,
                    $"上传成功：{_servers.Count} 台服务器、{_appSettings.SocksProxies.Count} 个代理",
                    "上传成功", AppMsgIcon.Success);
            }
            catch (Exception ex)
            {
                AppLogger.Error("cloud sync upload failed", ex);
                StatusText.Text = "云同步上传失败";
                AppMsg.Show(this, $"上传失败：{ex.Message}", "上传失败", AppMsgIcon.Error);
            }
            finally
            {
                SyncMenu_CloudUpload.IsEnabled   = true;
                SyncMenu_CloudDownload.IsEnabled = true;
            }
        }

        private async void CloudDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _appSettings.CloudSync;
            if (!cfg.Enabled)
            {
                AppMsg.Show(this, "请先在设置中启用并配置云同步。", "云同步未启用", AppMsgIcon.Info);
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.SyncPassword))
            {
                AppMsg.Show(this, "同步密码不能为空，请在设置中配置同步密码。", "同步密码未设置", AppMsgIcon.Warning);
                return;
            }

            SyncMenu_CloudUpload.IsEnabled   = false;
            SyncMenu_CloudDownload.IsEnabled = false;
            StatusText.Text = "正在从云端下载…";
            try
            {
                var svc    = new CloudSyncService(cfg);
                var result = await svc.DownloadAsync();
                cfg.LastSyncUtc = DateTime.UtcNow.ToString("O");
                _appSettings.Save();

                if (result.Servers.Count == 0 && result.Proxies.Count == 0)
                {
                    StatusText.Text = "云端数据为空";
                    AppMsg.Show(this, "云端备份中没有任何数据。", "下载结果", AppMsgIcon.Info);
                    return;
                }

                StatusText.Text = "正在应用云端数据…";
                await ApplyImportAsync(result);
                AppLogger.Info($"cloud sync download: {result.Servers.Count} servers");
            }
            catch (Exception ex)
            {
                AppLogger.Error("cloud sync download failed", ex);
                StatusText.Text = "云同步下载失败";
                AppMsg.Show(this, $"下载失败：{ex.Message}", "下载失败", AppMsgIcon.Error);
            }
            finally
            {
                SyncMenu_CloudUpload.IsEnabled   = true;
                SyncMenu_CloudDownload.IsEnabled = true;
            }
        }

        // ── 应用导入结果（本地导入与云同步共用）────────────────────────────────

        private async Task ApplyImportAsync(ImportResult importedResult)
        {
            int proxiesAdded = 0, proxiesUpdated = 0;
            foreach (var importedProxy in importedResult.Proxies)
            {
                importedProxy.EnsureId();
                var existingProxy = _appSettings.SocksProxies.FirstOrDefault(p =>
                    string.Equals(p.Id, importedProxy.Id, StringComparison.OrdinalIgnoreCase));
                if (existingProxy == null)
                {
                    _appSettings.SocksProxies.Add(importedProxy);
                    proxiesAdded++;
                }
                else
                {
                    existingProxy.Name            = importedProxy.Name;
                    existingProxy.Host            = importedProxy.Host;
                    existingProxy.Port            = importedProxy.Port;
                    existingProxy.Username        = importedProxy.Username;
                    existingProxy.Password        = importedProxy.Password;
                    existingProxy.UseTls          = importedProxy.UseTls;
                    existingProxy.TlsServerName   = importedProxy.TlsServerName;
                    existingProxy.TlsPinnedSha256 = importedProxy.TlsPinnedSha256;
                    proxiesUpdated++;
                }
            }
            if (proxiesAdded > 0 || proxiesUpdated > 0)
                _appSettings.Save();

            var imported = importedResult.Servers;
            if (imported.Count == 0)
            {
                var msg = proxiesAdded + proxiesUpdated > 0
                    ? $"代理：新增 {proxiesAdded} 个，更新 {proxiesUpdated} 个"
                    : "导入数据中没有服务器";
                AppMsg.Show(this, msg, "提示", AppMsgIcon.Info);
                return;
            }

            var conflicts    = imported.FindAll(imp =>
                _servers.Exists(e =>
                    string.Equals(e.IP, imp.IP, StringComparison.OrdinalIgnoreCase) &&
                    e.Port == imp.Port));
            var nonConflicts = imported.FindAll(imp =>
                !_servers.Exists(e =>
                    string.Equals(e.IP, imp.IP, StringComparison.OrdinalIgnoreCase) &&
                    e.Port == imp.Port));

            bool overwriteConflicts = false;
            if (conflicts.Count > 0)
            {
                var conflictMsg =
                    $"导入的 {imported.Count} 台服务器中，有 {conflicts.Count} 台与现有服务器 IP:Port 相同：\n\n" +
                    string.Join("\n", conflicts.Take(5).Select(s => $"  • {s.Name}  ({s.IP}:{s.Port})")) +
                    (conflicts.Count > 5 ? $"\n  ... 共 {conflicts.Count} 条" : "") +
                    "\n\n点击「是」覆盖现有条目，「否」跳过冲突，「取消」放弃本次导入";

                var conflictResult = AppMsg.Show(this,
                    conflictMsg, "导入冲突", AppMsgIcon.Question, AppMsgButton.YesNoCancel);
                if (conflictResult == AppMsgResult.Cancel) return;
                overwriteConflicts = conflictResult == AppMsgResult.Yes;
            }

            int added = 0, overwritten = 0, skipped = 0;
            var maxOrder = await _serverRepository.GetMaxSortOrderAsync();

            foreach (var s in nonConflicts)
            {
                s.SortOrder = ++maxOrder;
                await _serverRepository.InsertAsync(s);
                _servers.Add(s);
                added++;
            }
            foreach (var imp in conflicts)
            {
                if (overwriteConflicts)
                {
                    var existing = _servers.Find(e =>
                        string.Equals(e.IP, imp.IP, StringComparison.OrdinalIgnoreCase) &&
                        e.Port == imp.Port)!;
                    existing.Name             = imp.Name;
                    existing.Username         = imp.Username;
                    existing.Password         = imp.Password;
                    existing.Description      = imp.Description;
                    existing.Group            = imp.Group;
                    existing.Protocol         = imp.Protocol;
                    existing.SshPrivateKeyPath = imp.SshPrivateKeyPath;
                    existing.SocksProxyId     = imp.SocksProxyId;
                    existing.SocksProxyName   = imp.SocksProxyName;
                    await _serverRepository.UpdateAsync(existing);
                    overwritten++;
                }
                else skipped++;
            }

            ResetToDefaultSort();
            RefreshServerView();
            AppLogger.Info($"import applied: added={added}, overwritten={overwritten}, skipped={skipped}");

            var summary = $"完成：新增 {added} 台";
            if (overwritten > 0)    summary += $"，覆盖 {overwritten} 台";
            if (skipped > 0)        summary += $"，跳过 {skipped} 台";
            if (proxiesAdded > 0)   summary += $"，新增代理 {proxiesAdded} 个";
            if (proxiesUpdated > 0) summary += $"，更新代理 {proxiesUpdated} 个";
            StatusText.Text = summary;
            AppMsg.Show(this, summary, "导入结果", AppMsgIcon.Success);
        }

        // 密码输入对话框

        private string? PromptPassword(string title, string prompt)
        {
            var win = new Window
            {
                Owner                 = this,
                Title                 = title,
                Width                 = 400,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode            = ResizeMode.NoResize,
                Background            = ColBg
            };

            // 提示文字
            var lbl = new System.Windows.Controls.TextBlock
            {
                Text            = prompt,
                Foreground      = ColSubtext,
                FontSize        = 12,
                TextWrapping    = TextWrapping.Wrap,
                Margin          = new Thickness(0, 0, 0, 12)
            };

            // 密码输入框
            var pwdBox = new System.Windows.Controls.PasswordBox
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                Foreground      = ColText,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
                BorderThickness = new Thickness(1),
                CaretBrush      = ColBlue,
                SelectionBrush  = ColBlue,
                Padding         = new Thickness(10, 0, 10, 0),
                Height          = 34,
                FontSize        = 13,
                Margin          = new Thickness(0, 0, 0, 0)
            };

            // 按钮行：[取消] [确定]
            var cancelBtn = new Button
            {
                Content = "取 消",
                Width   = 92, Height = 34,
                Margin  = new Thickness(0, 0, 10, 0),
                Style   = (Style)Application.Current.FindResource("DlgSecondaryBtn")
            };
            var okBtn = new Button
            {
                Content = "确 定",
                Width   = 92, Height = 34,
                Style   = (Style)Application.Current.FindResource("DlgPrimaryBtn")
            };

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin              = new Thickness(0, 20, 0, 0)
            };
            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
            root.Children.Add(lbl);
            root.Children.Add(pwdBox);
            root.Children.Add(btnRow);
            win.Content = root;

            okBtn.Click     += (_, __) => { win.DialogResult = true;  win.Close(); };
            cancelBtn.Click += (_, __) => { win.DialogResult = false; win.Close(); };
            pwdBox.KeyDown  += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { win.DialogResult = true; win.Close(); }
            };
            win.Loaded += (_, __) => pwdBox.Focus();

            return win.ShowDialog() == true ? pwdBox.Password : null;
        }
    }
}
