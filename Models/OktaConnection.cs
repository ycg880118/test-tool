namespace OktaUserManager.Models;

/// <summary>
/// Holds the Okta org URL and API token used for every request.
/// </summary>
public class OktaConnection
{
    /// <summary>e.g. https://dev-12345.okta.com  (no trailing /api/v1)</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The SSWS API token from Okta (Security &gt; API &gt; Tokens).</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>When creating new users, whether to activate them immediately.</summary>
    public bool Activate { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiToken);
}
