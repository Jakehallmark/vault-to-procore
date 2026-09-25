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
    private readonly Label summary = new() { AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 7, 12, 7), Text = "Ready" };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly  = true, AllowUserToAddRows = false, AllowsUserToDeleteRows = false,
        AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, RowHeadersVisible = false, BackgroundColor = Color.White,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
    };
    private readonly ComboBox filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly List<Control> actions = [];
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };

    public MainForm(AppStore store)
    {
        this.store = store;
        settings = store.Latest();
        Text = "Vault Transfer Tool";
        Font = new Font("Segoe UI", 10);
        Size = new Size(1220, 720); MinimumSize = new Size(980, 600);
        StartPosition = FormStartPosition.CenterScreen;
        var header = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(18), BackColor = Color.FromArgb(240, 240, 240) };
        header.Controls.Add(new Label { Text = "Configure the tool to scan and transfer files from Vault to Procore Drive",
            Dock = DockStyle.Bottom, Height = 27, ForeColor = Color.WhiteSmoke });
        header.Controls.Add(new Label { Text = "Vault Transfer Tool", Dock = DockStyle.Top, Height = 40, 
            Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.WhiteSmoke });
        Controls.Add(tabs); Controls.Add(status); Controls.Add(header);
        BuildSettings(); BuildReview(); BuildHistory();
        ApplySettings(settings); RefreshReport();
        FormClosing += (U, e) =>
        {
            if (work is not null) { work.Cancel(); e.Cancel = true; status.Text = "Cancelling..."; }
        };
    }

    private Button Action(string text, Action clicked)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, Padding = new  Padding(12, 0, 12, 0) };
        button.Click += (_, _) => Guard(clicked); actions.Add(button); return button;
    }

    private static Label label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

    private void BuildSettings()
    {
        var page = new TabPage("Settings") { Padding = new Padding(12), AutoScroll = true };
        var layout = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(string text, Control control)
        {
            var row = Layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(label(text), 0, row); control.Dock = DockStyle.Top;
            control.Margin = new Padding(0, 6, 0, 12); layout.Controls.Add(control, 1, row);
        }
        mode.Items.AddRange([new Settings().Mode, Settings.LocalTest]);
        Add("Connection Mode", mode);
        Add("Connection Status", new Label { AutoSize = true, ForeColor = Color.DarkSlateGray,
            Text = "Testing only for now"});
        Control Folder(TextBox box)
        {
            var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            box.Dock = DockStyle.Fill;
            panel.Controls.Add(box);
            panel.Controls.Add(Action("Browse", () =>
            {
                using var dialog = new FolderBrowserDialog { SelectedPath = box.Text };
                if (dialog.ShowDialog() == DialogResult.OK) { box.Text = dialog.SelectedPath; }
            }));
            return panel;
        }
        Add("Source Folder", Folder(source));
        Add("Staging Folder", Folder(stage));
        Add("Destination Folder", Folder(destination));
        Add("Project Number Pattern", pattern);
        Add("File Extensions (comma-separated)", extensions);
        Add("Exclusions (one per line)", exclusions);
        Add("Folder Mappings (one per line, format: source=destination)", mappings);
        Add("Rules Format", new Label { AutoSize = true, 
            Text = "Extensions: .pdf, .sup, .wset, .wtool, etc. Exclusions: relative paths or file names. Mappings: relative source path=relative destination path." });
            var buttons = new FlowLayoutPanel { AutoSize = true };
            buttons.Controls.Add(Action("Save Settings", SaveSettings));
            buttons.Controls.Add(Action("Load Project Folders", LoadProjectFolders));
            buttons.Controls.Add(Action("Scan", async () => await ScanAsync()));
            Add("", buttons);
        page.Controls.Add(layout); tabs.TabPages.Add(page);
    }

    private void BuildReview()
    {
        var page = new TabPage("Review") { Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.Controls.Add(summary);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        filter.Items.AddRange(["All Items", "Pending", "Approved", "Rejected", "Failed", "Skipped"]);
        filter.SelectedIndex = 0; filter.SelectedIndexChanged += (_, _) => RefreshReport(); toolbar.Controls.Add(filter);
        toolbar.Controls.Add(Action("Approve Selected", () => Decide("Approved")));
        toolbar.Controls.Add(Action("Reject Selected", () => Decide("Rejected")));
        toolbar.Controls.Add(Action("Reset Selected", () => Decide("Pending")));
        toolbar.Controls.Add(Action("Copy Approved | Retry", async () => await CopyAsync()));
        toolbar.Controls.Add(Action("Export Report", ExportReport));
        cancel.Click += (_, _) => work?.Cancel(); toolbar.Controls.Add(cancel); layout.controls.Add(toolbar);
        foreach (var (name, width) in new[] { ("Decision", 65f), ("Status", 70f), ("Project", 80f), ("Source", 300f), ("Destination", 300f), ("Size", 80f), ("Reason", 120f) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, FillWeight = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.SelectionChanged += (_, _) => ShowDetails();
        layout.Controls.Add(grid); layout.Controls.Add(details); page.Controls.Add(layout); tabs.TabPages.Add(page);
        }

    private void BuildHistory()
    {
        var page = new TabPage("History") { Padding = new Padding(12) };
        var info = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
            Text = "This tool is designed to scan and transfer files from Vault to Procore Drive. It allows you to configure source and destination folders, specify file extensions, exclusions, and folder mappings. You can review the scan results, approve or reject items, and export reports for further analysis." };
        var open = Action("Open logs", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Saved... (*.jason)|*.json", InitialDirectory = store.ReportsFolder };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var loaded = AppStore.Read<ScanReport>(dialog.FileName);
            if (!Guid.TryParseExact(loaded.Id, "N", out _) || loaded.SettingsFingerprint != loaded.Settings.Fingerprint())
               throw  new InvalidDataException("The selected file is not valid.");
            report = loaded; RefreshReport(); tabs.SelectedIndex = 1;
            status.Text = "Opened";
        });
        open.Dock = DockStyle.Bottom;
        page.Controls.Add(info); page.Controls.Add(open); tabs.TabPages.Add(page);
    }

    private void ApplySettings(Settings value)
    {
        mode.SelectedItem = value.Mode; source.Text = value.SourceFolder; stage.Text = value.StagingFolder;
        destination.Text = value.DestinationFolder; pattern.Text = value.ProjectPattern; 
        extensions.Text = string.Join(",", value.Extensions); 
        mappings.Lines = value.ProjectFolders.Select(pair => pair.key + " = " + pair.Value).ToArray();
    }

    private Settings ReadSettings()
    {
        var value = new Settings { Mode = mode.SelectedItem?.ToString() ?? "", SourceFolder = source.Text.Trim(),
        StagingFolder = stage.Text.Trim(), DestinationFolder = destination.Text.Trim(), ProjectPattern = pattern.Text, 
        Extensions = extensions.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ExcludePatterns = exclusions.Lines.Select( s => s.Trim()).Where(s => s.Length > 0).ToArray() };
        foreach (var line in mappings.Lines.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !value.ProjectFolders.TryAdd(parts[0], parts[1]))
                throw new InvalidDataException("Use one unique project mapping per line: 100xxx = Project A\\Subfolder");
        }
        return value;
    }

    private void SaveSettings()
    {
        var  value = ReadSettings();
        if (value.Mode == Settings.LocalTest) TransferEngine.Validate(value);
        foreach (var path in new [] { value.SourceFolder, value.StagingFolder, value.DestinationFolder }.Where(path.IsPathFullyQualified))
        {
            var root = TransferEngine.Root(path) + Path.DirectorySeperatorChar;
            if ((store.Root + Path.DirectorySperatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The {path} folder cannot be a parent of the tool's root folder.");
        }
        store.SaveSettings(value); Settings = value;
        status.Text = report is not null && report.SettingsFingerprint != value.Fingerprint()
            ? "Settings saved." : "Settings saved. Scan again to update report.";
    }

    private void LoadSample()
    {
        var root = Path.Combine(store.Root, "Sample", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(input, "100001")); 
    }