namespace OktaUserManager.Models;

public enum UpsertAction { Created, Updated, Skipped, Failed }

/// <summary>Outcome of processing a single CSV row against Okta.</summary>
public class UpsertResult
{
    public int RowNumber { get; set; }
    public string Identifier { get; set; } = "";
    public UpsertAction Action { get; set; }
    public string Message { get; set; } = "";
}
