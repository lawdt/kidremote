using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KidRemote.Interop;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

public partial class LockOverlayWindow : Window
{
    private const double BugSpeed = 70;          // пикселей в секунду
    private const double BugTurnRate = 220;      // градусов в секунду

    private readonly DispatcherTimer _bugTimer;
    private readonly Random _random = new();

    private Point _bugTarget;
    private double _bugAngle;
    private double _bugPauseLeft;
    private bool _allowClose;

    /// <summary>Ребёнок нажал «Написать родителям».</summary>
    public event Action? MessageRequested;

    public LockOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        _bugTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _bugTimer.Tick += (_, _) => MoveBug();
    }

    /// <summary>Жук ползёт к случайной точке, иногда замирает и выбирает новую.</summary>
    private void MoveBug()
    {
        var width = Bugs.ActualWidth;
        var height = Bugs.ActualHeight;
        if (width < 100 || height < 100) return;

        const double step = 0.033;

        if (_bugPauseLeft > 0)
        {
            _bugPauseLeft -= step;
            return;
        }

        var x = Canvas.GetLeft(Bug);
        var y = Canvas.GetTop(Bug);
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            x = width / 2;
            y = height / 2;
            PickTarget(width, height);
        }

        var dx = _bugTarget.X - x;
        var dy = _bugTarget.Y - y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < 6)
        {
            PickTarget(width, height);
            // Жук иногда замирает — так он выглядит живым, а не заводным.
            if (_random.NextDouble() < 0.4) _bugPauseLeft = 0.5 + _random.NextDouble() * 2.5;
            return;
        }

        // Эмодзи нарисовано головой вверх, поэтому к углу направления добавляем прямой.
        var desired = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
        _bugAngle = TurnTowards(_bugAngle, desired, BugTurnRate * step);
        BugRotation.Angle = _bugAngle;

        var move = BugSpeed * step;
        Canvas.SetLeft(Bug, x + dx / distance * move);
        Canvas.SetTop(Bug, y + dy / distance * move);
    }

    private void PickTarget(double width, double height)
    {
        const double margin = 40;
        _bugTarget = new Point(
            margin + _random.NextDouble() * Math.Max(1, width - margin * 2),
            margin + _random.NextDouble() * Math.Max(1, height - margin * 2));
    }

    private static double TurnTowards(double current, double target, double maxStep)
    {
        var diff = (target - current + 540) % 360 - 180;
        if (Math.Abs(diff) <= maxStep) return target;

        return current + Math.Sign(diff) * maxStep;
    }

    /// <summary>Растягивает окно ровно по границам конкретного монитора.</summary>
    internal void BindToScreen(Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        Left = bounds.Left / scaleX;
        Top = bounds.Top / scaleY;
        Width = bounds.Width / scaleX;
        Height = bounds.Height / scaleY;
    }

    internal void Render(string phrase)
    {
        // Вместо констатации факта — фраза, ради которой стоит встать.
        Glyph.Text = "🔒";
        Headline.Text = phrase;
        Subtitle.Visibility = Visibility.Collapsed;

        FadeIn();
    }

    private void FadeIn()
    {
        Headline.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(600),
            FillBehavior = FillBehavior.Stop
        });
    }

    private const string HealthText =
        "Играть в это время врачи не рекомендуют: поздние игры сбивают сон и бьют по самочувствию на следующий день.";

    /// <summary>Часы и ночная приписка. Вызывается раз в секунду, пока экран закрыт.</summary>
    internal void UpdateClock(DateTime now, bool lateHours)
    {
        Clock.Text = now.ToString("HH:mm");

        var note = lateHours ? HealthText : string.Empty;
        if (HealthNote.Text != note) HealthNote.Text = note;

        HealthNote.Visibility = lateHours ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMessageClick(object sender, RoutedEventArgs e) => MessageRequested?.Invoke();

    /// <summary>Кнопка нужна только на основном мониторе, на остальных она лишняя.</summary>
    internal void HideMessageButton() => MessageButton.Visibility = Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        _bugTimer.Start();
    }

    public void CloseForReal()
    {
        _bugTimer.Stop();
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 не должен убирать блокировку.
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
