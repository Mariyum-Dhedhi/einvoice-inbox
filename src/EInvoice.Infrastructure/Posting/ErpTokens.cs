using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EInvoice.Infrastructure.Posting;

/// <summary>Who we are to the ERP (OAuth 2.0 client credentials). The secret is never part of the text form.</summary>
public sealed record ErpCredentials(Uri TokenUrl, string ClientId, string ClientSecret, string? Scope)
{
    public override string ToString() => $"ErpCredentials {{ TokenUrl = {TokenUrl}, ClientId = {ClientId}, Scope = {Scope} }}";
}

/// <summary>No token to be had right now (token endpoint down, unreachable or answering nonsense): try again later.</summary>
public sealed class ErpTokenUnavailableException : Exception
{
    public ErpTokenUnavailableException()
        : base("Could not get a token from the ERP.")
    {
    }

    public ErpTokenUnavailableException(string message)
        : base(message)
    {
    }

    public ErpTokenUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The ERP refused who we are (the token endpoint refused our client, or a fresh token was refused): final.</summary>
public sealed class ErpCredentialsRefusedException : Exception
{
    public ErpCredentialsRefusedException()
        : base(HttpErpConnector.CredentialsRefused)
    {
    }

    public ErpCredentialsRefusedException(string message)
        : base(message)
    {
    }

    public ErpCredentialsRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Gets an access token with the client credentials grant, keeps it, and renews it 60 seconds before it expires: a
/// token that runs out while a call is on its way would fail a call that was valid when it left. One renewal at a time;
/// calls that arrive meanwhile wait for it and share its token. One per application (a singleton).
/// Nothing of the request or the answer goes into an exception message; the secret and the token never leave here
/// except in the token request and the Authorization header.
/// </summary>
public sealed class ErpTokenSource(Func<HttpClient> http, ErpCredentials credentials, TimeProvider time)
{
    /// <summary>The named HttpClient for the token endpoint.</summary>
    public const string ClientName = "erp-token";

    public static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Token? _current;

    private sealed record Token(string Value, DateTimeOffset ExpiresAt);

    /// <summary>A token with more than 60 seconds left, fetched first if there is none.</summary>
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh(_current) is { } token)
        {
            return token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(_current) is { } fetchedMeanwhile)
            {
                return fetchedMeanwhile;
            }

            _current = await FetchAsync(cancellationToken).ConfigureAwait(false);
            return _current.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The ERP refused <paramref name="rejected"/> (401): a new token, unless another call has already renewed it, so
    /// many calls that hit the same 401 cause one renewal.
    /// </summary>
    public async Task<string> RenewAsync(string rejected, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is { } current && current.Value != rejected && Fresh(current) is { } renewedMeanwhile)
            {
                return renewedMeanwhile;
            }

            _current = await FetchAsync(cancellationToken).ConfigureAwait(false);
            return _current.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Fresh(Token? token) => token is not null && time.GetUtcNow() < token.ExpiresAt - RenewBefore ? token.Value : null;

    private async Task<Token> FetchAsync(CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", credentials.ClientId),
            new("client_secret", credentials.ClientSecret),
        };
        if (!string.IsNullOrWhiteSpace(credentials.Scope))
        {
            form.Add(new("scope", credentials.Scope));
        }

        try
        {
            var requestedAt = time.GetUtcNow();
            using var client = http();
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(credentials.TokenUrl, content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                throw new ErpCredentialsRefusedException();
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ErpTokenUnavailableException();
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String
                && accessToken.GetString() is { Length: > 0 } value
                && root.TryGetProperty("token_type", out var type) && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds) && seconds > 0)
            {
                return new Token(value, requestedAt + TimeSpan.FromSeconds(seconds));
            }

            throw new ErpTokenUnavailableException();
        }
        catch (JsonException)
        {
            throw new ErpTokenUnavailableException();
        }
        catch (HttpRequestException)
        {
            throw new ErpTokenUnavailableException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ErpTokenUnavailableException(); // the token endpoint did not answer in time
        }
    }
}

/// <summary>
/// Puts the ERP token on every call. On a 401 the token is renewed once and the same call is sent once more; a second
/// 401 is returned as it is (the connector calls it final). Never a loop.
/// </summary>
public sealed class ErpAuthHandler(ErpTokenSource tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var token = await tokens.GetAsync(cancellationToken).ConfigureAwait(false);
        using var first = Copy(request, body, token);
        var response = await base.SendAsync(first, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        var renewed = await tokens.RenewAsync(token, cancellationToken).ConfigureAwait(false);
        using var second = Copy(request, body, renewed);
        return await base.SendAsync(second, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same call with a token: a request message can be sent only once.</summary>
    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? body, string token)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers.Where(h => !string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)))
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        copy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return copy;
    }
}
