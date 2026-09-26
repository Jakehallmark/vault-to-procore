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
    private readonly Label vaultNote = new() { AutoSize = true, MaximumSize = new Size(520, 0), Text = "Vault uses the server and database saved in Vault Professional." };
    private readonly Label procoreState = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 24, Value = 4, Width = 64 };
    private readonly CheckBox schedule = new() { Text = "Scan on a schedule", AutoSize = true, Checked = true };
    private readonly Panel projectsView = new() { Dock = DockStyle.Fill };
    private readonly Panel reviewView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel changesView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel settingsView = new() { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(20) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(16, 8, 16, 0), AutoEllipsis = true };
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
        BackColor = Color.FromArgb(244, 246, 248);
        Size = new Size(1180, 760);
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        var bar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.White, Padding = new Padding(16, 12, 16, 12) };
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(220, 224, 228) });
        var navigationBar = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.White };
        navigationBar.Controls.Add(Nav("Projects", ShowProjects));
        navigationBar.Controls.Add(Nav("To approve", ShowReview));
        navigationBar.Controls.Add(Nav("Changes", ShowChanges));
        navigationBar.Controls.Add(Nav("Settings", ShowSettings));
        var scan = PrimaryButton("Scan");
        scan.Dock = DockStyle.Right;
        scan.Click += async (_, _) => { SaveSettings(); await ScanAsync(manual: true); };
        bar.Controls.Add(scan);
        bar.Controls.Add(navigationBar);
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
        ApplyVaultLogin();
        interval.Value = int.TryParse(catalog.GetSetting("interval_hours", "4"), out var hours) ? Math.Clamp(hours, 1, 24) : 4;
        schedule.Checked = catalog.GetSetting("schedule_enabled", "true") == "true";
        tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "Vault Transfer" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Scan", null, async (_, _) => await ScanAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
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
        status.BackColor = Color.White;
        status.Text = "Next scan at " + nextScan.ToLocalTime().ToString("h:mm tt") + ".";
    }

    private static Button PrimaryButton(string text)
    {
        var button = new Button
        {
            Text = text, Width = 96, Height = 30, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            BackColor = Color.FromArgb(32, 54, 86), ForeColor = Color.White
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 72, 110);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(24, 40, 66);
        return button;
    }

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = QuietButton(text);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Button QuietButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(10, 4, 10, 4) };
        button.FlatAppearance.BorderColor = Color.FromArgb(190, 198, 206);
        return button;
    }

    private Button Nav(string text, Action show)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Tag = text,
            Margin = new Padding(0, 0, 4, 0), Padding = new Padding(12, 4, 12, 4)
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => show();
        navigation.Add(button);
        return button;
    }

    private void MarkNav(string name)
    {
        foreach (var button in navigation)
        {
            button.BackColor = button.Text == name ? Color.FromArgb(232, 237, 242) : Color.White;
            button.ForeColor = button.Text == name ? Color.FromArgb(32, 54, 86) : Color.FromArgb(55, 65, 74);
        }
        projectsView.Visible = name == "Projects";
        reviewView.Visible = name == "To approve";
        changesView.Visible = name == "Changes";
        settingsView.Visible = name == "Settings";
    }

    private void BuildProjects()
    {
        projectsView.BackColor = Color.White;
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 280, BackColor = Color.White };
        projectList.MultiSelect = false;
        projectList.Columns.Add("Number", "Number");
        projectList.Columns.Add("Name", "Procore project");
        projectList.Columns.Add("Match", "Match");
        projectList.Columns.Add("Approval", "Approval");
        projectList.SelectionChanged += (_, _) =>
        {
            if (loading || projectList.SelectedRows.Count == 0) return;
            selectedProject = projectList.SelectedRows[0].Tag as string;
            ShowFiles();
        };
        findProject.TextChanged += (_, _) => ReloadProjects();
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        left.Controls.Add(projectList);
        left.Controls.Add(findProject);
        split.Panel1.Controls.Add(left);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 4, 0, 8) };
        actions.Controls.Add(ActionRow("Project", [
            ProjectDecisionButton("Approve", () => selectedProject is null ? [] : [selectedProject]),
            ProjectDecisionButton("Deny", () => selectedProject is null ? [] : [selectedProject], "Denied"),
            ProjectDecisionButton("Mark pending", () => selectedProject is null ? [] : [selectedProject], "Pending")
        ]));
        actions.Controls.Add(ActionRow("Selected files", [
            DecisionButton("Approve", fileList, "Approved"),
            DecisionButton("Deny", fileList, "Denied"),
            DecisionButton("Mark pending", fileList, "Pending")
        ]));
        var filePath = fileList.Columns.Add("Path", "Folder and file");
        fileList.Columns.Add("Version", "Version");
        fileList.Columns.Add("Approval", "Approval");
        fileList.Columns.Add("Transfer", "Transfer");
        fileList.Columns[filePath].FillWeight = 280;
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8), BackColor = Color.White };
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
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 16, 12, 8), BackColor = Color.White };
        actions.Controls.Add(ProjectDecisionButton("Approve", () => SelectedProjectNumbers(reviewList)));
        actions.Controls.Add(ProjectDecisionButton("Deny", () => SelectedProjectNumbers(reviewList), "Denied"));
        actions.Controls.Add(ProjectDecisionButton("Mark pending", () => SelectedProjectNumbers(reviewList), "Pending"));
        reviewList.Columns.Add("Project", "Project");
        reviewList.Columns.Add("Name", "Procore project");
        reviewList.Columns.Add("Match", "Match");
        reviewView.BackColor = Color.White;
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
        changesView.BackColor = Color.White;
        changesView.Padding = new Padding(16, 12, 16, 8);
        changesView.Controls.Add(changeList);
    }

    private void BuildSettings()
    {
        settingsView.BackColor = Color.White;
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
        Add("Vault", vaultNote);
        Add("Procore", procoreState);
        Add("", ActionButton("Sign in to Procore", SignInProcoreAsync));
        Add("Scan every", interval);
        var hours = new Label { Text = "hours", AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
        var hoursRow = new FlowLayoutPanel { AutoSize = true };
        hoursRow.Controls.Add(interval);
        hoursRow.Controls.Add(hours);
        layout.Controls.Remove(interval);
        layout.Controls.Add(hoursRow, 1, layout.RowCount - 1);
        Add("", schedule);
        var save = QuietButton("Save");
        save.Click += (_, _) => SaveSettings();
        Add("", save);
        settingsView.Controls.Add(layout);
    }

    private static FlowLayoutPanel ActionRow(string caption, Button[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 6, 12, 0), MinimumSize = new Size(110, 0) });
        foreach (var button in buttons) row.Controls.Add(button);
        return row;
    }

    private Button ProjectDecisionButton(string text, Func<string[]> numbers, string decision = "Approved")
    {
        var button = QuietButton(text);
        button.Click += (_, _) => DecideProjects(numbers(), decision);
        return button;
    }

    private static string[] SelectedProjectNumbers(DataGridView grid) =>
        grid.SelectedRows.Cast<DataGridViewRow>().Select(row => (string)row.Tag!).ToArray();

    private void DecideProjects(IReadOnlyList<string> numbers, string decision)
    {
        if (numbers.Count == 0)
        {
            status.Text = "Select one or more projects.";
            return;
        }
        try
        {
            catalog.SetProjectApproval(numbers, decision);
            status.Text = decision switch
            {
                "Approved" => numbers.Count + " " + Plural(numbers.Count, "project") + " approved. Nothing has been uploaded.",
                "Denied" => numbers.Count + " " + Plural(numbers.Count, "project") + " denied.",
                _ => numbers.Count + " " + Plural(numbers.Count, "project") + " marked pending."
            };
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Vault Transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        Reload();
    }

    private Button DecisionButton(string text, DataGridView grid, string decision)
    {
        var button = QuietButton(text);
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

    private static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoGenerateColumns = false, RowHeadersVisible = false, BackgroundColor = Color.White,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(230, 234, 238), EnableHeadersVisualStyles = false, ColumnHeadersHeight = 34
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 246, 248);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(50, 58, 66);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 230, 240);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 24, 28);
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        grid.RowTemplate.Height = 30;
        return grid;
    }

    private void SaveSettings()
    {
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
                string vaultMessage;
                try { vaultMessage = await ScanVault(running.Token); }
                catch (Exception error)
                {
                    status.Text = error.Message;
                    return;
                }
                var savedSignIn = File.Exists(ProcoreTokenPath());
                status.Text = savedSignIn ? "Signing in to Procore." : "Signing in to Procore. A browser window will open once.";
                string procoreMessage;
                try { procoreMessage = await ScanProcore(running.Token); }
                catch (Exception error)
                {
                    status.Text = vaultMessage + " Procore scan failed. " + error.Message;
                    return;
                }
                nextScan = DateTimeOffset.UtcNow.AddHours((double)interval.Value);
                status.Text = vaultMessage + " " + procoreMessage + " " + ComparisonText()
                    + " Next scan at " + nextScan.ToLocalTime().ToString("h:mm tt") + ".";
                tray.ShowBalloonTip(2000, "Vault Transfer", ComparisonText(), ToolTipIcon.Info);
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
                var progress = new Progress<string>(text => status.Text = text);
                await new ProcoreClient().SignIn(scriptFolder, progress, running.Token);
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

    private static string ProcoreTokenPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultTransfer", "procore.refresh");

    private void UpdateProcoreSignIn() =>
        procoreState.Text = File.Exists(ProcoreTokenPath())
            ? "Procore sign-in is saved on this computer."
            : "Procore sign-in is not saved.";

    private void ApplyVaultLogin()
    {
        try
        {
            var login = VaultProfessionalLogin.Read();
            vaultNote.Text = login is null
                ? "Vault uses the server and database saved in Vault Professional."
                : "Vault Professional: " + login.Value.Server + " / " + login.Value.Database;
        }
        catch (InvalidDataException error)
        {
            vaultNote.Text = error.Message;
        }
    }

    private async Task<string> ScanVault(CancellationToken cancel)
    {
        ApplyVaultLogin();
        status.Text = "Signing in to Vault.";
        var id = catalog.StartScan("Vault");
        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        try
        {
            var script = Path.Combine(scriptFolder, "VaultConnectionCheck.ps1");
            if (!File.Exists(script)) throw new InvalidDataException("Vault scan script is not next to the app.");
            var arguments = "-NoProfile -File \"" + script + "\" -ScanProjects -ProjectsOnly";
            var progress = new Progress<string>(text => { if (text.StartsWith("Scanning:", StringComparison.Ordinal)) status.Text = text; });
            var output = await RunProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", arguments, progress, cancel);
            var inventory = Newest(Path.Combine(scriptFolder, "reports"), "inventory.jsonl", started);
            var summaryPath = inventory is null ? "" : Path.Combine(Path.GetDirectoryName(inventory)!, "scan-summary.json");
            if (inventory is null || !Catalog.VaultScanFinished(summaryPath))
                throw new InvalidDataException("Vault scan did not finish. " + Tail(output));
            var mode = Catalog.VaultScanMode(summaryPath);
            var count = mode == "Projects"
                ? catalog.ApplyVaultProjects(Catalog.ReadVaultProjects(inventory))
                : catalog.ApplyVaultInventory(Catalog.ReadVaultJsonl(inventory), Catalog.VaultScanRemovesMissing(summaryPath));
            catalog.FinishScan(id, "Succeeded", count + (mode == "Projects" ? " Vault projects." : " Vault changes."));
            return count + " Vault projects.";
        }
        catch (Exception error)
        {
            catalog.FinishScan(id, "Failed", Tail(error.Message));
            throw;
        }
    }

    private async Task<string> ScanProcore(CancellationToken cancel)
    {
        var id = catalog.StartScan("Procore");
        try
        {
            ShowProjects();
            var progress = new Progress<string>(text => status.Text = text);
            var result = await new ProcoreClient().Scan(scriptFolder, catalog.ProcoreUnchanged, catalog.SaveProcoreProject, progress, cancel);
            catalog.FinishScan(id, "Succeeded", result.Read + " changed, " + result.Unchanged + " unchanged.");
            return result.Read == 0
                ? result.Unchanged + " Procore projects were already current."
                : "Read " + result.Read + " Procore projects. " + result.Unchanged + " were already current.";
        }
        catch (Exception error)
        {
            catalog.FinishScan(id, "Failed", Tail(error.Message));
            throw;
        }
    }

    private string ComparisonText()
    {
        if (!catalog.HasSucceededScan("Vault") || !catalog.HasSucceededScan("Procore"))
            return catalog.HasSucceededScan("Vault")
                ? "Procore has not been scanned, so projects are not matched."
                : "Vault has not been scanned, so projects are not matched.";
        var counts = catalog.MatchCounts();
        return "Matched " + counts.Matched + ". Vault only " + counts.VaultOnly
            + ". Procore only " + counts.ProcoreOnly + ". Needs review " + counts.NeedsReview + ".";
    }

    private static async Task<string> RunProcess(string fileName, string arguments, IProgress<string> progress, CancellationToken cancel)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName, Arguments = arguments, WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (output) output.AppendLine(eventArgs.Data);
            progress.Report(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            lock (output) output.AppendLine(eventArgs.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancel);
        process.WaitForExit();
        string text;
        lock (output) text = output.ToString();
        if (process.ExitCode != 0) throw new InvalidDataException(PlainError(text));
        return text;
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

    private static string PlainError(string text)
    {
        foreach (var raw in text.Split('\r', '\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('+') || line.StartsWith("At ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("throw ", StringComparison.Ordinal) || line.StartsWith("CategoryInfo", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("FullyQualifiedErrorId", StringComparison.Ordinal) || line.Contains("RuntimeException", StringComparison.Ordinal)) continue;
            return line.Length <= 240 ? line : line[..240];
        }
        return "The scan did not finish.";
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
            var index = projectList.Rows.Add(project.Number, project.ProcoreName, MatchLabel(project.Match), project.Approval.Length == 0 ? "" : project.Approval);
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
        projectDetail.Text = MatchLabel(project.Match)
            + "\nVault folder: " + (project.VaultFolder.Length == 0 ? "none" : project.VaultFolder)
            + "\nProcore project: " + (project.ProcoreId.Length == 0 ? "none" : project.ProcoreId)
            + (project.Active.Length == 0 ? "" : " (" + project.Active.ToLowerInvariant() + ")")
            + (project.Approval.Length == 0 ? "" : "\nCheck: " + project.Approval);
        foreach (var file in catalog.Files(project.Number, null))
        {
            var index = fileList.Rows.Add(file.DocumentsPath, file.Version, file.Approval, TransferLabel(file.TransferStatus));
            fileList.Rows[index].Tag = file.FileId;
        }
    }

    private void ReloadReview()
    {
        reviewList.Rows.Clear();
        foreach (var project in catalog.Projects().Where(project => project.Approval == "Pending"))
        {
            var index = reviewList.Rows.Add(project.Number, project.ProcoreName, MatchLabel(project.Match));
            reviewList.Rows[index].Tag = project.Number;
        }
    }

    private void ReloadChanges()
    {
        changeList.Rows.Clear();
        foreach (var change in catalog.Changes(300))
            changeList.Rows.Add(change.When, change.Project, ChangeLabel(change.Kind), change.Version, change.Path);
    }
}
