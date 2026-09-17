using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace KidRemote.Ui;

/// <summary>
/// Рисует эмодзи картинкой. Нужен потому, что WPF не умеет цветные шрифты и выводит
/// эмодзи чёрно-белым контуром — Skia же отрисовывает их как положено.
/// </summary>
internal static class EmojiRenderer
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new();
    private static readonly object Sync = new();

    public static BitmapSource? Render(string emoji, int size)
    {
        var key = $"{emoji}:{size}";

        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var rendered = Draw(emoji, size);
            Cache[key] = rendered;
            return rendered;
        }
    }

    private static BitmapSource? Draw(string emoji, int size)
    {
        try
        {
            using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            using var typeface = SKFontManager.Default.MatchCharacter(
                                     null, SKFontStyle.Normal, null, char.ConvertToUtf32(emoji, 0))
                                 ?? SKTypeface.FromFamilyName("Segoe UI Emoji");

            using var paint = new SKPaint
            {
                Typeface = typeface,
                TextSize = size * 0.86f,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center,
                SubpixelText = true
            };

            var metrics = paint.FontMetrics;
            var baseline = size / 2f - (metrics.Ascent + metrics.Descent) / 2f;
            canvas.DrawText(emoji, size / 2f, baseline, paint);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var source = new BitmapImage();
            source.BeginInit();
            source.StreamSource = new MemoryStream(data.ToArray());
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();

            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Складывает текст в строку, подменяя эмодзи цветными картинками.</summary>
    public static void AppendTo(System.Windows.Documents.InlineCollection inlines, string text,
        System.Windows.Media.Brush brush, double fontSize)
    {
        foreach (var (part, isEmoji) in Split(text))
        {
            if (isEmoji)
            {
                var image = Render(part, (int)Math.Round(fontSize * 1.25));
                if (image is not null)
                {
                    inlines.Add(new System.Windows.Documents.InlineUIContainer(new System.Windows.Controls.Image
                    {
                        Source = image,
                        Width = fontSize * 1.25,
                        Height = fontSize * 1.25,
                        Margin = new System.Windows.Thickness(1, 0, 1, -2)
                    }));

                    continue;
                }
            }

            inlines.Add(new System.Windows.Documents.Run(part) { Foreground = brush });
        }
    }

    /// <summary>Разбивает текст на обычные куски и эмодзи, сохраняя порядок.</summary>
    public static IEnumerable<(string Text, bool IsEmoji)> Split(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var buffer = new System.Text.StringBuilder();

        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;

            if (IsEmoji(element))
            {
                if (buffer.Length > 0)
                {
                    yield return (buffer.ToString(), false);
                    buffer.Clear();
                }

                yield return (element, true);
            }
            else
            {
                buffer.Append(element);
            }
        }

        if (buffer.Length > 0) yield return (buffer.ToString(), false);
    }

    private static bool IsEmoji(string element)
    {
        if (element.Length == 0) return false;

        var code = char.ConvertToUtf32(element, 0);

        return code is >= 0x1F000 and <= 0x1FAFF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2B00 and <= 0x2BFF
            or 0x2705 or 0x274C or 0x2764 or 0x00A9 or 0x00AE or 0x203C or 0x2049;
    }
}
