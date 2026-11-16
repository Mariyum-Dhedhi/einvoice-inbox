using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EInvoice.MockErp;

/// <summary>
/// A mock of the ERP's vendor-invoice endpoint. POST data/VendorInvoices books an invoice once per Idempotency-Key and
/// answers its document number (201 the first time, 200 for the same key again). It is strict like a real system:
/// an unknown or missing field is 400, a currency other than EUR is 422 (the company books in EUR).
/// Control endpoints under _mock let tests plan failures (by invoice number), list bookings and reset.
/// </summary>
public sealed class MockErpProgram
{
    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<Ledger>();
        builder.Services.AddProblemDetails();
        var app = builder.Build();

        app.MapPost("/data/VendorInvoices", async (HttpRequest request, Ledger ledger) =>
        {
            if (!Guid.TryParse(request.Headers["Idempotency-Key"], out var key))
            {
                return Results.Problem(statusCode: 400, title: "The Idempotency-Key header (a GUID) is required.");
            }

            VendorInvoice? invoice;
            try
            {
                invoice = await JsonSerializer.DeserializeAsync<VendorInvoice>(request.Body, Ledger.Json, request.HttpContext.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.Problem(statusCode: 400, title: "The body is not a vendor invoice of the agreed format.");
            }

            if (invoice is null)
            {
                return Results.Problem(statusCode: 400, title: "The body is empty.");
            }

            var failure = ledger.TakeFailure(invoice.InvoiceNumber);
            if (failure is { DelayMs: > 0 })
            {
                await Task.Delay(failure.DelayMs, request.HttpContext.RequestAborted);
            }

            if (failure is { Status: >= 400, AfterBooking: false })
            {
                return Results.Problem(statusCode: failure.Status, title: "A planned failure of the mock ERP.");
            }

            if (invoice.Currency != "EUR")
            {
                return Results.Problem(statusCode: 422, title: $"Currency {invoice.Currency} cannot be booked: the company books in EUR only.");
            }

            if (invoice.Lines.Count == 0)
            {
                return Results.Problem(statusCode: 422, title: "A vendor invoice needs at least one line.");
            }

            var (booking, isNew) = ledger.Book(key, invoice);
            if (failure is { Status: >= 400, AfterBooking: true })
            {
                return Results.Problem(statusCode: failure.Status, title: "A planned failure after booking (the reply is lost).");
            }

            var answer = new { documentNumber = booking.DocumentNumber };
            return isNew ? Results.Json(answer, statusCode: 201) : Results.Json(answer, statusCode: 200);
        });

        app.MapPost("/_mock/failures", (FailurePlan plan, Ledger ledger) =>
        {
            ledger.Plan(plan);
            return Results.NoContent();
        });
        app.MapGet("/_mock/postings", (Ledger ledger) => Results.Json(ledger.Bookings()));
        app.MapPost("/_mock/reset", (Ledger ledger) =>
        {
            ledger.Reset();
            return Results.NoContent();
        });
        return app;
    }
}

public sealed record VendorInvoice(
    string InvoiceNumber,
    string VendorAccount,
    DateOnly InvoiceDate,
    string Currency,
    decimal NetAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? PurchaseOrder,
    IReadOnlyList<VendorInvoiceLine> Lines);

public sealed record VendorInvoiceLine(
    int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal Amount, decimal TaxRatePercent, int? PurchaseOrderLine);

/// <param name="InvoiceNumber">Only postings of this invoice fail (null: any), so parallel tests do not use up each other's failures.</param>
/// <param name="Status">The status to answer; a 2xx with a delay simulates a slow but successful ERP.</param>
/// <param name="AfterBooking">Book first, then answer the failure: the ERP has it, the caller never hears.</param>
public sealed record FailurePlan(string? InvoiceNumber, int Status, int Count, bool AfterBooking, int DelayMs);

public sealed record Booking(Guid IdempotencyKey, string DocumentNumber, VendorInvoice Invoice);

/// <summary>The mock's in-memory books and its planned failures.</summary>
public sealed class Ledger
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private readonly ConcurrentDictionary<Guid, Booking> _bookings = new();
    private readonly List<(FailurePlan Plan, int Remaining)> _failures = [];
    private readonly Lock _gate = new();
    private int _next;

    public (Booking Booking, bool IsNew) Book(Guid key, VendorInvoice invoice)
    {
        var isNew = false;
        var booking = _bookings.GetOrAdd(key, k =>
        {
            isNew = true;
            return new Booking(k, $"VI-{Interlocked.Increment(ref _next):000000}", invoice);
        });
        return (booking, isNew);
    }

    public void Plan(FailurePlan plan)
    {
        lock (_gate)
        {
            _failures.Add((plan, plan.Count));
        }
    }

    public FailurePlan? TakeFailure(string invoiceNumber)
    {
        lock (_gate)
        {
            var index = _failures.FindIndex(f => f.Remaining > 0 && (f.Plan.InvoiceNumber is null || f.Plan.InvoiceNumber == invoiceNumber));
            if (index < 0)
            {
                return null;
            }

            var (plan, remaining) = _failures[index];
            _failures[index] = (plan, remaining - 1);
            return plan;
        }
    }

    public IReadOnlyList<Booking> Bookings() => [.. _bookings.Values.OrderBy(b => b.DocumentNumber, StringComparer.Ordinal)];

    public void Reset()
    {
        lock (_gate)
        {
            _bookings.Clear();
            _failures.Clear();
        }
    }
}
