namespace VaultTransfer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            var failures = SelfTests.Run() + CatalogTests.Run() + ProcoreClient.SelfTest();
            return failures == 0 ? 0 : 1;
        }
        ApplicationConfiguration.Initialize();
        var appFolder = AppContext.BaseDirectory;
        try
        {
            using var instance = new FileStream(Path.Combine(appFolder, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new Catalog(appFolder);
            using var form = new MonitorForm(catalog, AppContext.BaseDirectory);
            Application.Run(form);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show("The app could not open. It may already be running.\n\n" + error.Message, "Vault Transfer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
