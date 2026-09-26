using System.Xml.Linq;

namespace VaultTransfer;

internal static class VaultProfessionalLogin
{
    public static (string Server, string Database)? Read(string? path = null)
    {
        path ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Autodesk Vault Professional 2024", "ApplicationPreferences.xml");
        if (!File.Exists(path)) return null;
        var category = XDocument.Load(path).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Category" && (string?)element.Attribute("ID") == "Login");
        if (category is null) return null;
        var server = Property(category, "ServerName");
        var database = Property(category, "DatabaseName");
        if (server.Length == 0 || database.Length == 0) return null;
        var auth = Property(category, "SelectedAuthenticationType");
        if (auth.Length > 0 && auth != "1")
            throw new InvalidDataException("Vault Professional is not set to Windows authentication. Sign in there with Windows Account, then scan again.");
        return (server, database);
    }

    public static (string ClientFolder, string Server, string Vault)? ReadSecrets(string appDirectory)
    {
        var path = Path.Combine(appDirectory, ".secrets");
        if (!File.Exists(path)) return null;
        var section = false;
        var folder = "";
        var server = "";
        var vault = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                section = line.Equals("#Vault Client", StringComparison.Ordinal);
                continue;
            }
            if (!section) continue;
            var index = line.IndexOf(':');
            if (index < 1) throw new InvalidDataException("Invalid Vault client entry. Expected ClientFolder:, Server:, or Vault:.");
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (key == "ClientFolder") folder = value;
            else if (key == "Server") server = value;
            else if (key == "Vault") vault = value;
            else throw new InvalidDataException("Invalid Vault client entry. Expected ClientFolder:, Server:, or Vault:.");
        }
        if (folder.Length == 0 && server.Length == 0 && vault.Length == 0) return null;
        return (folder, server, vault);
    }

    private static string Property(XElement category, string name) =>
        category.Elements().FirstOrDefault(element => element.Name.LocalName == "Property" && (string?)element.Attribute("Name") == name)
            ?.Attribute("Value")?.Value.Trim() ?? "";
}
