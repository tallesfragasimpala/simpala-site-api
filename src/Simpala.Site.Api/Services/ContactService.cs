using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Simpala.Site.Api.DTOs;
using Simpala.Site.Api.Persistence;

namespace Simpala.Site.Api.Services;

public sealed class InvalidChallengeException : Exception { }

public sealed class ContactService(ContactStore store, ChallengeService challenges, DocumentValidator documents,
    EmailComposer composer, TimeProvider clock, IHostEnvironment environment)
{
    public ContactResponse Preview(string company, string key, ContactRequest request)
    {
        var contact = ContactValidation.Normalize(request, documents);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(contact))));
        var record = store.Find(company, key);
        if (record is not null)
        {
            if (record.Fingerprint != hash) throw new IdempotencyConflictException();
        }
        else
        {
            var proof = challenges.Validate(request.ChallengeToken, request.ChallengeAnswer) ?? throw new InvalidChallengeException();
            record = store.CreateOrGet(company, key, hash, proof.Nonce, proof.ExpiresAt, clock.GetUtcNow());
        }
        // This is a preview, never a queued mail. No sender or network client exists in this delivery.
        var preview = composer.Compose(company, record.Registration, record.CreatedAt, contact, environment.IsEnvironment("HML"));
        return new(record.Registration, record.State.ToString(), true,
            "Simulação local. Nenhum e-mail foi enviado e nenhum chamado foi criado.", preview);
    }
}
