namespace Simpala.Site.Api.Configuration;

public sealed class MailOptions
{
    public bool EnableSending { get; set; }
}

public sealed class SupportMailOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string ClientSecretFile { get; set; } = "";
}
