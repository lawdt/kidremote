using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KidRemote.Core;
using KidRemote.Interop;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

public partial class CountdownWindow : Window
{
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x17, 0x44));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly Brush UnlimitedBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xC4, 0xFF));

    private readonly AppConfig _config;

    internal CountdownWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

        NormalBrush.Freeze();
        WarnBrush.Freeze();
        DangerBrush.Freeze();
        IdleBrush.Freeze();
        UnlimitedBrush.Freeze();

        Loaded += OnLoaded;
        // Ширина плашки меняется вместе с текстом — держим её приклеенной к углу.
        SizeChanged += (_, _) => MoveToCorner();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Плашка не должна перехватывать клики, светиться в Alt+Tab и красть фокус у игры.
        var style = NativeMethods.GetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        MoveToCorner();
    }

    public void MoveToCorner()
    {
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        var width = ActualWidth * scale.DpiScaleX;
        var height = ActualHeight * scale.DpiScaleY;
        const int margin = 24;

        var (left, top) = _config.OverlayCorner.ToLowerInvariant() switch
        {
            "topleft" => (area.Left + margin, area.Top + margin),
            "bottomleft" => (area.Left + margin, area.Bottom - (int)height - margin),
            "bottomright" => (area.Right - (int)width - margin, area.Bottom - (int)height - margin),
            _ => (area.Right - (int)width - margin, area.Top + margin)
        };

        Left = left / scale.DpiScaleX;
        Top = top / scale.DpiScaleY;
    }

    /// <summary>Рисует остаток. Мигание запускается только когда значение реально изменилось.</summary>
    internal void Render(BankState state, long remainingSeconds, bool consuming, bool valueChanged)
    {
        if (state == BankState.Unlimited)
        {
            TimeText.Text = "БЕЗЛИМИТ";
            TimeText.FontSize = 24;
            TimeText.Foreground = UnlimitedBrush;
            Root.Opacity = 1;
            return;
        }

        TimeText.FontSize = 34;
        TimeText.Text = TimeFormat.Compact(remainingSeconds);

        if (!consuming)
        {
            TimeText.Foreground = IdleBrush;
            Root.Opacity = 0.75;
            return;
        }

        Root.Opacity = 1;

        if (remainingSeconds <= _config.DangerSeconds)
        {
            TimeText.Foreground = DangerBrush;
            if (valueChanged && remainingSeconds % 5 == 0) Blink(160);
        }
        else if (remainingSeconds <= _config.WarnSeconds)
        {
            TimeText.Foreground = WarnBrush;
            if (valueChanged && remainingSeconds % 60 == 0) Blink(320);
        }
        else
        {
            TimeText.Foreground = NormalBrush;
        }
    }

    private void Blink(int halfPeriodMs)
    {
        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.15,
            Duration = TimeSpan.FromMilliseconds(halfPeriodMs),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            // Без этого анимация "залипает" и дальнейшие присвоения Opacity перестают работать.
            FillBehavior = FillBehavior.Stop
        };

        Root.BeginAnimation(OpacityProperty, animation);
    }
}
