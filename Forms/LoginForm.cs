using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TraeCheckin;

/// <summary>
/// 内嵌 WebView2 登录窗口。
/// 用户在此完成一次登录，程序从 localStorage 读取 Cloud-IDE-Token 并返回。
/// 使用持久化用户数据目录，登录态 cookies 会被保留，便于后续自动刷新 token。
/// </summary>
public class LoginForm : Form
{
    private readonly string _userDataDir;
    private readonly string? _initialToken;
    private WebView2? _webView;
    private readonly Action<string, string?> _onToken;
    private bool _tokenObtained;
    private readonly Button _btnClose = new();

    public LoginForm(string userDataDir, string? initialToken, Action<string, string?> onToken)
    {
        _userDataDir = userDataDir;
        _initialToken = initialToken;
        _onToken = onToken;

        // 启用 DPI 自动缩放：控件尺寸按 96-DPI 设计，高显示缩放下由框架统一放大（见 MainForm 同款修复）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = "登录 TRAE 账号";
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        _btnClose.Text = "关闭窗口";
        _btnClose.Click += (_, _) => Close();

        var top = new Panel { Dock = DockStyle.Top, Height = 44 };
        top.Controls.Add(_btnClose);
        _btnClose.Dock = DockStyle.Right;
        _btnClose.Width = 110;
        _btnClose.Margin = new Padding(8);

        var lbl = new Label
        {
            Text = "请在弹出的浏览器内登录 TRAE（手机号+验证码）。登录成功后本窗口会自动识别并关闭。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        top.Controls.Add(lbl);

        Controls.Add(top);
        Load += OnLoad;
        FormClosing += (_, e) =>
        {
            // 用户关闭窗口时直接结束进程，避免残留 token 读取线程
            if (!_tokenObtained)
            {
                _onToken(string.Empty, null);
            }
        };
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            _webView.BringToFront();
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate("https://www.trae.cn/dashboard#usage");

            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += async (_, _) => await TryReadToken();
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("初始化浏览器失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _onToken(string.Empty, null);
            Close();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) _ = TryReadToken();
    }

    private async Task TryReadToken()
    {
        if (_tokenObtained || _webView?.CoreWebView2 == null) return;
        try
        {
            var result = await _webView.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{var t=localStorage.getItem('Cloud-IDE-Token');return t?t:'';}catch(e){return '';}})()");
            var token = result.Trim('"');
            // 仅接受真正重新登录产生的新 token，避免误读 localStorage 中残留的失效 token
            if (!TokenUtils.ShouldAcceptNewToken(_initialToken, token)) return;

            // 通过 CookieManager 读取会话 Cookie（HttpOnly，localStorage 取不到）。
            // X-Cloudide-Session 约 14 天有效，是 token 失效时静默换新的凭证。
            string? session = null;
            try
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.trae.cn/");
                foreach (var c in cookies)
                {
                    if (c.Name == "X-Cloudide-Session")
                    {
                        session = c.Value;
                        break;
                    }
                }
            }
            catch { /* 读取 Cookie 失败时保持 session 为 null，仍可继续登录 */ }

            _tokenObtained = true;
            _onToken(token, session);
            Close();
        }
        catch { /* 页面尚未就绪，等待下个时钟周期 */ }
    }
}