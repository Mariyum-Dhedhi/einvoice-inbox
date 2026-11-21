namespace EInvoice.Application.Posting;

public enum BreakerState
{
    /// <summary>Calls go to the ERP; temporary failures in a row are counted.</summary>
    Closed,

    /// <summary>Too many failures: no calls until the open period is over.</summary>
    Open,

    /// <summary>The open period is over and one trial call is out; its result decides.</summary>
    HalfOpen,
}

public enum BreakerPermit
{
    Denied,

    /// <summary>A normal call, while closed.</summary>
    Call,

    /// <summary>The one call allowed after the open period.</summary>
    Trial,
}

/// <param name="FailuresToOpen">Temporary failures in a row that open the breaker (1 to 100).</param>
/// <param name="OpenFor">How long it stays open before a trial call (1 second to 1 hour).</param>
public sealed record BreakerSettings
{
    public BreakerSettings(int failuresToOpen, TimeSpan openFor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresToOpen, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(failuresToOpen, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(openFor, TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(openFor, TimeSpan.FromHours(1));
        FailuresToOpen = failuresToOpen;
        OpenFor = openFor;
    }

    /// <summary>5 failures in a row, then 2 minutes without calls.</summary>
    public static BreakerSettings Default { get; } = new(5, TimeSpan.FromMinutes(2));

    public int FailuresToOpen { get; }

    public TimeSpan OpenFor { get; }
}

/// <summary>
/// Stops calling an ERP that keeps failing, so it can recover and our tries are not used up while it is down. One per
/// application (a singleton): every posting pass and every call sees the same state. Thread-safe.
/// Only temporary failures (no answer, timeout, 408, 429, 5xx) count against the ERP; any answer, a rejection too,
/// shows that it is there. While open, results of calls that started earlier are ignored; only the trial decides.
/// </summary>
public sealed class ErpCircuitBreaker(BreakerSettings settings, TimeProvider time)
{
    private readonly Lock _gate = new();
    private BreakerState _state = BreakerState.Closed;
    private int _failures;
    private DateTimeOffset _until; // open: when a trial may go; half-open: when an unanswered trial is given up

    public BreakerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Open (or half-open with a trial that never reported) and the time is up: the next call is a trial.</summary>
    public bool TrialDue
    {
        get
        {
            lock (_gate)
            {
                return _state != BreakerState.Closed && time.GetUtcNow() >= _until;
            }
        }
    }

    /// <summary>Asks to call the ERP now. A trial must be reported with <see cref="Record"/> like any call.</summary>
    public BreakerPermit TryEnter()
    {
        lock (_gate)
        {
            if (_state == BreakerState.Closed)
            {
                return BreakerPermit.Call;
            }

            var now = time.GetUtcNow();
            if (now < _until)
            {
                return BreakerPermit.Denied;
            }

            _state = BreakerState.HalfOpen;
            _until = now + settings.OpenFor;
            return BreakerPermit.Trial;
        }
    }

    /// <param name="erpAnswered">The ERP answered (posted or rejected); false for a temporary failure.</param>
    public void Record(BreakerPermit permit, bool erpAnswered)
    {
        lock (_gate)
        {
            switch (permit, _state)
            {
                case (BreakerPermit.Trial, BreakerState.HalfOpen) when erpAnswered:
                case (BreakerPermit.Call, BreakerState.Closed) when erpAnswered:
                    _state = BreakerState.Closed;
                    _failures = 0;
                    break;
                case (BreakerPermit.Trial, BreakerState.HalfOpen):
                    Open();
                    break;
                case (BreakerPermit.Call, BreakerState.Closed):
                    if (++_failures >= settings.FailuresToOpen)
                    {
                        Open();
                    }

                    break;
                default:
                    break; // a late result while open or half-open: says nothing about now
            }
        }
    }

    private void Open()
    {
        _state = BreakerState.Open;
        _failures = 0;
        _until = time.GetUtcNow() + settings.OpenFor;
    }
}
