using System.Diagnostics;

namespace TraeCheckin;

/// <summary>
/// 「云端签到」页：把签到脚本一键部署到用户自己的 GitHub 仓库，
/// 通过 GitHub Actions 实现无需本机挂机的每日自动签到。
/// </summary>
public partial class MainForm
{
    private readonly GitHubApiClient _ghApi = new();
    private readonly Label _lblCloudState = new();
    private readonly Label _lblCloudCode = new();
    private readonly Button _btnCloudAction = new();
    private readonly Button _btnUsePat = new();
    private readonly TableLayoutPanel _cloudActionRow = new();
    private readonly ListBox _cloudLog = new();
    private bool _cloudBusy;
    private GitHubDeviceCode? _pendingCode;

    private Panel BuildCloud()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = ContentBg, Padding = new Padding(S(16)) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = ContentBg };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(180)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(92)));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(CardPanel("云端自动签到（GitHub Actions）", BuildCloudAction()), 0, 0);
        grid.Controls.Add(CardPanel("说明", BuildCloudHint()), 0, 1);

        _cloudLog.Dock = DockStyle.Fill;
        _cloudLog.BackColor = CardBg;
        _cloudLog.ForeColor = TextMuted;
        _cloudLog.BorderStyle = BorderStyle.None;
        _cloudLog.HorizontalScrollbar = false;
        grid.Controls.Add(CardPanel("部署日志", _cloudLog), 0, 2);

        p.Controls.Add(grid);
        RefreshCloudState();
        return p;
    }

    private Control BuildCloudAction()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = CardBg };

        _lblCloudState.Dock = DockStyle.Top;
        _lblCloudState.Height = S(40);
        _lblCloudState.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _lblCloudState.ForeColor = TextMain;
        _lblCloudState.TextAlign = ContentAlignment.MiddleLeft;

        _lblCloudCode.Dock = DockStyle.Top;
        _lblCloudCode.Height = S(30);
        _lblCloudCode.Font = new Font("Consolas", 12, FontStyle.Bold);
        _lblCloudCode.ForeColor = Accent;
        _lblCloudCode.TextAlign = ContentAlignment.MiddleLeft;
        _lblCloudCode.Visible = false;

        _btnCloudAction.Dock = DockStyle.Fill;
        _btnCloudAction.FlatStyle = FlatStyle.Flat;
        _btnCloudAction.FlatAppearance.BorderSize = 0;
        _btnCloudAction.BackColor = Accent;
        _btnCloudAction.ForeColor = Color.White;
        _btnCloudAction.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _btnCloudAction.Cursor = Cursors.Hand;
        _btnCloudAction.Click += async (_, _) => await OnCloudActionAsync();

        _btnUsePat.Dock = DockStyle.Fill;
        _btnUsePat.Text = "粘贴 Token（PAT）";
        _btnUsePat.FlatStyle = FlatStyle.Flat;
        _btnUsePat.FlatAppearance.BorderColor = Accent;
        _btnUsePat.FlatAppearance.BorderSize = 1;
        _btnUsePat.BackColor = CardBg;
        _btnUsePat.ForeColor = Accent;
        _btnUsePat.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _btnUsePat.Cursor = Cursors.Hand;
        _btnUsePat.Click += async (_, _) => await UsePatAsync();

        _cloudActionRow.Dock = DockStyle.Bottom;
        _cloudActionRow.Height = S(48);
        _cloudActionRow.ColumnCount = 2;
        _cloudActionRow.RowCount = 1;
        _cloudActionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _cloudActionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _cloudActionRow.Controls.Add(_btnCloudAction, 0, 0);
        _cloudActionRow.Controls.Add(_btnUsePat, 1, 0);

        panel.Controls.Add(_cloudActionRow);
        panel.Controls.Add(_lblCloudCode);
        panel.Controls.Add(_lblCloudState);
        return panel;
    }

    /// <summary>控制底部按钮区形态：patOption=true 时双按钮并列，false 时主按钮占满两列。</summary>
    private void UpdateCloudActionUi(bool patOption)
    {
        if (patOption)
        {
            _btnUsePat.Visible = true;
            _cloudActionRow.SetColumnSpan(_btnCloudAction, 1);
        }
        else
        {
            _btnUsePat.Visible = false;
            _cloudActionRow.SetColumnSpan(_btnCloudAction, 2);
        }
    }

    /// <summary>粘贴 PAT 入口：弹窗输入 → 校验 → 保存并触发部署检测。</summary>
    private async Task UsePatAsync()
    {
        if (_cloudBusy) return;
        var dlg = new TextInputDialog("粘贴 GitHub Token（PAT）",
            "自动打开 GitHub 创建页可简化操作：\n" +
            "1) 点下方「打开 GitHub 创建页」进入 Fine-grained Token 页面（如未登录需先登录）\n" +
            "2) Repository access 勾选 TRAE-Automatic-sign-in\n" +
            "3) Permissions：Actions / Contents / Pull requests / Secrets / Workflows 均选 Read and write\n" +
            "4) 生成后把 github_pat_ 开头的 Token 粘贴到下方输入框，点「确定」即完成授权",
            "https://github.com/settings/personal-access-tokens/new");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string pat = dlg.Value ?? "";
        if (string.IsNullOrWhiteSpace(pat)) return;

        _cloudBusy = true;
        _btnUsePat.Enabled = false;
        try
        {
            var v = await _ghApi.ValidatePatAsync(pat);
            if (!v.IsValid)
            {
                SetCloudLog("PAT 校验失败：" + (v.Error ?? "未知错误"));
                return;
            }
            _config.GitHubToken = pat;
            _config.GitHubLogin = v.Login;
            _config.Save();
            SetCloudLog("已使用 PAT 授权：" + v.Login + (v.CanWrite ? "" : "（尚未检测到 fork 仓库，部署时会自动创建）"));
            RefreshCloudState();
        }
        finally
        {
            _cloudBusy = false;
            _btnUsePat.Enabled = true;
        }
    }

    private Control BuildCloudHint()
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Text = "一键把签到脚本部署到你自己的 GitHub 仓库，之后无需本机挂机，GitHub 每天自动帮你签到。",
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private void RefreshCloudState()
    {
        if (!string.IsNullOrEmpty(_config.GitHubToken))
        {
            UpdateCloudActionUi(false);
            _lblCloudState.Text = "已授权" + (string.IsNullOrEmpty(_config.GitHubLogin) ? "" : "：" + _config.GitHubLogin);
            _btnCloudAction.Text = "检测部署状态…";
            _btnCloudAction.Enabled = false;
            _ = RefreshDeploymentStateAsync();
        }
        else
        {
            UpdateCloudActionUi(true);
            _lblCloudState.Text = "尚未授权 GitHub";
            _btnCloudAction.Text = "授权 GitHub";
            _btnCloudAction.Enabled = true;
        }
    }

    /// <summary>
    /// 已授权后自动检测云端部署状态：授权失效则回到未授权，已部署则把按钮改成「重新部署」，
    /// 未部署则保留「一键部署到云端」。
    /// </summary>
    private async Task RefreshDeploymentStateAsync()
    {
        if (string.IsNullOrEmpty(_config.GitHubToken)) return;
        string token = _config.GitHubToken;
        string login = _config.GitHubLogin ?? "";
        try
        {
            if (string.IsNullOrEmpty(login))
            {
                login = await _ghApi.GetLoginAsync(token) ?? "";
                if (!string.IsNullOrEmpty(login))
                {
                    _config.GitHubLogin = login;
                    _config.Save();
                }
            }

            if (string.IsNullOrEmpty(login))
            {
                // token 已失效（/user 返回 401）
                ClearCloudAuth();
                SetCloudLog("GitHub 授权已失效，请重新授权");
                RefreshCloudState();
                return;
            }

            var status = await _ghApi.GetDeploymentStatusAsync(token, login);

            if (!status.IsAuthorized)
            {
                // 授权已失效：清除本地授权信息，回到未授权状态
                ClearCloudAuth();
                SetCloudLog("GitHub 授权已失效，请重新授权");
                RefreshCloudState();
                return;
            }

            if (status.IsDeployed)
            {
                _lblCloudState.Text = status.DeployedAccountCount >= 2
                    ? $"已部署完成（{status.DeployedAccountCount} 个账号），云端每天北京时间 8:00 自动签到"
                    : "已部署完成（1 个账号），云端每天北京时间 8:00 自动签到";
                _btnCloudAction.Text = "重新部署";
            }
            else
            {
                _lblCloudState.Text = "已授权" + (string.IsNullOrEmpty(login) ? "" : "：" + login) + "（尚未部署）";
                _btnCloudAction.Text = "一键部署到云端";
            }
        }
        catch (Exception ex)
        {
            _lblCloudState.Text = "检测部署状态失败，仍可手动部署";
            _btnCloudAction.Text = "一键部署到云端";
            SetCloudLog("检测部署状态失败：" + ex.Message);
        }
        finally
        {
            _btnCloudAction.Enabled = true;
        }
    }

    private async Task OnCloudActionAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_config.GitHubToken))
            {
                if (_cloudBusy) return;
                await DeployAsync();
                return;
            }

            if (_cloudBusy)
            {
                ReopenAuthPage();
                return;
            }

            await AuthAsync();
        }
        catch (Exception ex)
        {
            SetCloudLog("操作异常：" + ex.Message);
        }
    }

    private async Task AuthAsync()
    {
        _cloudBusy = true;
        try
        {
            var code = await _ghApi.RequestDeviceCodeAsync();
            if (code == null)
            {
                SetCloudLog("申请设备码失败：" + (_ghApi.LastError ?? "未知错误"));
                return;
            }

            _pendingCode = code;
            _lblCloudCode.Text = "授权码：" + code.UserCode + "（已复制，粘贴到网页即可）";
            _lblCloudCode.Visible = true;
            _lblCloudState.Text = "请在打开的网页中输入授权码完成授权";
            _btnCloudAction.Text = "重新打开授权网页";
            OpenAuthPage(code);

            int interval = code.Interval >= 1 ? code.Interval : 5;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(interval * 1000);
                var (state, token) = await _ghApi.PollForAccessTokenAsync(code.DeviceCode);
                if (state == DeviceAuthState.Success && !string.IsNullOrEmpty(token))
                {
                    var login = await _ghApi.GetLoginAsync(token);
                    _config.GitHubToken = token;
                    _config.GitHubLogin = login;
                    _config.Save();
                    _pendingCode = null;
                    _lblCloudCode.Visible = false;
                    SetCloudLog("授权成功" + (string.IsNullOrEmpty(login) ? "" : "：" + login));
                    RefreshCloudState();
                    return;
                }
                if (state == DeviceAuthState.Failed)
                {
                    _pendingCode = null;
                    _lblCloudCode.Visible = false;
                    SetCloudLog("授权失败：" + (_ghApi.LastError ?? "未知错误"));
                    RefreshCloudState();
                    return;
                }
            }
            _pendingCode = null;
            _lblCloudCode.Visible = false;
            SetCloudLog("授权超时，请重新授权");
            RefreshCloudState();
        }
        finally
        {
            _cloudBusy = false;
        }
    }

    private void OpenAuthPage(GitHubDeviceCode code)
    {
        try { Clipboard.SetText(code.UserCode); } catch { /* 复制失败不阻断 */ }
        try { Process.Start(new ProcessStartInfo(code.VerificationUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetCloudLog("自动打开浏览器失败，请手动访问：" + code.VerificationUri + "（" + ex.Message + "）"); }
    }

    private void ReopenAuthPage()
    {
        if (_pendingCode == null) return;
        SetCloudLog("已重新打开授权网页，授权码：" + _pendingCode.UserCode);
        OpenAuthPage(_pendingCode);
    }

    /// <summary>清除本地 GitHub 授权信息，回到未授权状态。</summary>
    private void ClearCloudAuth()
    {
        _config.GitHubToken = null;
        _config.GitHubLogin = null;
        _config.Save();
        _pendingCode = null;
        _lblCloudCode.Visible = false;
    }

    /// <summary>云端最多部署的账号数（与 checkin.yml 显式枚举一致，超限需扩 workflow）。</summary>
    private const int CloudAccountLimit = 4;

    private async Task DeployAsync()
    {
        var enabled = _accountStore.EnabledAccounts().ToList();
        if (enabled.Count == 0)
        {
            MessageBox.Show("没有启用的 Trae 账号，请先到「设置」页添加/登录账号后再部署。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (enabled.Any(a => string.IsNullOrEmpty(a.Session)))
        {
            MessageBox.Show("存在未登录的账号（缺少会话 Cookie），请先在「设置」页逐一登录后再部署。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (enabled.Count > CloudAccountLimit)
        {
            MessageBox.Show($"当前启用了 {enabled.Count} 个账号，云端部署上限为 {CloudAccountLimit} 个"
                + "（受 checkin.yml 显式 secret 映射限制）。如需更多，请停用部分账号或扩展 workflow。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var a in enabled)
        {
            _accountStore.EnsureDeviceId(a);
        }
        _config.Save();

        _cloudBusy = true;
        _btnCloudAction.Enabled = false;
        try
        {
            string token = _config.GitHubToken!;

            // 先校验授权有效性并获取用户名：token 失效时 /user 返回 401，直接清理授权
            string login = await _ghApi.GetLoginAsync(token) ?? "";
            if (string.IsNullOrEmpty(login))
            {
                ClearCloudAuth();
                SetCloudLog("GitHub 授权已失效，请重新授权");
                RefreshCloudState();
                return;
            }
            if (_config.GitHubLogin != login)
            {
                _config.GitHubLogin = login;
                _config.Save();
            }

            SetCloudLog("开始部署…");

            SetCloudLog("正在 fork 仓库…");
            if (!await _ghApi.ForkAsync(token, login)) { HandleDeployFailure(); return; }
            SetCloudLog("fork 完成");

            // 顺手给源仓库点个 star（owner 本人自动跳过，失败不影响部署）
            if (await _ghApi.StarSourceRepoAsync(token, login))
                SetCloudLog("已为源仓库点赞 ★");
            else
                SetCloudLog("为源仓库点赞失败（可忽略）");

            for (int i = 0; i < enabled.Count; i++)
            {
                var acc = enabled[i];
                int idx = i + 1;
                var sName = GitHubApiClient.SessionSecretNameFor(idx);
                var dName = GitHubApiClient.DeviceSecretNameFor(idx);

                SetCloudLog($"正在写入 {sName} secret…");
                if (!await _ghApi.SetSecretAsync(token, login, sName, acc.Session)) { HandleDeployFailure(); return; }
                SetCloudLog($"{sName} 写入成功");

                SetCloudLog($"正在写入 {dName} secret…");
                if (!await _ghApi.SetSecretAsync(token, login, dName, acc.DeviceId)) { HandleDeployFailure(); return; }
                SetCloudLog($"{dName} 写入成功");
            }

            // 清理本次减少后遗留的多余 secret，避免云端误签已删除/停用的账号
            for (int n = enabled.Count + 1; n <= CloudAccountLimit; n++)
            {
                var stale = GitHubApiClient.SessionSecretNameFor(n);
                SetCloudLog($"正在清理多余 {stale} secret…");
                if (!await _ghApi.DeleteSecretAsync(token, login, stale)) { HandleDeployFailure(); return; }
                SetCloudLog($"{stale} 已清理");
                var staleD = GitHubApiClient.DeviceSecretNameFor(n);
                if (!await _ghApi.DeleteSecretAsync(token, login, staleD)) { HandleDeployFailure(); return; }
            }

            if (!string.IsNullOrEmpty(_config.FeishuWebhook))
            {
                SetCloudLog("正在写入 FEISHU_WEBHOOK secret…");
                if (!await _ghApi.SetSecretAsync(token, login, GitHubApiClient.FeishuWebhookSecretName, _config.FeishuWebhook)) { HandleDeployFailure(); return; }
                SetCloudLog("FEISHU_WEBHOOK 写入成功");
            }

            SetCloudLog("正在查找 checkin workflow…");
            long wfId = await _ghApi.GetWorkflowIdAsync(token, login);
            if (wfId < 0)
            {
                // 公共仓库被 fork 后定时 workflow 默认禁用，fork 上 Actions 未启用时 workflow 列表为空。
                // 自动开启该 fork 的 Actions 并轮询等待 GitHub 登记，最多约 60 秒。
                SetCloudLog("fork 中未找到 checkin workflow，正在开启该 fork 的 GitHub Actions…");
                if (!await _ghApi.EnsureActionsEnabledAsync(token, login)) { HandleDeployFailure(); return; }
                for (int i = 0; i < 12 && wfId < 0; i++)
                {
                    await Task.Delay(5000);
                    wfId = await _ghApi.GetWorkflowIdAsync(token, login);
                }
                if (wfId < 0)
                {
                    SetCloudLog("已开启 Actions 但仍未登记 workflow：若该 fork 创建时间早于源仓库添加"
                        + " checkin.yml，请删除此 fork 后重新部署。");
                    HandleDeployFailure();
                    return;
                }
                SetCloudLog("GitHub Actions 已开启，checkin workflow 已登记");
            }
            if (!await _ghApi.EnableWorkflowAsync(token, login, wfId)) { HandleDeployFailure(); return; }
            SetCloudLog("workflow 已启用");

            SetCloudLog("正在触发一次验证运行…");
            if (!await _ghApi.DispatchWorkflowAsync(token, login, wfId)) { HandleDeployFailure(); return; }

            SetCloudLog("等待运行结果…");
            string? conclusion = null;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(5000);
                conclusion = await _ghApi.GetLatestRunConclusionAsync(token, login);
                if (!string.IsNullOrEmpty(conclusion)) break;
            }

            if (conclusion == "success")
            {
                SetCloudLog($"部署成功！{enabled.Count} 个账号已就绪，GitHub 将每天北京时间 8:00 自动签到。");
                _lblCloudState.Text = enabled.Count >= 2
                    ? $"已部署完成（{enabled.Count} 个账号），云端每天自动签到"
                    : "已部署完成（1 个账号），云端每天自动签到";
                _btnCloudAction.Text = "重新部署";
                // 同步刷新仪表盘的云端签到状态，避免停留在旧的「未部署」
                await RefreshCloudStatusAsync();
            }
            else if (string.IsNullOrEmpty(conclusion))
            {
                SetCloudLog("已触发，但未在超时时间内拿到结果，请到 GitHub Actions 页面查看。");
            }
            else
            {
                SetCloudLog("运行结论：" + conclusion + "，请到 GitHub Actions 页面查看日志。");
            }
        }
        finally
        {
            _cloudBusy = false;
            _btnCloudAction.Enabled = true;
        }
    }

    /// <summary>部署失败统一处理：授权失效时清理本地授权并回到重新授权状态。</summary>
    private void HandleDeployFailure()
    {
        var err = _ghApi.LastError ?? "";
        if (err.Contains("授权已失效", StringComparison.Ordinal))
        {
            ClearCloudAuth();
            SetCloudLog(err);
            RefreshCloudState();
        }
        else
        {
            SetCloudLog(string.IsNullOrEmpty(err) ? "部署失败，请重试" : err);
        }
    }

    private void SetCloudLog(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(SetCloudLog), msg); return; }
        _cloudLog.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        _cloudLog.TopIndex = _cloudLog.Items.Count - 1;
    }
}
