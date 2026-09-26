using System.Globalization;

namespace VaultTransfer;

public sealed class AppLog
{
    public const string FileName = "vault-transfer.log";
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private readonly Catalog catalog;
    private readonly object gate = new();
    private DateTimeOffset lastPrune = DateTimeOffset.MinValue;

    public AppLog(Catalog catalog, string folder)
    {
        this.catalog = catalog;
        FilePath = Path.Combine(folder, FileName);
        Prune();
    }

    public string FilePath { get; }

    public void Information(string source, string message) => Write("Information", source, message);

    public void Warning(string source, string message) => Write("Warning", source, message);

    public void Error(string source, string message) => Write("Error", source, message);

    public void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Keep;
        catalog.PruneLogs(cutoff);
        lock (gate)
        {
            if (!File.Exists(FilePath)) return;
            var lines = File.ReadAllLines(FilePath);
            var kept = lines.Where(line => Kept(line, cutoff)).ToArray();
            if (kept.Length != lines.Length) File.WriteAllLines(FilePath, kept);
        }
        lastPrune = DateTimeOffset.UtcNow;
    }

    private void Write(string level, string source, string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0) return;
        var when = DateTimeOffset.Now;
        catalog.AddLog(level, source, message, when);
        var line = when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + "\t" + level + "\t" + source + "\t" + message;
        lock (gate) File.AppendAllText(FilePath, line + Environment.NewLine);
        if (DateTimeOffset.UtcNow - lastPrune >= TimeSpan.FromHours(1)) Prune();
    }

    private static bool Kept(string line, DateTimeOffset cutoff)
    {
        if (line.Length < 19) return false;
        return DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            && new DateTimeOffset(local) >= cutoff;
    }
}
