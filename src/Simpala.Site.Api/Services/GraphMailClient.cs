using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Simpala.Site.Api.Configuration;
using Simpala.Site.Api.DTOs;

namespace Simpala.Site.Api.Services;

public enum MailOutcome { Accepted, Failed, Unknown }
public sealed class MailAuthenticationException : Exception { }

// Deliberately no retries: sendMail has no server-side idempotency guarantee.
public sealed class GraphMailClient(IHttpClientFactory clients, IOptions<SupportMailOptions> options,
    TimeProvider clock, ILogger<GraphMailClient> logger) : IDisposable
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is not null && expiresAt > clock.GetUtcNow().AddMinutes(2)) return accessToken;
            var settings = options.Value;
            using var client = clients.CreateClient("Entra");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["scope"] = "https://graph.microsoft.com/.default",
                ["client_id"] = settings.ClientId, ["client_secret"] = settings.ClientSecret
            });
            using var response = await client.PostAsync(
                $"https://login.microsoftonline.com/{Guid.Parse(settings.TenantId):D}/oauth2/v2.0/token", form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                int? entraCode = null;
                try
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (error.RootElement.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array
                        && codes.GetArrayLength() > 0 && codes[0].TryGetInt32(out var code)) entraCode = code;
                }
                catch (JsonException) { }
                logger.LogWarning("Autenticação de e-mail recusada pelo Entra. HTTP {Status}; código Entra {Code}. Nenhuma tentativa de envio iniciada.",
                    (int)response.StatusCode, entraCode);
                throw new MailAuthenticationException();
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("access_token", out var token)
                || string.IsNullOrWhiteSpace(token.GetString())
                || !json.RootElement.TryGetProperty("expires_in", out var expiry)
                || !expiry.TryGetInt32(out var seconds) || seconds < 1)
                throw new MailAuthenticationException();
            accessToken = token.GetString()!;
            expiresAt = clock.GetUtcNow().AddSeconds(seconds);
            return accessToken;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Não foi possível autenticar o serviço de e-mail. Tipo {Type}. Nenhuma tentativa de envio iniciada.", exception.GetType().Name);
            throw new MailAuthenticationException();
        }
        finally { tokenLock.Release(); }
    }

    public async Task<MailOutcome> SendAsync(EmailPreview mail, string token, CancellationToken cancellationToken)
    {
        // Defense in depth: never accept an arbitrary sender or recipient, including internal callers.
        if (mail.From != mail.To || mail.From is not
            ("faleconosco.financeira@simpala.com.br" or "faleconosco.consorcio@simpala.com.br"))
            throw new InvalidOperationException("Caixa de e-mail não permitida.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(mail.From)}/sendMail");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                subject = mail.Subject,
                body = new { contentType = "Text", content = mail.Body },
                toRecipients = new[] { new { emailAddress = new { address = mail.To } } }
            },
            saveToSentItems = true
        });
        try
        {
            using var client = clients.CreateClient("Graph");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted) return MailOutcome.Accepted;
            // Explicit client rejection means not accepted. 408/5xx/redirect/unexpected responses are uncertain.
            var status = (int)response.StatusCode;
            return status is >= 400 and < 500 && status != 408 ? MailOutcome.Failed : MailOutcome.Unknown;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        { return MailOutcome.Unknown; }
    }

    public void Dispose() => tokenLock.Dispose();
}
