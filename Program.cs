namespace VaultTransfer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) return SelfTests.Run();
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultTransfer");
        try
        {
            var store = new AppStore(dataFolder);
            // Exclusive open is released by Windows when the app exits, including after a crash.
            using var instance = new FileStream(Path.Combine(dataFolder, "app.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            using var form = new MainForm(store);
            Application.Run(form);
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            MessageBox.Show("The app could not open. It may already be running, or its local data folder may be unavailable.\n\n" +
                error.Message, "Vault Transfer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
