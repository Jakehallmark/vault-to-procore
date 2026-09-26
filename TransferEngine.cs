using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VaultTransfer;

public static class TransferEngine
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
    private static readonly HashSet<string> Actionable = ["Suggested", "Staged", "Failed"];

    public static string Hash(string path, CancellationToken cancel = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        cancel.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if ((Directory.Exists(current.FullName) || File.Exists(current.FullName)) &&
                (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked paths are not supported: {current.FullName}");
    }

    public static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new InvalidDataException("Destination mappings must be non-empty relative folder paths.");
        var parts = value.Replace('/', '\\').Split('\\');
        foreach (var part in parts)
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase, PatternTimeout))
                throw new InvalidDataException($"Invalid destination name: {part}");
        return Path.Combine(parts);
    }

    public static string Contained(string root, string relative)
    {
        root = Root(root);
        var path = Path.GetFullPath(Path.Combine(root, Relative(relative)));
        if (!Within(path, root)) throw new InvalidDataException("Destination escapes its configured folder.");
        NoLinks(path);
        return path;
    }

    public static void Validate(Settings settings)
    {
        if (settings.Mode != Settings.LocalTest)
            throw new InvalidDataException("Vault 2024 and Procore Drive are not connected yet. Use local test folders to verify the review workflow.");
        var values = new[] { settings.SourceFolder, settings.StagingFolder, settings.DestinationFolder };
        if (values.Any(v => string.IsNullOrWhiteSpace(v) || !Path.IsPathFullyQualified(v)))
            throw new InvalidDataException("Choose absolute source, staging, and test destination folders.");
        var roots = values.Select(Root).ToArray();
        foreach (var root in roots) NoLinks(root);
        if (!Directory.Exists(roots[0])) throw new InvalidDataException("The source folder does not exist.");
        if (roots.Skip(1).Any(File.Exists)) throw new InvalidDataException("Staging and destination must be folders.");
        for (var i = 0; i < roots.Length; i++)
            for (var j = i + 1; j < roots.Length; j++)
                if (Within(roots[i], roots[j]) || Within(roots[j], roots[i]))
                    throw new InvalidDataException("Source, staging, and destination must be separate, non-overlapping folders.");
        var pattern = new Regex(settings.ProjectPattern, RegexOptions.None, PatternTimeout);
        if (pattern.GetGroupNumbers().Length != 2)
            throw new InvalidDataException("Project pattern must have exactly one capture group for the project number.");
        if (settings.ProjectFolders.Count == 0) throw new InvalidDataException("Add at least one project mapping.");
        foreach (var mapping in settings.ProjectFolders)
        {
            if (string.IsNullOrWhiteSpace(mapping.Key)) throw new InvalidDataException("Project numbers cannot be empty.");
            Relative(mapping.Value);
        }
        if (settings.Extensions.Any(ext => !ext.StartsWith('.') || ext.Length < 2))
            throw new InvalidDataException("Extensions must start with a dot, such as .pdf.");
    }

    public static ScanReport Scan(Settings settings, CancellationToken cancel = default, IProgress<string>? progress = null)
    {
        Validate(settings);
        settings = settings.Clone();
        var report = new ScanReport { Settings = settings, SettingsFingerprint = settings.Fingerprint() };
        var pattern = new Regex(settings.ProjectPattern, RegexOptions.None, PatternTimeout);
        var exclusions = settings.ExcludePatterns.Select(p => new Regex("^" + Regex.Escape(p.Replace('\\', '/'))
            .Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase, PatternTimeout)).ToArray();
        var folders = new Stack<string>();
        folders.Push(Root(settings.SourceFolder));
        try
        {
            while (folders.TryPop(out var folder))
            {
                cancel.ThrowIfCancellationRequested();
                string[] entries;
                try { NoLinks(folder); entries = Directory.GetFileSystemEntries(folder); }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                { report.Errors.Add($"{folder}: {error.Message}"); continue; }
                foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        var attrs = File.GetAttributes(entry);
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        { report.Errors.Add($"Linked path was not scanned: {entry}"); continue; }
                        if ((attrs & FileAttributes.Directory) != 0) { folders.Push(entry); continue; }
                    }
                    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                    { report.Errors.Add($"{entry}: {error.Message}"); continue; }
                    var item = new ReviewItem { Source = Path.GetRelativePath(settings.SourceFolder, entry) };
                    report.Items.Add(item);
                    if (report.Items.Count % 25 == 0) progress?.Report($"Scanned {report.Items.Count:N0} files…");
                    try
                    {
                        item.Bytes = new FileInfo(entry).Length;
                        if (settings.Extensions.Length > 0 && !settings.Extensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
                        { item.Status = "Excluded"; item.Reason = "Extension is not included."; continue; }
                        if (exclusions.Any(p => p.IsMatch(item.Source.Replace('\\', '/'))))
                        { item.Status = "Excluded"; item.Reason = "Matched an excluded path pattern."; continue; }
                        var parts = item.Source.Split(Path.DirectorySeparatorChar);
                        var matches = parts.SkipLast(1).SelectMany((part, index) => pattern.Matches(part)
                            .Select(m => (Index: index, Number: m.Groups[1].Value))).ToArray();
                        var numbers = matches.Select(m => m.Number).Distinct().ToArray();
                        if (numbers.Length != 1) throw new InvalidDataException("No unique project number found in source folders.");
                        item.Project = numbers[0];
                        if (!settings.ProjectFolders.TryGetValue(item.Project, out var mapped))
                            throw new InvalidDataException("Project number has no destination mapping.");
                        var projectFolder = parts[matches[0].Index];
                        item.Destination = Relative(Path.Combine(mapped, Path.Combine(parts.Skip(matches[0].Index + 1).ToArray())));
                        var documentsRoot = item.Destination.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                        if (documentsRoot.Equals(projectFolder, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The Vault project folder is the Procore project and must not be created in Documents.");
                        if (item.Bytes == 0) throw new InvalidDataException("Empty files are excluded from transfer.");
                        item.Sha256 = Hash(entry, cancel);
                        foreach (var root in new[] { settings.StagingFolder, settings.DestinationFolder })
                        {
                            var existing = Contained(root, item.Destination);
                            if (Directory.Exists(existing) || (File.Exists(existing) && Hash(existing, cancel) != item.Sha256))
                                throw new InvalidDataException($"Existing destination conflicts; overwrite blocked: {existing}");
                        }
                        if (File.Exists(Contained(settings.DestinationFolder, item.Destination)))
                        { item.Status = "Unchanged"; item.Reason = "Identical file is already in the local test destination."; }
                        else item.Reason = $"Included extension; project mapping {item.Project}; preserve folders below project.";
                    }
                    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
                    {
                        item.Status = "Blocked"; item.Reason = error.Message;
                        if (error is IOException or UnauthorizedAccessException)
                            report.Errors.Add($"{item.Source}: {error.Message}");
                    }
                }
            }
            report.Complete = report.Errors.Count == 0;
        }
        catch (OperationCanceledException) { report.Errors.Add("Scan cancelled; results are incomplete."); }
        foreach (var group in report.Items.Where(i => i.Destination.Length > 0).GroupBy(i => i.Destination, StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1)
                foreach (var item in group) { item.Status = "Blocked"; item.Reason = "Multiple source files map to the same destination."; }
        return report;
    }

    public static void Decide(ScanReport report, HashSet<string> ids, string decision)
    {
        if (decision is not ("Approved" or "Denied" or "Pending")) throw new ArgumentException("Unknown decision.");
        if (!report.Complete) throw new InvalidDataException("Complete a fresh scan without scan errors before approving files.");
        foreach (var item in report.Items.Where(i => ids.Contains(i.Id) && Actionable.Contains(i.Status)))
        { item.Decision = decision; item.Record(decision); }
    }

    public static void CopyVerified(string source, string target, string expected, CancellationToken cancel = default)
    {
        NoLinks(source); NoLinks(target);
        if (File.Exists(target))
        {
            if (Hash(target, cancel) == expected) return;
            throw new InvalidDataException("Destination content conflicts; overwrite blocked.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = input.Read(buffer)) > 0)
                { cancel.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            if (Hash(temporary, cancel) != expected) throw new InvalidDataException("Source changed. Scan and review again.");
            cancel.ThrowIfCancellationRequested();
            NoLinks(target);
            File.Move(temporary, target, false);
            if (Hash(target) != expected) throw new InvalidDataException("Destination verification failed.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Transfer(ScanReport report, Settings settings, AppStore store,
        CancellationToken cancel = default, IProgress<string>? progress = null)
    {
        Validate(settings);
        if (!report.Complete || settings.Fingerprint() != report.SettingsFingerprint)
            throw new InvalidDataException("Settings changed or scan is incomplete. Scan and review again.");
        foreach (var item in report.Items.Where(i => i.Decision == "Approved" && Actionable.Contains(i.Status)))
        {
            if (cancel.IsCancellationRequested) break;
            try
            {
                var source = Contained(settings.SourceFolder, item.Source);
                if (Hash(source, cancel) != item.Sha256)
                {
                    item.Status = "Blocked"; item.Decision = "Pending";
                    throw new InvalidDataException("Source changed since approval. Scan and review again.");
                }
                progress?.Report($"Staging {item.Source}");
                var staged = Contained(settings.StagingFolder, item.Destination);
                CopyVerified(source, staged, item.Sha256, cancel);
                item.Status = "Staged"; item.Reason = "Verified local staging copy."; item.Record("Staged");
                store.Save(report);
                progress?.Report($"Copying to local test destination: {item.Destination}");
                CopyVerified(staged, Contained(settings.DestinationFolder, item.Destination), item.Sha256, cancel);
                item.Status = "Test copied"; item.Reason = "Both local copies verified. No Procore upload occurred.";
                item.Record("Test copied");
            }
            catch (OperationCanceledException)
            {
                item.Reason = "Cancelled. Completed staging is retained; retry approved files to continue.";
                item.Record("Cancelled"); store.Save(report); break;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (item.Status != "Blocked") item.Status = "Failed";
                item.Reason = error.Message; item.Record("Failed: " + error.Message);
            }
            store.Save(report);
        }
    }
}
