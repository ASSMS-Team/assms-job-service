namespace JobService.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; init; } = "assms-customer-asset-service";

    public string Audience { get; init; } = "assms-internal";

    public string SigningKey { get; init; } = string.Empty;
}
