using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using OktaUserManager.Models;

namespace OktaUserManager.Services;

/// <summary>Reads an uploaded CSV stream into headers + rows.</summary>
public class CsvService
{
    public async Task<CsvData> ParseAsync(Stream stream, string fileName)
    {
        var data = new CsvData { FileName = fileName };

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null, // don't throw on short rows
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
            DetectColumnCountChanges = false,
        };

        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();
        data.Headers = (csv.HeaderRecord ?? Array.Empty<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();

        while (await csv.ReadAsync())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in data.Headers)
            {
                row[header] = csv.GetField(header) ?? "";
            }
            data.Rows.Add(row);
        }

        return data;
    }
}
