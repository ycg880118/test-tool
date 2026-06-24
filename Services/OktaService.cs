using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    public OktaService(IHttpClientFactory factory) => _factory = factory;

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
            return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
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
