using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Simpala.Site.Api.Configuration;
using Simpala.Site.Api.DTOs;
using Simpala.Site.Api.Persistence;
using Simpala.Site.Api.Services;

namespace Simpala.Site.Api.Controllers;

[ApiController]
[Route("api/contact")]
[EnableRateLimiting("contact")]
[RequestSizeLimit(16_384)]
public sealed class ContactController(ContactService service, ChallengeService challenges, DocumentValidator documents, IOptions<MailOptions> options, ContactStore store, TimeProvider clock) : ControllerBase
{
    [HttpGet("challenge")]
    public ActionResult<Challenge> GetChallenge() => Ok(challenges.Create());

    // The random UUID acts as a receipt secret; never put it in a URL or return personal data.
    [HttpGet("{company}/status")]
    [EnableRateLimiting("contact-status")]
    public IActionResult Status(string company)
    {
        if (company is not "financeira" and not "consorcio") return NotFound();
        if (!Guid.TryParseExact(Request.Headers["Idempotency-Key"].ToString(), "D", out var key)) return BadRequest();
        store.MarkStale(clock.GetUtcNow());
        var record = store.Find(company, key.ToString("D"));
        var mode = options.Value.EnableSending ? "Production" : "Simulation";
        if (record is null || record.Mode != mode) return NotFound();
        return Ok(new { record.Registration, State = record.State.ToString(), Simulation = record.Mode == "Simulation" });
    }

    [HttpPost("{company}")]
    [Consumes("application/json")]
    [ProducesResponseType<ContactResponse>(200)]
    [ProducesResponseType<ContactResponse>(202)]
    public async Task<ActionResult<ContactResponse>> Post(string company, [FromBody] ContactRequest request)
    {
        if (company is not "financeira" and not "consorcio") return NotFound();
        // A simulation client must never trigger a real send, even if server settings change.
        var requestedMode = Request.Headers["X-Contact-Mode"].ToString();
        var actualMode = options.Value.EnableSending ? "graph" : "simulation";
        if (requestedMode.Length > 0 && requestedMode != actualMode)
            return Conflict(new { message = "O modo do formulário não corresponde ao serviço de atendimento." });
        if (!Guid.TryParseExact(Request.Headers["Idempotency-Key"].ToString(), "D", out var key))
            return BadRequest(new { message = "Informe uma chave de idempotência válida." });
        var errors = ContactValidation.Validate(request, documents);
        if (errors.Count > 0) return BadRequest(new ValidationProblemDetails(errors));
        try
        {
            var response = await service.SubmitAsync(company, key.ToString("D"), request);
            var status = response.Simulation ? 200 : response.State switch
            {
                "Accepted" or "Sending" => 202,
                "Failed" => 502,
                _ => 409
            };
            return StatusCode(status, response);
        }
        catch (MailAuthenticationException)
        { return StatusCode(503, new { code = "mail_authentication_unavailable", message = "O serviço de atendimento está temporariamente indisponível. Tente novamente em instantes." }); }
        catch (IdempotencyConflictException)
        { return Conflict(new { message = "Esta chave já foi utilizada com outros dados." }); }
        catch (Exception exception) when (exception is InvalidChallengeException or ChallengeConsumedException)
        { return BadRequest(new { message = "Verificação inválida ou expirada. Solicite um novo desafio." }); }
    }
}
