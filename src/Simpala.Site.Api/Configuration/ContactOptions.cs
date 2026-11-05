namespace Simpala.Site.Api.Configuration;

public sealed class ContactOptions
{
    // Local demonstration default; the business must approve retention before deployment.
    public int RetentionDays { get; set; } = 30;
}
