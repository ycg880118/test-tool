using System.Text.Json;
using OktaUserManager.Models;

namespace OktaUserManager.Services;

/// <summary>
/// Loads/saves the Okta connection (URL + token + activate flag) so the
/// selection is remembered between runs, and imports a connection from an
/// arbitrary JSON file the user picks.
/// </summary>
public class ConnectionService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ConnectionService(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "connection.json");
    }

    public string ConfigPath => _path;
    public bool HasSaved => File.Exists(_path);

    /// <summary>The remembered connection, or null if none saved yet.</summary>
    public OktaConnection? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            return JsonSerializer.Deserialize<OktaConnection>(File.ReadAllText(_path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse a connection from raw JSON (e.g. an uploaded file).</summary>
    public OktaConnection? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OktaConnection>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(OktaConnection conn) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(conn, _json));

    public void Forget()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
