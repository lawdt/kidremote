using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using KidRemote.Core;
using KidRemote.Interop;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

public partial class LockOverlayWindow : Window
{
    private bool _allowClose;

    public LockOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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

    internal void Render(BankState state, string phrase)
    {
        if (state == BankState.Paused)
        {
            Glyph.Text = "⏸";
            Headline.Text = "Перерыв";
            SetSubtitle("Время на паузе и не расходуется");
        }
        else
        {
            Glyph.Text = "🔒";
            Headline.Text = "Время вышло";
            SetSubtitle(phrase);
        }
    }

    private void SetSubtitle(string text)
    {
        if (Subtitle.Text == text) return;

        Subtitle.Text = text;

        // Новая фраза проявляется, а не подменяется рывком.
        Subtitle.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(450),
            FillBehavior = FillBehavior.Stop
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongAuto(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    public void CloseForReal()
    {
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
