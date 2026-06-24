using OktaUserManager.Models;

namespace OktaUserManager.Services;

/// <summary>
/// Per-circuit workflow state shared by the page across the connect → upload →
/// map → run steps.
/// </summary>
public class AppState
{
    public OktaConnection Connection { get; } = new();
    public CsvData? Csv { get; set; }
    public MappingConfig Mapping { get; set; } = new();

    /// <summary>CSV columns the user has chosen to ignore (not send to Okta).</summary>
    public HashSet<string> ExcludedColumns { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When a CSV row matches no existing Okta user, create them?</summary>
    public bool CreateMissing { get; set; } = true;

    /// <summary>
    /// Okta profile fields available as mapping targets. Defaults to a static
    /// list, replaced by the live schema once fetched from the API.
    /// </summary>
    public List<OktaProfileField> ProfileFields { get; set; } =
        OktaProfileFields.All.ToList();

    /// <summary>True once the list came from the Okta API rather than the fallback.</summary>
    public bool FieldsFromOkta { get; set; }

    public IEnumerable<string> RequiredFieldNames =>
        ProfileFields.Where(f => f.Required).Select(f => f.Name);

    public bool IsIncluded(string header) => !ExcludedColumns.Contains(header);
}
