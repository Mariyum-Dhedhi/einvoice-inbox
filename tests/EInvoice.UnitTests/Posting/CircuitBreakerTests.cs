using EInvoice.Application.Posting;

namespace EInvoice.UnitTests.Posting;

/// <summary>
/// The ERP circuit breaker: after 5 temporary failures in a row it stops calling the ERP for 2 minutes (open), then lets
/// one call through (half-open). That call decides: an answer closes it, another failure opens it again. Any answer
/// from the ERP (posted or rejected) means the ERP is there; only a temporary failure counts against it.
/// </summary>
public sealed class CircuitBreakerTests
{
    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly MovableTime _time = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));

    private ErpCircuitBreaker Breaker() => new(BreakerSettings.Default, _time);

    private static void Fail(ErpCircuitBreaker breaker, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var permit = breaker.TryEnter();
            Assert.NotEqual(BreakerPermit.Denied, permit);
            breaker.Record(permit, erpAnswered: false);
        }
    }

    [Fact]
    public void The_defaults_are_five_failures_and_two_minutes()
    {
        Assert.Equal((5, TimeSpan.FromMinutes(2)), (BreakerSettings.Default.FailuresToOpen, BreakerSettings.Default.OpenFor));
    }

    [Fact]
    public void Four_failures_in_a_row_keep_it_closed_and_the_fifth_opens_it()
    {
        var breaker = Breaker();

        Fail(breaker, 4);
        Assert.Equal((BreakerState.Closed, BreakerPermit.Call), (breaker.State, breaker.TryEnter()));

        breaker.Record(BreakerPermit.Call, erpAnswered: false);
        Assert.Equal((BreakerState.Open, BreakerPermit.Denied), (breaker.State, breaker.TryEnter()));
    }

    [Fact]
    public void An_answer_in_between_starts_the_count_again()
    {
        var breaker = Breaker();
        Fail(breaker, 4);
        breaker.Record(breaker.TryEnter(), erpAnswered: true); // posted or rejected: the ERP is there

        Fail(breaker, 4);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void While_open_nothing_is_let_through_until_the_time_is_up()
    {
        var breaker = Breaker();
        Fail(breaker, 5);

        _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerPermit.Denied, breaker.TryEnter());

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerPermit.Trial, breaker.TryEnter());
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void Half_open_lets_exactly_one_call_through()
    {
        var breaker = Breaker();
        Fail(breaker, 5);
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(BreakerPermit.Trial, breaker.TryEnter());
        Assert.Equal(BreakerPermit.Denied, breaker.TryEnter());
        Assert.Equal(BreakerPermit.Denied, breaker.TryEnter());
    }

    [Fact]
    public void A_successful_trial_closes_it()
    {
        var breaker = Breaker();
        Fail(breaker, 5);
        _time.Advance(TimeSpan.FromMinutes(2));

        breaker.Record(breaker.TryEnter(), erpAnswered: true);

        Assert.Equal((BreakerState.Closed, BreakerPermit.Call), (breaker.State, breaker.TryEnter()));
    }

    [Fact]
    public void A_failed_trial_opens_it_again_for_the_full_time()
    {
        var breaker = Breaker();
        Fail(breaker, 5);
        _time.Advance(TimeSpan.FromMinutes(2));

        breaker.Record(breaker.TryEnter(), erpAnswered: false);

        Assert.Equal((BreakerState.Open, BreakerPermit.Denied), (breaker.State, breaker.TryEnter()));
        _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerPermit.Denied, breaker.TryEnter());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerPermit.Trial, breaker.TryEnter());
    }

    [Fact]
    public void A_trial_whose_result_never_comes_does_not_block_it_for_ever()
    {
        // The worker stopped while the trial call ran: after another open period, a new trial may go.
        var breaker = Breaker();
        Fail(breaker, 5);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(BreakerPermit.Trial, breaker.TryEnter());

        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(BreakerPermit.Trial, breaker.TryEnter());
    }

    [Fact]
    public void Late_results_of_calls_that_started_before_it_opened_do_not_close_it()
    {
        // Four calls were in flight when the fifth failure opened it; an answer arriving later says nothing about now.
        var breaker = Breaker();
        var inFlight = breaker.TryEnter();
        Fail(breaker, 5);

        breaker.Record(inFlight, erpAnswered: true);

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(5, 0)]
    public void Settings_out_of_range_are_refused(int failures, int openSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BreakerSettings(failures, TimeSpan.FromSeconds(openSeconds)));
    }
}
