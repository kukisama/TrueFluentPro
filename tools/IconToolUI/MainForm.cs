using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Principal;
using IconTool.Core;

namespace IconToolUI;

/// <summary>
/// 目录图标管理工具主窗体。
/// </summary>
public class MainForm : Form
{
    private readonly TextBox _pathBox;
    private readonly Button _browseBtn;
    private readonly Button _loadBtn;
    private readonly FlowLayoutPanel _iconGrid;
    private readonly Label _statusLabel;
    private readonly Button _setIconBtn;
    private readonly ProgressBar _progressBar;

    private IconItemData? _selectedIcon;
    private Panel? _selectedPanel;
    private readonly List<IconItemData> _icons = [];
    private readonly bool _isAdmin;

    public MainForm(string[] args)
    {
        Text = "IconTool UI — 目录图标管理";
        Size = new Size(920, 660);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(243, 243, 243);

        _isAdmin = CheckIsAdmin();
        if (_isAdmin) Text += " [管理员]";

        // ── 顶部面板 ──
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.FromArgb(230, 230, 230)
        };

        var dirLabel = new Label
        {
            Text = "📁 目录",
            AutoSize = true,
            Location = new Point(12, 14),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
        };

        _pathBox = new TextBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(80, 10),
            Width = topPanel.Width - 80 - 160,
            PlaceholderText = "输入目录路径或点击浏览..."
        };
        _pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { LoadIcons(); e.SuppressKeyPress = true; } };

        _browseBtn = new Button
        {
            Text = "浏览",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Size = new Size(68, 28),
            FlatStyle = FlatStyle.System
        };
        _browseBtn.Click += BrowseBtn_Click;

        _loadBtn = new Button
        {
            Text = "加载",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Size = new Size(68, 28),
            FlatStyle = FlatStyle.System
        };
        _loadBtn.Click += (_, _) => LoadIcons();

        topPanel.Controls.AddRange([dirLabel, _pathBox, _browseBtn, _loadBtn]);
        topPanel.Resize += (_, _) => LayoutTopPanel();

        // ── 底部面板 ──
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.FromArgb(230, 230, 230)
        };

        _statusLabel = new Label
        {
            Text = "就绪 — 选择一个目录以浏览其中 EXE 文件的图标",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(100, 100, 100)
        };

        _setIconBtn = new Button
        {
            Text = "✓ 设为目录图标",
            Dock = DockStyle.Right,
            Width = 140,
            Enabled = false,
            FlatStyle = FlatStyle.System,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
        };
        _setIconBtn.Click += (_, _) => SetAsDirectoryIcon();

        bottomPanel.Controls.Add(_statusLabel);
        bottomPanel.Controls.Add(_setIconBtn);

        // ── 进度条 ──
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 3,
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };

        // ── 图标网格 ──
        _iconGrid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(249, 249, 249),
            WrapContents = true
        };

        Controls.Add(_iconGrid);
        Controls.Add(_progressBar);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);

        // 命令行参数
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            _pathBox.Text = args[0];
            BeginInvoke(LoadIcons);
        }
    }

    private void LayoutTopPanel()
    {
        var p = _pathBox.Parent!;
        int rightEdge = p.ClientSize.Width - 12;
        _loadBtn.Location = new Point(rightEdge - _loadBtn.Width, 10);
        _browseBtn.Location = new Point(_loadBtn.Left - _browseBtn.Width - 6, 10);
        _pathBox.Width = _browseBtn.Left - _pathBox.Left - 8;
    }

    private void BrowseBtn_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择要浏览的目录",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.SelectedPath;
            LoadIcons();
        }
    }

    private async void LoadIcons()
    {
        var path = _pathBox.Text.Trim();
        if (string.IsNullOrEmpty(path)) return;

        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            _statusLabel.Text = $"❌ 目录不存在: {path}";
            return;
        }

        _pathBox.Text = path;
        _progressBar.Visible = true;
        _iconGrid.SuspendLayout();
        ClearIcons();
        _statusLabel.Text = "正在扫描 EXE 文件...";

        try
        {
            var exeIcons = await Task.Run(() => PeIconExtractor.ScanDirectory(path));

            if (exeIcons.Count == 0)
            {
                _statusLabel.Text = "未找到包含图标的 EXE 文件";
                return;
            }

            int total = 0;
            foreach (var exe in exeIcons)
            {
                foreach (var group in exe.IconGroups)
                {
                    var item = new IconItemData
                    {
                        ExeFileName = exe.ExeFileName,
                        GroupId = group.GroupId,
                        SizeDescription = IcoHelper.DescribeSizes(group.IcoData),
                        MaxSize = IcoHelper.GetMaxSize(group.IcoData),
                        IcoData = group.IcoData
                    };
                    _icons.Add(item);
                    _iconGrid.Controls.Add(CreateIconCard(item));
                    total++;
                }
            }

            _statusLabel.Text = $"找到 {total} 个图标（来自 {exeIcons.Count} 个 EXE 文件）";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ 扫描失败: {ex.Message}";
        }
        finally
        {
            _iconGrid.ResumeLayout(true);
            _progressBar.Visible = false;
        }
    }

    private Panel CreateIconCard(IconItemData item)
    {
        var card = new Panel
        {
            Size = new Size(152, 130),
            Margin = new Padding(4),
            BackColor = Color.White,
            Tag = item,
            Cursor = Cursors.Hand
        };
        card.Paint += CardPaint;

        // 图标预览
        var pic = new PictureBox
        {
            Size = new Size(56, 56),
            Location = new Point(48, 8),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(240, 240, 240),
            Image = CreatePreview(item.IcoData)
        };

        // 文件名
        var nameLabel = new Label
        {
            Text = $"{Path.GetFileName(item.ExeFileName)} #{item.GroupId}",
            AutoSize = false,
            Size = new Size(140, 18),
            Location = new Point(6, 70),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold),
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };

        // 尺寸
        var sizeInfo = item.MaxSize >= 256 ? $"{item.SizeDescription} ★HD" : item.SizeDescription;
        var sizeLabel = new Label
        {
            Text = sizeInfo,
            AutoSize = false,
            Size = new Size(140, 16),
            Location = new Point(6, 90),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei UI", 7.5f),
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };

        // 文件大小
        var fileSizeLabel = new Label
        {
            Text = FormatFileSize(item.IcoData.Length),
            AutoSize = false,
            Size = new Size(140, 14),
            Location = new Point(6, 108),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Microsoft YaHei UI", 7f),
            BackColor = Color.Transparent
        };

        card.Controls.AddRange([pic, nameLabel, sizeLabel, fileSizeLabel]);

        // 点击选中（卡片和所有子控件都响应）
        void OnClick(object? s, EventArgs e) => SelectIcon(item, card);
        card.Click += OnClick;
        foreach (Control c in card.Controls) c.Click += OnClick;

        // 双击设为目录图标
        void OnDoubleClick(object? s, EventArgs e) { SelectIcon(item, card); SetAsDirectoryIcon(); }
        card.DoubleClick += OnDoubleClick;
        foreach (Control c in card.Controls) c.DoubleClick += OnDoubleClick;

        return card;
    }

    private static void CardPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel card) return;
        var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
        using var pen = new Pen(card.BackColor == Color.FromArgb(232, 242, 255)
            ? Color.FromArgb(0, 120, 212)
            : Color.FromArgb(210, 210, 210));
        using var path = RoundedRect(rect, 6);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void SelectIcon(IconItemData item, Panel card)
    {
        // 取消之前的选中
        if (_selectedPanel is not null)
        {
            _selectedPanel.BackColor = Color.White;
            _selectedPanel.Invalidate();
        }

        _selectedIcon = item;
        _selectedPanel = card;
        card.BackColor = Color.FromArgb(232, 242, 255);
        card.Invalidate();
        _setIconBtn.Enabled = true;

        var label = $"{Path.GetFileName(item.ExeFileName)} #{item.GroupId}";
        var sizeInfo = item.MaxSize >= 256 ? $"{item.SizeDescription} ★HD" : item.SizeDescription;
        _statusLabel.Text = $"已选中: {label} ({sizeInfo})";
    }

    private async void SetAsDirectoryIcon()
    {
        if (_selectedIcon is null || string.IsNullOrWhiteSpace(_pathBox.Text)) return;

        try
        {
            var safeName = SanitizeFileName(
                Path.GetFileNameWithoutExtension(
                    Path.GetFileName(_selectedIcon.ExeFileName)));
            var icoFileName = $"{safeName}_icon{_selectedIcon.GroupId}.ico";

            DirectoryIconService.SetDirectoryIconFromBytes(
                _selectedIcon.IcoData,
                icoFileName,
                _pathBox.Text);

            _statusLabel.Text = $"✅ 已将图标设为目录图标 → {icoFileName}";
        }
        catch (UnauthorizedAccessException)
        {
            if (_isAdmin)
            {
                _statusLabel.Text = "❌ 即使以管理员身份运行仍无法写入，请检查目录权限";
                return;
            }

            var result = MessageBox.Show(this,
                "设置目录图标需要修改文件属性和写入 desktop.ini，当前权限不足。\n是否以管理员身份重新启动？",
                "权限不足",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
                RestartElevated(_pathBox.Text);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ 设置失败: {ex.Message}";
        }
    }

    private void ClearIcons()
    {
        _selectedIcon = null;
        _selectedPanel = null;
        _setIconBtn.Enabled = false;
        _icons.Clear();

        foreach (Control c in _iconGrid.Controls)
        {
            if (c is Panel card)
            {
                foreach (Control child in card.Controls)
                {
                    if (child is PictureBox pb) pb.Image?.Dispose();
                    child.Dispose();
                }
            }
            c.Dispose();
        }
        _iconGrid.Controls.Clear();
    }

    private static Image? CreatePreview(byte[] icoData)
    {
        try
        {
            var imageData = IcoHelper.ExtractLargestImageData(icoData);
            if (imageData is not null && IcoHelper.IsPng(imageData))
            {
                using var ms = new MemoryStream(imageData);
                return Image.FromStream(ms);
            }

            using var icoStream = new MemoryStream(icoData);
            var icon = new Icon(icoStream, new Size(256, 256));
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Where(c => !invalidChars.Contains(c)));
        return string.IsNullOrWhiteSpace(sanitized) ? "icon" : sanitized;
    }

    private static bool CheckIsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartElevated(string? directoryPath)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Verb = "runas",
            UseShellExecute = true
        };

        if (!string.IsNullOrEmpty(directoryPath))
            psi.Arguments = $"\"{directoryPath}\"";

        try
        {
            Process.Start(psi);
            Application.Exit();
        }
        catch
        {
            // 用户取消了 UAC
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ClearIcons();
        base.OnFormClosed(e);
    }
}

/// <summary>
/// 图标数据项。
/// </summary>
internal class IconItemData
{
    public string ExeFileName { get; set; } = "";
    public int GroupId { get; set; }
    public string SizeDescription { get; set; } = "";
    public int MaxSize { get; set; }
    public byte[] IcoData { get; set; } = [];
}
