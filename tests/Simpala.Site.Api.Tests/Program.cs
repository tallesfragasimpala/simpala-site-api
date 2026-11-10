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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
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
        new("Cliente Exemplo", "cliente@exemplo.com", "52998224725", "51999999999", "Preciso de atendimento."));
    var expected = string.Join("\r\n", new[]
    {
        "Área de interesse SIMPALA Financeira - 03/11/2026 10:30:00.", "",
        "Nº Registro: FORMS-FIN-TEST", "Nome: Cliente Exemplo", "E-mail: cliente@exemplo.com",
        "CPF: 529.982.247-25", "Telefone: (51) 99999-9999", "Mensagem: Preciso de atendimento.",
    });
    Check.Equal(expected, email.Body);
    Check.That(!email.Body.Contains("Aceito receber contato por:") && !email.Body.Contains("Política de Privacidade:"), "Campos removidos não aparecem no e-mail");
    Check.Equal("Entre em contato", email.Subject);
    Check.Equal(email.From, email.To);
    Check.Equal("faleconosco.financeira@simpala.com.br", email.From);
    var consortium = new EmailComposer(new DocumentValidator()).Compose("consorcio", "FORMS-CON-TEST",
        DateTimeOffset.UtcNow, new("Teste", "teste@exemplo.com", "12ABC34501DE35", "5133334444", "Teste."));
    Check.Equal("Entre em contato", consortium.Subject);
    Check.That(!consortium.Body.Contains("Aceito receber contato por:") && !consortium.Body.Contains("Política de Privacidade:"), "Consórcio também omite campos removidos");
    Check.That(consortium.Body.StartsWith("Área de interesse SIMPALA Consórcios - "), "Prefixo do Consórcio");
    Check.That(consortium.Body.EndsWith("Mensagem: Teste.", StringComparison.Ordinal), "Mensagem sem prefixo automático");
    Check.That(consortium.Body.Contains("CPF: 12.ABC.345/01DE-35"), "Rótulo CPF preservado");
    Check.That(!consortium.Body.Contains(": ON"), "Não inventar consentimento");
    var manual = new EmailComposer(new DocumentValidator()).Compose("financeira", "FORMS-FIN-MANUAL",
        DateTimeOffset.UtcNow, new("Teste", "teste@exemplo.com", "52998224725", "51999999999", "[TESTE HML] Digitado pelo usuário."));
    Check.That(manual.Body.EndsWith("Mensagem: [TESTE HML] Digitado pelo usuário.", StringComparison.Ordinal), "Prefixo manual preservado sem duplicação");
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

await Test("Production fechada por padrão e envio bloqueado em Development", async () =>
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

await Test("Simulação, limite de corpo, rota e propriedades desconhecidas", async () =>
{
    await using var fixture = await Fixture.Start();
    var challenge = await fixture.Challenge();
    var payload = Fixture.Payload(challenge);
    var success = await fixture.Post(payload);
    Check.Equal(HttpStatusCode.OK, success.StatusCode);
    using var response = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
    Check.That(response.RootElement.GetProperty("preview").GetProperty("body").GetString()!
        .EndsWith("Mensagem: Preciso de atendimento.", StringComparison.Ordinal), "Simulação preserva o texto sem prefixo");
    using var unknown = await fixture.Client.PostAsJsonAsync("/api/contact/outra", payload);
    Check.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    payload["recipient"] = "arbitrario@exemplo.com";
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Post(payload)).StatusCode);
    payload.Remove("recipient");
    payload["message"] = new string('x', 20_000);
    Check.Equal(HttpStatusCode.RequestEntityTooLarge, (await fixture.Post(payload)).StatusCode);
});

await Test("Graph: concorrência, cache de token, destinatário fixo, texto e idempotência", async () =>
{
    var graph = new GraphStub(HttpStatusCode.Accepted) { Delay = true };
    await using var fixture = await Fixture.Start("Production", graph.Configure);
    var challenge = await fixture.Challenge();
    var payload = Fixture.Payload(challenge);
    var key = Guid.NewGuid().ToString();
    var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Post(payload, key)));
    foreach (var item in responses) Check.Equal(HttpStatusCode.Accepted, item.StatusCode);
    var final = await fixture.Post(payload, key);
    using var json = JsonDocument.Parse(await final.Content.ReadAsStringAsync());
    Check.Equal("Accepted", json.RootElement.GetProperty("state").GetString()!);
    Check.That(!json.RootElement.GetProperty("simulation").GetBoolean(), "Modo real com transporte falso");
    Check.That(!json.RootElement.TryGetProperty("preview", out _), "Não devolver conteúdo pessoal no modo real");
    Check.Equal(1, graph.Sends);
    Check.Equal(1, graph.Tokens);
    Check.That(graph.SendUrl!.Contains("faleconosco.financeira%40simpala.com.br/sendMail"), "Caixa fixa na URL");
    using var body = JsonDocument.Parse(graph.SendBody!);
    var message = body.RootElement.GetProperty("message");
    Check.Equal("Text", message.GetProperty("body").GetProperty("contentType").GetString()!);
    Check.That(message.GetProperty("body").GetProperty("content").GetString()!.EndsWith("Mensagem: Preciso de atendimento.", StringComparison.Ordinal), "Graph recebe mensagem sem prefixo automático");
    Check.Equal("faleconosco.financeira@simpala.com.br", message.GetProperty("toRecipients")[0]
        .GetProperty("emailAddress").GetProperty("address").GetString()!);
    Check.That(body.RootElement.GetProperty("saveToSentItems").GetBoolean(), "Salvar em enviados");
    var second = await fixture.Challenge();
    Check.Equal(HttpStatusCode.Accepted, (await fixture.Post(Fixture.Payload(second))).StatusCode);
    Check.Equal(2, graph.Sends);
    Check.Equal(1, graph.Tokens);
    var consortiumChallenge = await fixture.Challenge();
    using var consortiumRequest = new HttpRequestMessage(HttpMethod.Post, "/api/contact/consorcio");
    consortiumRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    consortiumRequest.Content = JsonContent.Create(Fixture.Payload(consortiumChallenge));
    Check.Equal(HttpStatusCode.Accepted, (await fixture.Client.SendAsync(consortiumRequest)).StatusCode);
    Check.That(graph.SendUrl!.Contains("faleconosco.consorcio%40simpala.com.br/sendMail"), "Caixa Consórcio");
    Check.That(graph.SendBody!.Contains("SIMPALA Cons"), "Texto do Consórcio");
    Check.Equal(3, graph.Sends);
});

await Test("Graph: rejeição, timeout e 5xx nunca provocam reenvio automático", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError, HttpStatusCode.RequestTimeout })
    {
        var graph = new GraphStub(status);
        await using var fixture = await Fixture.Start("Production", graph.Configure);
        var challenge = await fixture.Challenge();
        var payload = Fixture.Payload(challenge);
        var key = Guid.NewGuid().ToString();
        var expected = status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? HttpStatusCode.BadGateway : HttpStatusCode.Conflict;
        Check.Equal(expected, (await fixture.Post(payload, key)).StatusCode);
        Check.Equal(expected, (await fixture.Post(payload, key)).StatusCode);
        Check.Equal(1, graph.Sends);
    }
    var timeout = new GraphStub(HttpStatusCode.Accepted) { Timeout = true };
    await using var timed = await Fixture.Start("Production", timeout.Configure);
    var data = Fixture.Payload(await timed.Challenge());
    var id = Guid.NewGuid().ToString();
    var response = await timed.Post(data, id);
    Check.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Check.That((await response.Content.ReadAsStringAsync()).Contains("Unknown"), "Resultado incerto persistido");
    Check.Equal(HttpStatusCode.Conflict, (await timed.Post(data, id)).StatusCode);
    Check.Equal(1, timeout.Sends);
});

await Test("Graph: falha de autenticação anterior ao envio permite repetição segura", async () =>
{
    var graph = new GraphStub(HttpStatusCode.Accepted) { RejectToken = true };
    await using var fixture = await Fixture.Start("Production", graph.Configure);
    var payload = Fixture.Payload(await fixture.Challenge());
    var key = Guid.NewGuid().ToString();
    var failure = await fixture.Post(payload, key);
    Check.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
    Check.That(!(await failure.Content.ReadAsStringAsync()).Contains("dummy-secret"), "Segredo não exposto");
    Check.Equal(0, graph.Sends);
    graph.RejectToken = false;
    Check.Equal(HttpStatusCode.Accepted, (await fixture.Post(payload, key)).StatusCode);
    Check.Equal(1, graph.Sends);
});

await Test("Migração v1 preserva simulações e tentativas interrompidas ficam Unknown", () =>
{
    var path = Path.Combine(Path.GetTempPath(), "simpala-migration-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Requests(Company TEXT,IdempotencyKey TEXT,Fingerprint TEXT,Registration TEXT UNIQUE,
                    CreatedAt TEXT,State TEXT,Simulation INTEGER CHECK(Simulation=1),PRIMARY KEY(Company,IdempotencyKey));
                INSERT INTO Requests VALUES('financeira','old','hash','OLD','2026-01-01T00:00:00.0000000+00:00','Pending',1);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        var store = new ContactStore(path);
        store.Initialize();
        store.Initialize();
        var old = store.Find("financeira", "old")!;
        Check.Equal("Simulation", old.Mode);
        Check.That(!store.TryStart(old, DateTimeOffset.UtcNow), "Simulação nunca pode ser enviada");
        var now = DateTimeOffset.UtcNow;
        var live = store.CreateOrGet("financeira", "live", "hash", "nonce", now.AddMinutes(10), now, "HML");
        Check.That(store.TryStart(live, now.AddMinutes(-3)), "Claim real");
        Check.That(!store.TryStart(live, now), "Segundo claim impedido");
        store.MarkStale(now);
        Check.Equal(ContactState.Unknown, store.Find("financeira", "live")!.State);
        Check.That(!store.TryStart(live, now), "Tentativa incerta não é reenviada");
        store.Purge(now.AddDays(1), now);
        Check.That(store.Find("financeira", "live") is not null, "Retenção preserva incerteza para reconciliação");
    }
    finally
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
    return Task.CompletedTask;
});

await Test("Simulação não é promovida para envio real com a mesma chave", async () =>
{
    var graph = new GraphStub(HttpStatusCode.Accepted);
    await using var fixture = await Fixture.Start("Production", graph.Configure);
    var payload = Fixture.Payload(await fixture.Challenge());
    var key = Guid.NewGuid().ToString();
    var normalized = new NormalizedContact("Cliente Exemplo", "cliente@exemplo.com", "52998224725", "51999999999", "Preciso de atendimento.");
    var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized))));
    fixture.Store.CreateOrGet("financeira", key, hash, "simulation-nonce",
        DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow);
    Check.Equal(HttpStatusCode.Conflict, (await fixture.Post(payload, key)).StatusCode);
    Check.Equal(0, graph.Sends);
    Check.Equal(0, graph.Tokens);
});

await Test("Formulário: CORS exato, preflight e modo de simulação impedindo envio real", async () =>
{
    var graph = new GraphStub(HttpStatusCode.Accepted);
    await using var fixture = await Fixture.Start("Production", builder =>
    {
        graph.Configure(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Contact:AllowedOrigins:0"] = "http://localhost:8000" });
    });
    using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/contact/financeira");
    preflight.Headers.Add("Origin", "http://localhost:8000");
    preflight.Headers.Add("Access-Control-Request-Method", "POST");
    preflight.Headers.Add("Access-Control-Request-Headers", "content-type,idempotency-key,x-contact-mode");
    var allowed = await fixture.Client.SendAsync(preflight);
    Check.Equal("http://localhost:8000", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
    Check.That(allowed.Headers.GetValues("Access-Control-Allow-Headers").Single().Contains("X-Contact-Mode", StringComparison.OrdinalIgnoreCase), "Cabeçalho de modo permitido");
    using var blockedOrigin = new HttpRequestMessage(HttpMethod.Get, "/health");
    blockedOrigin.Headers.Add("Origin", "https://untrusted.example");
    var blocked = await fixture.Client.SendAsync(blockedOrigin);
    Check.That(!blocked.Headers.Contains("Access-Control-Allow-Origin"), "Origem desconhecida sem autorização CORS");
    var payload = Fixture.Payload(await fixture.Challenge());
    using var mismatch = new HttpRequestMessage(HttpMethod.Post, "/api/contact/financeira");
    mismatch.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
    mismatch.Headers.Add("X-Contact-Mode", "simulation");
    mismatch.Content = JsonContent.Create(payload);
    Check.Equal(HttpStatusCode.Conflict, (await fixture.Client.SendAsync(mismatch)).StatusCode);
    Check.Equal(0, graph.Sends);
    Check.Equal(0, graph.Tokens);
});

await Test("Consulta de andamento sem dados pessoais, sem envio e isolada por empresa", async () =>
{
    var graph = new GraphStub(HttpStatusCode.Accepted);
    await using var fixture = await Fixture.Start("Production", graph.Configure);
    var payload = Fixture.Payload(await fixture.Challenge());
    var key = Guid.NewGuid().ToString();
    Check.Equal(HttpStatusCode.Accepted, (await fixture.Post(payload, key)).StatusCode);
    using var query = new HttpRequestMessage(HttpMethod.Get, "/api/contact/financeira/status");
    query.Headers.Add("Idempotency-Key", key);
    var response = await fixture.Client.SendAsync(query);
    Check.Equal(HttpStatusCode.OK, response.StatusCode);
    var content = await response.Content.ReadAsStringAsync();
    using var json = JsonDocument.Parse(content);
    Check.Equal("Accepted", json.RootElement.GetProperty("state").GetString()!);
    Check.Equal(3, json.RootElement.EnumerateObject().Count());
    Check.That(!content.Contains("cliente@") && !content.Contains("52998224725") && !content.Contains("fake-token"), "Consulta não expõe conteúdo nem token");
    Check.Equal(1, graph.Sends);
    using var wrongCompany = new HttpRequestMessage(HttpMethod.Get, "/api/contact/consorcio/status");
    wrongCompany.Headers.Add("Idempotency-Key", key);
    Check.Equal(HttpStatusCode.NotFound, (await fixture.Client.SendAsync(wrongCompany)).StatusCode);
    Check.Equal(HttpStatusCode.BadRequest, (await fixture.Client.GetAsync("/api/contact/financeira/status")).StatusCode);
});

await Test("Falha antes de enviar permite corrigir dados com a mesma chave e sem duplicidade", async () =>
{
    var graph = new GraphStub(HttpStatusCode.Accepted) { RejectToken = true };
    await using var fixture = await Fixture.Start("Production", graph.Configure);
    var payload = Fixture.Payload(await fixture.Challenge());
    var key = Guid.NewGuid().ToString();
    Check.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.Post(payload, key)).StatusCode);
    var before = fixture.Store.Find("financeira", key)!;
    var fresh = await fixture.Challenge();
    payload["challengeToken"] = fresh.Token;
    payload["challengeAnswer"] = Fixture.Answer(fresh.Question);
    payload["message"] = "Mensagem corrigida antes do envio.";
    graph.RejectToken = false;
    Check.Equal(HttpStatusCode.Accepted, (await fixture.Post(payload, key)).StatusCode);
    Check.Equal(before.Registration, fixture.Store.Find("financeira", key)!.Registration);
    Check.Equal(1, graph.Sends);
    Check.That(graph.SendBody!.Contains("Mensagem corrigida antes do envio."), "Envia somente o texto atualizado");
    payload["message"] = "Outra alteração depois de enviar.";
    Check.Equal(HttpStatusCode.Conflict, (await fixture.Post(payload, key)).StatusCode);
    Check.Equal(1, graph.Sends);
});

await Test("Edição de Pending invalida claim antigo e não altera tentativa já iniciada", async () =>
{
    await using var fixture = await Fixture.Start();
    var now = DateTimeOffset.UtcNow;
    var old = fixture.Store.CreateOrGet("financeira", Guid.NewGuid().ToString(), "old", "nonce-old", now.AddMinutes(10), now, "HML");
    var updated = fixture.Store.ReplacePending(old, "new", "nonce-new", now.AddMinutes(10));
    Check.That(!fixture.Store.TryStart(old, now), "Conteúdo antigo não pode iniciar o envio");
    Check.That(fixture.Store.TryStart(updated, now), "Conteúdo novo pode iniciar uma única vez");
    var conflict = false;
    try { fixture.Store.ReplacePending(updated, "third", "nonce-third", now.AddMinutes(10)); }
    catch (IdempotencyConflictException) { conflict = true; }
    Check.That(conflict, "Alteração concorrente após Sending é rejeitada");
});

await Test("Criptool: vetor conhecido e configuração inválida", async () =>
{
    const string key = "Ybp8od1Q7bUEyj02qj3WEqiJP5KXDnlF";
    const string iv = "8inqNW7q9sFDVIy9";
    const string encrypted = "cb/CKyFmKUGzHdPHzb7LgijIDAtVN8wA4gWAdcnpARYuDFetWmhpoMjPmGMTqqg0";
    Check.Equal("Texto original: segredo-ficticio-teste-123",
        Simpala.Site.Api.Configuration.MailSecretProtector.Unprotect(encrypted, key, iv));
    foreach (var invalid in new[] { (encrypted, "short", iv), (encrypted, key, "short"), ("invalid", key, iv), ("", key, iv), (encrypted, new string('é', 32), iv) })
    {
        var rejected = false;
        try { Simpala.Site.Api.Configuration.MailSecretProtector.Unprotect(invalid.Item1, invalid.Item2, invalid.Item3); }
        catch (InvalidOperationException) { rejected = true; }
        Check.That(rejected, "Configuração inválida deve ser rejeitada sem expor valores");
    }
    var previousKey = Environment.GetEnvironmentVariable("SupportMail__EncryptionKey");
    try
    {
        Environment.SetEnvironmentVariable("SupportMail__EncryptionKey", key);
        var graph = new GraphStub(HttpStatusCode.Accepted);
        await using var fixture = await Fixture.Start("Production", builder =>
        {
            graph.Configure(builder);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["SupportMail:ClientSecret"] = "",
                ["SupportMail:EncryptedClientSecret"] = encrypted,
                ["SupportMail:EncryptionIV"] = iv
            });
        });
        Check.Equal(HttpStatusCode.Accepted, (await fixture.Post(Fixture.Payload(await fixture.Challenge()))).StatusCode);
        Check.Equal(1, graph.Sends);
    }
    finally { Environment.SetEnvironmentVariable("SupportMail__EncryptionKey", previousKey); }
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
    public ContactStore Store => app.Services.GetRequiredService<ContactStore>();
    public static async Task<Fixture> Start(string environment = "Development", Action<WebApplicationBuilder>? configure = null)
    {
        var data = Path.Combine(Path.GetTempPath(), "simpala-api-test-" + Guid.NewGuid().ToString("N"));
        var secure = environment == "Production";
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
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Contact:DataDirectory"] = data,
                ["SupportMail:EncryptedClientSecret"] = "",
                ["SupportMail:EncryptionIV"] = "",
                ["EmailSettings:EnableSending"] = "false",
                ["Contact:EnablePublicAccess"] = "false"
            });
            builder.Logging.ClearProviders();
            configure?.Invoke(builder);
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

// Every HTTP request in these tests is intercepted. No credentials or Microsoft endpoints are contacted.
sealed class GraphStub(HttpStatusCode status) : HttpMessageHandler, IHttpClientFactory
{
    public int Tokens;
    public int Sends;
    public bool RejectToken;
    public bool Timeout;
    public bool Delay;
    public string? SendUrl;
    public string? SendBody;

    public void Configure(WebApplicationBuilder builder)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EmailSettings:EnableSending"] = "true",
            ["SupportMail:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["SupportMail:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["SupportMail:ClientSecret"] = "dummy-secret"
        });
        builder.Services.AddSingleton<IHttpClientFactory>(this);
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(2) };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "login.microsoftonline.com")
        {
            Interlocked.Increment(ref Tokens);
            return new HttpResponseMessage(RejectToken ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent(RejectToken ? "{}" : "{\"access_token\":\"fake-token\",\"expires_in\":3600}") };
        }
        Check.Equal("graph.microsoft.com", request.RequestUri.Host);
        Check.Equal("fake-token", request.Headers.Authorization!.Parameter!);
        Interlocked.Increment(ref Sends);
        SendUrl = request.RequestUri.AbsoluteUri;
        SendBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (Timeout) throw new TaskCanceledException("Simulated timeout");
        if (Delay) await Task.Delay(100, cancellationToken);
        return new HttpResponseMessage(status);
    }
}
