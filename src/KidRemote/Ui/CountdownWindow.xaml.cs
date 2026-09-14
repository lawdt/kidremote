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

    private static readonly Brush CalmBackground = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
    private static readonly Brush DangerBackground = new SolidColorBrush(Color.FromArgb(0xD9, 0x5A, 0x00, 0x12));

    /// <summary>Насколько близко к плашке должен подойти курсор, чтобы она спряталась.</summary>
    private const int HoverMargin = 60;

    private readonly AppConfig _config;
    private bool _hidden;

    static CountdownWindow()
    {
        NormalBrush.Freeze();
        WarnBrush.Freeze();
        DangerBrush.Freeze();
        IdleBrush.Freeze();
        UnlimitedBrush.Freeze();
        CalmBackground.Freeze();
        DangerBackground.Freeze();
    }

    internal CountdownWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

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

    /// <summary>
    /// Прячет плашку, когда курсор подходит вплотную: окно сквозное для мыши,
    /// поэтому событий наведения у него нет и близость считаем сами.
    /// </summary>
    public void UpdateCursorProximity()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return;

        var cursor = Forms.Cursor.Position;
        var near =
            cursor.X >= rect.Left - HoverMargin && cursor.X <= rect.Right + HoverMargin &&
            cursor.Y >= rect.Top - HoverMargin && cursor.Y <= rect.Bottom + HoverMargin;

        if (near == _hidden) return;

        _hidden = near;
        Opacity = near ? 0 : 1;
    }

    /// <summary>Рисует остаток. Мигание запускается только когда значение реально изменилось.</summary>
    internal void Render(BankState state, long remainingSeconds, bool consuming, bool valueChanged)
    {
        if (state == BankState.Unlimited)
        {
            TimeText.Text = "БЕЗЛИМИТ";
            TimeText.FontSize = 24;
            TimeText.Foreground = UnlimitedBrush;
            Root.Background = CalmBackground;
            Root.Opacity = 1;
            return;
        }

        TimeText.FontSize = 34;
        TimeText.Text = TimeFormat.Compact(remainingSeconds);

        if (!consuming)
        {
            TimeText.Foreground = IdleBrush;
            Root.Background = CalmBackground;
            Root.Opacity = 0.75;
            return;
        }

        Root.Opacity = 1;

        if (remainingSeconds <= _config.DangerSeconds)
        {
            TimeText.Foreground = DangerBrush;
            Root.Background = DangerBackground;
            if (valueChanged && remainingSeconds % 5 == 0) Blink(110, 3);
        }
        else if (remainingSeconds <= _config.WarnSeconds)
        {
            TimeText.Foreground = WarnBrush;
            Root.Background = CalmBackground;
            if (valueChanged && remainingSeconds % 60 == 0) Blink(260, 2);
        }
        else
        {
            TimeText.Foreground = NormalBrush;
            Root.Background = CalmBackground;
        }
    }

    private void Blink(int halfPeriodMs, int times)
    {
        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(halfPeriodMs),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(times),
            // Без этого анимация "залипает" и дальнейшие присвоения Opacity перестают работать.
            FillBehavior = FillBehavior.Stop
        };

        Root.BeginAnimation(OpacityProperty, animation);
    }
}
