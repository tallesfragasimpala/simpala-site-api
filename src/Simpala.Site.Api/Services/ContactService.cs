using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Simpala.Site.Api.Configuration;
using Simpala.Site.Api.DTOs;
using Simpala.Site.Api.Persistence;

namespace Simpala.Site.Api.Services;

public sealed class InvalidChallengeException : Exception { }

public sealed class ContactService(ContactStore store, ChallengeService challenges, DocumentValidator documents,
    EmailComposer composer, TimeProvider clock, IOptions<MailOptions> options,
    GraphMailClient graph, IHostApplicationLifetime lifetime)
{
    public async Task<ContactResponse> SubmitAsync(string company, string key, ContactRequest request)
    {
        var live = options.Value.EnableSending;
        var mode = live ? "Production" : "Simulation";
        var contact = ContactValidation.Normalize(request, documents);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(contact))));
        store.MarkStale(clock.GetUtcNow());
        var record = store.Find(company, key);
        if (record is not null)
        {
            if (record.Mode != mode) throw new IdempotencyConflictException();
            if (record.Fingerprint != hash)
            {
                if (!live || record.State != ContactState.Pending) throw new IdempotencyConflictException();
                var proof = challenges.Validate(request.ChallengeToken, request.ChallengeAnswer) ?? throw new InvalidChallengeException();
                record = store.ReplacePending(record, hash, proof.Nonce, proof.ExpiresAt);
            }
        }
        else
        {
            var proof = challenges.Validate(request.ChallengeToken, request.ChallengeAnswer) ?? throw new InvalidChallengeException();
            record = store.CreateOrGet(company, key, hash, proof.Nonce, proof.ExpiresAt, clock.GetUtcNow(), mode);
        }
        var preview = composer.Compose(company, record.Registration, record.CreatedAt, contact);
        if (!live) return new(record.Registration, record.State.ToString(), true,
            "Simulação local. Nenhum e-mail foi enviado e nenhum chamado foi criado.", preview);

        if (record.State == ContactState.Pending)
        {
            // Token failures happen before claiming: a retry with the same key remains safe.
            var token = await graph.GetTokenAsync(lifetime.ApplicationStopping);
            if (store.TryStart(record, clock.GetUtcNow()))
            {
                // A browser disconnect must not cancel an already committed mail attempt.
                var outcome = await graph.SendAsync(preview, token, lifetime.ApplicationStopping);
                store.Complete(record, outcome switch
                {
                    MailOutcome.Accepted => ContactState.Accepted,
                    MailOutcome.Failed => ContactState.Failed,
                    _ => ContactState.Unknown
                });
            }
        }
        record = store.Find(company, key)!;
        var message = record.State switch
        {
            ContactState.Accepted => "Solicitação aceita pelo serviço de e-mail. A entrega ainda não foi confirmada.",
            ContactState.Sending => "Solicitação em processamento. Consulte novamente usando os mesmos dados e a mesma chave.",
            ContactState.Failed => "O serviço de e-mail recusou a solicitação. Informe o protocolo ao atendimento.",
            _ => "Não foi possível confirmar o envio. Informe o protocolo ao atendimento; não faça um novo envio."
        };
        return new(record.Registration, record.State.ToString(), false, message, null);
    }
}
