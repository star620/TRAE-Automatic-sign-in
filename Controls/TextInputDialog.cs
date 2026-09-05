using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TraeCheckin;

/// <summary>
/// 轻量文本输入弹窗：说明 + 掩码输入框 + 显示/隐藏 + 粘贴按钮 + 确定/取消。
/// 用于云端页粘贴 GitHub Token（PAT）。可选传入外部页面地址，自动显示「打开页面」按钮。
/// </summary>
public class TextInputDialog : Form
{
    private static readonly Color Accent = Color.FromArgb(59, 130, 246);
    private static readonly Color TextMain = Color.FromArgb(15, 23, 42);
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);

    private readonly TextBox _txt = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _chkShow = new() { Text = "显示", AutoSize = true };

    /// <summary>确定后返回用户输入（含密码掩码解除后的值）。</summary>
    public string? Value { get; private set; }

    /// <param name="openUrl">非空时显示「打开页面」按钮，点击用默认浏览器打开该地址（如 PAT 创建页）。</param>
    public TextInputDialog(string title, string hint, string? openUrl = null)
    {
        Text = title;
        Font = new Font("Segoe UI", 9);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(MainForm.S(560), MainForm.S(352));
        BackColor = Color.White;
        ForeColor = TextMain;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(MainForm.S(20)), ColumnCount = 1, RowCount = 4, BackColor = Color.White };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainForm.S(168)));  // 说明（需容纳可换行的引导文字，给足高度避免截断）
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainForm.S(34)));   // 输入行
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainForm.S(30)));   // 工具行
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, MainForm.S(46)));   // 按钮行

        var lblHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = hint,
            ForeColor = TextMuted,
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft
        };

        _txt.Dock = DockStyle.Fill;
        _txt.BorderStyle = BorderStyle.FixedSingle;
        _txt.Font = new Font("Consolas", 10);

        var btnPaste = new Button { Text = "从剪贴板粘贴", AutoSize = false, Width = MainForm.S(150), Height = MainForm.S(26), FlatStyle = FlatStyle.Flat };
        btnPaste.Click += (_, _) =>
        {
            try { if (Clipboard.ContainsText()) _txt.Text = Clipboard.GetText().Trim(); }
            catch { /* 剪贴板不可用时忽略 */ }
        };
        _chkShow.CheckedChanged += (_, _) => _txt.UseSystemPasswordChar = !_chkShow.Checked;

        var toolRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        toolRow.Controls.Add(_chkShow);
        toolRow.Controls.Add(btnPaste);

        if (!string.IsNullOrWhiteSpace(openUrl))
        {
            var btnOpen = new Button
            {
                Text = "打开 GitHub 创建页",
                AutoSize = false,
                Width = MainForm.S(170),
                Height = MainForm.S(26),
                Margin = new Padding(MainForm.S(8), 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnOpen.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(openUrl) { UseShellExecute = true }); }
                catch { /* 打开失败不阻断 */ }
            };
            toolRow.Controls.Add(btnOpen);
        }

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = MainForm.S(96), Height = MainForm.S(34), BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = MainForm.S(96), Height = MainForm.S(34), FlatStyle = FlatStyle.Flat };

        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        btnRow.Controls.Add(cancel);
        btnRow.Controls.Add(ok);

        root.Controls.Add(lblHint, 0, 0);
        root.Controls.Add(_txt, 0, 1);
        root.Controls.Add(toolRow, 0, 2);
        root.Controls.Add(btnRow, 0, 3);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_txt.Text)) Value = _txt.Text.Trim();
        };
    }
}
