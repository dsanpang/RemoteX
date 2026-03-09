using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Color      = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
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
                MessageBox.Show("暂无服务器可导出", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var encrypt = MessageBox.Show(
                "是否对备份文件加密？\n\n" +
                "点击「是」→ 输入导出密码（跨机器迁移可用）\n" +
                "【注意】导出文件包含加密密码（若设置了迁移密码则加密），\n" +
                "【建议】请保存到安全位置，注意安全",
                "导出选项",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (encrypt == MessageBoxResult.Cancel) return;

            string? password = null;
            if (encrypt == MessageBoxResult.Yes)
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
                ServerExportImport.Export(_servers, dlg.FileName, password);
                _appSettings.LastExportDirectory = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
                _appSettings.Save();
                MessageBox.Show(
                    $"成功导出 {_servers.Count} 台服务器到：\n{dlg.FileName}",
                    "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Error("export failed", ex);
                MessageBox.Show($"导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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

            List<ServerInfo> imported;
            try
            {
                imported = ServerExportImport.Import(dlg.FileName, null);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("加密"))
            {
                var password = PromptPassword("输入密码", "此备份文件已加密，请输入解密密码");
                if (password == null) return;
                try
                {
                    imported = ServerExportImport.Import(dlg.FileName, password);
                }
                catch (Exception ex2)
                {
                    MessageBox.Show($"导入失败：{ex2.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (imported.Count == 0)
            {
                MessageBox.Show("备份文件中没有服务器数据", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 找出所有冲突（IP+Port 相同）
            var conflicts = imported.Where(imp =>
                _servers.Exists(e =>
                    string.Equals(e.IP, imp.IP, StringComparison.OrdinalIgnoreCase) &&
                    e.Port == imp.Port)
            ).ToList();

            var nonConflicts = imported.Where(imp =>
                !_servers.Exists(e =>
                    string.Equals(e.IP, imp.IP, StringComparison.OrdinalIgnoreCase) &&
                    e.Port == imp.Port)
            ).ToList();

            // 若有冲突，询问用户如何处理
            bool overwriteConflicts = false;
            if (conflicts.Count > 0)
            {
                var msg = $"导入的 {imported.Count} 台服务器中，有 {conflicts.Count} 台与现有服务器 IP:Port 相同：\n\n" +
                          string.Join("\n", conflicts.Take(5).Select(s => $"  • {s.Name}  ({s.IP}:{s.Port})")) +
                          (conflicts.Count > 5 ? $"\n  ... 共 {conflicts.Count} 条" : "") +
                          "\n\n请选择处理方式";

                var conflictResult = MessageBox.Show(
                    msg,
                    "导入冲突",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (conflictResult == MessageBoxResult.Cancel) return;
                overwriteConflicts = conflictResult == MessageBoxResult.Yes;
            }

            int added = 0, overwritten = 0, skipped = 0;
            var maxOrder = await _serverRepository.GetMaxSortOrderAsync();

            // 导入无冲突项
            foreach (var s in nonConflicts)
            {
                s.SortOrder = ++maxOrder;
                await _serverRepository.InsertAsync(s);
                _servers.Add(s);
                added++;
            }

            // 处理冲突
            foreach (var imp in conflicts)
            {
                if (overwriteConflicts)
                {
                    var existing = _servers.Find(e =>
                        string.Equals(e.IP, imp.IP, StringComparison.OrdinalIgnoreCase) &&
                        e.Port == imp.Port)!;

                    existing.Name        = imp.Name;
                    existing.Username    = imp.Username;
                    existing.Password    = imp.Password;
                    existing.Description = imp.Description;
                    existing.Group       = imp.Group;
                    await _serverRepository.UpdateAsync(existing);
                    overwritten++;
                }
                else
                {
                    skipped++;
                }
            }

            ResetToDefaultSort();
            RefreshServerView();
            AppLogger.Info($"import: added={added}, overwritten={overwritten}, skipped={skipped}");

            var summary = $"导入完成：新增 {added} 台";
            if (overwritten > 0) summary += $"，覆盖 {overwritten} 台";
            if (skipped > 0)     summary += $"，跳过 {skipped} 台";
            MessageBox.Show(summary, "导入结果", MessageBoxButton.OK, MessageBoxImage.Information);
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
