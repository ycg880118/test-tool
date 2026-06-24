namespace OktaUserManager.Models;

/// <summary>
/// The editable, persisted mapping from CSV column headers to Okta profile
/// field names. An empty/absent value means "ignore this column".
/// </summary>
public class MappingConfig
{
    public Dictionary<string, string> Map { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Result of checking a mapping against an uploaded CSV.</summary>
public class MappingValidation
{
    /// <summary>CSV columns that have no Okta target (will be ignored).</summary>
    public List<string> UnmappedHeaders { get; set; } = new();

    /// <summary>Required Okta fields that no CSV column maps onto (blocks the run).</summary>
    public List<string> MissingRequiredFields { get; set; } = new();

    public bool IsValid => MissingRequiredFields.Count == 0;
}
