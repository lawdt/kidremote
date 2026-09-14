namespace KidRemote.Ui;

internal static class Alarm
{
    /// <summary>Короткий негромкий сигнал за минуту до блокировки.</summary>
    public static void OneMinuteWarning()
    {
        Task.Run(() =>
        {
            try
            {
                Console.Beep(880, 120);
                Thread.Sleep(90);
                Console.Beep(1175, 140);
            }
            catch
            {
                // На машинах без системного динамика Beep кидает исключение — молча пропускаем.
            }
        });
    }
}
