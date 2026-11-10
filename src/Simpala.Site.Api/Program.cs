using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Simpala.Site.Api.Configuration;
using Simpala.Site.Api.Persistence;
using Simpala.Site.Api.Services;

namespace Simpala.Site.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = BuildApp(args);
        await app.RunAsync();
    }

    public static WebApplication BuildApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.FullName
        });
        configure?.Invoke(builder);
        var sending = builder.Configuration.GetValue<bool>("EmailSettings:EnableSending");
        if (sending && !builder.Environment.IsProduction())
            throw new InvalidOperationException("Envio real permitido somente em Production.");
        // Public activation is separate from transport activation: production anti-abuse review is still required.
        var publicAccess = builder.Configuration.GetValue<bool>("Contact:EnablePublicAccess");
        if (publicAccess && (!sending || !builder.Environment.IsProduction()))
            throw new InvalidOperationException("Acesso público exige envio habilitado em Production.");
        builder.Services.Configure<MailOptions>(builder.Configuration.GetSection("EmailSettings"));
        builder.Services.AddOptions<SupportMailOptions>().Bind(builder.Configuration.GetSection("SupportMail"))
            .PostConfigure(value =>
            {
                if (value.EncryptedClientSecret.Length > 0)
                {
                    if (value.ClientSecret.Length > 0) throw new InvalidOperationException("Configure somente uma fonte de segredo.");
                    value.ClientSecret = MailSecretProtector.Unprotect(value.EncryptedClientSecret,
                        Environment.GetEnvironmentVariable("SupportMail__EncryptionKey") ?? "", value.EncryptionIV);
                }
            })
            .Validate(value => !sending || (Guid.TryParse(value.TenantId, out _) && Guid.TryParse(value.ClientId, out _)
                && !string.IsNullOrWhiteSpace(value.ClientSecret)), "Configure as credenciais do serviço de e-mail.")
            .ValidateOnStart();
        foreach (var name in new[] { "Entra", "Graph" })
            builder.Services.AddHttpClient(name, client => { client.Timeout = TimeSpan.FromSeconds(30); })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .RemoveAllLoggers();
        builder.Services.AddSingleton<GraphMailClient>();

        builder.Services.AddOptions<ContactOptions>().Bind(builder.Configuration.GetSection("Contact"))
            .Validate(options => options.RetentionDays is >= 1 and <= 3650, "Defina uma retenção entre 1 e 3650 dias.")
            .ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        var dataPath = Path.GetFullPath(builder.Configuration["Contact:DataDirectory"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
        Directory.CreateDirectory(dataPath);
        var protection = builder.Services.AddDataProtection().SetApplicationName("Simpala.Site.Api")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        builder.Services.AddSingleton(sp => new ContactStore(Path.Combine(dataPath, "contact.db")));
        builder.Services.AddSingleton<DocumentValidator>();
        builder.Services.AddSingleton<ChallengeService>();
        builder.Services.AddSingleton<EmailComposer>();
        builder.Services.AddSingleton<ContactService>();
        builder.Services.AddHostedService<RetentionWorker>();
        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.AddProblemDetails();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16_384);
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            var origins = builder.Configuration.GetSection("Contact:AllowedOrigins").Get<string[]>() ?? [];
            if (origins.Length > 0)
                policy.WithOrigins(origins).WithMethods("GET", "POST").WithHeaders("Content-Type", "Idempotency-Key", "X-Contact-Mode");
            else policy.SetIsOriginAllowed(_ => false);
        }));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("contact", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("contact-status", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, cancellation) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                { message = "Muitas tentativas. Aguarde um minuto e tente novamente." }, cancellation);
            };
        });

        var app = builder.Build();
        app.Services.GetRequiredService<ContactStore>().Initialize();
        // Generic responses even in Development; never return exceptions or personal data from logs.
        app.UseExceptionHandler(error => error.Run(async context =>
        {
            var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var tooLarge = exception is BadHttpRequestException { StatusCode: 413 };
            context.Response.StatusCode = tooLarge ? 413 : 500;
            await context.Response.WriteAsJsonAsync(new
            { message = tooLarge ? "A solicitação excede o tamanho permitido." : "Não foi possível processar a solicitação." });
        }));
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/api/contact"))
            {
                var local = context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
                if (!context.Request.IsHttps && !(local && app.Environment.IsDevelopment()))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { message = "Utilize uma conexão HTTPS." });
                    return;
                }
                var localAllowed = local && (app.Environment.IsDevelopment() || (sending && app.Environment.IsProduction()));
                if (!localAllowed && !publicAccess)
                {
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new { message = "O envio ainda não está disponível." });
                    return;
                }
            }
            await next();
        });
        app.UseCors();
        app.UseRateLimiter();
        app.MapControllers();
        app.MapGet("/health", () => Results.Ok(new { status = "ok", mode = sending ? "graph" : "simulation" }));
        return app;
    }
}
