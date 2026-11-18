using EInvoice.Application.Intake;
using EInvoice.Domain.Invoices;

namespace EInvoice.UnitTests.Intake;

/// <summary>
/// The public demo's upload limit (UploadLimit): at most so many uploads per person in any hour, so one visitor cannot
/// fill the database or keep the model busy for everyone. Over the limit nothing reaches the pipeline and nothing is stored.
/// </summary>
public sealed class UploadLimitTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly CountingIntake _inner = new();
    private readonly MovableTime _time = new(Start);

    private UploadLimit Limit(int perHour) => new(_inner, perHour, _time);

    private static Task<IntakeResult> UploadAsync(IInvoiceIntake intake, string user) =>
        intake.SubmitAsync(new UploadedFile("invoice.xml", new byte[] { 1 }), user, CancellationToken.None);

    [Fact]
    public async Task Uploads_up_to_the_limit_go_through_and_the_next_one_is_refused_without_reaching_the_pipeline()
    {
        var intake = Limit(perHour: 3);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(IntakeOutcome.Created, (await UploadAsync(intake, "clerk@example.com")).Outcome);
        }

        var refused = await UploadAsync(intake, "clerk@example.com");

        Assert.Equal(IntakeOutcome.TooManyUploads, refused.Outcome);
        Assert.Null(refused.InvoiceId);
        Assert.False(string.IsNullOrWhiteSpace(refused.Detail));
        Assert.Equal(3, _inner.Calls);
    }

    [Fact]
    public async Task Each_person_has_their_own_limit()
    {
        var intake = Limit(perHour: 1);
        await UploadAsync(intake, "first@example.com");

        Assert.Equal(IntakeOutcome.TooManyUploads, (await UploadAsync(intake, "first@example.com")).Outcome);
        Assert.Equal(IntakeOutcome.Created, (await UploadAsync(intake, "second@example.com")).Outcome);
    }

    [Fact]
    public async Task User_names_are_compared_ignoring_case_as_everywhere_else()
    {
        var intake = Limit(perHour: 1);
        await UploadAsync(intake, "clerk@example.com");

        Assert.Equal(IntakeOutcome.TooManyUploads, (await UploadAsync(intake, "CLERK@example.com")).Outcome);
    }

    [Fact]
    public async Task The_hour_slides_an_upload_counts_for_exactly_one_hour()
    {
        var intake = Limit(perHour: 2);
        await UploadAsync(intake, "clerk@example.com");            // 10:00
        _time.Now = Start.AddMinutes(30);
        await UploadAsync(intake, "clerk@example.com");            // 10:30

        _time.Now = Start.AddMinutes(59);
        Assert.Equal(IntakeOutcome.TooManyUploads, (await UploadAsync(intake, "clerk@example.com")).Outcome);

        _time.Now = Start.AddHours(1);                               // the 10:00 upload no longer counts
        Assert.Equal(IntakeOutcome.Created, (await UploadAsync(intake, "clerk@example.com")).Outcome);
        Assert.Equal(IntakeOutcome.TooManyUploads, (await UploadAsync(intake, "clerk@example.com")).Outcome);
    }

    [Fact]
    public async Task A_refused_attempt_does_not_use_up_the_limit_but_one_the_pipeline_refused_does()
    {
        // Retrying while over the limit costs nothing extra; a file the pipeline turned down (a duplicate, say) still
        // made the pipeline work, so it counts.
        var intake = Limit(perHour: 1);
        _inner.Result = new IntakeResult(IntakeOutcome.DuplicateFile, Guid.NewGuid(), Detail: "uploaded before");
        await UploadAsync(intake, "clerk@example.com");
        for (var i = 0; i < 5; i++)
        {
            await UploadAsync(intake, "clerk@example.com");
        }

        _inner.Result = new IntakeResult(IntakeOutcome.Created, Guid.NewGuid(), InvoiceStatus.Matched);
        _time.Now = Start.AddHours(1);
        Assert.Equal(IntakeOutcome.Created, (await UploadAsync(intake, "clerk@example.com")).Outcome);
        Assert.Equal(2, _inner.Calls);
    }

    [Fact]
    public async Task Simultaneous_uploads_cannot_slip_past_the_limit()
    {
        var intake = Limit(perHour: 5);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => UploadAsync(intake, "clerk@example.com"))));

        Assert.Equal(5, results.Count(r => r.Outcome == IntakeOutcome.Created));
        Assert.Equal(5, _inner.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_below_one_is_a_configuration_error(int perHour) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit(perHour));

    private sealed class CountingIntake : IInvoiceIntake
    {
        private int _calls;

        public int Calls => _calls;

        public IntakeResult Result { get; set; } = new(IntakeOutcome.Created, Guid.NewGuid(), InvoiceStatus.Matched);

        public Task<IntakeResult> SubmitAsync(UploadedFile file, string submittedBy, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Result);
        }
    }

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
