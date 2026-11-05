using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Simpala.Site.Api.DTOs;
using Simpala.Site.Api.Persistence;
using Simpala.Site.Api.Services;
using Api = Simpala.Site.Api.Program;

var passed = 0;
async Task Test(string name, Func<Task> action)
{
    await action();
    Console.WriteLine($"PASS {name}");
    passed++;
}

await Test("CPF e CNPJ numéricos e alfanuméricos", () =>
{
    var validator = new DocumentValidator();
    foreach (var document in new[] { "529.982.247-25", "01234567890", "11.222.333/0001-81", "12.abc.345/01de-35" })
        Check.That(validator.IsValid(document), document);
    foreach (var document in new[] { "11111111111", "00000000000000", "52998224724", "12ABC34501DE34", "12ABC34501DEA5", "5299822472A", "12ÁBC34501DE35", "12ABC34501DE350" })
        Check.That(!validator.IsValid(document), document);
    return Task.CompletedTask;
});

await Test("Formato exato, CRLF, São Paulo, remetente e destinatário", () =>
{
    var email = new EmailComposer(new DocumentValidator()).Compose("financeira", "FORMS-FIN-TEST",
        DateTimeOffset.Parse("2026-11-03T13:30:00Z"),
        new("Cliente Exemplo", "cliente@exemplo.com", "52998224725", "51999999999", "Preciso de atendimento."), false);
    var expected = string.Join("\r\n", new[]
    {
        "Área de interesse SIMPALA Financeira - 03/11/2026 10:30:00.", "",
        "Nº Registro: FORMS-FIN-TEST", "Nome: Cliente Exemplo", "E-mail: cliente@exemplo.com",
        "CPF: 529.982.247-25", "Telefone: (51) 99999-9999", "Mensagem: Preciso de atendimento.",
        "Aceito receber contato por: ", "Política de Privacidade: "
    });
    Check.Equal(expected, email.Body);
    Check.Equal(email.From, email.To);
    Check.Equal("faleconosco.financeira@simpala.com.br", email.From);
    var consortium = new EmailComposer(new DocumentValidator()).Compose("consorcio", "FORMS-CON-TEST",
        DateTimeOffset.UtcNow, new("Teste", "teste@exemplo.com", "12ABC34501DE35", "5133334444", "Teste."), true);
    Check.That(consortium.Body.StartsWith("Área de interesse SIMPALA Consórcios - "), "Prefixo do Consórcio");
    Check.That(consortium.Body.Contains("Mensagem: [TESTE HML] Teste."), "Prefixo HML");
    Check.That(consortium.Body.Contains("CPF: 12.ABC.345/01DE-35"), "Rótulo CPF preservado");
    Check.That(!consortium.Body.Contains(": ON"), "Não inventar consentimento");
    return Task.CompletedTask;
});

await Test("Desafio assinado, resposta incorreta, adulteração e expiração", () =>
{
    var clock = new TestClock(DateTimeOffset.UtcNow);
    var service = new ChallengeService(new EphemeralDataProtectionProvider(), clock);
    var challenge = service.Create();
    var answer = Fixture.Answer(challenge.Question);
    Check.That(service.Validate(challenge.Token, answer) is not null, "Desafio válido");
    Check.That(service.Validate(challenge.Token, "9999") is null, "Resposta incorreta");
    Check.That(service.Validate("invalid", answer) is null, "Token adulterado");
    clock.Now = clock.Now.AddMinutes(11);
    Check.That(service.Validate(challenge.Token, answer) is null, "Desafio expirado");
    return Task.CompletedTask;
});

await Test("Validação HTTP, honeypot, chave obrigatória e cabeçalhos injetados", async () =>
{
    await using var fixture = await Fixture.Start();
    var challenge = await fixture.Challenge();
    var payload = Fixture.Payload(challenge);
    payload["name"] = "Cliente\r\nNome: outro";
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
    payload["name"] = "Cliente Exemplo";
    payload["website"] = "spam";
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
    payload["website"] = "";
    payload["document"] = "52998224724";
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
    payload["document"] = "52998224725";
    using var noKey = await fixture.Client.PostAsJsonAsync("/api/contact/financeira", payload);
    Check.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
    payload["message"] = new string('x', 1001);
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
});

await Test("Concorrência, idempotência, conflito de conteúdo e antirreplay", async () =>
{
    await using var fixture = await Fixture.Start();
    var challenge = await fixture.Challenge();
    var payload = Fixture.Payload(challenge);
    var key = Guid.NewGuid().ToString();
    var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Post(payload, key)));
    foreach (var response in responses) Check.Equal(HttpStatusCode.OK, response.StatusCode);
    var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
    Check.That(bodies.Distinct().Count() == 1, "Mesma resposta e protocolo em concorrência");
    using var document = JsonDocument.Parse(bodies[0]);
    Check.That(document.RootElement.GetProperty("simulation").GetBoolean(), "Simulação explícita");
    Check.Equal("Pending", document.RootElement.GetProperty("state").GetString()!);
    payload["message"] = "Outro conteúdo";
    Check.Equal(HttpStatusCode.Conflict, (await fixture.Post(payload, key)).StatusCode);
    payload["message"] = "Preciso de atendimento.";
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
});

await Test("Persistência independente e retenção", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "simpala-store-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        var first = new ContactStore(path);
        first.Initialize();
        var now = DateTimeOffset.UtcNow;
        var created = first.CreateOrGet("financeira", "test-key", "hash", "nonce", now.AddMinutes(10), now.AddDays(-31));
        var reopened = new ContactStore(path);
        Check.Equal(created.Registration, reopened.Find("financeira", "test-key")!.Registration);
        Check.Equal(1, reopened.Purge(now.AddDays(-30), now));
        Check.That(reopened.Find("financeira", "test-key") is null, "Retenção aplicada");
    }
    finally
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
    return Task.CompletedTask;
});

await Test("Rate limiting por IP", async () =>
{
    await using var fixture = await Fixture.Start();
    for (var index = 0; index < 10; index++)
        Check.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/contact/challenge")).StatusCode);
    var rejection = await fixture.Client.GetAsync("/api/contact/challenge");
    Check.Equal(HttpStatusCode.TooManyRequests, rejection.StatusCode);
    Check.That(rejection.Headers.RetryAfter is not null, "Retry-After presente");
});

await Test("Production não coleta solicitações e EnableSending não habilita transmissão", async () =>
{
    await using var fixture = await Fixture.Start("Production");
    Check.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.Client.GetAsync("/api/contact/challenge")).StatusCode);
    var failed = false;
    try
    {
        await using var app = Api.BuildApp(["--environment", "Development"], builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EmailSettings:EnableSending"] = "true" }));
    }
    catch (InvalidOperationException) { failed = true; }
    Check.That(failed, "Envio deve falhar fechado");
});

await Test("HML por HTTPS, limite de corpo, rota e propriedades desconhecidas", async () =>
{
    await using var fixture = await Fixture.Start("HML");
    var challenge = await fixture.Challenge();
    var payload = Fixture.Payload(challenge);
    var success = await fixture.Post(payload);
    Check.Equal(HttpStatusCode.OK, success.StatusCode);
    using var response = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
    Check.That(response.RootElement.GetProperty("preview").GetProperty("body").GetString()!
        .Contains("Mensagem: [TESTE HML]"), "HML deve identificar a mensagem");
    using var unknown = await fixture.Client.PostAsJsonAsync("/api/contact/outra", payload);
    Check.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    payload["recipient"] = "arbitrario@exemplo.com";
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
    payload.Remove("recipient");
    payload["message"] = new string('x', 20_000);
    Check.Equal(HttpStatusCode.RequestEntityTooLarge, (await fixture.Post(payload)).StatusCode);
});

Console.WriteLine($"{passed} grupos de testes aprovados. Nenhum e-mail foi enviado.");

static class Check
{
    public static void That(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual) where T : notnull => That(EqualityComparer<T>.Default.Equals(expected, actual), $"Esperado: {expected}; recebido: {actual}");
}

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class Fixture(WebApplication app, HttpClient client, string dataDirectory, X509Certificate2? certificate) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public static async Task<Fixture> Start(string environment = "Development")
    {
        var data = Path.Combine(Path.GetTempPath(), "simpala-api-test-" + Guid.NewGuid().ToString("N"));
        var secure = environment is "HML" or "Production";
        X509Certificate2? certificate = null;
        if (secure)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Schannel on Windows requires a temporary user key container for a TLS server.
            // No certificate is trusted or installed in a certificate store.
            certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.UserKeySet);
        }
        string[] urls = secure ? [] : ["--urls", "http://127.0.0.1:0"];
        var host = Api.BuildApp(["--environment", environment, .. urls], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Contact:DataDirectory"] = data });
            builder.Logging.ClearProviders();
            if (certificate is not null)
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        });
        await host.StartAsync();
        var handler = new HttpClientHandler();
        if (certificate is not null)
            handler.ServerCertificateCustomValidationCallback = (request, cert, _, _) =>
                request.RequestUri!.IsLoopback && cert?.Thumbprint == certificate.Thumbprint;
        return new(host, new HttpClient(handler) { BaseAddress = new Uri(host.Urls.Single()) }, data, certificate);
    }
    public async Task<Challenge> Challenge() => (await Client.GetFromJsonAsync<Challenge>("/api/contact/challenge"))!;
    public static string Answer(string question) => Regex.Matches(question, "[0-9]+").Select(match => int.Parse(match.Value)).Sum().ToString();
    public static Dictionary<string, string> Payload(Challenge challenge) => new()
    {
        ["name"] = "Cliente Exemplo", ["email"] = "cliente@exemplo.com", ["document"] = "52998224725",
        ["phone"] = "51999999999", ["message"] = "Preciso de atendimento.",
        ["challengeToken"] = challenge.Token, ["challengeAnswer"] = Answer(challenge.Question), ["contactPreference"] = ""
    };
    public async Task<HttpResponseMessage> Post(Dictionary<string, string> payload, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contact/financeira");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(payload);
        return await Client.SendAsync(request);
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        certificate?.Dispose();
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
    }
}
