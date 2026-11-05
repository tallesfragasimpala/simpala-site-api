using System.ComponentModel.DataAnnotations;

namespace Simpala.Site.Api.DTOs;

public sealed class ContactRequest
{
    [Required, StringLength(150, MinimumLength = 2)]
    public string Name { get; init; } = "";
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = "";
    [Required, StringLength(18)]
    public string Document { get; init; } = "";
    [Required, StringLength(20)]
    public string Phone { get; init; } = "";
    [Required, StringLength(1000)]
    public string Message { get; init; } = "";
    // Accepted for compatibility, intentionally unused and never treated as consent.
    [StringLength(20)]
    public string? ContactPreference { get; init; }
    [Required, StringLength(4096)]
    public string ChallengeToken { get; init; } = "";
    [Required, StringLength(10)]
    public string ChallengeAnswer { get; init; } = "";
    [StringLength(200)]
    public string? Website { get; init; }
}

public sealed record NormalizedContact(string Name, string Email, string Document, string Phone, string Message);
public sealed record EmailPreview(string From, string To, string Subject, string ContentType, string Encoding, string Body);
public sealed record ContactResponse(string Registration, string State, bool Simulation, string Message, EmailPreview Preview);
