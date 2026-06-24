namespace OktaUserManager.Models;

/// <summary>Parsed contents of an uploaded CSV file.</summary>
public class CsvData
{
    public string FileName { get; set; } = "";

    /// <summary>Column headers, in file order.</summary>
    public List<string> Headers { get; set; } = new();

    /// <summary>One dictionary per data row, keyed by header (case-insensitive).</summary>
    public List<Dictionary<string, string>> Rows { get; set; } = new();

    public int RowCount => Rows.Count;
}
