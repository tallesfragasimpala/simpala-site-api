using System.Text;
using System.Text.RegularExpressions;
using Simpala.Site.Api.DTOs;

namespace Simpala.Site.Api.Services;

public static class ContactValidation
{
    public static Dictionary<string, string[]> Validate(ContactRequest request, DocumentValidator documents)
    {
        var errors = new Dictionary<string, string[]>();
        static bool UnsafeLine(string text) => text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');
        foreach (var (field, value) in new[] { ("name", request.Name), ("email", request.Email), ("document", request.Document), ("phone", request.Phone) })
            if (string.IsNullOrWhiteSpace(value) || UnsafeLine(value)) errors[field] = ["Confira o preenchimento deste campo."];
        if (request.Name.Trim().Length < 2) errors["name"] = ["Informe seu nome completo."];
        if (!Regex.IsMatch(request.Email.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
            errors["email"] = ["Informe um e-mail válido."];
        if (!documents.IsValid(request.Document)) errors["document"] = ["Informe um CPF ou CNPJ válido."];
        var phone = Regex.Replace(request.Phone, @"[()\- +]", "");
        if (!Regex.IsMatch(phone, @"^[1-9][0-9](?:[2-5][0-9]{7}|9[0-9]{8})$"))
            errors["phone"] = ["Informe um telefone brasileiro com DDD."];
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            errors["message"] = ["Informe uma mensagem válida."];
        if (!string.IsNullOrEmpty(request.Website)) errors["request"] = ["Não foi possível validar a solicitação."];
        return errors;
    }

    public static NormalizedContact Normalize(ContactRequest request, DocumentValidator documents) => new(
        request.Name.Trim().Normalize(NormalizationForm.FormC), request.Email.Trim().ToLowerInvariant(),
        documents.Normalize(request.Document), Regex.Replace(request.Phone, @"[()\- +]", ""),
        // Single line prevents user content from creating parser fields or changing the email layout.
        Regex.Replace(request.Message.Trim().Normalize(NormalizationForm.FormC), @"\s+", " "));
}
