using System.Diagnostics;

namespace VaultTransfer;

public sealed class MonitorForm : Form
{
    private readonly Catalog catalog;
    private readonly ScanCoordinator scans = new();
    private readonly string scriptFolder;
    private readonly NotifyIcon tray;
    private readonly DataGridView projectList = Grid();
    private readonly DataGridView fileList = Grid();
    private readonly DataGridView reviewList = Grid();
    private readonly DataGridView changeList = Grid();
    private readonly TextBox findProject = new() { Dock = DockStyle.Top, PlaceholderText = "Find a project" };
    private readonly Label projectTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
    private readonly Label projectDetail = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly TextBox server = new() { Width = 360 };
    private readonly TextBox database = new() { Width = 360 };
    private readonly Label vaultNote = new() { AutoSize = true, MaximumSize = new Size(520, 0), Visible = false, Text = "Taken from Vault Professional. Sign-in uses your Windows account." };
    private readonly Label procoreState = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 24, Value = 4, Width = 64 };
    private readonly CheckBox schedule = new() { Text = "Scan on a schedule", AutoSize = true, Checked = true };
    private readonly Panel projectsView = new() { Dock = DockStyle.Fill };
    private readonly Panel reviewView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel changesView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel settingsView = new() { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(20) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(12, 8, 12, 0) };
    private readonly List<Button> navigation = [];
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 30_000 };
    private DateTimeOffset nextScan = DateTimeOffset.UtcNow.AddMinutes(1);
    private string? selectedProject;
    private bool loading;
    private bool exiting;
    private CancellationTokenSource? running;

    public MonitorForm(Catalog catalog, string scriptFolder)
    {
        this.catalog = catalog;
        this.scriptFolder = scriptFolder;
        Text = "Vault Transfer";
        Font = new Font("Segoe UI", 10);
        Size = new Size(1180, 760);
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
        bar.Controls.Add(Nav("Projects", ShowProjects));
        bar.Controls.Add(Nav("To approve", ShowReview));
        bar.Controls.Add(Nav("Changes", ShowChanges));
        bar.Controls.Add(Nav("Settings", ShowSettings));
        var scan = new Button { Text = "Scan now", AutoSize = true, Margin = new Padding(24, 0, 0, 0) };
        scan.Click += async (_, _) => { SaveSettings(); await ScanAsync(manual: true); };
        bar.Controls.Add(scan);
        bar.Controls.Add(ActionButton("Sign in to Procore", SignInProcoreAsync));
        bar.Controls.Add(ActionButton("Scan Procore", ScanProcoreAsync));
        BuildProjects();
        BuildReview();
        BuildChanges();
        BuildSettings();
        Controls.Add(projectsView);
        Controls.Add(reviewView);
        Controls.Add(changesView);
        Controls.Add(settingsView);
        Controls.Add(bar);
        Controls.Add(status);
        server.Text = catalog.GetSetting("vault_server", "");
        database.Text = catalog.GetSetting("vault_database", "");
        ApplyVaultLogin();
        interval.Value = int.TryParse(catalog.GetSetting("interval_hours", "4"), out var hours) ? Math.Clamp(hours, 1, 24) : 4;
        schedule.Checked = catalog.GetSetting("schedule_enabled", "true") == "true";
        tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "Vault Transfer" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Scan now", null, async (_, _) => await ScanAsync(manual: true));
        menu.Items.Add("Sign in to Procore", null, async (_, _) => await SignInProcoreAsync());
        menu.Items.Add("Scan Procore", null, async (_, _) => await ScanProcoreAsync());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        clock.Tick += async (_, _) =>
        {
            if (schedule.Checked && DateTimeOffset.UtcNow >= nextScan) await ScanAsync(manual: false);
        };
        clock.Start();
        FormClosing += OnClosing;
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) return;
            Hide();
            WindowState = FormWindowState.Normal;
        };
        ShowProjects();
        Reload();
        UpdateProcoreSignIn();
        status.Text = "Next scan at " + nextScan.ToLocalTime().ToString("h:mm tt") + ".";
    }

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private Button Nav(string text, Action show)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Tag = text, Margin = new Padding(0, 0, 6, 0) };
        button.FlatAppearance.BorderColor = Color.FromArgb(190, 198, 206);
        button.Click += (_, _) => show();
        navigation.Add(button);
        return button;
    }

    private void MarkNav(string name)
    {
        foreach (var button in navigation)
            button.BackColor = button.Text == name ? Color.FromArgb(225, 232, 238) : SystemColors.Control;
        projectsView.Visible = name == "Projects";
        reviewView.Visible = name == "To approve";
        changesView.Visible = name == "Changes";
        settingsView.Visible = name == "Settings";
    }

    private void BuildProjects()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 220 };
        projectList.MultiSelect = false;
        projectList.Columns.Add("Number", "Number");
        projectList.Columns.Add("Name", "Procore project");
        projectList.Columns.Add("Match", "Match");
        projectList.Columns.Add("Pending", "To approve");
        projectList.SelectionChanged += (_, _) =>
        {
            if (loading || projectList.SelectedRows.Count == 0) return;
            selectedProject = projectList.SelectedRows[0].Tag as string;
            ShowFiles();
        };
        findProject.TextChanged += (_, _) => ReloadProjects();
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(projectList);
        left.Controls.Add(findProject);
        split.Panel1.Controls.Add(left);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        actions.Controls.Add(DecisionButton("Approve", fileList, "Approved"));
        actions.Controls.Add(DecisionButton("Deny", fileList, "Denied"));
        actions.Controls.Add(DecisionButton("Mark pending", fileList, "Pending"));
        var filePath = fileList.Columns.Add("Path", "Folder and file");
        fileList.Columns.Add("Version", "Version");
        fileList.Columns.Add("Approval", "Approval");
        fileList.Columns.Add("Transfer", "Transfer");
        fileList.Columns[filePath].FillWeight = 280;
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 8, 8) };
        var heading = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(projectTitle);
        heading.Controls.Add(projectDetail);
        right.Controls.Add(fileList);
        right.Controls.Add(actions);
        right.Controls.Add(heading);
        split.Panel2.Controls.Add(right);
        projectsView.Controls.Add(split);
    }

    private void BuildReview()
    {
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 12, 12, 8) };
        actions.Controls.Add(DecisionButton("Approve", reviewList, "Approved"));
        actions.Controls.Add(DecisionButton("Deny", reviewList, "Denied"));
        reviewList.Columns.Add("Project", "Project");
        reviewList.Columns.Add("Name", "Procore project");
        var reviewPath = reviewList.Columns.Add("Path", "Folder and file");
        reviewList.Columns.Add("Version", "Version");
        reviewList.Columns[reviewPath].FillWeight = 280;
        reviewView.Padding = new Padding(0, 0, 8, 8);
        reviewView.Controls.Add(reviewList);
        reviewView.Controls.Add(actions);
    }

    private void BuildChanges()
    {
        changeList.Columns.Add("When", "When");
        changeList.Columns.Add("Project", "Project");
        changeList.Columns.Add("Kind", "Change");
        changeList.Columns.Add("Version", "Version");
        var changePath = changeList.Columns.Add("Path", "Path");
        changeList.Columns[changePath].FillWeight = 280;
        changesView.Padding = new Padding(8);
        changesView.Controls.Add(changeList);
    }

    private void BuildSettings()
    {
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Add(string label, Control control)
        {
            var row = layout.RowCount++;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 12, 0) }, 0, row);
            control.Margin = new Padding(0, 4, 0, 8);
            layout.Controls.Add(control, 1, row);
        }
        Add("Vault server", server);
        Add("Vault database", database);
        Add("", vaultNote);
        Add("", procoreState);
        var procoreActions = new FlowLayoutPanel { AutoSize = true };
        procoreActions.Controls.Add(ActionButton("Sign in to Procore", SignInProcoreAsync));
        procoreActions.Controls.Add(ActionButton("Scan Procore", ScanProcoreAsync));
        Add("", procoreActions);
        Add("Scan every", interval);
        var hours = new Label { Text = "hours", AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        var hoursRow = new FlowLayoutPanel { AutoSize = true };
        hoursRow.Controls.Add(interval);
        hoursRow.Controls.Add(hours);
        layout.Controls.Remove(interval);
        layout.Controls.Add(hoursRow, 1, layout.RowCount - 1);
        Add("", schedule);
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        Add("", save);
        settingsView.Controls.Add(layout);
    }

    private Button DecisionButton(string text, DataGridView grid, string decision)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => Decide(grid, decision);
        return button;
    }

    private void Decide(DataGridView grid, string decision)
    {
        var ids = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => (string)row.Tag!).ToArray();
        if (ids.Length == 0)
        {
            status.Text = "Select one or more files.";
            return;
        }
        try
        {
            catalog.SetApproval(ids, decision);
            status.Text = decision switch
            {
                "Approved" => ids.Length + " " + Plural(ids.Length, "file") + " approved. Nothing has been uploaded.",
                "Denied" => ids.Length + " " + Plural(ids.Length, "file") + " denied.",
                _ => ids.Length + " " + Plural(ids.Length, "file") + " marked pending."
            };
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Vault Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        Reload();
    }

    private static string Plural(int count, string word) => count == 1 ? word : word + "s";

    private void ShowProjects() { MarkNav("Projects"); }
    private void ShowReview() { MarkNav("To approve"); }
    private void ShowChanges() { MarkNav("Changes"); }
    private void ShowSettings() { MarkNav("Settings"); }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, RowHeadersVisible = false, BackgroundColor = Color.White,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, BorderStyle = BorderStyle.None
    };

    private void SaveSettings()
    {
        catalog.SetSetting("vault_server", server.Text.Trim());
        catalog.SetSetting("vault_database", database.Text.Trim());
        catalog.SetSetting("interval_hours", interval.Value.ToString());
        catalog.SetSetting("schedule_enabled", schedule.Checked ? "true" : "false");
        status.Text = "Saved.";
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (exiting)
        {
            running?.Cancel();
            clock.Stop();
            tray.Visible = false;
            tray.Dispose();
            return;
        }
        e.Cancel = true;
        Hide();
        tray.ShowBalloonTip(2000, "Vault Transfer", "Vault Transfer is still running.", ToolTipIcon.Info);
    }

    private async Task ScanAsync(bool manual)
    {
        var started = await scans.TryRun(async cancel =>
        {
            running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                status.Text = "Scanning Vault.";
                await ScanVault(running.Token);
                var savedSignIn = File.Exists(ProcoreTokenPath());
                status.Text = savedSignIn ? "Signing in to Procore." : "Signing in to Procore. A browser window will open once.";
                await ScanProcore(running.Token);
                nextScan = DateTimeOffset.UtcNow.AddHours((double)interval.Value);
                status.Text = "Scan finished. Next scan at " + nextScan.ToLocalTime().ToString("h:mm tt") + ".";
                tray.ShowBalloonTip(2000, "Vault Transfer", "Scan finished.", ToolTipIcon.Info);
            }
            finally { running.Dispose(); running = null; }
        });
        if (!started && manual) status.Text = "A scan is already running.";
        Reload();
    }

    private async Task SignInProcoreAsync()
    {
        var started = await scans.TryRun(async cancel =>
        {
            running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                status.Text = "Opening Procore sign-in. Approve it in the browser.";
                var script = Path.Combine(scriptFolder, "ConnectProcoreProduction.ps1");
                await RunProcess("pwsh", "-NoProfile -File \"" + script + "\" -SignInOnly", running.Token);
                if (!File.Exists(ProcoreTokenPath())) throw new InvalidDataException("Procore sign-in did not save.");
                status.Text = "Procore sign-in saved.";
            }
            catch (Exception error)
            {
                status.Text = error.Message;
            }
            finally { running.Dispose(); running = null; }
        });
        if (!started) status.Text = "A scan is already running.";
        UpdateProcoreSignIn();
    }

    private async Task ScanProcoreAsync()
    {
        var started = await scans.TryRun(async cancel =>
        {
            running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            try
            {
                var savedSignIn = File.Exists(ProcoreTokenPath());
                status.Text = savedSignIn ? "Scanning Procore." : "Scanning Procore. A browser window will open for sign-in.";
                await ScanProcore(running.Token);
            }
            finally { running.Dispose(); running = null; }
        });
        if (!started) status.Text = "A scan is already running.";
        UpdateProcoreSignIn();
        Reload();
    }

    private static string ProcoreTokenPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultTransfer", "procore.refresh");

    private void UpdateProcoreSignIn() =>
        procoreState.Text = File.Exists(ProcoreTokenPath())
            ? "Procore sign-in is saved on this computer."
            : "Procore sign-in is not saved. Use Sign in to Procore, then Scan Procore.";

    private void ApplyVaultLogin()
    {
        try
        {
            var login = VaultProfessionalLogin.Read();
            if (login is null) return;
            server.Text = login.Value.Server;
            database.Text = login.Value.Database;
            server.ReadOnly = true;
            database.ReadOnly = true;
            vaultNote.Visible = true;
        }
        catch (InvalidDataException error)
        {
            status.Text = error.Message;
        }
    }

    private async Task ScanVault(CancellationToken cancel)
    {
        ApplyVaultLogin();
        status.Text = "Signing in to Vault.";
        var id = catalog.StartScan("Vault");
        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        try
        {
            var script = Path.Combine(scriptFolder, "VaultConnectionCheck.ps1");
            var arguments = "-NoProfile -File \"" + script + "\" -ScanProjects";
            if (!server.ReadOnly && server.Text.Trim().Length > 0 && database.Text.Trim().Length > 0)
                arguments += " -Server \"" + server.Text.Trim() + "\" -Vault \"" + database.Text.Trim() + "\"";
            var output = await RunProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", arguments, cancel);
            var inventory = Newest(Path.Combine(scriptFolder, "reports"), "inventory.jsonl", started);
            if (inventory is null || !Catalog.VaultScanFinished(Path.Combine(Path.GetDirectoryName(inventory)!, "scan-summary.json")))
                throw new InvalidDataException("Vault scan did not finish. " + Tail(output));
            var count = catalog.ApplyVaultInventory(Catalog.ReadVaultJsonl(inventory), true);
            catalog.FinishScan(id, "Succeeded", count + " changes.");
        }
        catch (Exception error)
        {
            catalog.FinishScan(id, "Failed", Tail(error.Message));
            status.Text = error.Message;
        }
    }

    private async Task ScanProcore(CancellationToken cancel)
    {
        var id = catalog.StartScan("Procore");
        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        try
        {
            var script = Path.Combine(scriptFolder, "ConnectProcoreProduction.ps1");
            var output = await RunProcess("pwsh", "-NoProfile -File \"" + script + "\" -IncludeDetails -SkipComparison", cancel);
            var report = Newest(Path.Combine(scriptFolder, "reports"), "projects.json", started);
            if (report is null) throw new InvalidDataException("Procore scan did not finish. " + Tail(output));
            var count = catalog.ApplyProcoreProjects(Catalog.ReadProcoreProjects(report));
            catalog.FinishScan(id, "Succeeded", count + " changes.");
            status.Text = "Procore scan finished. " + count + " " + Plural(count, "change") + ".";
        }
        catch (Exception error)
        {
            catalog.FinishScan(id, "Failed", Tail(error.Message));
            status.Text = error.Message;
        }
    }

    private static async Task<string> RunProcess(string fileName, string arguments, CancellationToken cancel)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName, Arguments = arguments, WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        process.Start();
        var read = Task.WhenAll(process.StandardOutput.ReadToEndAsync(cancel), process.StandardError.ReadToEndAsync(cancel));
        await process.WaitForExitAsync(cancel);
        var output = await read;
        if (process.ExitCode != 0) throw new InvalidDataException(Tail(output[0] + "\n" + output[1]));
        return output[0] + "\n" + output[1];
    }

    private static string? Newest(string folder, string name, DateTimeOffset started)
    {
        if (!Directory.Exists(folder)) return null;
        return Directory.GetFiles(folder, name, SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTimeUtc >= started.UtcDateTime)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private static string Tail(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 400 ? text : text[^400..];
    }

    private static string MatchLabel(string match) => match switch
    {
        "Exact" => "Matched",
        "Vault" => "Vault only",
        "Procore" => "Procore only",
        _ => match
    };

    private static string TransferLabel(string status) => status == "Observed" ? "Not transferred" : status;

    private static string ChangeLabel(string kind) => kind switch
    {
        "FileAdded" => "File added",
        "FileRemoved" => "File removed",
        "VersionChanged" => "Version changed",
        "ProcoreLinked" => "Procore linked",
        "ActiveChanged" => "Active changed",
        "NeedsReview" => "Needs review",
        "Approved" => "Approved",
        "Denied" => "Denied",
        "Pending" => "Marked pending",
        _ => kind
    };

    private void Reload()
    {
        ReloadProjects();
        ShowFiles();
        ReloadReview();
        ReloadChanges();
    }

    private void ReloadProjects()
    {
        loading = true;
        var query = findProject.Text.Trim();
        projectList.Rows.Clear();
        foreach (var project in catalog.Projects())
        {
            if (query.Length > 0 && project.Number.Contains(query, StringComparison.OrdinalIgnoreCase) == false
                && project.ProcoreName.Contains(query, StringComparison.OrdinalIgnoreCase) == false
                && project.VaultFolder.Contains(query, StringComparison.OrdinalIgnoreCase) == false) continue;
            var index = projectList.Rows.Add(project.Number, project.ProcoreName, MatchLabel(project.Match), project.Pending);
            projectList.Rows[index].Tag = project.Number;
            if (project.Number == selectedProject) projectList.Rows[index].Selected = true;
        }
        loading = false;
        if (projectList.SelectedRows.Count == 0) selectedProject = null;
    }

    private void ShowFiles()
    {
        fileList.Rows.Clear();
        var project = catalog.Projects().SingleOrDefault(item => item.Number == selectedProject);
        if (project is null)
        {
            projectTitle.Text = "Select a project";
            projectDetail.Text = "";
            return;
        }
        projectTitle.Text = project.Number + (project.ProcoreName.Length > 0 ? "  " + project.ProcoreName : "");
        projectDetail.Text = "Vault folder: " + project.VaultFolder
            + "\nProcore project: " + (project.ProcoreId.Length == 0 ? "none" : project.ProcoreId)
            + (project.Active.Length == 0 ? "" : " (" + project.Active.ToLowerInvariant() + ")");
        foreach (var file in catalog.Files(project.Number, null))
        {
            var index = fileList.Rows.Add(file.DocumentsPath, file.Version, file.Approval, TransferLabel(file.TransferStatus));
            fileList.Rows[index].Tag = file.FileId;
        }
    }

    private void ReloadReview()
    {
        reviewList.Rows.Clear();
        var projects = catalog.Projects().ToDictionary(project => project.Number);
        foreach (var file in catalog.Files(null, "Pending"))
        {
            if (!projects.TryGetValue(file.ProjectNumber, out var project) || project.Match != "Exact") continue;
            var index = reviewList.Rows.Add(file.ProjectNumber, project.ProcoreName, file.DocumentsPath, file.Version);
            reviewList.Rows[index].Tag = file.FileId;
        }
    }

    private void ReloadChanges()
    {
        changeList.Rows.Clear();
        foreach (var change in catalog.Changes(300))
            changeList.Rows.Add(change.When, change.Project, ChangeLabel(change.Kind), change.Version, change.Path);
    }
}
