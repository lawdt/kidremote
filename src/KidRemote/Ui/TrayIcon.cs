using System.Drawing;
using System.Drawing.Drawing2D;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;
using KidRemote.Core;
using Forms = System.Windows.Forms;

namespace KidRemote.Ui;

/// <summary>
/// Значок в трее. Цвет кружка повторяет состояние таймера, поэтому статус видно
/// не заглядывая в телефон.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private Icon? _current;

    public event Action? ExitRequested;
    public event Action? OpenConfigRequested;

    public TrayIcon()
    {
        _statusItem = new Forms.ToolStripMenuItem("Загрузка…") { Enabled = false };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть настройки", null, (_, _) => OpenConfigRequested?.Invoke());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Text = "KidRemote",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application
        };
    }

    public void Render(BankState state, long remainingSeconds)
    {
        var (color, caption) = state switch
        {
            BankState.Unlimited => (Color.FromArgb(0x40, 0xC4, 0xFF), "Безлимит"),
            BankState.Paused => (Color.FromArgb(0x9E, 0x9E, 0x9E), $"Пауза · {TimeFormat.Compact(remainingSeconds)}"),
            BankState.Locked => (Color.FromArgb(0xFF, 0x17, 0x44), "Время вышло"),
            _ => (RunningColor(remainingSeconds), $"Осталось {TimeFormat.Compact(remainingSeconds)}")
        };

        _statusItem.Text = caption;
        _icon.Text = $"KidRemote — {caption}";
        SwapIcon(color);
    }

    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    private static Color RunningColor(long remainingSeconds) => remainingSeconds switch
    {
        <= 60 => Color.FromArgb(0xFF, 0x17, 0x44),
        <= 300 => Color.FromArgb(0xFF, 0xD6, 0x00),
        _ => Color.FromArgb(0x00, 0xE6, 0x76)
    };

    private void SwapIcon(Color color)
    {
        var previous = _current;

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(200, 0, 0, 0), 2);
            graphics.DrawEllipse(pen, 3, 3, 26, 26);
        }

        var handle = bitmap.GetHicon();
        try
        {
            _current = (Icon)Icon.FromHandle(handle).Clone();
            _icon.Icon = _current;
        }
        finally
        {
            NativeDestroyIcon(handle);
        }

        previous?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
