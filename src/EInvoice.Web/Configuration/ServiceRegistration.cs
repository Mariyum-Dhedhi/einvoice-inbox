using EInvoice.Application.Intake;
using EInvoice.Application.Matching;
using EInvoice.Application.Parsing;
using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Queries;
using EInvoice.Infrastructure.Validation;
using EInvoice.Web.Api;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Configuration;

internal static class ServiceRegistration
{
    // Room for the multipart envelope around the file itself.
    private const long MultipartOverhead = 64 * 1024;

    /// <summary>
    /// Settings first, each validated when the application starts, so a wrong value stops it immediately
    /// instead of failing on the first upload. Then the use cases and their adapters.
    /// </summary>
    public static IServiceCollection AddEInvoice(this IServiceCollection services)
    {
        services.AddOptions<DatabaseSettings>()
            .Configure<IConfiguration>((s, config) => s.ConnectionString = config.GetConnectionString("EInvoice"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseSettings>, DatabaseSettingsValidator>();
        services.AddOptions<IntakeSettings>().BindConfiguration(IntakeSettings.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<KositSettings>().BindConfiguration(KositSettings.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IValidateOptions<KositSettings>, KositSettingsValidator>();
        services.AddOptions<MatchingSettings>().BindConfiguration(MatchingSettings.Section).ValidateDataAnnotations().ValidateOnStart();

        // Request limits follow the document limit: a larger upload is cut off while it is read.
        services.AddOptions<FormOptions>().Configure<IOptions<IntakeSettings>>((form, intake) => form.MultipartBodyLengthLimit = intake.Value.MaxDocumentBytes);
        services.AddOptions<KestrelServerOptions>().Configure<IOptions<IntakeSettings>>((kestrel, intake) =>
            kestrel.Limits.MaxRequestBodySize = intake.Value.MaxDocumentBytes + MultipartOverhead);

        services.AddDbContext<EInvoiceDbContext>((sp, db) => db.UseSqlServer(sp.GetRequiredService<IOptions<DatabaseSettings>>().Value.ConnectionString));

        services.AddHttpClient<IConformanceValidator, KositValidatorClient>((sp, http) =>
        {
            var kosit = sp.GetRequiredService<IOptions<KositSettings>>().Value;
            http.BaseAddress = new Uri(kosit.BaseUrl!, UriKind.Absolute);
            http.Timeout = TimeSpan.FromSeconds(kosit.TimeoutSeconds);
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new IntakeOptions(sp.GetRequiredService<IOptions<IntakeSettings>>().Value.MaxDocumentBytes));
        services.AddSingleton(sp => new MatchingPolicy(sp.GetRequiredService<IOptions<MatchingSettings>>().Value.PriceTolerancePercent));
        services.AddSingleton<IInvoiceDocumentParser>(_ => new XRechnungParser()); // default XML limits

        services.AddScoped<IValidationDataSource, EfValidationDataSource>();
        services.AddScoped<InvoiceValidator>();
        services.AddScoped<IInvoiceMatcher, SqlInvoiceMatcher>();
        services.AddScoped<IIntakeStore, EfIntakeStore>();
        services.AddScoped<IRematchStore, EfRematchStore>();
        services.AddScoped<IInvoiceQueries, EfInvoiceQueries>();
        services.AddScoped<InvoiceIntake>();
        services.AddScoped<InvoiceRematch>();

        services.AddSingleton<ICurrentUser, AnonymousUser>();
        return services;
    }
}
