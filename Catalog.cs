using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VaultTransfer;

public sealed record VaultFileRecord(string VaultPath, string FileId, int Version);
public sealed record ProcoreProjectRecord(string CompanyId, string ProjectId, string? Number, string Name, bool? Active, string? UpdatedAt = null);
public sealed record ProjectRow(string Number, string VaultFolder, string ProcoreId, string ProcoreName, string Active, string Match, int Files, int Pending, string LastChange, string Approval);
public sealed record FileRow(string FileId, string ProjectNumber, string DocumentsPath, int Version, string Approval, string TransferStatus);
public sealed record ChangeRow(string When, string Project, string Kind, string Version, string Path);
public sealed record ScanRow(string Source, string Status, string Ended, string Summary);
public sealed record MatchCounts(int Matched, int VaultOnly, int ProcoreOnly, int NeedsReview);

public sealed class Catalog : IDisposable
{
    public const string DatabaseFileName = "vault-transfer.db";
    private static readonly Regex ProjectFile = new(
        @"^\$/Designs/Projects/\d{6}-\d{6}/(\d{6})(?=\D|$)([^/]*)/(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object gate = new();
    private readonly SqliteConnection db;

    public Catalog(string folder) : this(folder, LegacyCatalogFile()) { }

    internal Catalog(string folder, string? legacyCatalogPath)
    {
        Directory.CreateDirectory(folder);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, DatabaseFileName),
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        db = new SqliteConnection(builder.ConnectionString);
        db.Open();
        Sql("PRAGMA journal_mode=WAL;");
        Sql("""
            CREATE TABLE IF NOT EXISTS settings (
              key TEXT PRIMARY KEY NOT NULL,
              value TEXT NOT NULL
            )
            """);
        Sql("""
            CREATE TABLE IF NOT EXISTS projects (
              number TEXT PRIMARY KEY NOT NULL,
              vault_path TEXT NOT NULL DEFAULT '',
              vault_folder TEXT NOT NULL DEFAULT '',
              procore_company_id TEXT NOT NULL DEFAULT '',
              procore_project_id TEXT NOT NULL DEFAULT '',
              procore_name TEXT NOT NULL DEFAULT '',
              procore_active INTEGER,
              match_status TEXT NOT NULL DEFAULT '',
              approval TEXT NOT NULL DEFAULT '',
              updated_at TEXT NOT NULL DEFAULT '',
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL
            )
            """);
        Sql("""
            CREATE TABLE IF NOT EXISTS files (
              file_id TEXT PRIMARY KEY NOT NULL,
              project_number TEXT NOT NULL,
              vault_path TEXT NOT NULL DEFAULT '',
              documents_path TEXT NOT NULL DEFAULT '',
              version INTEGER NOT NULL,
              sha256 TEXT NOT NULL DEFAULT '',
              transfer_status TEXT NOT NULL DEFAULT '',
              approval TEXT NOT NULL DEFAULT 'Pending',
              missing INTEGER NOT NULL DEFAULT 0,
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL
            )
            """);
        Sql("""
            CREATE TABLE IF NOT EXISTS changes (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              file_id TEXT NOT NULL DEFAULT '',
              project_number TEXT NOT NULL,
              observed_utc TEXT NOT NULL,
              kind TEXT NOT NULL,
              previous_version INTEGER,
              version INTEGER,
              vault_path TEXT NOT NULL DEFAULT ''
            )
            """);
        Sql("""
            CREATE TABLE IF NOT EXISTS scans (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              source TEXT NOT NULL,
              started_utc TEXT NOT NULL,
              ended_utc TEXT,
              status TEXT NOT NULL,
              summary TEXT NOT NULL DEFAULT ''
            )
            """);
        Sql("""
            CREATE TABLE IF NOT EXISTS procore_seen (
              project_id TEXT PRIMARY KEY NOT NULL,
              updated_at TEXT NOT NULL DEFAULT '',
              number TEXT NOT NULL DEFAULT '',
              name TEXT NOT NULL DEFAULT '',
              active INTEGER
            )
            """);
        Sql("CREATE INDEX IF NOT EXISTS files_project ON files(project_number)");
        Sql("CREATE INDEX IF NOT EXISTS changes_project ON changes(project_number)");
        if (!string.IsNullOrEmpty(legacyCatalogPath)) ImportLegacy(legacyCatalogPath);
    }

    public string GetSetting(string key, string fallback)
    {
        lock (gate)
        {
            using var command = Command("SELECT value FROM settings WHERE key = $key");
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string ?? fallback;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (gate)
        {
            Sql("""
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """, ("$key", key), ("$value", value));
        }
    }

    public long StartScan(string source)
    {
        lock (gate)
        {
            Sql("INSERT INTO scans (source, started_utc, status, summary) VALUES ($source, $started, 'Running', '')",
                ("$source", source), ("$started", Now()));
            return LastId();
        }
    }

    public void FinishScan(long id, string status, string summary)
    {
        lock (gate)
        {
            var rows = Sql("UPDATE scans SET ended_utc = $ended, status = $status, summary = $summary WHERE id = $id",
                ("$ended", Now()), ("$status", status), ("$summary", summary), ("$id", id));
            if (rows != 1) throw new InvalidDataException("That scan is not in the list.");
        }
    }

    public int ApplyVaultProjects(IReadOnlyList<string> folderPaths)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in folderPaths)
            {
                if (!TrySplitProject(path, out var number, out var folder)) continue;
                if (!seen.Add(number)) throw new InvalidDataException("Vault returned the same project number twice.");
                EnsureProject(number, path, folder);
                RefreshMatch(number);
            }
            transaction.Commit();
            return seen.Count;
        }
    }

    public int ApplyVaultInventory(IReadOnlyList<VaultFileRecord> files, bool complete)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
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
                foreach (var existing in CurrentFiles().Where(file => !seen.Contains(file.FileId)))
                {
                    Sql("UPDATE files SET missing = 1, last_seen = $seen WHERE file_id = $id",
                        ("$seen", Now()), ("$id", existing.FileId));
                    AddChange(existing.FileId, existing.ProjectNumber, "FileRemoved", existing.Version, null, existing.VaultPath);
                    changes++;
                }
            }
            foreach (var number in projects) RefreshMatch(number);
            transaction.Commit();
            return changes;
        }
    }

    public IReadOnlySet<string> SeenProcoreProjectIds()
    {
        lock (gate)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            using var command = Command("SELECT project_id FROM procore_seen");
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return ids;
        }
    }

    public bool ProcoreUnchanged(string projectId, string? updatedAt, bool comparable, string? number, string? name, bool? active)
    {
        lock (gate)
        {
            var seen = FindSeen(projectId);
            if (seen is null) return false;
            if (!string.IsNullOrEmpty(updatedAt) && seen.UpdatedAt.Length > 0)
                return seen.UpdatedAt == updatedAt;
            if (comparable && seen.Number == (number ?? "") && seen.Name == (name ?? "") && seen.Active == active)
            {
                if (!string.IsNullOrEmpty(updatedAt))
                    Sql("UPDATE procore_seen SET updated_at = $updated WHERE project_id = $id",
                        ("$updated", updatedAt), ("$id", projectId));
                return true;
            }
            return false;
        }
    }

    public void SaveProcoreProject(ProcoreProjectRecord incoming)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            RememberProcore(incoming);
            var number = string.IsNullOrWhiteSpace(incoming.Number) ? incoming.ProjectId : incoming.Number;
            var project = EnsureProject(number, "", "");
            if (project.ProcoreName.StartsWith("Needs review:", StringComparison.Ordinal)
                || (project.ProcoreProjectId.Length > 0 && project.ProcoreProjectId != incoming.ProjectId))
            {
                if (!project.ProcoreName.StartsWith("Needs review:", StringComparison.Ordinal))
                {
                    project.ProcoreName = "Needs review: more than one Procore project";
                    project.ProcoreProjectId = "";
                    project.ProcoreActive = null;
                    AddChange("", number, "NeedsReview", null, null, "More than one Procore project uses " + number);
                }
                project.LastSeenUtc = Now();
                RefreshMatch(project);
                transaction.Commit();
                return;
            }
            UpdateProcore(project, incoming);
            RefreshMatch(project);
            transaction.Commit();
        }
    }

    public void SetProjectApproval(IReadOnlyList<string> numbers, string decision)
    {
        if (decision is not ("Approved" or "Denied" or "Pending")) throw new InvalidDataException("Unknown approval.");
        if (numbers.Count == 0) throw new InvalidDataException("Select at least one project.");
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            var chosen = numbers.Select(number => FindProject(number) ?? throw new InvalidDataException("That project is not in the list.")).ToArray();
            foreach (var project in chosen)
            {
                project.Approval = decision;
                AddChange("", project.Number, decision, null, null, project.ProcoreName);
                WriteProject(project);
            }
            transaction.Commit();
        }
    }

    public int ApplyProcoreProjects(IReadOnlyList<ProcoreProjectRecord> projects)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            var changes = 0;
            foreach (var project in projects) RememberProcore(project);
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
                RefreshMatch(project);
            }
            transaction.Commit();
            return changes;
        }
    }

    public void ApplyStagedFiles(string projectNumber, IReadOnlyList<(string FileId, int Version, string DocumentsPath, string Sha256)> files)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            EnsureProject(projectNumber, "", "");
            foreach (var file in files)
            {
                var existing = FindFile(file.FileId);
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
                WriteFile(existing);
            }
            transaction.Commit();
        }
    }

    public void SetApproval(IReadOnlyList<string> fileIds, string decision)
    {
        if (decision is not ("Approved" or "Denied" or "Pending")) throw new InvalidDataException("Unknown approval.");
        if (fileIds.Count == 0) throw new InvalidDataException("Select at least one file.");
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            var chosen = fileIds.Select(id => FindFile(id) ?? throw new InvalidDataException("That file is not in the list.")).ToArray();
            foreach (var file in chosen)
            {
                if (file.Missing == 1) throw new InvalidDataException(file.DocumentsPath + " is no longer in Vault.");
                var project = FindProject(file.ProjectNumber) ?? throw new InvalidDataException("That project is not in the list.");
                if (project.MatchStatus != "Exact" || project.ProcoreProjectId.Length == 0)
                    throw new InvalidDataException("Project " + project.Number + " is not matched to one Procore project.");
            }
            foreach (var file in chosen)
            {
                file.Approval = decision;
                WriteFile(file);
                AddChange(file.FileId, file.ProjectNumber, decision, null, file.Version, file.DocumentsPath);
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<FileRow> Files(string? projectNumber, string? approval)
    {
        lock (gate)
        {
            using var command = Command("""
                SELECT file_id, project_number, documents_path, version, approval, transfer_status
                FROM files
                WHERE missing = 0
                  AND ($project IS NULL OR project_number = $project)
                  AND ($approval IS NULL OR approval = $approval)
                ORDER BY project_number, documents_path COLLATE NOCASE
                """);
            command.Parameters.AddWithValue("$project", (object?)projectNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$approval", (object?)approval ?? DBNull.Value);
            var rows = new List<FileRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new FileRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
            return rows;
        }
    }

    public IReadOnlyList<ProjectRow> Projects()
    {
        lock (gate)
        {
            using var command = Command("""
                SELECT number, vault_folder, procore_project_id, procore_name, procore_active, match_status, approval,
                  (SELECT COUNT(*) FROM files WHERE project_number = projects.number AND missing = 0),
                  (SELECT COUNT(*) FROM files WHERE project_number = projects.number AND missing = 0 AND approval = 'Pending'),
                  (SELECT observed_utc FROM changes WHERE project_number = projects.number ORDER BY id DESC LIMIT 1)
                FROM projects
                ORDER BY number
                """);
            var rows = new List<ProjectRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var active = reader.IsDBNull(4) ? "" : reader.GetInt64(4) == 0 ? "Inactive" : "Active";
                rows.Add(new ProjectRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), active,
                    reader.GetString(5), reader.GetInt32(7), reader.GetInt32(8),
                    reader.IsDBNull(9) ? "" : reader.GetString(9), reader.GetString(6)));
            }
            return rows;
        }
    }

    public IReadOnlyList<ChangeRow> Changes(int limit)
    {
        lock (gate)
        {
            using var command = Command("""
                SELECT observed_utc, project_number, kind, previous_version, version, vault_path
                FROM changes ORDER BY id DESC LIMIT $limit
                """);
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<ChangeRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var previous = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                var version = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
                var shown = previous is null ? version?.ToString() ?? "" : previous + " → " + version;
                rows.Add(new ChangeRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), shown, reader.GetString(5)));
            }
            return rows;
        }
    }

    public MatchCounts MatchCounts()
    {
        lock (gate)
        {
            var matched = 0;
            var vaultOnly = 0;
            var procoreOnly = 0;
            var needsReview = 0;
            using var command = Command("SELECT match_status, COUNT(*) FROM projects GROUP BY match_status");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var count = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
                switch (reader.GetString(0))
                {
                    case "Exact": matched = count; break;
                    case "Vault": vaultOnly = count; break;
                    case "Procore": procoreOnly = count; break;
                    case "Needs review": needsReview = count; break;
                }
            }
            return new MatchCounts(matched, vaultOnly, procoreOnly, needsReview);
        }
    }

    public bool HasSucceededScan(string source)
    {
        lock (gate)
        {
            using var command = Command("SELECT 1 FROM scans WHERE source = $source AND status = 'Succeeded' LIMIT 1");
            command.Parameters.AddWithValue("$source", source);
            return command.ExecuteScalar() is not null;
        }
    }

    public IReadOnlyList<ScanRow> Scans(int limit)
    {
        lock (gate)
        {
            using var command = Command("SELECT source, status, ended_utc, started_utc, summary FROM scans ORDER BY id DESC LIMIT $limit");
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<ScanRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new ScanRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? reader.GetString(3) : reader.GetString(2), reader.GetString(4)));
            return rows;
        }
    }

    public bool VaultRescanIsDue(DateTimeOffset now)
    {
        var fullAt = GetSetting("VaultFullScanUtc", "");
        return !DateTimeOffset.TryParse(fullAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fullStamp)
            || fullStamp <= now.AddDays(-7);
    }

    public static IReadOnlyList<string> ReadVaultProjects(string path)
    {
        var folders = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            folders.Add(Required(json.RootElement, "VaultPath"));
        }
        return folders;
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
            project.TryGetProperty("Active", out active) && active.ValueKind == JsonValueKind.False ? false : null,
            Optional(project, "UpdatedAt"))).ToArray();
    }

    public static bool VaultScanFinished(string summaryPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(summaryPath));
        return json.RootElement.TryGetProperty("EnumerationFinished", out var finished) && finished.GetBoolean();
    }

    public static bool VaultScanRemovesMissing(string summaryPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(summaryPath));
        var root = json.RootElement;
        if (!root.TryGetProperty("Complete", out var complete)) return true;
        if (complete.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new InvalidDataException("Vault scan summary is missing Complete.");
        return complete.GetBoolean();
    }

    public static string VaultScanStarted(string summaryPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(summaryPath));
        return json.RootElement.TryGetProperty("StartedUtc", out var started) && started.ValueKind == JsonValueKind.String
            ? started.GetString() ?? "" : "";
    }

    public static string VaultScanMode(string summaryPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(summaryPath));
        return json.RootElement.TryGetProperty("ScanMode", out var mode) && mode.ValueKind == JsonValueKind.String
            ? mode.GetString() ?? "Full" : "Full";
    }

    public void Dispose()
    {
        lock (gate) db.Dispose();
    }

    private ProjectRecord EnsureProject(string number, string vaultPath, string folder)
    {
        var project = FindProject(number);
        if (project is null)
        {
            project = new ProjectRecord { Number = number, MatchStatus = "Vault", FirstSeenUtc = Now(), LastSeenUtc = Now() };
        }
        if (vaultPath.Length > 0) project.VaultPath = vaultPath;
        if (folder.Length > 0) project.VaultFolderName = folder;
        project.LastSeenUtc = Now();
        WriteProject(project);
        return project;
    }

    private int UpsertFile(string number, string fileId, string vaultPath, string documents, int version, string transferStatus, string sha)
    {
        var existing = FindFile(fileId);
        if (existing is null)
        {
            WriteFile(new FileRecord
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
        WriteFile(existing);
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
        if (!string.IsNullOrEmpty(incoming.UpdatedAt)) project.UpdatedAt = incoming.UpdatedAt;
        project.LastSeenUtc = Now();
        if (previousId.Length > 0 && previousActive != incoming.Active)
        {
            AddChange("", project.Number, "ActiveChanged", null, null, incoming.Name);
            return 1;
        }
        if (previousId.Length == 0)
        {
            if (project.Approval.Length == 0) project.Approval = "Pending";
            AddChange("", project.Number, "ProcoreLinked", null, null, incoming.ProjectId + " " + incoming.Name);
            return 1;
        }
        return 0;
    }

    private void RefreshMatch(string number) => RefreshMatch(FindProject(number) ?? throw new InvalidDataException("That project is not in the list."));

    private void RefreshMatch(ProjectRecord project)
    {
        project.MatchStatus = project.ProcoreName.StartsWith("Needs review:", StringComparison.Ordinal) ? "Needs review"
            : project.VaultPath.Length > 0 && project.ProcoreProjectId.Length > 0 ? "Exact"
            : project.ProcoreProjectId.Length > 0 ? "Procore" : "Vault";
        WriteProject(project);
    }

    private void RememberProcore(ProcoreProjectRecord incoming)
    {
        if (incoming.ProjectId.Length == 0) return;
        var number = incoming.Number ?? "";
        Sql("""
            INSERT INTO procore_seen (project_id, updated_at, number, name, active)
            VALUES ($id, $updated, $number, $name, $active)
            ON CONFLICT(project_id) DO UPDATE SET
              updated_at = CASE WHEN excluded.updated_at = '' THEN procore_seen.updated_at ELSE excluded.updated_at END,
              number = excluded.number,
              name = excluded.name,
              active = excluded.active
            """,
            ("$id", incoming.ProjectId),
            ("$updated", incoming.UpdatedAt ?? ""),
            ("$number", number),
            ("$name", incoming.Name ?? ""),
            ("$active", DbBool(incoming.Active)));
    }

    private void AddChange(string fileId, string number, string kind, int? previous, int? version, string path) =>
        Sql("""
            INSERT INTO changes (file_id, project_number, observed_utc, kind, previous_version, version, vault_path)
            VALUES ($file, $project, $when, $kind, $previous, $version, $path)
            """,
            ("$file", fileId), ("$project", number), ("$when", Now()), ("$kind", kind),
            ("$previous", previous), ("$version", version), ("$path", path));

    private static readonly Regex ProjectFolder = new(
        @"^\$/Designs/Projects/\d{6}-\d{6}/(\d{6})(?=\D|$)([^/]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool TrySplitProject(string vaultPath, out string number, out string folder)
    {
        number = folder = "";
        var match = ProjectFolder.Match(vaultPath.TrimEnd('/'));
        if (!match.Success) return false;
        number = match.Groups[1].Value;
        folder = number + match.Groups[2].Value;
        return true;
    }

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

    private ProjectRecord? FindProject(string number)
    {
        using var command = Command("""
            SELECT number, vault_path, vault_folder, procore_company_id, procore_project_id, procore_name,
                   procore_active, match_status, approval, updated_at, first_seen, last_seen
            FROM projects WHERE number = $number
            """);
        command.Parameters.AddWithValue("$number", number);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ProjectRecord
        {
            Number = reader.GetString(0),
            VaultPath = reader.GetString(1),
            VaultFolderName = reader.GetString(2),
            ProcoreCompanyId = reader.GetString(3),
            ProcoreProjectId = reader.GetString(4),
            ProcoreName = reader.GetString(5),
            ProcoreActive = reader.IsDBNull(6) ? null : reader.GetInt64(6) != 0,
            MatchStatus = reader.GetString(7),
            Approval = reader.GetString(8),
            UpdatedAt = reader.GetString(9),
            FirstSeenUtc = reader.GetString(10),
            LastSeenUtc = reader.GetString(11)
        };
    }

    private void WriteProject(ProjectRecord project) =>
        Sql("""
            INSERT INTO projects (
              number, vault_path, vault_folder, procore_company_id, procore_project_id, procore_name,
              procore_active, match_status, approval, updated_at, first_seen, last_seen)
            VALUES (
              $number, $vault, $folder, $company, $procore, $name, $active, $match, $approval, $updated, $first, $last)
            ON CONFLICT(number) DO UPDATE SET
              vault_path = excluded.vault_path,
              vault_folder = excluded.vault_folder,
              procore_company_id = excluded.procore_company_id,
              procore_project_id = excluded.procore_project_id,
              procore_name = excluded.procore_name,
              procore_active = excluded.procore_active,
              match_status = excluded.match_status,
              approval = excluded.approval,
              updated_at = excluded.updated_at,
              first_seen = excluded.first_seen,
              last_seen = excluded.last_seen
            """,
            ("$number", project.Number), ("$vault", project.VaultPath), ("$folder", project.VaultFolderName),
            ("$company", project.ProcoreCompanyId), ("$procore", project.ProcoreProjectId), ("$name", project.ProcoreName),
            ("$active", DbBool(project.ProcoreActive)), ("$match", project.MatchStatus), ("$approval", project.Approval),
            ("$updated", project.UpdatedAt), ("$first", project.FirstSeenUtc), ("$last", project.LastSeenUtc));

    private FileRecord? FindFile(string fileId)
    {
        using var command = Command("""
            SELECT file_id, project_number, vault_path, documents_path, version, sha256, transfer_status, approval, missing, first_seen, last_seen
            FROM files WHERE file_id = $id
            """);
        command.Parameters.AddWithValue("$id", fileId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    private List<FileRecord> CurrentFiles()
    {
        using var command = Command("""
            SELECT file_id, project_number, vault_path, documents_path, version, sha256, transfer_status, approval, missing, first_seen, last_seen
            FROM files WHERE missing = 0
            """);
        var files = new List<FileRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) files.Add(ReadFile(reader));
        return files;
    }

    private static FileRecord ReadFile(SqliteDataReader reader) => new()
    {
        FileId = reader.GetString(0),
        ProjectNumber = reader.GetString(1),
        VaultPath = reader.GetString(2),
        DocumentsPath = reader.GetString(3),
        Version = reader.GetInt32(4),
        Sha256 = reader.GetString(5),
        TransferStatus = reader.GetString(6),
        Approval = reader.GetString(7),
        Missing = reader.GetInt32(8),
        FirstSeenUtc = reader.GetString(9),
        LastSeenUtc = reader.GetString(10)
    };

    private void WriteFile(FileRecord file) =>
        Sql("""
            INSERT INTO files (
              file_id, project_number, vault_path, documents_path, version, sha256, transfer_status, approval, missing, first_seen, last_seen)
            VALUES ($id, $project, $vault, $documents, $version, $sha, $status, $approval, $missing, $first, $last)
            ON CONFLICT(file_id) DO UPDATE SET
              project_number = excluded.project_number,
              vault_path = excluded.vault_path,
              documents_path = excluded.documents_path,
              version = excluded.version,
              sha256 = excluded.sha256,
              transfer_status = excluded.transfer_status,
              approval = excluded.approval,
              missing = excluded.missing,
              first_seen = excluded.first_seen,
              last_seen = excluded.last_seen
            """,
            ("$id", file.FileId), ("$project", file.ProjectNumber), ("$vault", file.VaultPath), ("$documents", file.DocumentsPath),
            ("$version", file.Version), ("$sha", file.Sha256), ("$status", file.TransferStatus), ("$approval", file.Approval),
            ("$missing", file.Missing), ("$first", file.FirstSeenUtc), ("$last", file.LastSeenUtc));

    private SeenRecord? FindSeen(string projectId)
    {
        using var command = Command("SELECT project_id, updated_at, number, name, active FROM procore_seen WHERE project_id = $id");
        command.Parameters.AddWithValue("$id", projectId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SeenRecord(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4) != 0);
    }

    private void ImportLegacy(string path)
    {
        if (!File.Exists(path)) return;
        using (var count = Command("SELECT COUNT(*) FROM projects"))
        {
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }
        using (var count = Command("SELECT COUNT(*) FROM files"))
        {
            if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }
        var store = JsonSerializer.Deserialize<LegacyStore>(File.ReadAllText(path), JsonOptions);
        if (store is null) return;
        using var transaction = db.BeginTransaction();
        foreach (var project in store.Projects)
        {
            WriteProject(new ProjectRecord
            {
                Number = project.Number,
                VaultPath = project.VaultPath,
                VaultFolderName = project.VaultFolderName,
                ProcoreCompanyId = project.ProcoreCompanyId,
                ProcoreProjectId = project.ProcoreProjectId,
                ProcoreName = project.ProcoreName,
                ProcoreActive = project.ProcoreActive,
                MatchStatus = project.MatchStatus,
                Approval = project.Approval,
                FirstSeenUtc = project.FirstSeenUtc,
                LastSeenUtc = project.LastSeenUtc
            });
        }
        foreach (var file in store.Files)
        {
            WriteFile(new FileRecord
            {
                FileId = file.FileId,
                ProjectNumber = file.ProjectNumber,
                VaultPath = file.VaultPath,
                DocumentsPath = file.DocumentsPath,
                Version = file.Version,
                Sha256 = file.Sha256,
                TransferStatus = file.TransferStatus,
                Approval = file.Approval,
                Missing = file.Missing,
                FirstSeenUtc = file.FirstSeenUtc,
                LastSeenUtc = file.LastSeenUtc
            });
        }
        foreach (var change in store.Changes)
        {
            Sql("""
                INSERT INTO changes (file_id, project_number, observed_utc, kind, previous_version, version, vault_path)
                VALUES ($file, $project, $when, $kind, $previous, $version, $path)
                """,
                ("$file", change.FileId), ("$project", change.ProjectNumber), ("$when", change.ObservedUtc), ("$kind", change.Kind),
                ("$previous", change.PreviousVersion), ("$version", change.Version), ("$path", change.VaultPath));
        }
        foreach (var scan in store.Scans)
        {
            Sql("""
                INSERT INTO scans (id, source, started_utc, ended_utc, status, summary)
                VALUES ($id, $source, $started, $ended, $status, $summary)
                """,
                ("$id", scan.Id), ("$source", scan.Source), ("$started", scan.StartedUtc), ("$ended", scan.EndedUtc),
                ("$status", scan.Status), ("$summary", scan.Summary));
        }
        foreach (var pair in store.Settings)
            Sql("INSERT INTO settings (key, value) VALUES ($key, $value)", ("$key", pair.Key), ("$value", pair.Value));
        foreach (var id in store.SeenProcoreIds)
        {
            var project = store.Projects.FirstOrDefault(item => item.ProcoreProjectId == id);
            var number = project is null || project.Number == id ? "" : project.Number;
            Sql("""
                INSERT INTO procore_seen (project_id, updated_at, number, name, active)
                VALUES ($id, '', $number, $name, $active)
                ON CONFLICT(project_id) DO NOTHING
                """,
                ("$id", id), ("$number", number), ("$name", project?.ProcoreName ?? ""), ("$active", DbBool(project?.ProcoreActive)));
        }
        transaction.Commit();
    }

    private int Sql(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private long LastId()
    {
        using var command = Command("SELECT last_insert_rowid()");
        return (long)command.ExecuteScalar()!;
    }

    private static object DbBool(bool? value) => value is null ? DBNull.Value : value.Value ? 1L : 0L;
    private static string Now() => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    private static string LegacyCatalogFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultTransfer", "catalog", "catalog.json");
    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!
        : throw new InvalidDataException("Missing " + name + ".");
    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
        public string Approval { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
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

    private sealed record SeenRecord(string UpdatedAt, string Number, string Name, bool? Active);

    private sealed class LegacyStore
    {
        public List<ProjectRecord> Projects { get; set; } = [];
        public List<FileRecord> Files { get; set; } = [];
        public List<LegacyChange> Changes { get; set; } = [];
        public List<LegacyScan> Scans { get; set; } = [];
        public List<string> SeenProcoreIds { get; set; } = [];
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    private sealed class LegacyChange
    {
        public string FileId { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public string ObservedUtc { get; set; } = "";
        public string Kind { get; set; } = "";
        public int? PreviousVersion { get; set; }
        public int? Version { get; set; }
        public string VaultPath { get; set; } = "";
    }

    private sealed class LegacyScan
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
