using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EInvoice.MockErp;

/// <summary>
/// A mock of the ERP's vendor-invoice endpoint. POST data/VendorInvoices books an invoice once per Idempotency-Key and
/// answers its document number (201 the first time, 200 for the same key again). It is strict like a real system:
/// an unknown or missing field is 400, a currency other than EUR is 422 (the company books in EUR).
/// Control endpoints under _mock let tests plan failures (by invoice number), list bookings and reset.
/// Chaos mode (off by default; Chaos:* settings or POST _mock/chaos) answers a share of all postings with trouble
/// (see <see cref="Chaos"/>); GET _mock/stats tells what the mock saw.
/// It is its own token issuer (OAuth 2.0 client credentials, like Entra ID in front of D365): POST oauth/token for the
/// configured client (Auth:ClientId, Auth:ClientSecret, required; Auth:TokenLifetimeSeconds), and every data call needs
/// one of its unexpired tokens as "Authorization: Bearer", else 401. See <see cref="TokenIssuer"/>.
/// It also holds master data for the ERP sync (package 16c): GET data/Vendors, data/PurchaseOrders, data/ProductReceipts,
/// OData style, seeded at start (MasterData:*; see <see cref="MasterData"/> and <see cref="ODataQuery"/>).
/// </summary>
public sealed class MockErpProgram
{
    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<Ledger>();
        builder.Services.AddSingleton(sp => TokenIssuer.FromConfiguration(sp.GetRequiredService<IConfiguration>().GetSection("Auth")));
        builder.Services.AddSingleton(sp => new Chaos(ChaosPlan.FromConfiguration(sp.GetRequiredService<IConfiguration>().GetSection("Chaos"))));
        builder.Services.AddSingleton(sp => MasterData.FromConfiguration(sp.GetRequiredService<IConfiguration>().GetSection("MasterData"), DateTimeOffset.UtcNow));
        builder.Services.AddProblemDetails();
        var app = builder.Build();
        _ = app.Services.GetRequiredService<TokenIssuer>(); // no secret configured: refuse to start, not at the first call
        _ = app.Services.GetRequiredService<MasterData>(); // seeded at start, so the first read is not slow

        app.MapPost("/oauth/token", async (HttpRequest request, TokenIssuer issuer) =>
        {
            issuer.CountRequest();
            if (issuer.EndpointDown)
            {
                return Results.Problem(statusCode: 503, title: "The token endpoint is down (planned).");
            }

            if (!request.HasFormContentType)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: 400);
            }

            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            if (form["grant_type"] != "client_credentials")
            {
                return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
            }

            if (!issuer.IsClient(form["client_id"], form["client_secret"]))
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            }

            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { access_token = issuer.Issue(), token_type = "Bearer", expires_in = issuer.LifetimeSeconds });
        });

        app.MapPost("/data/VendorInvoices", async (HttpRequest request, Ledger ledger, Chaos chaos, TokenIssuer issuer) =>
        {
            using var visit = chaos.Enter();
            if (!issuer.Accepts(request.Headers.Authorization))
            {
                request.HttpContext.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                return Results.Problem(statusCode: 401, title: "A valid bearer token is required.");
            }

            var trouble = chaos.Pick();
            switch (trouble)
            {
                case ChaosKind.Throttle:
                    request.HttpContext.Response.Headers.RetryAfter = chaos.Plan.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Problem(statusCode: 429, title: "Too many requests (chaos).");
                case ChaosKind.Error:
                    return Results.Problem(statusCode: 500, title: "Internal error (chaos).");
                case ChaosKind.Unavailable:
                    request.HttpContext.Response.Headers.RetryAfter = chaos.Plan.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Problem(statusCode: 503, title: "Service unavailable (chaos).");
                case ChaosKind.Slow:
                    await Task.Delay(chaos.Plan.SlowMs, request.HttpContext.RequestAborted);
                    break;
            }

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

            if (trouble == ChaosKind.Malformed)
            {
                return Results.Text("{\"documentNumber\":", "application/json", statusCode: 201); // booked, the answer broken
            }

            var answer = new { documentNumber = booking.DocumentNumber };
            return isNew ? Results.Json(answer, statusCode: 201) : Results.Json(answer, statusCode: 200);
        });

        app.MapGet("/data/{segment}", async (string segment, HttpRequest request, MasterData data, Chaos chaos, TokenIssuer issuer) =>
        {
            using var visit = chaos.Enter();
            if (!issuer.Accepts(request.Headers.Authorization))
            {
                request.HttpContext.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                return Results.Problem(statusCode: 401, title: "A valid bearer token is required.");
            }

            var trouble = chaos.Pick();
            switch (trouble)
            {
                case ChaosKind.Throttle:
                    request.HttpContext.Response.Headers.RetryAfter = chaos.Plan.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Problem(statusCode: 429, title: "Too many requests (chaos).");
                case ChaosKind.Error:
                    return Results.Problem(statusCode: 500, title: "Internal error (chaos).");
                case ChaosKind.Unavailable:
                    request.HttpContext.Response.Headers.RetryAfter = chaos.Plan.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Problem(statusCode: 503, title: "Service unavailable (chaos).");
                case ChaosKind.Slow:
                    await Task.Delay(chaos.Plan.SlowMs, request.HttpContext.RequestAborted);
                    break;
                case ChaosKind.Malformed:
                    return Results.Text("{\"value\":[", "application/json", statusCode: 200); // the page broke on the way
            }

            if (ODataQuery.Segment(segment) is not { } parsed)
            {
                return Results.Problem(statusCode: 404, title: "The ERP has no such set.");
            }

            var (set, key) = parsed;

            return set switch
            {
                "Vendors" => Read(data.Vendors, v => v.ModifiedDateTime, v => v.Id),
                "PurchaseOrders" => Read(data.Orders, o => o.ModifiedDateTime, o => o.Id),
                "ProductReceipts" => Read(data.Receipts, r => r.ModifiedDateTime, r => r.Id),
                _ => Results.Problem(statusCode: 404, title: "The ERP has no such set."),
            };

            IResult Read<T>(IReadOnlyList<T> records, Func<T, DateTimeOffset> modified, Func<T, string> id)
            {
                if (key is not null)
                {
                    if (request.Query.Count > 0)
                    {
                        return Results.Problem(statusCode: 400, title: "A single record takes no query options.");
                    }

                    var one = records.FirstOrDefault(r => id(r) == key);
                    return one is null ? Results.Problem(statusCode: 404, title: "No such record.") : Results.Json(one, MasterData.Json);
                }

                var (query, problem) = ODataQuery.Parse(request.Query);
                return query is null
                    ? Results.Problem(statusCode: 400, title: problem)
                    : Results.Json(ODataQuery.Page(records, modified, id, query, request), MasterData.Json);
            }
        });

        app.MapPost("/_mock/master-data/changes", (MasterDataChange change, MasterData data) =>
        {
            if (change.PurchaseOrders < 1 || change.PurchaseOrders > data.Orders.Count)
            {
                return Results.Problem(statusCode: 400, title: $"purchaseOrders must be 1 to {data.Orders.Count}.");
            }

            return Results.Json(new { changed = data.Change(change.PurchaseOrders, change.Seed, DateTimeOffset.UtcNow) });
        });
        app.MapGet("/_mock/master-data/export", (MasterData data) => Results.Text(data.Export(), "text/plain; charset=utf-8"));
        app.MapPost("/_mock/failures", (FailurePlan plan, Ledger ledger) =>
        {
            ledger.Plan(plan);
            return Results.NoContent();
        });
        app.MapGet("/_mock/postings", (Ledger ledger) => Results.Json(ledger.Bookings()));
        app.MapPost("/_mock/chaos", (ChaosPlan plan, Chaos chaos) =>
        {
            if (plan.Problem() is { } problem)
            {
                return Results.Problem(statusCode: 400, title: problem);
            }

            chaos.Set(plan);
            return Results.NoContent();
        });
        app.MapGet("/_mock/stats", (Chaos chaos, TokenIssuer issuer) => Results.Json(chaos.Stats(issuer.Requests)));
        app.MapPost("/_mock/auth", (AuthPlan plan, TokenIssuer issuer) =>
        {
            issuer.Set(plan);
            return Results.NoContent();
        });
        app.MapGet("/_mock/tokens", (TokenIssuer issuer) => Results.Json(issuer.Issued()));
        app.MapPost("/_mock/reset", (Ledger ledger, Chaos chaos, TokenIssuer issuer) =>
        {
            ledger.Reset();
            chaos.ResetStats();
            issuer.Reset();
            return Results.NoContent();
        });
        return app;
    }
}

/// <param name="PurchaseOrders">How many distinct orders change now.</param>
/// <param name="Seed">For a repeatable choice; null: random.</param>
public sealed record MasterDataChange(int PurchaseOrders, int? Seed);

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

public enum ChaosKind
{
    None,

    /// <summary>429 with Retry-After; nothing booked.</summary>
    Throttle,

    /// <summary>500; nothing booked.</summary>
    Error,

    /// <summary>503 with Retry-After; nothing booked.</summary>
    Unavailable,

    /// <summary>The right answer, SlowMs late.</summary>
    Slow,

    /// <summary>Booked, then an answer that is not JSON (as if the reply broke on the way).</summary>
    Malformed,
}

/// <param name="Share">Share of postings that get trouble, 0 (off) to 1.</param>
/// <param name="Kinds">Which kinds of trouble, chosen evenly (lower case names); none given: all five.</param>
/// <param name="Seed">For a repeatable sequence; null: random.</param>
public sealed record ChaosPlan(double Share, IReadOnlyList<string>? Kinds, int? Seed, int RetryAfterSeconds = 2, int SlowMs = 2000)
{
    private static readonly ChaosKind[] All = [ChaosKind.Throttle, ChaosKind.Error, ChaosKind.Unavailable, ChaosKind.Slow, ChaosKind.Malformed];

    public IReadOnlyList<ChaosKind> ChosenKinds() => Kinds is { Count: > 0 } ? [.. Kinds.Select(Parse)] : All;

    /// <summary>Why this plan makes no sense, or null.</summary>
    public string? Problem()
    {
        if (double.IsNaN(Share) || Share is < 0 or > 1)
        {
            return "Share must be between 0 and 1.";
        }

        if (Kinds?.Any(k => Array.IndexOf(All, Parse(k)) < 0) == true)
        {
            return "Kinds are throttle, error, unavailable, slow and malformed.";
        }

        if (RetryAfterSeconds is < 0 or > 3600 || SlowMs is < 0 or > 60_000)
        {
            return "RetryAfterSeconds must be 0 to 3600 and SlowMs 0 to 60000.";
        }

        return null;
    }

    public static ChaosPlan FromConfiguration(IConfigurationSection section)
    {
        var plan = new ChaosPlan(
            section.GetValue("Share", 0d),
            section.GetSection("Kinds").Get<string[]>(),
            section.GetValue<int?>("Seed"),
            section.GetValue("RetryAfterSeconds", 2),
            section.GetValue("SlowMs", 2000));
        return plan.Problem() is { } problem ? throw new InvalidOperationException($"Chaos settings: {problem}") : plan;
    }

    /// <summary>A kind by its lower case name; None for anything else (numbers included).</summary>
    private static ChaosKind Parse(string name) => Array.Find(All, k => k.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Decides which posting gets trouble, and counts what the mock saw (requests, most at once, trouble by kind).</summary>
public sealed class Chaos
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ChaosKind, int> _counts = [];
    private Random _random;
    private int _requests;
    private int _inFlight;
    private int _maxInFlight;

    public Chaos(ChaosPlan plan)
    {
        Plan = plan;
        _random = NewRandom(plan);
    }

    public ChaosPlan Plan { get; private set; }

    public void Set(ChaosPlan plan)
    {
        lock (_gate)
        {
            Plan = plan;
            _random = NewRandom(plan);
        }
    }

    public ChaosKind Pick()
    {
        lock (_gate)
        {
            if (Plan.Share <= 0 || _random.NextDouble() >= Plan.Share)
            {
                return ChaosKind.None;
            }

            var kinds = Plan.ChosenKinds();
            var kind = kinds[_random.Next(kinds.Count)];
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
            return kind;
        }
    }

    /// <summary>One request in flight until disposed.</summary>
    public IDisposable Enter()
    {
        lock (_gate)
        {
            _requests++;
            _maxInFlight = Math.Max(_maxInFlight, ++_inFlight);
        }

        return new Visit(this);
    }

    public object Stats(int tokenRequests)
    {
        lock (_gate)
        {
            return new
            {
                requests = _requests,
                tokenRequests,
                maxInFlight = _maxInFlight,
                chaos = new[] { ChaosKind.Throttle, ChaosKind.Error, ChaosKind.Unavailable, ChaosKind.Slow, ChaosKind.Malformed }
                    .ToDictionary(k => k.ToString().ToLowerInvariant(), k => _counts.GetValueOrDefault(k)),
            };
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            _counts.Clear();
            _requests = 0;
            _maxInFlight = 0;
        }
    }

    private static Random NewRandom(ChaosPlan plan) => plan.Seed is { } seed ? new Random(seed) : new Random();

    private void Leave()
    {
        lock (_gate)
        {
            _inFlight--;
        }
    }

    private sealed class Visit(Chaos chaos) : IDisposable
    {
        public void Dispose() => chaos.Leave();
    }
}

/// <param name="ExpireAll">Every token issued so far stops working now (new ones work).</param>
/// <param name="RejectAll">Every token is refused, new ones too, until this is set back.</param>
/// <param name="TokenEndpointDown">The token endpoint answers 503.</param>
public sealed record AuthPlan(bool ExpireAll, bool RejectAll, bool TokenEndpointDown);

/// <summary>Issues random bearer tokens to the one configured client and checks them on data calls. Thread-safe.</summary>
public sealed class TokenIssuer
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
    private readonly List<string> _issued = [];
    private readonly byte[] _clientId;
    private readonly byte[] _clientSecret;
    private bool _rejectAll;
    private int _requests;

    private TokenIssuer(string clientId, string clientSecret, int lifetimeSeconds)
    {
        _clientId = System.Text.Encoding.UTF8.GetBytes(clientId);
        _clientSecret = System.Text.Encoding.UTF8.GetBytes(clientSecret);
        LifetimeSeconds = lifetimeSeconds;
    }

    public int LifetimeSeconds { get; }

    public bool EndpointDown { get; private set; }

    public int Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests;
            }
        }
    }

    public static TokenIssuer FromConfiguration(IConfigurationSection section)
    {
        var secret = section["ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Auth:ClientSecret is required: the mock ERP issues tokens only to a configured client.");
        }

        var lifetime = section.GetValue("TokenLifetimeSeconds", 3600);
        if (lifetime is < 1 or > 86_400)
        {
            throw new InvalidOperationException("Auth:TokenLifetimeSeconds must be between 1 and 86400.");
        }

        return new TokenIssuer(section["ClientId"] is { Length: > 0 } id ? id : "einvoice", secret, lifetime);
    }

    public void CountRequest()
    {
        lock (_gate)
        {
            _requests++;
        }
    }

    /// <summary>Compared in constant time, as a real issuer would.</summary>
    public bool IsClient(string? clientId, string? clientSecret) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(clientId ?? string.Empty), _clientId)
        & System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(clientSecret ?? string.Empty), _clientSecret);

    public string Issue()
    {
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            _tokens[token] = DateTimeOffset.UtcNow.AddSeconds(LifetimeSeconds);
            _issued.Add(token);
        }

        return token;
    }

    public bool Accepts(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        lock (_gate)
        {
            return !_rejectAll && _tokens.TryGetValue(authorization["Bearer ".Length..], out var expires) && DateTimeOffset.UtcNow < expires;
        }
    }

    public void Set(AuthPlan plan)
    {
        lock (_gate)
        {
            if (plan.ExpireAll)
            {
                _tokens.Clear();
            }

            _rejectAll = plan.RejectAll;
            EndpointDown = plan.TokenEndpointDown;
        }
    }

    public IReadOnlyList<string> Issued()
    {
        lock (_gate)
        {
            return [.. _issued];
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _tokens.Clear();
            _issued.Clear();
            _rejectAll = false;
            EndpointDown = false;
            _requests = 0;
        }
    }
}
