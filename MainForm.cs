namespace VaultTransfer;

public sealed class MainForm : Form
{
    private readonly AppStore store;
    private Settings settings;
    private ScanReport? report;
    private CancellationTokenSource? work;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private readonly TextBox source = new(), stage = new(), destination = new(), pattern = new(), extensions = new();
    private readonly TextBox exclusions = new() { Multiline = true, Height = 62, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox mappings = new() { Multiline = true, Height = 100, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly Label summary = new() { AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 7, 12, 7), Text = "Vault and Procore Drive connections must be verified before scanning." };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, RowHeadersVisible = false, BackgroundColor = Color.White,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
    };
    private readonly ComboBox filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly List<Control> actions = [];
    private readonly Button cancel = new() { Text = "Cancel operation", AutoSize = true, Enabled = false };

    public MainForm(AppStore store)
    {
        this.store = store;
        settings = store.LoadSettings();
        report = store.Latest();
        // Previous local demonstration settings and reviews must not enter the live workflow.
        if (settings.Mode == Settings.LocalTest) settings = new Settings();
        if (report?.Settings.Mode == Settings.LocalTest) report = null;
        Text = "Vault Transfer — review before copying";
        Font = new Font("Segoe UI", 10);
        Size = new Size(1220, 860); MinimumSize = new Size(980, 760);
        StartPosition = FormStartPosition.CenterScreen;
        var header = new Panel { Dock = DockStyle.Top, Height = 108, Padding = new Padding(18), BackColor = Color.FromArgb(25, 48, 67) };
        header.Controls.Add(new Label { Text = "Configure  →  Scan  →  Review  →  Approve  →  Stage  →  Transfer",
            Dock = DockStyle.Bottom, Height = 27, ForeColor = Color.WhiteSmoke });
        header.Controls.Add(new Label { Text = "Vault Transfer", Dock = DockStyle.Top, Height = 40,
            Font = new Font("Segoe UI", 23, FontStyle.Bold), ForeColor = Color.White });
        Controls.Add(tabs); Controls.Add(status); Controls.Add(header);
        BuildSettings(); BuildReview(); BuildHistory();
        ApplySettings(settings); RefreshReport();
        FormClosing += (_, e) =>
        {
            if (work is not null) { work.Cancel(); e.Cancel = true; status.Text = "Cancelling… close the window once the operation stops."; }
        };
    }

    private Button Action(string text, Action clicked)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(7, 3, 7, 3) };
        button.Click += (_, _) => Guard(clicked); actions.Add(button); return button;
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 9, 12, 3) };

    private void BuildSettings()
    {
        var page = new TabPage("1   Settings") { Padding = new Padding(18), AutoScroll = true };
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(string text, Control control)
        {
            var row = layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(Label(text), 0, row); control.Dock = DockStyle.Top;
            control.Margin = new Padding(3, 4, 3, 9); layout.Controls.Add(control, 1, row);
        }
        mode.Items.Add(new Settings().Mode);
        Add("Connection mode", mode);
        Add("Connection status", new Label { AutoSize = true, ForeColor = Color.DarkSlateGray,
            Text = "Real Vault 2024 and Procore Drive connections are required. Scanning is unavailable until they are verified.\nThe app runs only while you open it. No scheduled task or service is installed." });
        Control Folder(TextBox box)
        {
            var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            box.Dock = DockStyle.Fill;
            panel.Controls.Add(box);
            panel.Controls.Add(Action("Browse…", () =>
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            })); return panel;
        }
        Add("Source folder (test)", Folder(source));
        Add("Local staging folder", Folder(stage));
        Add("Destination folder (test)", Folder(destination));
        Add("Project-number pattern", pattern);
        Add("Included extensions", extensions);
        Add("Excluded paths", exclusions);
        Add("Project folder mappings", mappings);
        Add("Rule format", new Label { AutoSize = true, Text = "Extensions: .pdf, .dwg (blank includes all). Exclusions: one wildcard pattern per line.\nMappings: one per line, for example 001234 = Project A\\Documents\nProject pattern: one capture group; matched against folder names. Folders below the project are preserved." });
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(Action("Save settings", SaveSettings));
        buttons.Controls.Add(Action("Scan and review", async () => await ScanAsync()));
        Add("", buttons);
        page.Controls.Add(layout); tabs.TabPages.Add(page);
    }

    private void BuildReview()
    {
        var page = new TabPage("2   Review & transfer") { Padding = new Padding(14) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.Controls.Add(summary);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        filter.Items.AddRange(["All items", "Pending", "Approved", "Denied", "Blocked", "Failed", "Test copied", "Excluded", "Unchanged"]);
        filter.SelectedIndex = 0; filter.SelectedIndexChanged += (_, _) => RefreshReport(); toolbar.Controls.Add(filter);
        toolbar.Controls.Add(Action("Approve selected", () => Decide("Approved")));
        toolbar.Controls.Add(Action("Deny selected", () => Decide("Denied")));
        toolbar.Controls.Add(Action("Reset selected", () => Decide("Pending")));
        toolbar.Controls.Add(Action("Copy approved / retry", async () => await CopyAsync()));
        toolbar.Controls.Add(Action("Export report…", Export));
        cancel.Click += (_, _) => work?.Cancel(); toolbar.Controls.Add(cancel); layout.Controls.Add(toolbar);
        foreach (var (name, width) in new[] { ("Decision", 65f), ("Status", 70f), ("Project", 65f), ("Source", 180f), ("Destination", 180f), ("Reason", 230f) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.SelectionChanged += (_, _) => ShowDetails();
        layout.Controls.Add(grid); layout.Controls.Add(details); page.Controls.Add(layout); tabs.TabPages.Add(page);
    }

    private void BuildHistory()
    {
        var page = new TabPage("3   History & connections") { Padding = new Padding(20) };
        var info = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
            Text = "MANUAL LAUNCH\r\nOpen the app when needed. Closing it stops execution. No background scheduling is installed.\r\n\r\n" +
            "REVIEW HISTORY\r\nEach scan creates a separate saved report. The latest report reopens on launch. Approvals and transfer results are saved after each action. " +
            "Select a row to see its event history. Older reports remain as JSON files in:\r\n" + store.ReportsFolder + "\r\n\r\n" +
            "CONNECTIONS STILL REQUIRED\r\nVault 2024: verify a supported inventory and download connection on the work laptop.\r\n" +
            "Procore Drive: verify how the app can select a project/folder, upload, and confirm success. A local folder copy is not a Procore upload.\r\n\r\n" +
            "Approval is intended to authorize both production steps: Vault to staging, then staging through Procore Drive. " +
            "Production mode is blocked until those connections are implemented and tested.\r\n\r\n" +
            "LOCAL TEST RULES\r\nOnly approved files copy. Existing different files and ambiguous project matches are blocked. " +
            "Changed settings require a new scan and new approval. Changed file content is blocked. Source files are retained." };
        var open = Action("Open a saved review…", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Saved review (*.json)|*.json", InitialDirectory = store.ReportsFolder };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var loaded = AppStore.Read<ScanReport>(dialog.FileName);
            if (loaded.Settings.Mode == Settings.LocalTest)
                throw new InvalidDataException("Local demonstration reviews cannot be used for company transfers.");
            if (!Guid.TryParseExact(loaded.Id, "N", out _) || loaded.SettingsFingerprint != loaded.Settings.Fingerprint())
                throw new InvalidDataException("This is not a valid saved review.");
            report = loaded; RefreshReport(); tabs.SelectedIndex = 1;
            status.Text = "Saved review opened. Current settings must match before approving or copying.";
        });
        open.Dock = DockStyle.Bottom;
        page.Controls.Add(info); page.Controls.Add(open); tabs.TabPages.Add(page);
    }

    private void ApplySettings(Settings value)
    {
        mode.SelectedItem = value.Mode; source.Text = value.SourceFolder; stage.Text = value.StagingFolder;
        destination.Text = value.DestinationFolder; pattern.Text = value.ProjectPattern;
        extensions.Text = string.Join(", ", value.Extensions); exclusions.Lines = value.ExcludePatterns;
        mappings.Lines = value.ProjectFolders.Select(pair => pair.Key + " = " + pair.Value).ToArray();
    }

    private Settings ReadSettings()
    {
        var value = new Settings { Mode = mode.SelectedItem?.ToString() ?? "", SourceFolder = source.Text.Trim(),
            StagingFolder = stage.Text.Trim(), DestinationFolder = destination.Text.Trim(), ProjectPattern = pattern.Text,
            Extensions = extensions.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            ExcludePatterns = exclusions.Lines.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray() };
        foreach (var line in mappings.Lines.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !value.ProjectFolders.TryAdd(parts[0], parts[1]))
                throw new InvalidDataException("Use one unique project mapping per line: 001234 = Project A\\Documents");
        }
        return value;
    }

    private void SaveSettings()
    {
        var value = ReadSettings();
        if (value.Mode == Settings.LocalTest) TransferEngine.Validate(value);
        // App data must never be included in the input scan or copy roots.
        foreach (var path in new[] { value.SourceFolder, value.StagingFolder, value.DestinationFolder }.Where(Path.IsPathFullyQualified))
        {
            var root = TransferEngine.Root(path) + Path.DirectorySeparatorChar;
            if ((store.Root + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a folder that does not contain this app's settings and reports.");
        }
        store.SaveSettings(value); settings = value;
        status.Text = report is not null && report.SettingsFingerprint != value.Fingerprint()
            ? "Settings saved. Scan again and review before copying." : "Settings saved.";
    }

    private async Task RunWork(Func<CancellationToken, IProgress<string>, Task> operation)
    {
        if (work is not null) return;
        work = new CancellationTokenSource();
        foreach (var action in actions) action.Enabled = false;
        tabs.TabPages[0].Enabled = false; grid.Enabled = false; filter.Enabled = false; cancel.Enabled = true;
        try
        {
            var progress = new Progress<string>(text => status.Text = text);
            await operation(work.Token, progress);
        }
        catch (Exception error) { status.Text = error.Message; MessageBox.Show(this, error.Message, "Operation stopped", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally
        {
            work.Dispose(); work = null; cancel.Enabled = false;
            foreach (var action in actions) action.Enabled = true;
            tabs.TabPages[0].Enabled = true; grid.Enabled = true; filter.Enabled = true; RefreshReport();
        }
    }

    private async Task ScanAsync()
    {
        try { SaveSettings(); } catch (Exception error) { MessageBox.Show(this, error.Message); return; }
        await RunWork(async (token, progress) =>
        {
            var result = await Task.Run(() => TransferEngine.Scan(settings, token, progress));
            store.Save(result); report = result; tabs.SelectedIndex = 1;
            status.Text = result.Complete ? "Scan complete. Select rows to approve or deny." : "Scan incomplete. See scan errors below; copying is blocked.";
        });
    }

    private async Task CopyAsync()
    {
        if (report is null) return;
        try
        {
            SaveSettings();
            if (settings.Fingerprint() != report.SettingsFingerprint) throw new InvalidDataException("Settings changed. Scan and review again.");
        }
        catch (Exception error) { MessageBox.Show(this, error.Message); return; }
        await RunWork(async (token, progress) =>
        {
            await Task.Run(() => TransferEngine.Transfer(report, settings, store, token, progress));
            status.Text = token.IsCancellationRequested ? "Copying cancelled. Completed copies retained." : "Local test run finished. Review each result; no Procore upload occurred.";
        });
    }

    private void Decide(string decision)
    {
        if (report is null) return;
        if (ReadSettings().Fingerprint() != report.SettingsFingerprint) throw new InvalidDataException("Settings changed. Scan again before reviewing.");
        var ids = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => ((ReviewItem)row.Tag!).Id).ToHashSet();
        TransferEngine.Decide(report, ids, decision); store.Save(report); RefreshReport();
    }

    private void RefreshReport()
    {
        grid.Rows.Clear();
        if (report is null) { summary.Text = "No live scan yet. Verify Vault and Procore Drive connections first."; return; }
        var selected = filter.SelectedItem?.ToString();
        foreach (var item in report.Items.Where(i => selected == "All items" || selected == i.Decision || selected == i.Status))
        {
            var index = grid.Rows.Add(item.Decision, item.Status, item.Project, item.Source, item.Destination, item.Reason);
            grid.Rows[index].Tag = item;
            grid.Rows[index].DefaultCellStyle.BackColor = item.Status is "Blocked" or "Failed" ? Color.MistyRose
                : item.Decision == "Approved" || item.Status == "Test copied" ? Color.Honeydew : Color.White;
        }
        grid.ClearSelection();
        summary.Text = $"LOCAL TEST • {(report.Complete ? "Complete scan" : "INCOMPLETE SCAN")} • {report.Items.Count:N0} files • " +
            $"{report.Items.Count(i => i.Decision == "Approved")} approved • {report.Items.Count(i => i.Decision == "Denied")} denied • " +
            $"{report.Items.Count(i => i.Status == "Test copied")} copied • {report.Items.Count(i => i.Status == "Blocked")} blocked";
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is ReviewItem item)
            details.Text = $"Source: {item.Source}\r\nStaging: {Path.Combine(report!.Settings.StagingFolder, item.Destination)}\r\n" +
                $"Test destination: {Path.Combine(report.Settings.DestinationFolder, item.Destination)}\r\n" +
                $"{item.Bytes:N0} bytes | SHA-256: {item.Sha256}\r\n{item.Reason}\r\n" + string.Join("\r\n", item.History);
        else details.Text = report?.Errors.Count > 0 ? "SCAN ERRORS\r\n" + string.Join("\r\n", report.Errors)
            : "Select one or more rows to approve or deny. Ctrl-click or Shift-click selects multiple rows.\r\nCopy approved processes all approved items in the report, including items hidden by the filter.";
    }

    private void Export()
    {
        if (report is null) return;
        using var dialog = new SaveFileDialog { Filter = "CSV report (*.csv)|*.csv", FileName = "transfer-review.csv" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AppStore.ExportCsv(report, dialog.FileName);
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Check settings", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

}
