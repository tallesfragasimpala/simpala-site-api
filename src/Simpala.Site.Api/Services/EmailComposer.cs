using System.Globalization;
using Simpala.Site.Api.DTOs;

namespace Simpala.Site.Api.Services;

public sealed class EmailComposer(DocumentValidator documents)
{
    public static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public EmailPreview Compose(string company, string registration, DateTimeOffset created, NormalizedContact contact)
    {
        var financial = company == "financeira";
        var mailbox = financial ? "faleconosco.financeira@simpala.com.br" : "faleconosco.consorcio@simpala.com.br";
        var area = financial ? "SIMPALA Financeira" : "SIMPALA Consórcios";
        var date = TimeZoneInfo.ConvertTime(created, SaoPaulo).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var phone = contact.Phone;
        var split = phone.Length == 11 ? 7 : 6;
        var formattedPhone = $"({phone[..2]}) {phone[2..split]}-{phone[split..]}";
        var body = string.Join("\r\n", new[]
        {
            $"Área de interesse {area} - {date}.", "",
            $"Nº Registro: {registration}", $"Nome: {contact.Name}", $"E-mail: {contact.Email}",
            $"CPF: {documents.Format(contact.Document)}", $"Telefone: {formattedPhone}", $"Mensagem: {contact.Message}",
        });
        return new(mailbox, mailbox, "Entre em contato", "text/plain", "UTF-8", body);
    }
}
