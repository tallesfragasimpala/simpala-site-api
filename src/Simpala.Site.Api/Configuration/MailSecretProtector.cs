using System.Security.Cryptography;
using System.Text;

namespace Simpala.Site.Api.Configuration;

// Compatible with Criptool: raw UTF-8 key/IV, AES-256-CBC, PKCS7, Base64.
// This legacy format does not provide authenticated encryption.
public static class MailSecretProtector
{
    public static string Unprotect(string encryptedSecret, string key, string iv)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[]? plaintext = null;
        try
        {
            var ivBytes = Encoding.UTF8.GetBytes(iv);
            if (keyBytes.Length != 32 || ivBytes.Length != 16 || encryptedSecret.Length is < 1 or > 65536)
                throw new CryptographicException();
            var ciphertext = Convert.FromBase64String(encryptedSecret);
            if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0) throw new CryptographicException();
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            aes.IV = ivBytes;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            var secret = new UTF8Encoding(false, true).GetString(plaintext);
            if (string.IsNullOrWhiteSpace(secret)) throw new CryptographicException();
            return secret;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or DecoderFallbackException)
        {
            throw new InvalidOperationException("Não foi possível descriptografar o segredo. Confira a chave, o IV e o valor criptografado.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
