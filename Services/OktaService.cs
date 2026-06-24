using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OktaUserManager.Models;

namespace OktaUserManager.Services;

/// <summary>
/// Thin wrapper over the Okta Users API using a plain HttpClient.
/// Auth is the SSWS token header. We deliberately avoid the Okta SDK to keep
/// the request shape fully under our control.
/// </summary>
public class OktaService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<OktaService> _logger;

    public OktaService(IHttpClientFactory factory, ILogger<OktaService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    private HttpClient CreateClient(OktaConnection conn)
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri(conn.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("SSWS", conn.ApiToken);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Quick check that the URL + token work.</summary>
    public async Task<(bool ok, string message)> TestConnectionAsync(OktaConnection conn)
    {
        try
        {
            using var client = CreateClient(conn);
            var resp = await client.GetAsync("api/v1/users?limit=1");
            if (resp.IsSuccessStatusCode)
                return (true, "Connection successful.");

            var body = await resp.Content.ReadAsStringAsync();
            // Show the final URL actually hit (after any redirects) — a 405 here
            // usually means the org URL points at a non-API host (e.g. the
            // "-admin" console domain) rather than https://your-org.okta.com.
            var hit = resp.RequestMessage?.RequestUri?.ToString() ?? "(unknown URL)";
            return (false,
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from GET {hit}: {Trim(body)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Full request/response dump for troubleshooting (e.g. a 405). Sends the
    /// exact GET the app uses, with redirects DISABLED so any 3xx is visible,
    /// and reports every request/response header, the body, and exceptions.
    /// The token is masked. Also written to the server console log.
    /// </summary>
    public async Task<string> DiagnoseAsync(OktaConnection conn)
    {
        var sb = new StringBuilder();
        var token = (conn.ApiToken ?? "").Trim();
        var rawToken = conn.ApiToken ?? "";

        sb.AppendLine($"Time:            {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Configured URL:  '{conn.BaseUrl}'");

        var baseUrl = (conn.BaseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return sb.AppendLine("ERROR: Okta org URL is empty.").ToString();
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("WARNING: URL has no http(s):// scheme — add 'https://'.");
        if (baseUrl.Contains("-admin.", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("WARNING: URL contains '-admin' — the admin console host does NOT serve the API. Use https://your-org.okta.com.");

        var requestUrl = baseUrl.TrimEnd('/') + "/api/v1/users?limit=1";
        sb.AppendLine($"Request:         GET {requestUrl}");
        sb.AppendLine($"Token length:    {rawToken.Length}");
        if (rawToken != rawToken.Trim())
            sb.AppendLine("WARNING: token has leading/trailing whitespace (will be trimmed for this test).");
        sb.AppendLine($"Token (masked):  {Mask(token)}");

        // Disable auto-redirect so a 3xx (wrong host) shows up instead of being followed.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"SSWS {token}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        sb.AppendLine();
        sb.AppendLine("--- Request headers sent ---");
        foreach (var h in req.Headers)
        {
            var value = h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"SSWS {Mask(token)}"
                : string.Join(", ", h.Value);
            sb.AppendLine($"  {h.Key}: {value}");
        }

        try
        {
            using var resp = await client.SendAsync(req);

            sb.AppendLine();
            sb.AppendLine("--- Response ---");
            sb.AppendLine($"Status:          {(int)resp.StatusCode} {resp.ReasonPhrase}");

            sb.AppendLine("Response headers:");
            foreach (var h in resp.Headers)
                sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            foreach (var h in resp.Content.Headers)
                sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");

            if (resp.Content.Headers.Allow.Count > 0)
                sb.AppendLine($">>> Server allows methods: {string.Join(", ", resp.Content.Headers.Allow)}");

            if ((int)resp.StatusCode is >= 300 and < 400)
                sb.AppendLine($">>> REDIRECT to: {resp.Headers.Location} " +
                              "(the org URL is the wrong host — follow this or fix the URL).");

            var body = await resp.Content.ReadAsStringAsync();
            sb.AppendLine();
            sb.AppendLine("--- Response body (first 1000 chars) ---");
            sb.AppendLine(body.Length > 1000 ? body[..1000] + "…" : body);
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("--- Exception (no HTTP response received) ---");
            sb.AppendLine($"{ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                sb.AppendLine($"  Inner: {inner.GetType().Name}: {inner.Message}");
        }

        var report = sb.ToString();
        _logger.LogInformation("Okta connection diagnostics:\n{Report}", report);
        return report;
    }

    private static string Mask(string token)
    {
        if (string.IsNullOrEmpty(token)) return "(empty)";
        if (token.Length <= 8) return new string('*', token.Length);
        return token[..4] + new string('*', token.Length - 8) + token[^4..];
    }

    /// <summary>
    /// Fetches the org's user profile schema (base + custom attributes) so the
    /// mapping dropdowns reflect the real Okta fields instead of a static list.
    /// </summary>
    public async Task<(bool ok, List<OktaProfileField> fields, string message)>
        GetProfileFieldsAsync(OktaConnection conn)
    {
        try
        {
            using var client = CreateClient(conn);
            var resp = await client.GetAsync("api/v1/meta/schemas/user/default");
            if (!resp.IsSuccessStatusCode)
            {
                return (false, new(),
                    $"HTTP {(int)resp.StatusCode}: {Trim(await resp.Content.ReadAsStringAsync())}");
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var fields = new List<OktaProfileField>();

            if (doc.RootElement.TryGetProperty("definitions", out var defs))
            {
                // "base" holds the standard attributes, "custom" the org's custom ones.
                foreach (var section in new[] { "base", "custom" })
                {
                    if (!defs.TryGetProperty(section, out var sec)) continue;

                    var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (sec.TryGetProperty("required", out var reqArr) &&
                        reqArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in reqArr.EnumerateArray())
                            if (r.GetString() is { } name) required.Add(name);
                    }

                    if (sec.TryGetProperty("properties", out var props))
                    {
                        foreach (var prop in props.EnumerateObject())
                            fields.Add(new OktaProfileField(prop.Name, required.Contains(prop.Name)));
                    }
                }
            }

            if (fields.Count == 0)
                return (false, new(), "Schema returned no profile fields.");

            return (true, fields, $"Loaded {fields.Count} field(s) from Okta.");
        }
        catch (Exception ex)
        {
            return (false, new(), ex.Message);
        }
    }

    /// <summary>
    /// Processes every profile: looks the user up by login/email and either
    /// updates (partial POST) or creates them. Reports each result via callback
    /// so the UI can stream progress.
    /// </summary>
    public async Task ProcessAsync(
        OktaConnection conn,
        IReadOnlyList<Dictionary<string, string>> profiles,
        bool createMissing,
        Func<UpsertResult, Task> onResult)
    {
        using var client = CreateClient(conn);

        for (var i = 0; i < profiles.Count; i++)
        {
            var result = await UpsertOneAsync(client, conn, profiles[i], i + 1, createMissing);
            await onResult(result);
        }
    }

    private async Task<UpsertResult> UpsertOneAsync(
        HttpClient client, OktaConnection conn,
        Dictionary<string, string> profile, int rowNumber, bool createMissing)
    {
        var result = new UpsertResult { RowNumber = rowNumber };

        var identifier = profile.GetValueOrDefault("login")
                         ?? profile.GetValueOrDefault("email");
        result.Identifier = identifier ?? "(no login/email)";

        if (string.IsNullOrWhiteSpace(identifier))
        {
            result.Action = UpsertAction.Skipped;
            result.Message = "Row has no login or email value to identify the user.";
            return result;
        }

        try
        {
            var lookup = await client.GetAsync(
                $"api/v1/users/{Uri.EscapeDataString(identifier)}");

            var payload = JsonSerializer.Serialize(new { profile });

            if (lookup.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync());
                var id = doc.RootElement.GetProperty("id").GetString();

                // POST /users/{id} is a PARTIAL update: Okta only changes the profile
                // attributes present in the body and leaves every other attribute
                // untouched. The payload already contains only the selected, mapped,
                // non-blank fields, so unselected fields are never modified. (Do NOT
                // switch this to PUT, which replaces the whole profile.)
                var resp = await client.PostAsync(
                    $"api/v1/users/{id}",
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    result.Action = UpsertAction.Updated;
                    result.Message = "Updated existing user.";
                }
                else
                {
                    result.Action = UpsertAction.Failed;
                    result.Message = $"Update failed HTTP {(int)resp.StatusCode}: " +
                                     Trim(await resp.Content.ReadAsStringAsync());
                }
            }
            else if (lookup.StatusCode == HttpStatusCode.NotFound)
            {
                if (!createMissing)
                {
                    result.Action = UpsertAction.Skipped;
                    result.Message = "User does not exist and \"create new users\" is turned off.";
                    return result;
                }

                var activate = conn.Activate ? "true" : "false";
                var resp = await client.PostAsync(
                    $"api/v1/users?activate={activate}",
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                if (resp.IsSuccessStatusCode)
                {
                    result.Action = UpsertAction.Created;
                    result.Message = "Created new user.";
                }
                else
                {
                    result.Action = UpsertAction.Failed;
                    result.Message = $"Create failed HTTP {(int)resp.StatusCode}: " +
                                     Trim(await resp.Content.ReadAsStringAsync());
                }
            }
            else
            {
                result.Action = UpsertAction.Failed;
                result.Message = $"Lookup failed HTTP {(int)lookup.StatusCode}: " +
                                 Trim(await lookup.Content.ReadAsStringAsync());
            }
        }
        catch (Exception ex)
        {
            result.Action = UpsertAction.Failed;
            result.Message = ex.Message;
        }

        return result;
    }

    private static string Trim(string body) =>
        body.Length > 400 ? body[..400] + "…" : body;
}
