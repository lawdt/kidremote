using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

/// <summary>
/// Всплывающая карточка в углу экрана: ответ родителя должен быть виден прямо в игре,
/// а не только на экране блокировки.
/// </summary>
internal sealed class ToastWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(25);

    private readonly DispatcherTimer _hideTimer;

    public event Action? Clicked;

    public ToastWindow(string author, string text)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        ShowActivated = false;

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = $"✉ {author}",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xC5, 0xFF)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFF)),
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Нажмите, чтобы ответить",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x8D, 0xB5)),
            Margin = new Thickness(0, 10, 0, 0)
        });

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x1C, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x3C, 0x66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14, 18, 14),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = stack
        };

        border.MouseLeftButtonUp += (_, _) => Clicked?.Invoke();

        Content = border;

        _hideTimer = new DispatcherTimer { Interval = Lifetime };
        _hideTimer.Tick += (_, _) => CloseQuietly();

        Loaded += (_, _) =>
        {
            PlaceInCorner();
            _hideTimer.Start();
        };
    }

    private void PlaceInCorner()
    {
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        const int margin = 24;

        Left = (area.Right - margin) / scale.DpiScaleX - Width;
        Top = (area.Bottom - margin) / scale.DpiScaleY - ActualHeight;
    }

    public void CloseQuietly()
    {
        _hideTimer.Stop();
        Close();
    }
}
