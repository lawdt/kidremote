using System.IO;
using System.Media;

namespace KidRemote.Ui;

/// <summary>
/// Звуковые сигналы. Тон синтезируется в память и проигрывается через звуковую карту:
/// Console.Beep на многих машинах уходит в системный динамик, которого физически нет.
/// </summary>
internal static class Alarm
{
    private const int SampleRate = 44100;

    private static readonly Lazy<SoundPlayer?> Soft = new(() => Build(new[] { 880.0, 1175.0 }, 130, 0.20));
    private static readonly Lazy<SoundPlayer?> Urgent = new(() => Build(new[] { 1568.0, 1568.0 }, 90, 0.28));
    private static readonly Lazy<SoundPlayer?> Chime = new(() => Build(new[] { 659.3, 987.8 }, 190, 0.32));

    /// <summary>Мягкий сигнал на переходе минуты в жёлтой зоне.</summary>
    public static void Minute() => Play(Soft.Value);

    /// <summary>Резкий сигнал на последней минуте.</summary>
    public static void LastMinute() => Play(Urgent.Value);

    /// <summary>Мягкий сигнал о сообщении от родителя.</summary>
    public static void Message() => Play(Chime.Value);

    private static void Play(SoundPlayer? player)
    {
        try
        {
            if (player is not null)
            {
                player.Play();
                return;
            }

            // Синтезировать не вышло — пусть прозвучит хотя бы системный сигнал.
            SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            Core.Log.Write($"звук: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static SoundPlayer? Build(double[] tones, int msEach, double amplitude)
    {
        try
        {
            var stream = new MemoryStream(WriteWav(tones, msEach, amplitude));
            var player = new SoundPlayer(stream);
            player.Load();
            return player;
        }
        catch (Exception ex)
        {
            Core.Log.Write($"звук: не удалось подготовить сигнал, {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private static byte[] WriteWav(double[] tones, int msEach, double amplitude)
    {
        var samplesPerTone = SampleRate * msEach / 1000;
        var total = samplesPerTone * tones.Length;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        var dataSize = total * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)1);              // моно
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        // Короткое нарастание и затухание убирают щелчки на краях тона.
        var fade = Math.Max(1, samplesPerTone / 12);

        for (var t = 0; t < tones.Length; t++)
        {
            for (var i = 0; i < samplesPerTone; i++)
            {
                var envelope = 1.0;
                if (i < fade) envelope = i / (double)fade;
                else if (i > samplesPerTone - fade) envelope = (samplesPerTone - i) / (double)fade;

                var value = Math.Sin(2 * Math.PI * tones[t] * i / SampleRate) * amplitude * envelope;
                writer.Write((short)(value * short.MaxValue));
            }
        }

        writer.Flush();
        return memory.ToArray();
    }
}
