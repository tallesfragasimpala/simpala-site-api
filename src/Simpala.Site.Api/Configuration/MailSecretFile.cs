using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Simpala.Site.Api.Configuration;

// Encryption is bound to this Windows installation. ACLs restrict who can read the key ring.
public static class MailSecretFile
{
    private const string Application = "Simpala.Site.MailSecret";
    private static IDataProtectionProvider Provider(string directory, bool readOnly)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Segredo protegido exige Windows.");
        return WindowsProvider(directory, readOnly);
    }

    [SupportedOSPlatform("windows")]
    private static IDataProtectionProvider WindowsProvider(string directory, bool readOnly) =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "mail-keys")), builder =>
        {
            builder.SetApplicationName(Application).ProtectKeysWithDpapi(protectToLocalMachine: true);
            if (readOnly) builder.DisableAutomaticKeyGeneration();
        });

    public static void Save(string path, string secret)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Informe caminho absoluto.");
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Informe o segredo.");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var protector = Provider(directory, false).CreateProtector("client-secret-v1");
        // No plaintext temporary file is created.
        var encrypted = protector.Protect(secret);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(encrypted);
    }

    public static string Load(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new IOException();
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length is < 1 or > 65536) throw new CryptographicException();
            return Provider(Path.GetDirectoryName(path)!, true).CreateProtector("client-secret-v1").Unprotect(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        { throw new InvalidOperationException("Não foi possível ler o segredo protegido. Confira o servidor, as chaves e as permissões."); }
    }

    public static int ConfigureInteractive(string path)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use um terminal interativo.");
        Console.Write("Client secret (entrada oculta): ");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        try { Save(path, value.ToString()); }
        finally { value.Clear(); }
        Console.WriteLine("Segredo protegido neste servidor. Nenhum e-mail foi enviado.");
        return 0;
    }
}
