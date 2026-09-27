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
        if (args.Length == 2 && args[0] == "--ui-snapshot")
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try
            {
                // Offline layout diagnostic: no message loop or scheduled scans.
                using var catalog = new Catalog(appFolder);
                using var form = new MonitorForm(catalog, appFolder, new AppLog(catalog, appFolder));
                form.Show();
                form.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
                return 0;
            }
            catch (Exception error) { Console.WriteLine(error); return 1; }
        }
        try
        {
            using var instance = new FileStream(Path.Combine(appFolder, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var catalog = new Catalog(appFolder);
            var log = new AppLog(catalog, appFolder);
            log.Information("App", "Vault Transfer opened.");
            using var form = new MonitorForm(catalog, appFolder, log);
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
