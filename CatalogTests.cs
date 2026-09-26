namespace VaultTransfer;

internal static class CatalogTests
{
    public static int Run()
    {
        var failures = 0;
        void Test(string name, Action run)
        {
            try { run(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Test("Vault versions, removals, Procore links, and staged files stay with one project", () =>
        {
            var root = Directory.CreateTempSubdirectory("VaultCatalog-").FullName;
            try
            {
                using var catalog = new Catalog(Path.Combine(root, "catalog"), null);
                var first = "$/Designs/Projects/101000-101999/101097 - WM Wave 23 - Walmart Store 2151 1250kw/Settings Files/relay.zap15";
                var added = catalog.ApplyVaultInventory([new VaultFileRecord(first, "42", 3)], true);
                if (added != 1) throw new Exception("Expected one added file.");
                var project = catalog.Projects().Single();
                if (project.Number != "101097" || project.Files != 1 || project.Match != "Vault") throw new Exception("Project was not stored from the Vault path.");
                if (project.VaultFolder.Contains("101097 - WM") == false) throw new Exception("Vault folder name was not kept.");
                var staged = catalog.ApplyVaultInventory([new VaultFileRecord(first, "42", 3)], true);
                if (staged != 0 || catalog.Changes(10).Count(change => change.Kind == "VersionChanged") != 0) throw new Exception("Unchanged version was recorded as a change.");
                catalog.ApplyStagedFiles("101097", [("42", 3, "Settings Files/relay.zap15", "ABC")]);
                var changed = catalog.ApplyVaultInventory([new VaultFileRecord(first, "42", 4)], true);
                if (changed != 1) throw new Exception("Version change was not recorded.");
                var version = catalog.Changes(5).First(change => change.Kind == "VersionChanged");
                if (version.Version != "3 → 4" || version.Project != "101097") throw new Exception("Version history was not kept.");
                var early = false;
                try { catalog.SetApproval(["42"], "Approved"); }
                catch (InvalidDataException) { early = true; }
                if (!early) throw new Exception("A file was approved before its project matched one Procore project.");
                catalog.ApplyProcoreProjects([new ProcoreProjectRecord("12233", "2460697", "101097", "FY23 WM 2151 Sunrise, FL", true)]);
                catalog.SetApproval(["42"], "Approved");
                if (catalog.Files("101097", null).Single().Approval != "Approved") throw new Exception("Approval was not saved.");
                catalog.ApplyVaultInventory([new VaultFileRecord(first, "42", 5)], true);
                if (catalog.Files("101097", null).Single().Approval != "Pending") throw new Exception("A new version stayed approved.");
                project = catalog.Projects().Single(row => row.Number == "101097");
                if (project.Match != "Exact" || project.ProcoreName != "FY23 WM 2151 Sunrise, FL" || project.Active != "Active")
                    throw new Exception("Exact Procore project was not linked.");
                catalog.ApplyProcoreProjects([
                    new ProcoreProjectRecord("12233", "1", "101646", "A", true),
                    new ProcoreProjectRecord("12233", "2", "101646", "B", false)]);
                var duplicate = catalog.Projects().Single(row => row.Number == "101646");
                if (duplicate.Match != "Needs review") throw new Exception("Duplicate Procore numbers were merged into one project.");
                var removed = catalog.ApplyVaultInventory([], true);
                if (removed != 1 || catalog.Projects().Single(row => row.Number == "101097").Files != 0)
                    throw new Exception("A file missing from a finished Vault scan was kept as current.");
                var blocked = false;
                try { catalog.ApplyStagedFiles("101083", [("42", 4, "other.txt", "DEF")]); }
                catch (InvalidDataException) { blocked = true; }
                if (!blocked) throw new Exception("A staged file was accepted for a different project.");
                var coordinator = new ScanCoordinator();
                var started = new TaskCompletionSource();
                var firstScan = coordinator.TryRun(async _ => { started.SetResult(); await Task.Delay(500); });
                started.Task.Wait();
                var overlapped = coordinator.TryRun(_ => Task.CompletedTask).Result;
                firstScan.Wait();
                if (overlapped || !firstScan.Result) throw new Exception("Two scans ran at the same time.");
            }
            finally { Directory.Delete(root, true); }
        });
        Test("Saved Procore projects stay in the list and can be approved", () =>
        {
            var root = Directory.CreateTempSubdirectory("VaultProcore-").FullName;
            try
            {
                var databaseFolder = Path.Combine(root, "catalog");
                {
                using var catalog = new Catalog(databaseFolder, null);
                catalog.SaveProcoreProject(new ProcoreProjectRecord("12233", "2460697", "101097", "FY23 WM 2151 Sunrise, FL", true));
                if (!catalog.SeenProcoreProjectIds().Contains("2460697")) throw new Exception("The saved Procore project was not remembered.");
                var saved = catalog.Projects().Single();
                if (saved.Number != "101097" || saved.Approval != "Pending" || saved.Match != "Procore")
                    throw new Exception("The Procore project was not added to the list.");
                catalog.SetProjectApproval(["101097"], "Approved");
                catalog.SaveProcoreProject(new ProcoreProjectRecord("12233", "2460697", "101097", "FY23 WM 2151 Sunrise, FL", true));
                if (catalog.Projects().Single().Approval != "Approved") throw new Exception("A later scan cleared the project approval.");
                catalog.SaveProcoreProject(new ProcoreProjectRecord("12233", "3552178", null, "Norbord", true));
                if (catalog.Projects().Single(project => project.Number == "3552178").ProcoreName != "Norbord")
                    throw new Exception("A project without a number was dropped.");
                var stamp = "2026-09-01T00:00:00Z";
                catalog.SaveProcoreProject(new ProcoreProjectRecord("12233", "2460697", "101097", "FY23 WM 2151 Sunrise, FL", true, stamp));
                if (!catalog.ProcoreUnchanged("2460697", stamp, true, "101097", "FY23 WM 2151 Sunrise, FL", true))
                    throw new Exception("An unchanged Procore project was read again.");
                if (catalog.ProcoreUnchanged("2460697", "2026-09-02T00:00:00Z", true, "101097", "FY23 WM 2151 Sunrise, FL", true))
                    throw new Exception("A changed Procore project was skipped.");
                }
                using var reopened = new Catalog(databaseFolder, null);
                if (reopened.Projects().Single(project => project.Number == "101097").Approval != "Approved")
                    throw new Exception("Project history was not kept in the database.");
                if (!File.Exists(Path.Combine(root, "catalog", Catalog.DatabaseFileName)))
                    throw new Exception("The database was not written beside the app.");
            }
            finally { Directory.Delete(root, true); }
        });
        Test("An older project list is kept and an incremental Vault scan does not drop files", () =>
        {
            var root = Directory.CreateTempSubdirectory("VaultLegacy-").FullName;
            try
            {
                var legacy = Path.Combine(root, "catalog.json");
                File.WriteAllText(legacy, """
                    {"Projects":[{"Number":"101097","VaultPath":"$/Designs/Projects/101000-101999/101097 - Example","VaultFolderName":"101097 - Example","ProcoreCompanyId":"12233","ProcoreProjectId":"2460697","ProcoreName":"Sunrise","ProcoreActive":true,"MatchStatus":"Exact","Approval":"Approved","FirstSeenUtc":"2026-09-01T00:00:00.0000000+00:00","LastSeenUtc":"2026-09-01T00:00:00.0000000+00:00"}],"Files":[{"FileId":"42","ProjectNumber":"101097","VaultPath":"$/Designs/Projects/101000-101999/101097 - Example/Settings Files/relay.zap15","DocumentsPath":"Settings Files/relay.zap15","Version":3,"Sha256":"","TransferStatus":"Observed","Approval":"Pending","Missing":0,"FirstSeenUtc":"2026-09-01T00:00:00.0000000+00:00","LastSeenUtc":"2026-09-01T00:00:00.0000000+00:00"}],"Changes":[],"Scans":[],"SeenProcoreIds":["2460697"],"Settings":{}}
                    """);
                var folder = Path.Combine(root, "app");
                using (var catalog = new Catalog(folder, legacy))
                {
                    var project = catalog.Projects().Single();
                    if (project.Number != "101097" || project.Approval != "Approved" || project.Files != 1)
                        throw new Exception("The previous project list was not kept.");
                    if (!catalog.ProcoreUnchanged("2460697", null, true, "101097", "Sunrise", true))
                        throw new Exception("A saved Procore project with the same list fields was read again.");
                    if (!catalog.VaultRescanIsDue(DateTimeOffset.Parse("2026-09-26T00:00:00Z")))
                        throw new Exception("The first Vault scan was treated as an update.");
                    var path = "$/Designs/Projects/101000-101999/101097 - Example/Settings Files/relay.zap15";
                    var removed = catalog.ApplyVaultInventory([new VaultFileRecord(path, "42", 3)], false);
                    if (removed != 0 || catalog.Files("101097", null).Count != 1)
                        throw new Exception("An update scan removed a file that was not listed.");
                    catalog.SetSetting("VaultFullScanUtc", "2026-09-26T00:00:00.0000000+00:00");
                    if (catalog.VaultRescanIsDue(DateTimeOffset.Parse("2026-09-26T12:00:00Z")))
                        throw new Exception("A fresh full scan was due again the same day.");
                }
            }
            finally { Directory.Delete(root, true); }
        });
        Test("Vault Professional login uses the saved Windows server and database", () =>
        {
            var root = Directory.CreateTempSubdirectory("VaultLogin-").FullName;
            try
            {
                var path = Path.Combine(root, "ApplicationPreferences.xml");
                File.WriteAllText(path, """
                    <Categories>
                      <Category ID="Login">
                        <Property Name="AutoLogin" Value="True" />
                        <Property Name="ServerName" Value="vault.example" />
                        <Property Name="UserName" Value="" />
                        <Property Name="SelectedAuthenticationType" Value="1" />
                        <Property Name="Password" Value="do-not-read" />
                        <Property Name="DatabaseName" Value="Designs" />
                      </Category>
                    </Categories>
                    """);
                var login = VaultProfessionalLogin.Read(path);
                if (login is null || login.Value.Server != "vault.example" || login.Value.Database != "Designs")
                    throw new Exception("Saved Vault server and database were not read.");
                File.WriteAllText(path, """
                    <Categories><Category ID="Login">
                      <Property Name="ServerName" Value="vault.example" />
                      <Property Name="DatabaseName" Value="Designs" />
                      <Property Name="SelectedAuthenticationType" Value="0" />
                    </Category></Categories>
                    """);
                var refused = false;
                try { VaultProfessionalLogin.Read(path); }
                catch (InvalidDataException) { refused = true; }
                if (!refused) throw new Exception("A non-Windows Vault login was accepted.");
                if (VaultProfessionalLogin.Read(Path.Combine(root, "missing.xml")) is not null)
                    throw new Exception("A missing Vault preferences file returned a login.");
            }
            finally { Directory.Delete(root, true); }
        });
        return failures == 0 ? 0 : 1;
    }
}
