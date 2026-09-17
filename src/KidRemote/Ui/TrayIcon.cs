using System.Drawing;
using System.Drawing.Drawing2D;
using KidRemote.Core;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;
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
    private readonly Dictionary<CountdownMode, Forms.ToolStripMenuItem> _countdownItems = new();

    private Icon? _currentIcon;
    private IntPtr _currentHandle = IntPtr.Zero;
    private Color _currentColor = Color.Empty;

    public event Action? OpenConfigRequested;
    public event Action? ExitRequested;
    public event Action? MessageRequested;
    public event Action<CountdownMode>? CountdownModeChanged;

    public TrayIcon(CountdownMode countdownMode)
    {
        _statusItem = new Forms.ToolStripMenuItem("Загрузка…") { Enabled = false };

        var countdownMenu = new Forms.ToolStripMenuItem("Таймер на экране");
        AddCountdownOption(countdownMenu, CountdownMode.Always, "Показывать");
        AddCountdownOption(countdownMenu, CountdownMode.LastMinute, "Только последнюю минуту");
        AddCountdownOption(countdownMenu, CountdownMode.Never, "Не показывать");
        MarkCountdownMode(countdownMode);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(countdownMenu);
        menu.Items.Add("Написать родителям…", null, (_, _) => MessageRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть настройки…", null, (_, _) => OpenConfigRequested?.Invoke());
        menu.Items.Add("Выход…", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Text = "KidRemote",
            ContextMenuStrip = menu,
            Icon = LoadBaseIcon()
        };

        // Visible выставляем после назначения иконки: без неё оболочка иногда не создаёт значок.
        _icon.Visible = true;
    }

    private void AddCountdownOption(Forms.ToolStripMenuItem parent, CountdownMode mode, string caption)
    {
        var item = new Forms.ToolStripMenuItem(caption, null, (_, _) =>
        {
            MarkCountdownMode(mode);
            CountdownModeChanged?.Invoke(mode);
        });

        _countdownItems[mode] = item;
        parent.DropDownItems.Add(item);
    }

    private void MarkCountdownMode(CountdownMode mode)
    {
        foreach (var (key, item) in _countdownItems) item.Checked = key == mode;
    }

    /// <summary>Иконка из ресурсов самого exe, с запасным вариантом на случай неудачи.</summary>
    private static Icon LoadBaseIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var extracted = Icon.ExtractAssociatedIcon(path);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
            // Ниже вернём системную.
        }

        return SystemIcons.Application;
    }

    public void Render(BankState state, long remainingSeconds)
    {
        var (color, caption) = state switch
        {
            BankState.Unlimited => (Color.FromArgb(0x40, 0xC4, 0xFF), "Безлимит"),
            BankState.Locked => (Color.FromArgb(0xFF, 0x17, 0x44), "Время вышло"),
            _ => (RunningColor(remainingSeconds), $"Осталось {TimeFormat.Compact(remainingSeconds)}")
        };

        _statusItem.Text = caption;

        // Подсказка трея ограничена 63 символами, иначе NotifyIcon кидает исключение.
        var tooltip = $"KidRemote — {caption}";
        _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;

        if (color != _currentColor)
        {
            _currentColor = color;
            SwapIcon(color);
        }
    }

    public void ShowMessage(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Уведомления могут быть отключены политиками.
        }
    }

    private static Color RunningColor(long remainingSeconds) => remainingSeconds switch
    {
        <= 60 => Color.FromArgb(0xFF, 0x17, 0x44),
        <= 300 => Color.FromArgb(0xFF, 0xD6, 0x00),
        _ => Color.FromArgb(0x00, 0xE6, 0x76)
    };

    private void SwapIcon(Color color)
    {
        try
        {
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(color);
                graphics.FillEllipse(brush, 3, 3, 26, 26);
                using var pen = new Pen(Color.FromArgb(220, 10, 15, 30), 2.5f);
                graphics.DrawEllipse(pen, 3, 3, 26, 26);
            }

            var handle = bitmap.GetHicon();
            var previousIcon = _currentIcon;
            var previousHandle = _currentHandle;

            _currentIcon = Icon.FromHandle(handle);
            _currentHandle = handle;
            _icon.Icon = _currentIcon;

            // Прежний хэндл освобождаем только после того, как оболочка приняла новый.
            previousIcon?.Dispose();
            if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
        }
        catch
        {
            // Значок важнее его цвета: при сбое отрисовки оставляем предыдущий.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        if (_currentHandle != IntPtr.Zero) DestroyIcon(_currentHandle);
    }
}
