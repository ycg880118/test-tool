using System.Text.Json;
using OktaUserManager.Models;

namespace OktaUserManager.Services;

/// <summary>
/// Loads, saves, auto-suggests and validates the CSV-to-Okta field mapping.
/// The mapping is persisted to mapping.json in the app's content root so it
/// survives restarts and can be edited inside the app.
/// </summary>
public class MappingService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public MappingService(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "mapping.json");
    }

    public string ConfigPath => _path;

    public MappingConfig Load()
    {
        if (!File.Exists(_path)) return new MappingConfig();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<MappingConfig>(json) ?? new MappingConfig();
        }
        catch
        {
            return new MappingConfig();
        }
    }

    public void Save(MappingConfig config)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(config, _json));
    }

    /// <summary>
    /// For any header not already mapped, guess the Okta field by matching on a
    /// normalized name (ignoring case, spaces, underscores, dashes).
    /// </summary>
    public void AutoMap(MappingConfig config, IEnumerable<string> headers,
        IEnumerable<OktaProfileField> fields)
    {
        var fieldList = fields.ToList();
        foreach (var header in headers)
        {
            if (config.Map.TryGetValue(header, out var existing) &&
                !string.IsNullOrEmpty(existing))
            {
                continue; // keep a mapping the user already set
            }

            var normalizedHeader = Normalize(header);
            var match = fieldList
                .FirstOrDefault(f => Normalize(f.Name) == normalizedHeader);

            config.Map[header] = match?.Name ?? "";
        }
    }

    public MappingValidation Validate(MappingConfig config, IEnumerable<string> headers,
        IEnumerable<string> requiredFieldNames)
    {
        var headerList = headers.ToList();
        var result = new MappingValidation();

        foreach (var header in headerList)
        {
            var target = config.Map.GetValueOrDefault(header, "");
            if (string.IsNullOrEmpty(target))
                result.UnmappedHeaders.Add(header);
        }

        var mappedTargets = headerList
            .Select(h => config.Map.GetValueOrDefault(h, ""))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var required in requiredFieldNames)
        {
            if (!mappedTargets.Contains(required))
                result.MissingRequiredFields.Add(required);
        }

        return result;
    }

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
