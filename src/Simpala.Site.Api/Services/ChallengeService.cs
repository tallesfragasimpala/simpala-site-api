using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Simpala.Site.Api.Services;

public sealed record Challenge(string Token, string Question, DateTimeOffset ExpiresAt);
public sealed record ChallengeProof(string Nonce, int Answer, DateTimeOffset ExpiresAt);

public sealed class ChallengeService(IDataProtectionProvider provider, TimeProvider clock)
{
    private readonly IDataProtector protector = provider.CreateProtector("contact-challenge-v1");

    public Challenge Create()
    {
        var first = RandomNumberGenerator.GetInt32(4, 20);
        var second = RandomNumberGenerator.GetInt32(4, 20);
        var proof = new ChallengeProof(Guid.NewGuid().ToString("N"), first + second, clock.GetUtcNow().AddMinutes(10));
        return new(protector.Protect(JsonSerializer.Serialize(proof)), $"Quanto é {first} + {second}?", proof.ExpiresAt);
    }

    public ChallengeProof? Validate(string token, string answer)
    {
        try
        {
            var proof = JsonSerializer.Deserialize<ChallengeProof>(protector.Unprotect(token));
            return proof is not null && proof.ExpiresAt > clock.GetUtcNow()
                && int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                && proof.Answer == result ? proof : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        { return null; }
    }
}
