using System.Text.Json;
using System.Text.RegularExpressions;

namespace VaultTransfer;

public sealed record VaultFileRecord(string VaultPath, string FileId, int Version);
public sealed record ProcoreProjectRecord(string CompanyId, string ProjectId, string? Number, string Name, bool? Active);
public sealed record ProjectRow(string Number, string VaultFolder, string ProcoreId, string ProcoreName, string Active, string Match, int Files, int Pending, string LastChange);
public sealed record FileRow(string FileId, string ProjectNumber, string DocumentsPath, int Version, string Approval, string TransferStatus);
public sealed record ChangeRow(string When, string Project, string Kind, string Version, string Path);
public sealed record ScanRow(string Source, string Status, string Ended, string Summary);

public sealed class Catalog : IDisposable
{
    private static readonly Regex ProjectFile = new(
        @"^\$/Designs/Projects/\d{6}-\d{6}/(\d{6})(?=\D|$)([^/]*)/(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string filePath;
    private readonly object gate = new();
    private Store store = new();

    public Catalog(string folder)
    {
        Directory.CreateDirectory(folder);
        filePath = Path.Combine(folder, "catalog.json");
        if (File.Exists(filePath))
            store = JsonSerializer.Deserialize<Store>(File.ReadAllText(filePath), JsonOptions) ?? new Store();
    }

    public string GetSetting(string key, string fallback)
    {
        lock (gate) return store.Settings.TryGetValue(key, out var value) ? value : fallback;
    }

    public void SetSetting(string key, string value)
    {
        lock (gate) { store.Settings[key] = value; Save(); }
    }

    public long StartScan(string source)
    {
        lock (gate)
        {
            var scan = new ScanRecord { Id = store.Scans.Count + 1, Source = source, StartedUtc = Now(), Status = "Running" };
            store.Scans.Add(scan);
            Save();
            return scan.Id;
        }
    }

    public void FinishScan(long id, string status, string summary)
    {
        lock (gate)
        {
            var scan = store.Scans.Single(item => item.Id == id);
            scan.EndedUtc = Now();
            scan.Status = status;
            scan.Summary = summary;
            Save();
        }
    }

    public int ApplyVaultInventory(IReadOnlyList<VaultFileRecord> files, bool complete)
    {
        lock (gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var projects = new HashSet<string>(StringComparer.Ordinal);
            var changes = 0;
            foreach (var file in files)
            {
                if (!TrySplit(file.VaultPath, out var number, out var folder, out var documents)) continue;
                if (!seen.Add(file.FileId)) throw new InvalidDataException("Vault inventory contains the same file twice.");
                projects.Add(number);
                var folderPath = file.VaultPath[..(file.VaultPath.Length - documents.Length - 1)];
                EnsureProject(number, folderPath, folder);
                changes += UpsertFile(number, file.FileId, file.VaultPath, documents, file.Version, "Observed", "");
            }
            if (complete)
            {
                foreach (var existing in store.Files.Where(file => file.Missing == 0 && !seen.Contains(file.FileId)).ToArray())
                {
                    existing.Missing = 1;
                    existing.LastSeenUtc = Now();
                    AddChange(existing.FileId, existing.ProjectNumber, "FileRemoved", existing.Version, null, existing.VaultPath);
                    changes++;
                }
            }
            foreach (var number in projects) RefreshMatch(number);
            Save();
            return changes;
        }
    }

    public int ApplyProcoreProjects(IReadOnlyList<ProcoreProjectRecord> projects)
    {
        lock (gate)
        {
            var changes = 0;
            foreach (var group in projects.Where(project => !string.IsNullOrWhiteSpace(project.Number)).GroupBy(project => project.Number!))
            {
                var rows = group.ToArray();
                var project = EnsureProject(group.Key, "", "");
                if (rows.Length == 1)
                    changes += UpdateProcore(project, rows[0]);
                else
                {
                    var reviewName = "Needs review: " + rows.Length + " Procore projects";
                    var changed = project.ProcoreName != reviewName;
                    project.ProcoreCompanyId = rows[0].CompanyId;
                    project.ProcoreProjectId = "";
                    project.ProcoreName = reviewName;
                    project.ProcoreActive = null;
                    project.LastSeenUtc = Now();
                    if (changed)
                    {
                        AddChange("", group.Key, "NeedsReview", null, null, rows.Length + " Procore projects share this number");
                        changes++;
                    }
                }
                RefreshMatch(group.Key);
            }
            Save();
            return changes;
        }
    }

    public void ApplyStagedFiles(string projectNumber, IReadOnlyList<(string FileId, int Version, string DocumentsPath, string Sha256)> files)
    {
        lock (gate)
        {
            EnsureProject(projectNumber, "", "");
            foreach (var file in files)
            {
                var existing = store.Files.SingleOrDefault(item => item.FileId == file.FileId);
                if (existing is null)
                {
                    UpsertFile(projectNumber, file.FileId, "", file.DocumentsPath, file.Version, "Staged", file.Sha256);
                    continue;
                }
                if (!existing.ProjectNumber.Equals(projectNumber, StringComparison.Ordinal))
                    throw new InvalidDataException("A staged file belongs to a different project than this stage record.");
                existing.TransferStatus = "Staged";
                existing.Sha256 = file.Sha256;
                existing.DocumentsPath = file.DocumentsPath;
                existing.LastSeenUtc = Now();
            }
            Save();
        }
    }

    public void SetApproval(IReadOnlyList<string> fileIds, string decision)
    {
        if (decision is not ("Approved" or "Denied" or "Pending")) throw new InvalidDataException("Unknown approval.");
        if (fileIds.Count == 0) throw new InvalidDataException("Select at least one file.");
        lock (gate)
        {
            var chosen = fileIds.Select(id => store.Files.SingleOrDefault(file => file.FileId == id)
                ?? throw new InvalidDataException("That file is not in the list.")).ToArray();
            foreach (var file in chosen)
            {
                if (file.Missing == 1) throw new InvalidDataException(file.DocumentsPath + " is no longer in Vault.");
                var project = store.Projects.Single(item => item.Number == file.ProjectNumber);
                if (project.MatchStatus != "Exact" || project.ProcoreProjectId.Length == 0)
                    throw new InvalidDataException("Project " + project.Number + " is not matched to one Procore project.");
            }
            foreach (var file in chosen)
            {
                file.Approval = decision;
                AddChange(file.FileId, file.ProjectNumber, decision, null, file.Version, file.DocumentsPath);
            }
            Save();
        }
    }

    public IReadOnlyList<FileRow> Files(string? projectNumber, string? approval)
    {
        lock (gate)
        {
            return store.Files.Where(file => file.Missing == 0
                && (projectNumber is null || file.ProjectNumber == projectNumber)
                && (approval is null || file.Approval == approval))
                .OrderBy(file => file.ProjectNumber, StringComparer.Ordinal)
                .ThenBy(file => file.DocumentsPath, StringComparer.OrdinalIgnoreCase)
                .Select(file => new FileRow(file.FileId, file.ProjectNumber, file.DocumentsPath, file.Version, file.Approval, file.TransferStatus))
                .ToArray();
        }
    }

    public IReadOnlyList<ProjectRow> Projects()
    {
        lock (gate)
        {
            return store.Projects.OrderBy(project => project.Number, StringComparer.Ordinal).Select(project => new ProjectRow(
                project.Number, project.VaultFolderName, project.ProcoreProjectId, project.ProcoreName,
                project.ProcoreActive is null ? "" : project.ProcoreActive.Value ? "Active" : "Inactive",
                project.MatchStatus,
                store.Files.Count(file => file.ProjectNumber == project.Number && file.Missing == 0),
                store.Files.Count(file => file.ProjectNumber == project.Number && file.Missing == 0 && file.Approval == "Pending"),
                store.Changes.Where(change => change.ProjectNumber == project.Number).Select(change => change.ObservedUtc).LastOrDefault() ?? "")).ToArray();
        }
    }

    public IReadOnlyList<ChangeRow> Changes(int limit)
    {
        lock (gate)
        {
            return store.Changes.AsEnumerable().Reverse().Take(limit).Select(change =>
            {
                var shown = change.PreviousVersion is null ? change.Version?.ToString() ?? "" : change.PreviousVersion + " → " + change.Version;
                return new ChangeRow(change.ObservedUtc, change.ProjectNumber, change.Kind, shown, change.VaultPath);
            }).ToArray();
        }
    }

    public IReadOnlyList<ScanRow> Scans(int limit)
    {
        lock (gate) return store.Scans.AsEnumerable().Reverse().Take(limit)
            .Select(scan => new ScanRow(scan.Source, scan.Status, scan.EndedUtc ?? scan.StartedUtc, scan.Summary)).ToArray();
    }

    public static IReadOnlyList<VaultFileRecord> ReadVaultJsonl(string path)
    {
        var files = new List<VaultFileRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            files.Add(new VaultFileRecord(Required(root, "VaultPath"), Required(root, "FileId"), root.GetProperty("Version").GetInt32()));
        }
        return files;
    }

    public static IReadOnlyList<ProcoreProjectRecord> ReadProcoreProjects(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (!root.TryGetProperty("DetailsFinished", out var finished) || !finished.GetBoolean())
            throw new InvalidDataException("Procore project numbers are not available until a detail scan finishes.");
        var company = Required(root, "CompanyId");
        return root.GetProperty("Projects").EnumerateArray().Select(project => new ProcoreProjectRecord(
            company, Required(project, "ProjectId"), Optional(project, "Number"), Optional(project, "Name") ?? "",
            project.TryGetProperty("Active", out var active) && active.ValueKind == JsonValueKind.True ? true :
            project.TryGetProperty("Active", out active) && active.ValueKind == JsonValueKind.False ? false : null)).ToArray();
    }

    public static bool VaultScanFinished(string summaryPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(summaryPath));
        return json.RootElement.TryGetProperty("EnumerationFinished", out var finished) && finished.GetBoolean();
    }

    public void Dispose() { lock (gate) Save(); }

    private ProjectRecord EnsureProject(string number, string vaultPath, string folder)
    {
        var project = store.Projects.SingleOrDefault(item => item.Number == number);
        if (project is null)
        {
            project = new ProjectRecord { Number = number, MatchStatus = "Vault", FirstSeenUtc = Now(), LastSeenUtc = Now() };
            store.Projects.Add(project);
        }
        if (vaultPath.Length > 0) project.VaultPath = vaultPath;
        if (folder.Length > 0) project.VaultFolderName = folder;
        project.LastSeenUtc = Now();
        return project;
    }

    private int UpsertFile(string number, string fileId, string vaultPath, string documents, int version, string transferStatus, string sha)
    {
        var existing = store.Files.SingleOrDefault(item => item.FileId == fileId);
        if (existing is null)
        {
            store.Files.Add(new FileRecord
            {
                FileId = fileId, ProjectNumber = number, VaultPath = vaultPath, DocumentsPath = documents,
                Version = version, Sha256 = sha, TransferStatus = transferStatus, Approval = "Pending", FirstSeenUtc = Now(), LastSeenUtc = Now()
            });
            AddChange(fileId, number, "FileAdded", null, version, vaultPath);
            return 1;
        }
        if (!existing.ProjectNumber.Equals(number, StringComparison.Ordinal))
            throw new InvalidDataException("Vault file " + fileId + " is already tracked for a different project.");
        var kind = existing.Missing == 1 ? "FileAdded" : existing.Version == version ? "" : "VersionChanged";
        var status = kind == "VersionChanged" && existing.TransferStatus == "Staged" ? "Observed" : existing.TransferStatus == "Staged" ? "Staged" : transferStatus;
        if (vaultPath.Length > 0) existing.VaultPath = vaultPath;
        existing.DocumentsPath = documents;
        var previous = existing.Version;
        existing.Version = version;
        existing.TransferStatus = status;
        if (kind is "VersionChanged" or "FileAdded") existing.Approval = "Pending";
        existing.Missing = 0;
        existing.LastSeenUtc = Now();
        if (sha.Length > 0) existing.Sha256 = sha;
        if (kind.Length == 0) return 0;
        AddChange(fileId, number, kind, previous, version, vaultPath.Length > 0 ? vaultPath : existing.VaultPath);
        return 1;
    }

    private int UpdateProcore(ProjectRecord project, ProcoreProjectRecord incoming)
    {
        var previousId = project.ProcoreProjectId;
        var previousActive = project.ProcoreActive;
        project.ProcoreCompanyId = incoming.CompanyId;
        project.ProcoreProjectId = incoming.ProjectId;
        project.ProcoreName = incoming.Name;
        project.ProcoreActive = incoming.Active;
        project.LastSeenUtc = Now();
        if (previousId.Length > 0 && previousActive != incoming.Active)
        {
            AddChange("", project.Number, "ActiveChanged", null, null, incoming.Name);
            return 1;
        }
        if (previousId.Length == 0)
        {
            AddChange("", project.Number, "ProcoreLinked", null, null, incoming.ProjectId + " " + incoming.Name);
            return 1;
        }
        return 0;
    }

    private void RefreshMatch(string number)
    {
        var project = store.Projects.Single(item => item.Number == number);
        project.MatchStatus = project.ProcoreName.StartsWith("Needs review:", StringComparison.Ordinal) ? "Needs review"
            : project.VaultPath.Length > 0 && project.ProcoreProjectId.Length > 0 ? "Exact"
            : project.ProcoreProjectId.Length > 0 ? "Procore" : "Vault";
    }

    private void AddChange(string fileId, string number, string kind, int? previous, int? version, string path) =>
        store.Changes.Add(new ChangeRecord
        {
            FileId = fileId, ProjectNumber = number, ObservedUtc = Now(), Kind = kind,
            PreviousVersion = previous, Version = version, VaultPath = path
        });

    private static bool TrySplit(string vaultPath, out string number, out string folder, out string documents)
    {
        number = folder = documents = "";
        var match = ProjectFile.Match(vaultPath);
        if (!match.Success) return false;
        number = match.Groups[1].Value;
        folder = number + match.Groups[2].Value;
        documents = match.Groups[3].Value;
        return !documents.Equals(folder, StringComparison.OrdinalIgnoreCase)
            && !documents.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private void Save()
    {
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions));
        File.Move(temporary, filePath, true);
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
        : throw new InvalidDataException("Missing " + name + ".");
    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Store
    {
        public List<ProjectRecord> Projects { get; set; } = [];
        public List<FileRecord> Files { get; set; } = [];
        public List<ChangeRecord> Changes { get; set; } = [];
        public List<ScanRecord> Scans { get; set; } = [];
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    private sealed class ProjectRecord
    {
        public string Number { get; set; } = "";
        public string VaultPath { get; set; } = "";
        public string VaultFolderName { get; set; } = "";
        public string ProcoreCompanyId { get; set; } = "";
        public string ProcoreProjectId { get; set; } = "";
        public string ProcoreName { get; set; } = "";
        public bool? ProcoreActive { get; set; }
        public string MatchStatus { get; set; } = "";
        public string FirstSeenUtc { get; set; } = "";
        public string LastSeenUtc { get; set; } = "";
    }

    private sealed class FileRecord
    {
        public string FileId { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string VaultPath { get; set; } = "";
        public string DocumentsPath { get; set; } = "";
        public int Version { get; set; }
        public string Sha256 { get; set; } = "";
        public string TransferStatus { get; set; } = "";
        public string Approval { get; set; } = "Pending";
        public int Missing { get; set; }
        public string FirstSeenUtc { get; set; } = "";
        public string LastSeenUtc { get; set; } = "";
    }

    private sealed class ChangeRecord
    {
        public string FileId { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string ObservedUtc { get; set; } = "";
        public string Kind { get; set; } = "";
        public int? PreviousVersion { get; set; }
        public int? Version { get; set; }
        public string VaultPath { get; set; } = "";
    }

    private sealed class ScanRecord
    {
        public long Id { get; set; }
        public string Source { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string? EndedUtc { get; set; }
        public string Status { get; set; } = "";
        public string Summary { get; set; } = "";
    }
}

public sealed class ScanCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool IsRunning { get; private set; }

    public async Task<bool> TryRun(Func<CancellationToken, Task> scan, CancellationToken cancel = default)
    {
        if (!await gate.WaitAsync(0, cancel)) return false;
        try
        {
            IsRunning = true;
            await scan(cancel);
            return true;
        }
        finally
        {
            IsRunning = false;
            gate.Release();
        }
    }
}
