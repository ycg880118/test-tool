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
            _logger.LogWarning("Test connection failed: HTTP {Status} from GET {Url}. Body: {Body}",
                (int)resp.StatusCode, hit, Trim(body));
            return (false,
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from GET {hit}: {Trim(body)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test connection threw for URL {Url}", conn.BaseUrl);
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
                var hit = resp.RequestMessage?.RequestUri?.ToString() ?? "(unknown URL)";
                var body = Trim(await resp.Content.ReadAsStringAsync());
                _logger.LogWarning("Load Okta fields failed: HTTP {Status} from GET {Url}. Body: {Body}",
                    (int)resp.StatusCode, hit, body);
                return (false, new(), $"HTTP {(int)resp.StatusCode}: {body}");
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

            _logger.LogInformation("Loaded {Count} Okta profile field(s) from schema.", fields.Count);
            return (true, fields, $"Loaded {fields.Count} field(s) from Okta.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load Okta fields threw for URL {Url}", conn.BaseUrl);
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

        _logger.LogInformation("Starting Okta import of {Count} row(s). CreateMissing={CreateMissing}.",
            profiles.Count, createMissing);

        var counts = new Dictionary<UpsertAction, int>();
        for (var i = 0; i < profiles.Count; i++)
        {
            var result = await UpsertOneAsync(client, conn, profiles[i], i + 1, createMissing);
            counts[result.Action] = counts.GetValueOrDefault(result.Action) + 1;
            await onResult(result);
        }

        _logger.LogInformation(
            "Okta import finished. Created={Created}, Updated={Updated}, Skipped={Skipped}, Failed={Failed}.",
            counts.GetValueOrDefault(UpsertAction.Created),
            counts.GetValueOrDefault(UpsertAction.Updated),
            counts.GetValueOrDefault(UpsertAction.Skipped),
            counts.GetValueOrDefault(UpsertAction.Failed));
    }

    private async Task<UpsertResult> UpsertOneAsync(
        HttpClient client, OktaConnection conn,
        Dictionary<string, string> profile, int rowNumber, bool createMissing)
    {
        var result = new UpsertResult { RowNumber = rowNumber };

        var login = profile.GetValueOrDefault("login");
        var email = profile.GetValueOrDefault("email");
        var hasLogin = !string.IsNullOrWhiteSpace(login);
        var hasEmail = !string.IsNullOrWhiteSpace(email);

        // Identify by login or email — at least one is required.
        result.Identifier = hasLogin ? login! : hasEmail ? email! : "(no login/email)";

        if (!hasLogin && !hasEmail)
        {
            result.Action = UpsertAction.Skipped;
            result.Message = "Row has no login or email value to identify the user.";
            return Finish(result, rowNumber);
        }

        try
        {
            var existingId = await FindUserIdAsync(client, login, email);

            if (existingId is not null)
            {
                // POST /users/{id} is a PARTIAL update: Okta only changes the profile
                // attributes present in the body and leaves every other attribute
                // untouched. The payload already contains only the selected, mapped,
                // non-blank fields, so unselected fields are never modified. (Do NOT
                // switch this to PUT, which replaces the whole profile.)
                var payload = JsonSerializer.Serialize(new { profile });
                var resp = await client.PostAsync(
                    $"api/v1/users/{existingId}",
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
            else
            {
                if (!createMissing)
                {
                    result.Action = UpsertAction.Skipped;
                    result.Message = "User does not exist and \"create new users\" is turned off.";
                    return Finish(result, rowNumber);
                }

                // Create requires first and last name in addition to login/email.
                if (string.IsNullOrWhiteSpace(profile.GetValueOrDefault("firstName")) ||
                    string.IsNullOrWhiteSpace(profile.GetValueOrDefault("lastName")))
                {
                    result.Action = UpsertAction.Failed;
                    result.Message = "Cannot create user: firstName and lastName are required.";
                    return Finish(result, rowNumber);
                }

                // Okta requires both login and email to create a user. If only one was
                // provided, reuse it for the other (they are usually identical).
                var createProfile = new Dictionary<string, string>(profile);
                if (string.IsNullOrWhiteSpace(createProfile.GetValueOrDefault("login")) && hasEmail)
                    createProfile["login"] = email!;
                if (string.IsNullOrWhiteSpace(createProfile.GetValueOrDefault("email")) && hasLogin)
                    createProfile["email"] = login!;

                var activate = conn.Activate ? "true" : "false";
                var payload = JsonSerializer.Serialize(new { profile = createProfile });
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
        }
        catch (Exception ex)
        {
            result.Action = UpsertAction.Failed;
            result.Message = ex.Message;
            _logger.LogError(ex, "Row {Row} ({User}) threw during upsert.",
                rowNumber, result.Identifier);
        }

        return Finish(result, rowNumber);
    }

    /// <summary>
    /// Finds an existing user's id by login or email. Login uses the strongly
    /// consistent GET /users/{login}; email uses a search query (since the path
    /// lookup only resolves id/login, not arbitrary email). Returns null if none.
    /// </summary>
    private async Task<string?> FindUserIdAsync(HttpClient client, string? login, string? email)
    {
        if (!string.IsNullOrWhiteSpace(login))
        {
            var byLogin = await client.GetAsync($"api/v1/users/{Uri.EscapeDataString(login)}");
            if (byLogin.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await byLogin.Content.ReadAsStringAsync());
                return doc.RootElement.GetProperty("id").GetString();
            }
        }

        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(email))
            clauses.Add($"profile.email eq \"{SearchEscape(email!)}\"");
        if (!string.IsNullOrWhiteSpace(login))
            clauses.Add($"profile.login eq \"{SearchEscape(login!)}\"");

        if (clauses.Count > 0)
        {
            var query = Uri.EscapeDataString(string.Join(" or ", clauses));
            var resp = await client.GetAsync($"api/v1/users?search={query}&limit=1");
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                    doc.RootElement.GetArrayLength() > 0)
                {
                    return doc.RootElement[0].GetProperty("id").GetString();
                }
            }
        }

        return null;
    }

    private UpsertResult Finish(UpsertResult result, int rowNumber)
    {
        if (result.Action == UpsertAction.Failed)
            _logger.LogWarning("Row {Row} ({User}) failed: {Message}",
                rowNumber, result.Identifier, result.Message);
        return result;
    }

    // Escape double quotes inside an Okta search filter string value.
    private static string SearchEscape(string value) => value.Replace("\"", "\\\"");

    private static string Trim(string body) =>
        body.Length > 400 ? body[..400] + "…" : body;
}
