using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;

using Application = System.Windows.Application;


namespace RemoteX
{
    public partial class App : Application
    {
        private NotifyIcon?   _trayIcon;
        private MainWindow?   _mainWindow;
        private AppSettings?  _appSettings;

        internal AppSettings? AppSettings => _appSettings;
        private bool          _isExiting;

        /// <summary>资源解压任务，终端初始化前需 await。</summary>
        internal static Task ExtractionTask { get; private set; } = Task.CompletedTask;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppPaths.EnsureDataRoot();
            AppLogger.Initialize();
            AppLogger.Info("application startup");
            ExtractionTask = Task.Run(() => AssetExtractor.ExtractAll());

            _appSettings = AppSettings.Load();

            DispatcherUnhandledException += (_, args) =>
            {
                AppLogger.Error("dispatcher unhandled exception", args.Exception);
                AppMsg.Show(_mainWindow,
                    $"发生未处理的异常：\n{args.Exception.Message}",
                    "错误", AppMsgIcon.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    AppLogger.Error("appdomain unhandled exception", ex);
                else
                    AppLogger.Error("appdomain unhandled non-exception object");
            };

            // 托盘图标
            InitTrayIcon();

            // 主窗口
            _mainWindow = new MainWindow(_appSettings);
            _mainWindow.Closing += MainWindow_Closing;
            _mainWindow.Show();
        }

        private void InitTrayIcon()
        {
            System.Drawing.Icon? icon = null;
            try
            {
                var icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(icoPath))
                    icon = new System.Drawing.Icon(icoPath);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"load tray icon: {ex.Message}");
            }
            if (icon == null)
                icon = System.Drawing.SystemIcons.Application;

            var menu = new ContextMenuStrip();
            var showItem  = new ToolStripMenuItem("显示主窗口");
            var resetItem = new ToolStripMenuItem("重置关闭行为");
            var exitItem  = new ToolStripMenuItem("退出程序");

            showItem.Click  += (_, __) => ShowMainWindow();
            resetItem.Click += (_, __) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
                {
                    try
                    {
                        if (_appSettings != null && !_isExiting)
                        {
                            _appSettings.CloseAction = null;
                            _appSettings.Save();
                            _trayIcon?.ShowBalloonTip(2000, "RemoteX", "已重置关闭行为，下次关闭主窗口时将再次询问。", ToolTipIcon.Info);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"reset close action: {ex.Message}");
                    }
                }));
            };
            exitItem.Click  += (_, __) => ExitApp();

            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(resetItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon
            {
                Icon             = icon,
                Text             = "RemoteX",
                ContextMenuStrip = menu,
                Visible          = true
            };
            _trayIcon.DoubleClick += (_, __) => ShowMainWindow();
        }

        private void MainWindow_Closing(object? sender,
            System.ComponentModel.CancelEventArgs e)
        {
            var action = _appSettings?.CloseAction;

            if (action == "exit")
                return;

            if (action == "tray")
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }

            e.Cancel = true;
            ShowCloseDialog();
        }

        private void ShowCloseDialog()
        {
            if (_mainWindow == null || _isExiting) return;
            try
            {
                var dlg = new CloseActionDialog { Owner = _mainWindow };
                if (dlg.ShowDialog() != true) return;

                if (dlg.RememberChoice && _appSettings != null)
                {
                    _appSettings.CloseAction = dlg.MinimizeToTray ? "tray" : "exit";
                    _appSettings.Save();
                }

                if (dlg.MinimizeToTray)
                    MinimizeToTray();
                else
                    ExitApp();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("关闭") || ex.Message.Contains("closed") || ex.Message.Contains("Visibility"))
            {
                AppLogger.Warn($"show close dialog: {ex.Message}");
            }
        }

        private void MinimizeToTray()
        {
            _mainWindow?.Hide();
            _trayIcon?.ShowBalloonTip(
                1500, "RemoteX",
                "已最小化到系统托盘，双击图标可以再次显示",
                ToolTipIcon.Info);
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null || _isExiting) return;
            try
            {
                Current.Dispatcher.Invoke(() =>
                {
                    if (_isExiting || _mainWindow == null) return;
                    try
                    {
                        _mainWindow.Show();
                        _mainWindow.WindowState = System.Windows.WindowState.Normal;
                        _mainWindow.Activate();
                    }
                    catch (InvalidOperationException) { /* 窗口已关闭时忽略 */ }
                }, DispatcherPriority.Normal);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("关闭") || ex.Message.Contains("closed") || ex.Message.Contains("Visibility") || ex.Message.Contains("Show"))
            {
                AppLogger.Warn($"show main window: {ex.Message}");
            }
        }

        private void ExitApp()
        {
            _isExiting = true;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_mainWindow != null)
            {
                _mainWindow.Closing -= MainWindow_Closing;
                _mainWindow = null;
            }
            AppLogger.Info("application exit via tray");
            AppLogger.Dispose();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _trayIcon?.Dispose();
            AppLogger.Dispose();
            base.OnExit(e);
        }
    }
}
