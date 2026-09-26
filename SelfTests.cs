namespace VaultTransfer;

internal static class SelfTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("VaultTransferTests-").FullName;
        public Settings Settings { get; }
        public AppStore Store { get; }
        public Fixture()
        {
            Settings = new Settings { Mode = Settings.LocalTest, SourceFolder = Path.Combine(Root, "source"),
                StagingFolder = Path.Combine(Root, "stage"), DestinationFolder = Path.Combine(Root, "destination"),
                Extensions = [".txt"], ProjectFolders = new() { ["001234"] = "Project A\\Documents" } };
            Directory.CreateDirectory(Settings.SourceFolder);
            Store = new AppStore(Path.Combine(Root, "state"));
        }
        public string Add(string relative, string text = "drawing content")
        {
            var path = Path.Combine(Settings.SourceFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
        }
        public ScanReport Scan() => TransferEngine.Scan(Settings);
        public void Approve(ScanReport report) => TransferEngine.Decide(report,
            report.Items.Where(i => i.Status == "Suggested").Select(i => i.Id).ToHashSet(), "Approved");
        public void Copy(ScanReport report) => TransferEngine.Transfer(report, Settings, Store);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("VaultTransferTests-"))
                throw new Exception("Unexpected test cleanup path.");
            Directory.Delete(full, true);
        }
    }

    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    { public void Report(string value) => action(value); }

    private static void Check(bool condition, string message = "Check failed")
    { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { return; }
        throw new Exception("Expected operation to be rejected.");
    }

    public static int Run()
    {
        var results = new List<string>();
        var failures = 0;
        void Test(string name, Action<Fixture> run)
        {
            try { using var fixture = new Fixture(); run(fixture); results.Add("PASS " + name); }
            catch (Exception error) { failures++; results.Add("FAIL " + name + ": " + error); }
        }
        Test("Scan is read-only and preserves project number and relative folders", f =>
        {
            f.Add("001234 - Project/Plans/one.txt");
            var report = f.Scan(); var item = report.Items.Single();
            Check(report.Complete && item.Project == "001234" && item.Destination == @"Project A\Documents\Plans\one.txt");
            Check(!item.Destination.Split('\\').Contains("001234 - Project", StringComparer.OrdinalIgnoreCase));
            Check(!Directory.Exists(f.Settings.StagingFolder) && !Directory.Exists(f.Settings.DestinationFolder));
        });
        Test("Only approved files pass through both copy steps", f =>
        {
            f.Add("001234/yes.txt"); f.Add("001234/no.txt"); f.Add("001234/pending.txt");
            var report = f.Scan();
            TransferEngine.Decide(report, [report.Items.Single(i => i.Source.EndsWith("yes.txt")).Id], "Approved");
            TransferEngine.Decide(report, [report.Items.Single(i => i.Source.EndsWith("no.txt")).Id], "Denied");
            f.Copy(report);
            Check(Directory.GetFiles(f.Settings.StagingFolder, "*", SearchOption.AllDirectories).Length == 1);
            Check(Directory.GetFiles(f.Settings.DestinationFolder, "*", SearchOption.AllDirectories).Length == 1);
            Check(report.Items.Count(i => i.Status == "Test copied") == 1);
            Check(Directory.GetFiles(f.Settings.SourceFolder, "*", SearchOption.AllDirectories).Length == 3);
        });
        Test("Extension and folder exclusions are reported", f =>
        {
            f.Add("001234/Archive/old.txt"); f.Add("001234/no.bin"); f.Add("001234/yes.txt");
            var report = f.Scan(); Check(report.Items.Count(i => i.Status == "Excluded") == 2);
        });
        Test("Unknown and ambiguous projects are blocked", f =>
        {
            f.Add("999999/one.txt"); f.Add("001234/999999/two.txt"); f.Add("nothing/three.txt");
            Check(f.Scan().Items.All(i => i.Status == "Blocked"));
        });
        Test("Custom alphanumeric project format works", f =>
        {
            f.Settings.ProjectPattern = @"(JOB-\d{3})";
            f.Settings.ProjectFolders = new() { ["JOB-007"] = "Job Seven" };
            f.Add("JOB-007/folder/one.txt"); Check(f.Scan().Items.Single().Project == "JOB-007");
        });
        Test("Colliding destinations are blocked case-insensitively", f =>
        {
            f.Add("001234 first/one.txt"); f.Add("001234 second/one.txt");
            Check(f.Scan().Items.All(i => i.Status == "Blocked"));
        });
        Test("Changed source revokes approval and is not copied", f =>
        {
            var source = f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report);
            File.WriteAllText(source, "changed"); f.Copy(report);
            Check(report.Items.Single().Decision == "Pending" && report.Items.Single().Status == "Blocked");
            Check(!Directory.Exists(f.Settings.StagingFolder));
        });
        Test("Settings changes invalidate saved approvals", f =>
        {
            f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report);
            f.Settings.ProjectFolders["001234"] = "Another folder";
            Reject(() => f.Copy(report)); Check(!Directory.Exists(f.Settings.StagingFolder));
        });
        Test("Existing different destination is never overwritten", f =>
        {
            f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report);
            var target = Path.Combine(f.Settings.DestinationFolder, report.Items.Single().Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "keep me");
            f.Copy(report); Check(File.ReadAllText(target) == "keep me" && report.Items.Single().Status == "Failed");
            Check(f.Scan().Items.Single().Status == "Blocked");
        });
        Test("Staging corruption is blocked before destination copy", f =>
        {
            f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report);
            var stage = Path.Combine(f.Settings.StagingFolder, report.Items.Single().Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!); File.WriteAllText(stage, "different");
            f.Copy(report); Check(report.Items.Single().Status == "Failed" && !Directory.Exists(f.Settings.DestinationFolder));
        });
        Test("Identical files and completed retries do not copy again", f =>
        {
            f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report); f.Copy(report);
            var events = report.Items.Single().History.Count; f.Copy(report);
            Check(report.Items.Single().History.Count == events && f.Scan().Items.Single().Status == "Unchanged");
        });
        Test("Cancellation between steps saves staging and resumes", f =>
        {
            f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report);
            using var cancel = new CancellationTokenSource();
            TransferEngine.Transfer(report, f.Settings, f.Store, cancel.Token,
                new InlineProgress(text => { if (text.StartsWith("Copying to")) cancel.Cancel(); }));
            Check(report.Items.Single().Status == "Staged");
            Check(!Directory.Exists(f.Settings.DestinationFolder) || Directory.GetFiles(f.Settings.DestinationFolder, "*", SearchOption.AllDirectories).Length == 0);
            var saved = f.Store.Latest()!; f.Copy(saved); Check(saved.Items.Single().Status == "Test copied");
        });
        Test("Cancelled scans cannot approve or transfer", f =>
        {
            f.Add("001234/one.txt"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var report = TransferEngine.Scan(f.Settings, cancel.Token);
            Check(!report.Complete && report.Errors.Count > 0);
            Reject(() => f.Approve(report)); Reject(() => f.Copy(report));
        });
        Test("Vault project folder is not created in Documents", f =>
        {
            f.Settings.ProjectFolders["001234"] = "001234 - Project";
            f.Add("001234 - Project/Plans/one.txt");
            var item = f.Scan().Items.Single();
            Check(item.Status == "Blocked" && item.Reason.Contains("must not be created in Documents"));
        });
        Test("Destination traversal, reserved names, and overlapping roots are blocked", f =>
        {
            foreach (var value in new[] { "../outside", @"C:\outside", "CON", "folder/../outside", "folder/file:stream" })
            { f.Settings.ProjectFolders["001234"] = value; Reject(() => TransferEngine.Validate(f.Settings)); }
            f.Settings.ProjectFolders["001234"] = "Good";
            f.Settings.StagingFolder = Path.Combine(f.Settings.SourceFolder, "nested");
            Reject(() => TransferEngine.Validate(f.Settings));
        });
        Test("Review decisions survive restart and reports export", f =>
        {
            f.Add("001234/one.txt"); var report = f.Scan(); f.Approve(report); f.Store.Save(report);
            var saved = new AppStore(f.Store.Root).Latest()!; Check(saved.Items.Single().Decision == "Approved");
            AppStore.ExportCsv(saved, Path.Combine(f.Root, "report.csv"));
            Check(File.ReadAllText(Path.Combine(f.Root, "report.csv")).Contains("'001234"));
        });
        Test("Production mode cannot silently use local copying", f =>
        {
            f.Settings.Mode = new Settings().Mode; Reject(() => f.Scan());
        });
        Directory.CreateDirectory("test-results");
        results.Add($"{results.Count - failures} passed; {failures} failed.");
        File.WriteAllLines(Path.Combine("test-results", "self-tests.txt"), results);
        return failures == 0 ? 0 : 1;
    }
}
