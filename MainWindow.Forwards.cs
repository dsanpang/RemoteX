using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shapes;

using Renci.SshNet;

using WpfColor    = System.Windows.Media.Color;
using WpfBrush    = System.Windows.Media.SolidColorBrush;
using WpfBrushes  = System.Windows.Media.Brushes;
using WpfFW       = System.Windows.FontWeights;
using WpfHA       = System.Windows.HorizontalAlignment;
using WpfVA       = System.Windows.VerticalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfButton   = System.Windows.Controls.Button;
using WpfBorder   = System.Windows.Controls.Border;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfGrid     = System.Windows.Controls.Grid;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;
using WpfColDef   = System.Windows.Controls.ColumnDefinition;
using Cursors     = System.Windows.Input.Cursors;


namespace RemoteX
{
    public partial class MainWindow
    {
        // ── Fields ───────────────────────────────────────────────────────────

        private List<PortForwardRule> _portForwardRules = new();

        // key = rule Id, value = (SshClient, ForwardedPort)
        private readonly Dictionary<int, (SshClient Client, ForwardedPort Port)> _activeForwards = new();

        // ── Load ─────────────────────────────────────────────────────────────

        private async Task LoadPortForwardsAsync()
        {
            _portForwardRules = await _serverRepository.LoadPortForwardsAsync();
            RefreshForwardsPanel();
        }

        // ── Refresh panel ────────────────────────────────────────────────────

        private void RefreshForwardsPanel()
        {
            ForwardRulesList.Children.Clear();

            if (_portForwardRules.Count == 0)
            {
                var hint = new WpfTextBlock
                {
                    Text       = "暂无转发规则，点击 + 添加",
                    FontSize   = 12,
                    Foreground = ColSubtext,
                    HorizontalAlignment = WpfHA.Center,
                    Margin     = new Thickness(0, 20, 0, 0)
                };
                ForwardRulesList.Children.Add(hint);
            }
            else
            {
                foreach (var rule in _portForwardRules)
                    ForwardRulesList.Children.Add(BuildForwardRuleCard(rule));
            }

            RefreshProxyStatus();
        }

        private UIElement BuildForwardRuleCard(PortForwardRule rule)
        {
            var isActive = _activeForwards.ContainsKey(rule.Id);
            rule.IsActive = isActive;

            var badgeBg = rule.ForwardType switch
            {
                PortForwardType.Dynamic => WpfColor.FromRgb(0x1A, 0x32, 0x54),
                PortForwardType.Remote  => WpfColor.FromRgb(0x2D, 0x2B, 0x1E),
                _                       => WpfColor.FromRgb(0x1E, 0x3A, 0x2F)
            };
            var badgeFg = rule.ForwardType switch
            {
                PortForwardType.Dynamic => WpfColor.FromRgb(0x89, 0xB4, 0xFA),
                PortForwardType.Remote  => WpfColor.FromRgb(0xF9, 0xE2, 0xAF),
                _                       => WpfColor.FromRgb(0xA6, 0xE3, 0xA1)
            };

            var badge = new WpfBorder
            {
                Background   = new WpfBrush(badgeBg),
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(5, 1, 5, 1),
                Margin       = new Thickness(0, 0, 6, 0),
                VerticalAlignment = WpfVA.Center,
                Child = new WpfTextBlock
                {
                    Text       = rule.TypeBadge,
                    FontSize   = 9,
                    FontWeight = WpfFW.SemiBold,
                    Foreground = new WpfBrush(badgeFg)
                }
            };

            var dotColor = isActive ? WpfColor.FromRgb(0xA6, 0xE3, 0xA1) : WpfColor.FromRgb(0x6C, 0x70, 0x86);
            var dot = new Ellipse
            {
                Width  = 7,
                Height = 7,
                Fill   = new WpfBrush(dotColor),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = WpfVA.Center
            };

            var header = new WpfStackPanel { Orientation = WpfOrientation.Horizontal };
            header.Children.Add(dot);
            header.Children.Add(badge);
            header.Children.Add(new WpfTextBlock
            {
                Text         = rule.Name,
                FontSize     = 12,
                FontWeight   = WpfFW.SemiBold,
                Foreground   = ColText,
                VerticalAlignment = WpfVA.Center,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
            });

            var portSummary = new WpfTextBlock
            {
                Text      = rule.PortSummary,
                FontSize  = 11,
                Foreground = ColSubtext,
                Margin    = new Thickness(0, 2, 0, 0)
            };

            var serverLabel = new WpfTextBlock
            {
                Text      = string.IsNullOrWhiteSpace(rule.ServerName) ? $"服务器 #{rule.ServerId}" : rule.ServerName,
                FontSize  = 10,
                Foreground = ColOverlay0,
                Margin    = new Thickness(0, 1, 0, 0)
            };

            var info = new WpfStackPanel();
            info.Children.Add(header);
            info.Children.Add(portSummary);
            info.Children.Add(serverLabel);

            var toggleBtn = new WpfButton
            {
                Content = isActive ? "停止" : "启动",
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11,
                Background = isActive
                    ? new WpfBrush(WpfColor.FromRgb(0x2D, 0x1E, 0x1E))
                    : new WpfBrush(WpfColor.FromRgb(0x1E, 0x3A, 0x2F)),
                Foreground = isActive
                    ? new WpfBrush(WpfColor.FromRgb(0xF3, 0x8B, 0xA8))
                    : ColGreen,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            var capturedRule = rule;
            toggleBtn.Click += async (_, __) => await TogglePortForwardAsync(capturedRule);

            var editBtn = new WpfButton
            {
                Content         = "✎",
                Padding         = new Thickness(6, 4, 6, 4),
                FontSize        = 12,
                Background      = WpfBrushes.Transparent,
                Foreground      = ColSubtext,
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(4, 0, 0, 0)
            };
            editBtn.Click += async (_, __) => await EditPortForwardAsync(capturedRule);

            var deleteBtn = new WpfButton
            {
                Content         = "✕",
                Padding         = new Thickness(6, 4, 6, 4),
                FontSize        = 11,
                Background      = WpfBrushes.Transparent,
                Foreground      = new WpfBrush(WpfColor.FromRgb(0xF3, 0x8B, 0xA8)),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(2, 0, 0, 0)
            };
            deleteBtn.Click += async (_, __) => await DeletePortForwardAsync(capturedRule);

            var btnPanel = new WpfStackPanel
            {
                Orientation       = WpfOrientation.Horizontal,
                VerticalAlignment = WpfVA.Center
            };
            btnPanel.Children.Add(toggleBtn);
            btnPanel.Children.Add(editBtn);
            btnPanel.Children.Add(deleteBtn);

            var row = new WpfGrid { VerticalAlignment = WpfVA.Center };
            row.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new WpfColDef { Width = GridLength.Auto });
            System.Windows.Controls.Grid.SetColumn(info,     0);
            System.Windows.Controls.Grid.SetColumn(btnPanel, 1);
            row.Children.Add(info);
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

        private async void AddPortForward_Click(object sender, RoutedEventArgs e)
        {
            var sshServers = _servers.FindAll(s => s.Protocol == ServerProtocol.SSH);
            if (sshServers.Count == 0)
            {
                ShowToast("请先添加 SSH 服务器");
                return;
            }

            var win = new PortForwardEditWindow(sshServers) { Owner = this };
            if (win.ShowDialog() != true || win.Result == null) return;

            await _serverRepository.SavePortForwardAsync(win.Result);
            _portForwardRules.Add(win.Result);
            RefreshForwardsPanel();

            if (win.Result.AutoStart)
                await StartPortForwardAsync(win.Result);
        }

        // ── Edit ─────────────────────────────────────────────────────────────

        private async Task EditPortForwardAsync(PortForwardRule rule)
        {
            var sshServers = _servers.FindAll(s => s.Protocol == ServerProtocol.SSH);
            var win = new PortForwardEditWindow(sshServers, rule) { Owner = this };
            if (win.ShowDialog() != true || win.Result == null) return;

            rule.Name        = win.Result.Name;
            rule.ServerId    = win.Result.ServerId;
            rule.ServerName  = win.Result.ServerName;
            rule.LocalPort   = win.Result.LocalPort;
            rule.RemoteHost  = win.Result.RemoteHost;
            rule.RemotePort  = win.Result.RemotePort;
            rule.ForwardType = win.Result.ForwardType;
            rule.AutoStart   = win.Result.AutoStart;

            await _serverRepository.SavePortForwardAsync(rule);
            RefreshForwardsPanel();
        }

        // ── Delete ───────────────────────────────────────────────────────────

        private async Task DeletePortForwardAsync(PortForwardRule rule)
        {
            var result = AppMsg.Show(this, $"确认删除转发规则「{rule.Name}」？",
                "删除确认", AppMsgIcon.Warning, AppMsgButton.YesNo);
            if (result != AppMsgResult.Yes) return;

            if (_activeForwards.ContainsKey(rule.Id))
                StopPortForward(rule.Id);

            await _serverRepository.DeletePortForwardAsync(rule.Id);
            _portForwardRules.Remove(rule);
            RefreshForwardsPanel();
        }

        // ── Toggle ───────────────────────────────────────────────────────────

        private async Task TogglePortForwardAsync(PortForwardRule rule)
        {
            if (_activeForwards.ContainsKey(rule.Id))
            {
                StopPortForward(rule.Id);
                RefreshForwardsPanel();
            }
            else
            {
                await StartPortForwardAsync(rule);
            }
        }

        // ── Start ────────────────────────────────────────────────────────────

        private async Task StartPortForwardAsync(PortForwardRule rule)
        {
            var server = _servers.Find(s => s.Id == rule.ServerId);
            if (server == null)
            {
                ShowToast($"未找到服务器: {rule.ServerName}");
                return;
            }

            try
            {
                SshClient?    sshClient = null;
                ForwardedPort? fwdPort  = null;

                await Task.Run(() =>
                {
                    var auth     = BuildSshAuthForForward(server);
                    var connInfo = new ConnectionInfo(server.IP, server.Port, server.Username, auth)
                    {
                        Timeout = TimeSpan.FromSeconds(15)
                    };

                    sshClient = new SshClient(connInfo);
                    sshClient.Connect();

                    if (rule.ForwardType == PortForwardType.Dynamic)
                    {
                        fwdPort = new ForwardedPortDynamic((uint)rule.LocalPort);
                    }
                    else if (rule.ForwardType == PortForwardType.Remote)
                    {
                        fwdPort = new ForwardedPortRemote(
                            rule.RemoteHost, (uint)rule.RemotePort,
                            "127.0.0.1", (uint)rule.LocalPort);
                    }
                    else
                    {
                        fwdPort = new ForwardedPortLocal(
                            "127.0.0.1", (uint)rule.LocalPort,
                            rule.RemoteHost, (uint)rule.RemotePort);
                    }

                    sshClient.AddForwardedPort(fwdPort);
                    fwdPort.Start();
                });

                _activeForwards[rule.Id] = (sshClient!, fwdPort!);
                rule.IsActive = true;
                ShowToast($"转发已启动: {rule.Name}  {rule.PortSummary}");
                RefreshForwardsPanel();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"port forward start failed: {rule.Name}", ex);
                AppMsg.Show(this, $"启动转发失败：\n{ex.Message}", "错误", AppMsgIcon.Error);
            }
        }

        private static AuthenticationMethod BuildSshAuthForForward(ServerInfo server)
        {
            if (!string.IsNullOrWhiteSpace(server.SshPrivateKeyPath))
            {
                var pp = string.IsNullOrWhiteSpace(server.Password) ? null : server.Password;
                var kf = pp == null
                    ? new PrivateKeyFile(server.SshPrivateKeyPath)
                    : new PrivateKeyFile(server.SshPrivateKeyPath, pp);
                return new PrivateKeyAuthenticationMethod(server.Username, kf);
            }
            return new PasswordAuthenticationMethod(server.Username, server.Password ?? "");
        }

        // ── Stop ─────────────────────────────────────────────────────────────

        private void StopPortForward(int ruleId)
        {
            if (!_activeForwards.TryGetValue(ruleId, out var entry)) return;
            _activeForwards.Remove(ruleId);

            try { entry.Port.Stop(); }         catch { }
            try { entry.Port.Dispose(); }      catch { }
            try { entry.Client.Disconnect(); } catch { }
            try { entry.Client.Dispose(); }    catch { }

            var rule = _portForwardRules.Find(r => r.Id == ruleId);
            if (rule != null) rule.IsActive = false;
        }

        // ── Manage proxies ───────────────────────────────────────────────────

        private void ManageProxies_Click(object sender, RoutedEventArgs e)
        {
            var win = new ProxyManagerWindow(_appSettings) { Owner = this };
            win.ShowDialog();
            RefreshProxyStatus();
        }

        // ── Proxy status ─────────────────────────────────────────────────────

        private void RefreshProxyStatus()
        {
            ProxyStatusList.Children.Clear();

            var proxies = _appSettings.SocksProxies;
            if (proxies == null || proxies.Count == 0)
            {
                var hint = new WpfTextBlock
                {
                    Text       = "未配置代理服务器",
                    FontSize   = 11,
                    Foreground = ColOverlay0,
                    HorizontalAlignment = WpfHA.Center,
                    Margin     = new Thickness(0, 6, 0, 6)
                };
                ProxyStatusList.Children.Add(hint);
                return;
            }

            foreach (var proxy in proxies)
            {
                var nameText = new WpfTextBlock
                {
                    Text         = proxy.Name,
                    FontSize     = 11,
                    FontWeight   = WpfFW.SemiBold,
                    Foreground   = ColText,
                    VerticalAlignment = WpfVA.Center,
                    TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
                };
                var addrText = new WpfTextBlock
                {
                    Text      = $"{proxy.Host}:{proxy.Port}",
                    FontSize  = 10,
                    Foreground = ColOverlay0,
                    VerticalAlignment = WpfVA.Center
                };

                var proxyInfo = new WpfStackPanel();
                proxyInfo.Children.Add(nameText);
                proxyInfo.Children.Add(addrText);

                var dot = new Ellipse
                {
                    Width  = 7,
                    Height = 7,
                    Fill   = new WpfBrush(WpfColor.FromRgb(0x6C, 0x70, 0x86)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = WpfVA.Center
                };

                var row = new WpfGrid();
                row.ColumnDefinitions.Add(new WpfColDef { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new WpfColDef { Width = new GridLength(1, GridUnitType.Star) });
                System.Windows.Controls.Grid.SetColumn(dot,       0);
                System.Windows.Controls.Grid.SetColumn(proxyInfo, 1);
                row.Children.Add(dot);
                row.Children.Add(proxyInfo);

                var card = new WpfBorder
                {
                    Background      = WpfBrushes.Transparent,
                    CornerRadius    = new CornerRadius(4),
                    Padding         = new Thickness(8, 5, 8, 5),
                    Margin          = new Thickness(0, 1, 0, 1),
                    Child           = row
                };

                ProxyStatusList.Children.Add(card);
            }
        }
    }
}
