namespace KidRemote.Core;

internal enum BankState
{
    /// <summary>Баланс исчерпан — экран заблокирован.</summary>
    Locked,

    /// <summary>Есть баланс, блокировки нет.</summary>
    Running,

    /// <summary>Безлимит до отмены.</summary>
    Unlimited
}

/// <summary>
/// Банк игрового времени. Хранит именно остаток в секундах, а не момент истечения:
/// сон, блокировка сессии и простой не съедают выданные минуты.
/// </summary>
internal sealed class TimeBank
{
    private readonly object _sync = new();
    private long _remainingSeconds;
    private bool _unlimited;
    private double _carry;

    public event Action? Changed;

    public long RemainingSeconds
    {
        get { lock (_sync) return _remainingSeconds; }
    }

    public bool IsUnlimited
    {
        get { lock (_sync) return _unlimited; }
    }

    public BankState State
    {
        get
        {
            lock (_sync)
            {
                if (_unlimited) return BankState.Unlimited;
                return _remainingSeconds > 0 ? BankState.Running : BankState.Locked;
            }
        }
    }

    public void Restore(long remainingSeconds, bool unlimited)
    {
        lock (_sync)
        {
            // В безлимите остатка быть не должно. Состояние из прежних версий приводим к тому же виду.
            _remainingSeconds = unlimited ? 0 : Math.Max(0, remainingSeconds);
            _unlimited = unlimited;
            _carry = 0;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Списывает прошедшее время. Вызывается только когда выполнены все условия расхода
    /// (активная сессия, полноэкранное приложение на переднем плане, недавний ввод).
    /// </summary>
    public void Consume(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0) return;

        bool changed = false;

        lock (_sync)
        {
            if (_unlimited || _remainingSeconds <= 0) return;

            _carry += elapsedSeconds;
            var whole = (long)_carry;
            if (whole <= 0) return;

            _carry -= whole;
            var updated = Math.Max(0, _remainingSeconds - whole);
            if (updated != _remainingSeconds)
            {
                _remainingSeconds = updated;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Выдача времени выводит из безлимита: раз назначили конкретные минуты,
    /// значит вернулись к обычному режиму с отсчётом.
    /// </summary>
    public void Add(long seconds)
    {
        lock (_sync)
        {
            _remainingSeconds = Math.Max(0, _remainingSeconds + seconds);
            _unlimited = false;
            _carry = 0;
        }

        Changed?.Invoke();
    }

    public void Set(long seconds)
    {
        lock (_sync)
        {
            _remainingSeconds = Math.Max(0, seconds);
            _unlimited = false;
            _carry = 0;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Включение безлимита обнуляет отсчёт: время больше не считается, и возвращаться
    /// к старому остатку после отмены незачем — он выдаётся заново.
    /// </summary>
    public void SetUnlimited(bool unlimited)
    {
        lock (_sync)
        {
            _unlimited = unlimited;
            if (unlimited) _remainingSeconds = 0;
            _carry = 0;
        }

        Changed?.Invoke();
    }

    /// <summary>Немедленная блокировка: обнуляет баланс и снимает безлимит с паузой.</summary>
    public void LockNow()
    {
        lock (_sync)
        {
            _remainingSeconds = 0;
            _unlimited = false;
            _carry = 0;
        }

        Changed?.Invoke();
    }
}
