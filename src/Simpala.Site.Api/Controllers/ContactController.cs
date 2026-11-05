using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Simpala.Site.Api.DTOs;
using Simpala.Site.Api.Persistence;
using Simpala.Site.Api.Services;

namespace Simpala.Site.Api.Controllers;

[ApiController]
[Route("api/contact")]
[EnableRateLimiting("contact")]
[RequestSizeLimit(16_384)]
public sealed class ContactController(ContactService service, ChallengeService challenges, DocumentValidator documents) : ControllerBase
{
    [HttpGet("challenge")]
    public ActionResult<Challenge> GetChallenge() => Ok(challenges.Create());

    [HttpPost("{company}")]
    [Consumes("application/json")]
    [ProducesResponseType<ContactResponse>(200)]
    public ActionResult<ContactResponse> Post(string company, [FromBody] ContactRequest request)
    {
        if (company is not "financeira" and not "consorcio") return NotFound();
        if (!Guid.TryParseExact(Request.Headers["Idempotency-Key"].ToString(), "D", out var key))
            return BadRequest(new { message = "Informe uma chave de idempotência válida." });
        var errors = ContactValidation.Validate(request, documents);
        if (errors.Count > 0) return BadRequest(new ValidationProblemDetails(errors));
        try { return Ok(service.Preview(company, key.ToString("D"), request)); }
        catch (IdempotencyConflictException)
        { return Conflict(new { message = "Esta chave já foi utilizada com outros dados." }); }
        catch (Exception exception) when (exception is InvalidChallengeException or ChallengeConsumedException)
        { return BadRequest(new { message = "Verificação inválida ou expirada. Solicite um novo desafio." }); }
    }
}
