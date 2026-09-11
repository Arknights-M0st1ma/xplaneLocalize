using System.Globalization;

namespace XPlaneEfbBridge;

// Folder picker without the Windows shell dialog.
//
// The shell dialog (FolderBrowserDialog / IFileDialog) loads installed shell
// namespace extensions. One misbehaving third party extension (cloud drives,
// download managers, file managers) can hang or take the whole process down -
// including the tray icon and the running service. This picker is plain WinForms,
// so it cannot do that, and it accepts a pasted path as well.
internal sealed class FolderPicker : Form
{
    private readonly float scale;
    private readonly Func<string, (string Text, bool Ok)> describe;
    private readonly TextBox pathBox = new();
    private readonly TreeView tree = new();
    private readonly Label hint = new();
    private readonly Button confirm = new();

    private int S(int pixels) => (int)Math.Round(pixels * scale);

    public string SelectedPath { get; private set; } = "";

    public FolderPicker(string title, string description, string initial, Func<string, (string Text, bool Ok)> describe)
    {
        this.describe = describe;
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        scale = dpi / 96f;
        Text = title;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = SettingsForm.PickFont();
        BackColor = Color.White;
        ClientSize = new Size(S(700), S(540));
        MinimumSize = new Size(S(520), S(400));
        Icon = AppIcon.Load();
        Build(description);
        Populate();
        if (initial.Length > 0)
        {
            pathBox.Text = initial;
            Navigate(initial);
        }
        else
        {
            SetSelection("");
        }
    }

    private void Build(string description)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(S(14), S(12), S(14), S(12))
        };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var caption = new Label
        {
            Text = description,
            AutoSize = true,
            MaximumSize = new Size(S(650), 0),
            Margin = new Padding(0, 0, 0, S(8))
        };
        grid.Controls.Add(caption, 0, 0);

        var pathRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, S(8)) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathBox.Dock = DockStyle.Fill;
        pathBox.Margin = new Padding(0, 0, S(6), 0);
        pathBox.PlaceholderText = "也可以直接把文件夹路径粘贴到这里";
        pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(pathBox.Text.Trim()); } };
        var go = new Button { Text = "转到", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(70), S(28)), Margin = new Padding(0) };
        go.Click += (_, _) => Navigate(pathBox.Text.Trim());
        pathRow.Controls.Add(pathBox, 0, 0);
        pathRow.Controls.Add(go, 1, 0);
        grid.Controls.Add(pathRow, 0, 1);

        tree.Dock = DockStyle.Fill;
        tree.BorderStyle = BorderStyle.FixedSingle;
        tree.HideSelection = false;
        tree.ShowLines = true;
        tree.ShowRootLines = true;
        tree.BeforeExpand += (_, e) => { if (e.Node is not null) EnsureChildren(e.Node); };
        tree.AfterSelect += (_, e) => { if (e.Node?.Tag is string path) SetSelection(path); };
        tree.NodeMouseDoubleClick += (_, e) => { if (e.Node?.Tag is string) Accept(); };
        grid.Controls.Add(tree, 0, 2);

        hint.AutoSize = true;
        hint.MaximumSize = new Size(S(650), 0);
        hint.Margin = new Padding(0, S(8), 0, 0);
        grid.Controls.Add(hint, 0, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, S(10), 0, 0) };
        confirm.Text = "选择这个文件夹";
        confirm.AutoSize = true;
        confirm.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        confirm.MinimumSize = new Size(S(140), S(32));
        confirm.Click += (_, _) => Accept();
        var cancel = new Button { Text = "取消", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(90), S(32)), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(buttons, 0, 4);

        Controls.Add(grid);
        CancelButton = cancel;
    }

    private void Accept()
    {
        var path = pathBox.Text.Trim();
        if (path.Length == 0 || !Directory.Exists(path))
        {
            ShowHint($"这个文件夹不存在：{path}", false);
            return;
        }
        SelectedPath = path;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetSelection(string path)
    {
        SelectedPath = path;
        if (path.Length > 0) pathBox.Text = path;
        if (path.Length == 0) { hint.Text = ""; return; }
        var (text, ok) = describe(path);
        ShowHint(text, ok);
    }

    private void ShowHint(string text, bool ok)
    {
        hint.Text = text;
        hint.ForeColor = ok ? Color.FromArgb(21, 115, 71) : Color.FromArgb(178, 34, 34);
    }

    // ---------------------------------------------------------------- the tree
    private void Populate()
    {
        tree.BeginUpdate();
        try
        {
            var places = new TreeNode("常用位置");
            Add(places, "桌面", SafeFolder(Environment.SpecialFolder.DesktopDirectory));
            Add(places, "文档", SafeFolder(Environment.SpecialFolder.MyDocuments));
            Add(places, "下载", SafeFolder(Environment.SpecialFolder.UserProfile, "Downloads"));
            Add(places, "用户目录", SafeFolder(Environment.SpecialFolder.UserProfile));
            if (places.Nodes.Count > 0) tree.Nodes.Add(places);

            var computer = new TreeNode("此电脑");
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                    var label = drive.VolumeLabel.Length > 0 ? $"{drive.Name.TrimEnd('\\')}  {drive.VolumeLabel}" : drive.Name;
                    Add(computer, label, drive.RootDirectory.FullName);
                }
                catch { /* a drive that disappears mid-enumeration */ }
            }
            tree.Nodes.Add(computer);
        }
        finally { tree.EndUpdate(); }
    }

    private static string SafeFolder(Environment.SpecialFolder folder, string? child = null)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            if (child is not null && path.Length > 0) path = Path.Combine(path, child);
            return path;
        }
        catch { return ""; }
    }

    private void Add(TreeNode parent, string label, string path)
    {
        if (path.Length == 0 || !Directory.Exists(path)) return;
        var node = new TreeNode(label) { Tag = path };
        node.Nodes.Add(new TreeNode("…"));
        parent.Nodes.Add(node);
    }

    // Directories are read when a node is opened, never up front: enumerating a
    // whole drive on a slow disk would freeze the window.
    private void EnsureChildren(TreeNode node)
    {
        if (node.Tag is not string path) return;
        if (node.Nodes.Count == 1 && node.Nodes[0].Tag is null) node.Nodes.Clear();
        if (node.Nodes.Count > 0) return;
        tree.BeginUpdate();
        try
        {
            var count = 0;
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                if (count++ > 500) break;
                string name;
                try
                {
                    name = Path.GetFileName(child);
                    if (name.Length == 0) continue;
                    var attributes = File.GetAttributes(child);
                    if ((attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0) continue;
                }
                catch { continue; }
                var childNode = new TreeNode(name) { Tag = child };
                childNode.Nodes.Add(new TreeNode("…"));
                node.Nodes.Add(childNode);
            }
        }
        catch { /* unreadable folder: leave it empty instead of failing */ }
        finally { tree.EndUpdate(); }
    }

    private void Navigate(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return;
        string full;
        try { full = Path.GetFullPath(path); } catch { ShowHint("路径格式不正确。", false); return; }
        if (!Directory.Exists(full)) { ShowHint($"这个文件夹不存在：{full}", false); return; }
        var rootPath = Path.GetPathRoot(full) ?? "";
        var node = FindRoot(rootPath);
        if (node is not null)
        {
            foreach (var part in full[rootPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                EnsureChildren(node);
                TreeNode? next = null;
                foreach (TreeNode child in node.Nodes)
                {
                    if (child.Tag is string childPath && string.Equals(Path.GetFileName(childPath), part, StringComparison.OrdinalIgnoreCase)) { next = child; break; }
                }
                if (next is null) break;
                node = next;
            }
            node.Expand();
            node.EnsureVisible();
            tree.SelectedNode = node;
        }
        SetSelection(full);
    }

    private TreeNode? FindRoot(string rootPath)
    {
        foreach (TreeNode group in tree.Nodes)
        {
            foreach (TreeNode node in group.Nodes)
            {
                if (node.Tag is not string path) continue;
                try
                {
                    if (string.Equals(Path.GetPathRoot(path), rootPath, StringComparison.OrdinalIgnoreCase) && string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
                        return node;
                }
                catch { }
            }
        }
        return null;
    }
}
