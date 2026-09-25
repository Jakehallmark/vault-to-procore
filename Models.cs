using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VaultTransfer;

public sealed class Settings
{
    public string Mode { get; set; } = "Vault / Procore Drive (not connected)";
    public string SourceFolder { get; set; } = "";
    public string StagingFolder { get; set; } = "";
    public string DestinationFolder { get; set; } = "";
    public string ProjectPattern { get; set; } = @"(?<!\d)(\d{5,6})(?!\d)";
    public string[] Extensions { get; set; } =
    [
        ".pdf", ".rdb", ".sup", ".urs", ".usw", ".wset",
        ".zap14", ".zap15", ".zap15_1", ".zap16", ".zap17", ".zap18",
        ".s7s", ".cd3", ".cd31", ".cd32"
    ];
    public string[] ExcludePatterns { get; set; } = ["*/Archive/*"];
    public Dictionary<string, string> ProjectFolders { get; set; } = new();

    public const string LocalTest = "Local test folders";
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
}

public sealed class ReviewItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public string Project { get; set; } = "";
    public string Destination { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Decision { get; set; } = "Pending";
    public string Status { get; set; } = "Suggested";
    public string Reason { get; set; } = "";
    public List<string> History { get; set; } = [];
    public void Record(string text) => History.Add($"{DateTimeOffset.UtcNow:O} {text}");
}

public sealed class ScanReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public Settings Settings { get; set; } = new();
    public string SettingsFingerprint { get; set; } = "";
    public bool Complete { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<ReviewItem> Items { get; set; } = [];
}

public sealed class AppStore
{
    public string Root { get; }
    public string ReportsFolder => Path.Combine(Root, "reports");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(ReportsFolder);
    }

    public void SaveSettings(Settings settings) => WriteJson(Path.Combine(Root, "settings.json"), settings);
    public Settings LoadSettings() => File.Exists(Path.Combine(Root, "settings.json"))
        ? Read<Settings>(Path.Combine(Root, "settings.json")) : new Settings();
    public void Save(ScanReport report) => WriteJson(Path.Combine(ReportsFolder, report.Id + ".json"), report);
    public ScanReport? Latest()
    {
        var latest = new DirectoryInfo(ReportsFolder).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return latest is null ? null : Read<ScanReport>(latest.FullName);
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"Could not read {Path.GetFileName(path)}.");

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, value, JsonOptions);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ExportCsv(ScanReport report, string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        // Prefix text to prevent spreadsheet formula execution and preserve leading zeros.
        static string Cell(string text) => "\"'" + text.Replace("\"", "\"\"") + "\"";
        writer.WriteLine($"Mode,{Cell(report.Settings.Mode)},Scan complete,{report.Complete}");
        foreach (var error in report.Errors) writer.WriteLine("Scan error," + Cell(error));
        writer.WriteLine("Source,Project,Destination,Bytes,SHA256,Decision,Status,Reason");
        foreach (var item in report.Items)
            writer.WriteLine(string.Join(",", Cell(item.Source), Cell(item.Project), Cell(item.Destination),
                item.Bytes.ToString(), Cell(item.Sha256), Cell(item.Decision), Cell(item.Status), Cell(item.Reason)));
    }
}
