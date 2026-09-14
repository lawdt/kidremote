using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KidRemote.Interop;

namespace KidRemote.Ui;

/// <summary>
/// Пульсирующая красная рамка по периметру экрана на последней минуте.
/// Окно сквозное для мыши и не забирает фокус, поэтому игре не мешает.
/// </summary>
internal sealed class AlertFrameWindow : Window
{
    private const double BorderWidth = 10;
    private const double RestingOpacity = 0.9;

    private readonly Border _frame;
    private bool _active;

    public AlertFrameWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        ShowActivated = false;

        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44));
        brush.Freeze();

        _frame = new Border
        {
            BorderBrush = brush,
            BorderThickness = new Thickness(BorderWidth),
            Background = Brushes.Transparent
        };

        Content = _frame;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        StretchToPrimaryScreen();
    }

    /// <summary>
    /// Размеры берём в аппаратно-независимых пикселях: у ещё не показанного окна
    /// запрашивать масштаб DPI нельзя.
    /// </summary>
    private void StretchToPrimaryScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    public void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;

        if (active)
        {
            StretchToPrimaryScreen();
            _frame.Opacity = RestingOpacity;
            Show();
        }
        else
        {
            _frame.BeginAnimation(OpacityProperty, null);
            Hide();
        }
    }

    /// <summary>Вспышка рамки. Вызывается вместе со звуковым сигналом, чтобы они шли в такт.</summary>
    public void Flash()
    {
        if (!_active) return;

        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.05,
            Duration = TimeSpan.FromMilliseconds(110),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.Stop
        };

        _frame.BeginAnimation(OpacityProperty, animation);
    }
}
