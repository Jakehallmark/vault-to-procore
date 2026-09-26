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
                using var catalog = new Catalog(Path.Combine(root, "catalog"));
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
