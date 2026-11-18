using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// Sign-in cookies and form tokens are protected with keys the app keeps. In a container, keys kept inside it would be
/// gone after every restart or update (everyone signed out, every open form expired); with DataProtection:KeysPath the
/// keys live in a mounted folder, and the next start of the app reads what the last one wrote.
/// </summary>
public sealed class DataProtectionKeyTests : IDisposable
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";
    private const string Purpose = "DataProtectionKeyTests";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"einvoice-keys-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private ApiFactory Factory(string? keysPath)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["DataProtection:KeysPath"] = keysPath;
        return new ApiFactory(settings);
    }

    [Fact]
    public async Task What_one_start_of_the_app_protected_the_next_one_can_read()
    {
        string protectedText;
        await using (var first = Factory(_folder))
        {
            protectedText = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("signed in");
        }

        Assert.NotEmpty(Directory.GetFiles(_folder, "*.xml"));
        await using var second = Factory(_folder);
        Assert.Equal("signed in", second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedText));
    }

    [Fact]
    public async Task Without_the_folder_another_start_cannot_read_it()
    {
        // What the setting is for: two starts that keep their keys elsewhere (here: in memory of each test host) do not share them.
        string protectedText;
        await using (var first = Factory(_folder))
        {
            protectedText = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("signed in");
        }

        await using var other = Factory(Path.Combine(_folder, "elsewhere"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            other.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedText));
    }

    [Theory]
    [InlineData("keys")]
    [InlineData("./keys")]
    public async Task A_relative_keys_folder_stops_the_application_at_startup(string keysPath)
    {
        // Relative to what? The working directory of a container is not a place to keep keys by accident.
        await using var factory = Factory(keysPath);

        var error = Record.Exception(() => factory.CreateApiClient());

        var failure = Assert.IsType<OptionsValidationException>(error);
        Assert.Contains("KeysPath", failure.Message, StringComparison.Ordinal);
    }
}
