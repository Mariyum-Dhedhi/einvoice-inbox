using System.Globalization;

namespace EInvoice.Application.Intake;

/// <summary>
/// At most so many uploads per person in any hour (a sliding hour), for the public demo: one visitor cannot fill the
/// database or keep the model busy for everyone. Wraps the intake; over the limit nothing reaches it and nothing is
/// stored. Every upload that reached the intake counts, also one it refused; one refused here does not.
/// User names are compared ignoring case, as everywhere else.
/// </summary>
public sealed class UploadLimit(IInvoiceIntake inner, UploadCounter counter) : IInvoiceIntake
{
    /// <summary>With a counter of its own (one instance for the application's lifetime, as in the tests).</summary>
    public UploadLimit(IInvoiceIntake inner, int perHour, TimeProvider time)
        : this(inner, new UploadCounter(perHour, time))
    {
    }

    public Task<IntakeResult> SubmitAsync(UploadedFile file, string submittedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submittedBy);
        return counter.TryCount(submittedBy)
            ? inner.SubmitAsync(file, submittedBy, cancellationToken)
            : Task.FromResult(new IntakeResult(
                IntakeOutcome.TooManyUploads,
                Detail: string.Create(CultureInfo.InvariantCulture, $"At most {counter.PerHour} uploads per hour in this demo. Try again later.")));
    }
}

/// <summary>
/// The uploads of the last hour per person. One instance for the whole application (a singleton): the intake itself
/// lives per request, the count must not.
/// </summary>
public sealed class UploadCounter
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public UploadCounter(int perHour, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(perHour, 1);
        PerHour = perHour;
        _time = time;
    }

    public int PerHour { get; }

    /// <summary>Counts the upload if it is within the limit; checking and counting in one step, so parallel uploads cannot slip through.</summary>
    public bool TryCount(string user)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // Forget what no longer counts, for everyone: the table stays as small as the last hour's uploaders.
            foreach (var (name, times) in _uploads.ToList())
            {
                while (times.Count > 0 && now - times.Peek() >= Window)
                {
                    times.Dequeue();
                }

                if (times.Count == 0)
                {
                    _uploads.Remove(name);
                }
            }

            if (!_uploads.TryGetValue(user, out var recent))
            {
                recent = new Queue<DateTimeOffset>();
                _uploads[user] = recent;
            }

            if (recent.Count >= PerHour)
            {
                return false;
            }

            recent.Enqueue(now);
            return true;
        }
    }
}
