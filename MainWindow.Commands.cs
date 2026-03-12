using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

using WpfColor      = System.Windows.Media.Color;
using WpfBrush      = System.Windows.Media.SolidColorBrush;
using WpfBrushes    = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfFW         = System.Windows.FontWeights;
using WpfHA         = System.Windows.HorizontalAlignment;
using WpfVA         = System.Windows.VerticalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfButton     = System.Windows.Controls.Button;
using WpfBorder     = System.Windows.Controls.Border;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBlock  = System.Windows.Controls.TextBlock;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfColDef     = System.Windows.Controls.ColumnDefinition;
using WpfColChanged = System.Windows.Controls.TextChangedEventArgs;
using Cursors       = System.Windows.Input.Cursors;


namespace RemoteX
{
    public partial class MainWindow
    {
        // ── Fields ───────────────────────────────────────────────────────────

        private List<QuickCommand> _quickCommands = new();
        private string _commandFilter = "";

        // ── Load ─────────────────────────────────────────────────────────────

        private async Task LoadQuickCommandsAsync()
        {
            _quickCommands = await _serverRepository.LoadQuickCommandsAsync();
            RefreshCommandsPanel();
        }

        // ── Refresh panel ────────────────────────────────────────────────────

        private void RefreshCommandsPanel()
        {
            CommandsList.Children.Clear();

            var filter = _commandFilter.Trim();
            IEnumerable<QuickCommand> filtered = _quickCommands;

            if (!string.IsNullOrEmpty(filter))
            {
                var cmp = StringComparison.OrdinalIgnoreCase;
                filtered = _quickCommands.Where(c =>
                    c.Name.IndexOf(filter, cmp) >= 0 ||
                    c.Command.IndexOf(filter, cmp) >= 0 ||
                    c.Description.IndexOf(filter, cmp) >= 0);
            }

            var groups = filtered
                .GroupBy(c => c.Group)
                .OrderBy(g => g.Key)
                .ToList();

            if (!groups.Any())
            {
                var hint = new WpfTextBlock
                {
                    Text       = string.IsNullOrEmpty(filter) ? "暂无命令，点击 + 添加" : "无匹配命令",
                    FontSize   = 12,
                    Foreground = ColSubtext,
                    HorizontalAlignment = WpfHA.Center,
                    Margin     = new Thickness(0, 20, 0, 0)
                };
                CommandsList.Children.Add(hint);
                return;
            }

            foreach (var group in groups)
            {
                var groupHeader = new WpfTextBlock
                {
                    Text       = group.Key,
                    FontSize   = 10,
                    FontWeight = WpfFW.SemiBold,
                    Foreground = ColOverlay0,
                    Margin     = new Thickness(4, 10, 0, 4)
                };
                CommandsList.Children.Add(groupHeader);

                foreach (var cmd in group.OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
                    CommandsList.Children.Add(BuildCommandCard(cmd));
            }
        }

        private UIElement BuildCommandCard(QuickCommand qc)
        {
            var nameText = new WpfTextBlock
            {
                Text         = qc.Name,
                FontSize     = 12,
                FontWeight   = WpfFW.SemiBold,
                Foreground   = ColText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin       = new Thickness(0, 0, 0, 2)
            };

            var cmdPreview = qc.Command.Length > 60 ? qc.Command.Substring(0, 57) + "..." : qc.Command;
            var cmdText = new WpfTextBlock
            {
                Text         = cmdPreview,
                FontSize     = 11,
                Foreground   = ColSubtext,
                FontFamily   = new WpfFontFamily("Consolas,Courier New,monospace"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var infoPanel = new WpfStackPanel();
            infoPanel.Children.Add(nameText);
            infoPanel.Children.Add(cmdText);

            if (!string.IsNullOrWhiteSpace(qc.Description))
            {
                infoPanel.Children.Add(new WpfTextBlock
                {
                    Text         = qc.Description,
                    FontSize     = 10,
                    Foreground   = ColOverlay0,
                    Margin       = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var runBtn = new WpfButton
            {
                Content         = "▶",
                Padding         = new Thickness(8, 4, 8, 4),
                FontSize        = 13,
                Background      = new WpfBrush(WpfColor.FromRgb(0x1E, 0x3A, 0x2F)),
                Foreground      = ColGreen,
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                ToolTip         = "执行命令"
            };
            var capturedQc = qc;
            runBtn.Click += (_, __) => RunQuickCommand(capturedQc);

            var editBtn = new WpfButton
            {
                Content         = "✎",
                Padding         = new Thickness(6, 4, 6, 4),
                FontSize        = 12,
                Background      = WpfBrushes.Transparent,
                Foreground      = ColSubtext,
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(4, 0, 0, 0),
                ToolTip         = "编辑"
            };
            editBtn.Click += async (_, __) => await EditQuickCommandAsync(capturedQc);

            var deleteBtn = new WpfButton
            {
                Content         = "✕",
                Padding         = new Thickness(6, 4, 6, 4),
                FontSize        = 11,
                Background      = WpfBrushes.Transparent,
                Foreground      = new WpfBrush(WpfColor.FromRgb(0xF3, 0x8B, 0xA8)),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(2, 0, 0, 0),
                ToolTip         = "删除"
            };
            deleteBtn.Click += async (_, __) => await DeleteQuickCommandAsync(capturedQc);

            var btnPanel = new WpfStackPanel
            {
                Orientation       = WpfOrientation.Horizontal,
                VerticalAlignment = WpfVA.Center
            };
            btnPanel.Children.Add(runBtn);
            btnPanel.Children.Add(editBtn);
            btnPanel.Children.Add(deleteBtn);

            var row = new WpfGrid { VerticalAlignment = WpfVA.Center };
            row.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new WpfColDef { Width = GridLength.Auto });
            System.Windows.Controls.Grid.SetColumn(infoPanel, 0);
            System.Windows.Controls.Grid.SetColumn(btnPanel,  1);
            row.Children.Add(infoPanel);
            row.Children.Add(btnPanel);

            var card = new WpfBorder
            {
                Background      = new WpfBrush(WpfColor.FromRgb(0x18, 0x18, 0x25)),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 2, 0, 2),
                BorderBrush     = new WpfBrush(WpfColor.FromRgb(0x31, 0x32, 0x44)),
                BorderThickness = new Thickness(1),
                Child           = row
            };

            return card;
        }

        // ── Add ──────────────────────────────────────────────────────────────

        private async void AddQuickCommand_Click(object sender, RoutedEventArgs e)
        {
            var groups = _quickCommands.Select(c => c.Group).Distinct().OrderBy(g => g).ToList();
            var win = new QuickCommandEditWindow(null, groups) { Owner = this };
            if (win.ShowDialog() != true || win.Result == null) return;

            await _serverRepository.SaveQuickCommandAsync(win.Result);
            _quickCommands.Add(win.Result);
            RefreshCommandsPanel();
        }

        // ── Edit ─────────────────────────────────────────────────────────────

        private async Task EditQuickCommandAsync(QuickCommand qc)
        {
            var groups = _quickCommands.Select(c => c.Group).Distinct().OrderBy(g => g).ToList();
            var win = new QuickCommandEditWindow(qc, groups) { Owner = this };
            if (win.ShowDialog() != true || win.Result == null) return;

            qc.Name        = win.Result.Name;
            qc.Group       = win.Result.Group;
            qc.Command     = win.Result.Command;
            qc.Description = win.Result.Description;
            qc.SortOrder   = win.Result.SortOrder;

            await _serverRepository.SaveQuickCommandAsync(qc);
            RefreshCommandsPanel();
        }

        // ── Delete ───────────────────────────────────────────────────────────

        private async Task DeleteQuickCommandAsync(QuickCommand qc)
        {
            var result = AppMsg.Show(this, $"确认删除命令「{qc.Name}」？",
                "删除确认", AppMsgIcon.Warning, AppMsgButton.YesNo);
            if (result != AppMsgResult.Yes) return;

            await _serverRepository.DeleteQuickCommandAsync(qc.Id);
            _quickCommands.Remove(qc);
            RefreshCommandsPanel();
        }

        // ── Run ──────────────────────────────────────────────────────────────

        private void RunQuickCommand(QuickCommand qc)
        {
            var session = GetCurrentTabSession();
            if (session is not TerminalTabSession termSession)
            {
                ShowToast("请先连接一个 SSH/Telnet 终端");
                return;
            }

            var resolved = ResolveVariables(qc.Command, termSession.Server);

            // If there are remaining unresolved {variable} patterns, prompt for each
            var remaining = Regex.Matches(resolved, @"\{(\w+)\}");
            var seen = new HashSet<string>();
            foreach (Match m in remaining.Cast<Match>())
            {
                var varName = m.Groups[1].Value;
                if (!seen.Add(varName)) continue;
                var value = PromptCommandVariable(varName);
                if (value == null) return; // cancelled
                resolved = resolved.Replace($"{{{varName}}}", value);
            }

            termSession.Service.SendInput(resolved + "\n");
        }

        private static string ResolveVariables(string command, ServerInfo server)
        {
            return command
                .Replace("{host}", server.IP)
                .Replace("{port}", server.Port.ToString())
                .Replace("{user}", server.Username)
                .Replace("{name}", server.Name);
        }

        private string? PromptCommandVariable(string varName)
        {
            var win = new Window
            {
                Title                 = "输入变量值",
                Width                 = 340,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
                Background            = new WpfBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E))
            };

            var sp = new WpfStackPanel { Margin = new Thickness(20, 16, 20, 20) };
            sp.Children.Add(new WpfTextBlock
            {
                Text       = $"请输入变量 {{{varName}}} 的值：",
                Foreground = new WpfBrush(WpfColor.FromRgb(0xCD, 0xD6, 0xF4)),
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 0, 8)
            });

            var tb = new WpfTextBox
            {
                Background             = new WpfBrush(WpfColor.FromRgb(0x31, 0x32, 0x44)),
                Foreground             = new WpfBrush(WpfColor.FromRgb(0xCD, 0xD6, 0xF4)),
                BorderBrush            = new WpfBrush(WpfColor.FromRgb(0x45, 0x47, 0x5A)),
                BorderThickness        = new Thickness(1),
                CaretBrush             = new WpfBrush(WpfColor.FromRgb(0x89, 0xB4, 0xFA)),
                FontSize               = 13,
                Height                 = 34,
                Padding                = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = WpfVA.Center,
                Margin                 = new Thickness(0, 0, 0, 14)
            };

            var btnPanel = new WpfStackPanel
            {
                Orientation         = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHA.Right
            };
            var btnOk     = new WpfButton { Content = "确定", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new WpfButton { Content = "取消", Width = 80, Height = 30, IsCancel  = true };

            string? result = null;
            btnOk.Click     += (_, __) => { result = tb.Text; win.DialogResult = true; };
            btnCancel.Click += (_, __) => { win.DialogResult = false; };

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            sp.Children.Add(tb);
            sp.Children.Add(btnPanel);
            win.Content = sp;
            win.Loaded += (_, __) => tb.Focus();

            return win.ShowDialog() == true ? result : null;
        }

        // ── Search ───────────────────────────────────────────────────────────

        private void CommandSearch_Changed(object sender, WpfColChanged e)
        {
            _commandFilter = CommandSearchBox.Text;
            RefreshCommandsPanel();
        }
    }
}
