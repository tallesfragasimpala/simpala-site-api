using System.Text.RegularExpressions;

namespace Simpala.Site.Api.Services;

public sealed class DocumentValidator
{
    public string Normalize(string value) => Regex.Replace(
        Regex.Replace(value.Trim(), "[a-z]", match => match.Value.ToUpperInvariant()), @"[.\-/]", "");

    public bool IsValid(string value)
    {
        var code = Normalize(value);
        if (!Regex.IsMatch(code, @"^(?:[0-9]{11}|[A-Z0-9]{12}[0-9]{2})$")) return false;
        if (code.All(char.IsAsciiDigit) && code.Distinct().Count() == 1) return false;
        int[] weights = code.Length == 11 ? [10, 9, 8, 7, 6, 5, 4, 3, 2] : [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        static int Digit(string text, int[] factors)
        {
            var sum = text.Select((character, index) => (character - 48) * factors[index]).Sum();
            var remainder = sum % 11;
            return remainder < 2 ? 0 : 11 - remainder;
        }
        var first = Digit(code[..^2], weights);
        var second = Digit(code[..^2] + first, [weights[0] + 1, .. weights]);
        return code.EndsWith($"{first}{second}", StringComparison.Ordinal);
    }

    public string Format(string code) => code.Length == 11
        ? $"{code[..3]}.{code[3..6]}.{code[6..9]}-{code[9..]}"
        : $"{code[..2]}.{code[2..5]}.{code[5..8]}/{code[8..12]}-{code[12..]}";
}
