using System.Windows;
using System.Windows.Input;

namespace RemoteX;

public enum AppMsgIcon  { Info, Warning, Error, Question, Success }
public enum AppMsgButton { OK, OKCancel, YesNo, YesNoCancel }
public enum AppMsgResult { OK, Cancel, Yes, No }

public partial class AppMsg : Window
{
    private AppMsgResult _result = AppMsgResult.Cancel;

    // ── Static factory ──────────────────────────────────────────────────────

    /// <summary>Show a simple message (OK button only).</summary>
    public static AppMsgResult Show(
        Window? owner,
        string message,
        string title = "提示",
        AppMsgIcon icon = AppMsgIcon.Info)
        => ShowCore(owner, message, title, icon, AppMsgButton.OK);

    /// <summary>Show a confirmation dialog (Yes/No or OK/Cancel).</summary>
    public static AppMsgResult Show(
        Window? owner,
        string message,
        string title,
        AppMsgIcon icon,
        AppMsgButton buttons)
        => ShowCore(owner, message, title, icon, buttons);

    /// <summary>Convenience: returns true when user clicked OK or Yes.</summary>
    public static bool Confirm(
        Window? owner,
        string message,
        string title = "确认",
        AppMsgIcon icon = AppMsgIcon.Question)
    {
        var r = ShowCore(owner, message, title, icon, AppMsgButton.YesNo);
        return r == AppMsgResult.Yes || r == AppMsgResult.OK;
    }

    private static AppMsgResult ShowCore(Window? owner, string message, string title,
        AppMsgIcon icon, AppMsgButton buttons)
    {
        var dlg = new AppMsg();
        dlg.TitleText.Text   = title;
        dlg.MessageText.Text = message;

        // Icon glyph + colour
        (string glyph, string colour) = icon switch
        {
            AppMsgIcon.Warning  => ("⚠", "#F9E2AF"),
            AppMsgIcon.Error    => ("✕", "#F38BA8"),
            AppMsgIcon.Question => ("?", "#89B4FA"),
            AppMsgIcon.Success  => ("✓", "#A6E3A1"),
            _                   => ("ℹ", "#89B4FA"),
        };
        dlg.IconText.Text       = glyph;
        dlg.IconText.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colour));

        System.Windows.Media.SolidColorBrush ConfirmBrush(string hex) =>
            new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

        // Buttons
        switch (buttons)
        {
            case AppMsgButton.OK:
                dlg.CancelBtn.Visibility = Visibility.Collapsed;
                dlg.ConfirmText.Text     = "确定";
                dlg.ConfirmBtn.Tag       = ConfirmBrush("#89B4FA");
                break;

            case AppMsgButton.OKCancel:
                dlg.CancelBtn.Visibility = Visibility.Visible;
                dlg.CancelText.Text      = "取消";
                dlg.ConfirmText.Text     = "确定";
                dlg.ConfirmBtn.Tag       = ConfirmBrush("#89B4FA");
                break;

            case AppMsgButton.YesNo:
                dlg.CancelBtn.Visibility = Visibility.Visible;
                dlg.CancelText.Text      = "否";
                dlg.ConfirmText.Text     = "是";
                dlg.ConfirmBtn.Tag       = ConfirmBrush(icon == AppMsgIcon.Error ? "#F38BA8" : "#89B4FA");
                break;

            case AppMsgButton.YesNoCancel:
                dlg.CancelBtn.Visibility = Visibility.Visible;
                dlg.CancelText.Text      = "取消";
                dlg.ConfirmText.Text     = "是";
                dlg.ConfirmBtn.Tag       = ConfirmBrush("#89B4FA");
                break;
        }

        if (owner != null)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.ShowDialog();
        return dlg._result;
    }

    // ── Constructor ─────────────────────────────────────────────────────────

    public AppMsg()
    {
        InitializeComponent();
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _result = AppMsgResult.Cancel;
        Close();
    }

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        _result = AppMsgResult.OK;
        // Remap OK → Yes for YesNo dialogs (ConfirmText == "是")
        if (ConfirmText.Text == "是") _result = AppMsgResult.Yes;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // "否" maps to No; anything else is Cancel
        _result = CancelText.Text == "否" ? AppMsgResult.No : AppMsgResult.Cancel;
        Close();
    }
}
