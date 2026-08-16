using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AinaLife.GitHubTool;

public class GitHubToolConfig
{
    [DisplayName("GitHub Token")]
    [Description("GitHub Personal Access Token，需要 repo 权限。生成：GitHub → Settings → Developer settings → Personal access tokens → Tokens (classic)，勾选 repo")]
    public string GitHubToken { get; set; } = "";

    [DisplayName("文件内容最大返回字符数")]
    [Description("github_read_file 单次最多返回的字符数，默认 10000")]
    public int FileContentMaxChars { get; set; } = 10000;

    [DisplayName("检查Token时返回明文")]
    [Description("开启后 github_check_token 会返回明文 Token（仅供调试，请谨慎）")]
    public bool ExposeTokenInCheck { get; set; } = false;

    [DisplayName("允许删除仓库")]
    [Description("开启后 AI 才能调用 act=delete_repository 删除仓库（不可逆，默认关闭）")]
    public bool EnableDeleteRepository { get; set; } = false;
}

/// <summary>
/// GitHub 工具模块：HttpClient 直调 GitHub REST API，零 MCP 桥接，覆盖
/// 搜索 / 读取 / 创建 / 更新 / 批量 / fork 等完整操作。
/// </summary>
[Module("GitHub工具",
    "HttpClient 直调 GitHub REST API 的全套工具：搜索仓库/代码/Issue、读写文件、创建 Issue/PR/Release、批量提交、Fork、删除等，零 MCP 桥接，配置 Token 即可用。",
    url: "https://github.com/AinaLife-ai/Alife.GitHubTool",
    defaultCategory: "AinaLife/GitHub")]
public class GitHubToolModule(
    XmlFunctionCaller functionCaller,
    ILogger<GitHubToolModule> logger,
    Interactor<GitHubToolModule> interactor) :
    ChatBehaviour,
    IConfigurable<GitHubToolConfig>
{
    public GitHubToolConfig Configuration { get; set; } = null!;

    // 静态 HttpClient：模块热重载时复用，避免端口/连接耗尽
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.github.com"),
        Timeout = TimeSpan.FromSeconds(120),
    };

    private static readonly ConcurrentDictionary<string, string> DefaultBranchCache = new();

    private string _githubUser = "";

    private bool TokenConfigured => !string.IsNullOrWhiteSpace(Configuration.GitHubToken);

    // ==================== 基础请求 ====================

    private async Task<(int Code, string Body)> ReqAsync(string method, string path, object? body = null, int timeoutSeconds = 120)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("AinaLife-GitHubTool/4.0");
        if (TokenConfigured)
            req.Headers.TryAddWithoutValidation("Authorization", $"token {Configuration.GitHubToken}");
        if (body != null)
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var resp = await Http.SendAsync(req, cts.Token);
            string text = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, text);
        }
        catch (Exception e)
        {
            return (-1, $"{{\"message\": \"request error: {e.Message}\", \"documentation_url\": \"\"}}");
        }
    }

    private string CheckTokenError()
        => TokenConfigured
            ? ""
            : "GitHub Token 未配置，请在插件配置中填写 Personal Access Token（需要 repo 权限）。";

    private async Task<string> DefaultBranchAsync(string o, string r)
    {
        string key = $"{o}/{r}";
        if (DefaultBranchCache.TryGetValue(key, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;
        var (code, body) = await ReqAsync("GET", $"/repos/{o}/{r}");
        if (code >= 200 && code < 300)
        {
            try
            {
                string? db = JObject.Parse(body)["default_branch"]?.ToString();
                if (!string.IsNullOrEmpty(db))
                {
                    DefaultBranchCache[key] = db;
                    return db;
                }
            }
            catch (JsonException) { }
        }
        return "";
    }

    private async Task<string> ResolveBranchAsync(string o, string r, string br)
    {
        if (!string.IsNullOrEmpty(br))
            return br;
        string db = await DefaultBranchAsync(o, r);
        return !string.IsNullOrEmpty(db) ? db : "main";
    }

    private async Task<string> GetFileShaAsync(string o, string r, string p, string br)
    {
        var (code, body) = await ReqAsync("GET", $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(p)}?ref={Uri.EscapeDataString(br)}");
        if (code >= 200 && code < 300)
        {
            try
            {
                string? sha = JObject.Parse(body)["sha"]?.ToString();
                if (!string.IsNullOrEmpty(sha))
                    return sha;
            }
            catch (JsonException) { }
        }
        return "";
    }

    // ==================== 输出格式化 ====================

    private static string Fmt(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "No output";
        JToken? data;
        try { data = JToken.Parse(raw); }
        catch (JsonException) { return raw.Length > 500 ? raw[..500] : raw; }

        if (data is JObject obj)
        {
            if (obj["message"] != null && (obj["documentation_url"] != null || obj["errors"] != null))
            {
                string msg = $"Error: {obj[\"message\"]}";
                if (obj["errors"] is JArray errs && errs.Count > 0)
                {
                    string errText = errs.ToString(Formatting.None);
                    msg += "\n详情: " + (errText.Length > 400 ? errText[..400] : errText);
                }
                return msg;
            }
            if (obj["total_count"] != null)
            {
                var lines = new StringBuilder($"Total: {obj[\"total_count\"]}");
                if (obj["items"] is JArray items)
                {
                    int shown = 0;
                    foreach (var item in items)
                    {
                        if (shown++ >= 10) break;
                        string n = item["full_name"]?.ToString() ?? item["name"]?.ToString() ?? item["login"]?.ToString() ?? "?";
                        string u = item["html_url"]?.ToString() ?? "";
                        lines.Append('\n').Append("  ").Append(n).Append("  ").Append(u);
                    }
                    if (items.Count > 10)
                        lines.Append($"\n  ... and {items.Count - 10} more");
                }
                return lines.ToString();
            }
            if (obj["content"] is JObject contentObj && contentObj["sha"] != null)
                return $"sha: {contentObj[\"sha\"]}";
            if (obj["commit"] != null && obj["sha"] != null)
            {
                string cm = obj["commit"]?["message"]?.ToString() ?? "";
                if (cm.Length > 60) cm = cm[..60];
                return $"sha: {obj[\"sha\"]}  {cm}";
            }
            foreach (string k in new[] { "full_name", "sha", "name" })
            {
                if (obj[k] != null)
                {
                    return k switch
                    {
                        "full_name" => $"{obj[k]}  {obj[\"html_url\"]}",
                        "sha" => $"sha: {obj[k]}",
                        _ => $"{obj[k]}  {obj[\"html_url\"]}",
                    };
                }
            }
            if (obj["message"] != null)
            {
                string m = obj["message"]!.ToString();
                return m.Length > 200 ? m[..200] : m;
            }
            if (obj["id"] != null)
            {
                foreach (string k in new[] { "title", "name", "login", "message" })
                    if (obj[k] != null)
                    {
                        string v = obj[k]!.ToString();
                        return v.Length > 200 ? v[..200] : v;
                    }
            }
            string full = obj.ToString(Formatting.None);
            return full.Length > 500 ? full[..500] : full;
        }

        if (data is JArray arr)
        {
            var lines = new StringBuilder();
            int shown = 0;
            foreach (var item in arr)
            {
                if (shown++ >= 20) break;
                string? n = item["full_name"]?.ToString() ?? item["name"]?.ToString() ?? item["filename"]?.ToString() ?? item["login"]?.ToString();
                if (string.IsNullOrEmpty(n) && item["title"] != null)
                    n = item["number"] != null ? $"#{item[\"number\"]} {item[\"title\"]}" : item["title"]!.ToString();
                if (string.IsNullOrEmpty(n) && item["commit"] is JObject co)
                {
                    string msg1 = (co["message"]?.ToString() ?? "").Split('\n')[0];
                    if (msg1.Length > 60) msg1 = msg1[..60];
                    string sha = item["sha"]?.ToString() ?? "";
                    n = $"{sha[..Math.Min(8, sha.Length)]} {msg1}";
                }
                n ??= "?";
                string u = item["html_url"]?.ToString() ?? "";
                lines.Append("  ").Append(n).Append("  ").Append(u).Append('\n');
            }
            if (arr.Count > 20)
                lines.Append($"  ... and {arr.Count - 20} more");
            return lines.Length == 0 ? "Empty list" : lines.ToString().TrimEnd('\n');
        }

        string s = data.ToString();
        return s.Length > 500 ? s[..500] : s;
    }

    private static string FmtFileContent(string raw, int maxChars, int offset = 0)
    {
        if (string.IsNullOrEmpty(raw))
            return "No output";
        JToken? data;
        try { data = JToken.Parse(raw); }
        catch (JsonException) { return raw.Length > 500 ? raw[..500] : raw; }

        if (data is not JObject obj)
        {
            string full = data.ToString(Formatting.None);
            return full.Length > 500 ? full[..500] : full;
        }

        if (obj["message"] != null && (obj["documentation_url"] != null || obj["errors"] != null))
            return $"Error: {obj[\"message\"]}";

        string content = obj["content"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(content))
            return "File content not found or empty";

        string sha = obj["sha"]?.ToString() ?? "unknown";
        string name = obj["name"]?.ToString() ?? "";
        string path = obj["path"]?.ToString() ?? "";
        int size = obj["size"]?.ToObject<int>() ?? 0;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(content));
        }
        catch (Exception e)
        {
            return $"Failed to decode file content: {e.Message}";
        }

        int totalChars = decoded.Length;
        if (offset >= totalChars)
            return $"📄 文件: {path}\n名称: {name}\nSHA: {sha}\n总字符数: {totalChars}\n偏移量 {offset} 已超出文件结尾，无更多内容。";

        int endPos = Math.Min(offset + maxChars, totalChars);
        string displayContent = decoded[offset..endPos];
        bool hasMore = endPos < totalChars;

        var result = new StringBuilder();
        result.Append($"📄 文件: {path}\n");
        result.Append($"名称: {name}\n");
        result.Append($"SHA: {sha}\n");
        result.Append($"总字符数: {totalChars}\n");
        result.Append($"本次读取: {offset} → {endPos} (共 {endPos - offset} 字符)\n");
        result.Append($"还有更多: {(hasMore ? \"是\" : \"否\")}\n");
        result.Append($"\n--- 内容开始 ---\n{displayContent}\n--- 内容结束 ---");
        if (hasMore)
            result.Append($"\n\n💡 提示: 文件还有 {totalChars - endPos} 字符未读。如需继续，请使用相同的参数并设置 offset={endPos}。");
        return result.ToString();
    }

    // ==================== 工具函数 ====================

    [XmlFunction(FunctionMode.OneShot, name: "github_check_token")]
    [Description("检查 GitHub Token 是否已配置。已配置返回当前登录账号，未配置返回错误提示。")]
    public void GithubCheckToken()
    {
        if (!TokenConfigured)
        {
            interactor.Poke("❌ GitHub Token 未配置，请在插件配置中填写 Personal Access Token（需要 repo 权限）。");
            return;
        }
        string msg = $"✅ GitHub Token 已配置，当前登录账号为 {(_githubUser.Length > 0 ? _githubUser : \"未知\")}。";
        if (Configuration.ExposeTokenInCheck)
            msg += $"\n\n⚠️ 【明文 Token】{Configuration.GitHubToken}\n\n此 Token 仅用于调试，请勿分享或记录到对话日志中。";
        interactor.Poke(msg);
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_read_file")]
    [Description("读取 GitHub 仓库中文件的实际内容（自动 Base64 解码）。支持 offset/limit 分页读取大文件。分支参数留空自动使用仓库默认分支。示例：先 limit=5000 调用，若返回“还有更多: 是”则用相同参数 offset=5000 继续。")]
    public async Task GithubReadFile(
        [Description("仓库所有者（用户名或组织）")] string o,
        [Description("仓库名")] string r,
        [Description("文件路径，如 src/main.py")] string p,
        [Description("分支名，留空自动用默认分支")] string b = "",
        [Description("最大返回字符数，默认取插件配置")] int? limit = null,
        [Description("字符偏移量，用于分页，默认 0")] int offset = 0)
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }
        if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(r) || string.IsNullOrEmpty(p))
        { interactor.Poke("Missing required parameters: o (owner), r (repo), p (path)"); return; }
        if (limit < 1) { interactor.Poke("limit must be at least 1"); return; }
        if (offset < 0) { interactor.Poke("offset must be >= 0"); return; }

        int maxChars = limit ?? Configuration.FileContentMaxChars;
        string br = await ResolveBranchAsync(o, r, b);
        var (code, outBody) = await ReqAsync("GET", $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(p)}?ref={Uri.EscapeDataString(br)}");
        if (code != 200)
        {
            // 显式分支不存在时回退默认分支
            if (outBody.Contains("\"No commit found for the ref\""))
            {
                string db = await DefaultBranchAsync(o, r);
                if (!string.IsNullOrEmpty(db) && db != br)
                {
                    var (code2, out2) = await ReqAsync("GET", $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(p)}?ref={Uri.EscapeDataString(db)}");
                    if (code2 == 200)
                    {
                        interactor.Poke($"⚠️ 分支 {br} 不存在，已自动改用默认分支 {db}。\n\n" + FmtFileContent(out2, maxChars, offset));
                        return;
                    }
                }
            }
            interactor.Poke($"请求失败 (HTTP {code}): {Fmt(outBody)}");
            return;
        }
        interactor.Poke(FmtFileContent(outBody, maxChars, offset));
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_search")]
    [Description("搜索 GitHub（仓库/代码/Issue/用户）。t 为搜索类型：repositories=仓库、code=代码、issues=Issue、users=用户。query 使用 GitHub 搜索语法。Token 已配置，无需传认证参数。")]
    public async Task GithubSearch(
        [Description("搜索类型：repositories / code / issues / users")] string t,
        [Description("搜索查询（GitHub 搜索语法）")] string q,
        [Description("每页结果数（最大 100），默认 10")] int n = 10)
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }
        t = string.IsNullOrEmpty(t) ? "repositories" : t;
        if (n < 1) n = 10;
        if (n > 100) n = 100;
        var (code, outBody) = await ReqAsync("GET", $"/search/{Uri.EscapeDataString(t)}?q={Uri.EscapeDataString(q)}&per_page={n}");
        interactor.Poke(code == 200 ? Fmt(outBody) : $"请求失败 (HTTP {code}): {Fmt(outBody)}");
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_get")]
    [Description("获取 GitHub 元数据：文件 SHA（t=contents，需 p 路径）、Issue 详情（t=issue，需 i 编号）、PR 详情（t=pull_request，需 n 编号）、PR 文件列表（pull_request_files）、PR 状态（pull_request_status）、PR 评论（pull_request_comments）、PR Review（pull_request_reviews）。读文件实际内容请用 github_read_file。")]
    public async Task GithubGet(
        [Description("资源类型：contents / issue / pull_request / pull_request_files / pull_request_status / pull_request_comments / pull_request_reviews")] string t,
        [Description("仓库所有者")] string o,
        [Description("仓库名")] string r,
        [Description("文件路径（t=contents 时必填）")] string p = "",
        [Description("Issue 编号（t=issue 时必填）")] int i = 0,
        [Description("PR 编号（t=pull_request* 时必填）")] int n = 0,
        [Description("分支名，留空自动用默认分支")] string b = "")
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }
        string ep;
        switch (t)
        {
            case "contents":
                if (string.IsNullOrEmpty(p)) { interactor.Poke("Missing required parameter: p (file path) for t=contents"); return; }
                string br = await ResolveBranchAsync(o, r, b);
                ep = $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(p)}?ref={Uri.EscapeDataString(br)}";
                break;
            case "pull_request_status":
                string br2 = await ResolveBranchAsync(o, r, b);
                ep = $"/repos/{o}/{r}/commits/{Uri.EscapeDataString(br2)}/status";
                break;
            case "issue": ep = $"/repos/{o}/{r}/issues/{i}"; break;
            case "pull_request": ep = $"/repos/{o}/{r}/pulls/{n}"; break;
            case "pull_request_files": ep = $"/repos/{o}/{r}/pulls/{n}/files"; break;
            case "pull_request_comments": ep = $"/repos/{o}/{r}/pulls/{n}/comments"; break;
            case "pull_request_reviews": ep = $"/repos/{o}/{r}/pulls/{n}/reviews"; break;
            default: interactor.Poke($"Unknown target: {t}"); return;
        }
        var (code, outBody) = await ReqAsync("GET", ep);
        interactor.Poke(code == 200 ? Fmt(outBody) : $"请求失败 (HTTP {code}): {Fmt(outBody)}");
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_list")]
    [Description("列出仓库的 commits / issues / pull_requests。s 为状态筛选（issues/PRs 专用，open/closed/all，默认 open）；b 为分支（仅 commits 用，留空用默认分支）。")]
    public async Task GithubList(
        [Description("列出类型：commits / issues / pull_requests")] string t,
        [Description("仓库所有者")] string o,
        [Description("仓库名")] string r,
        [Description("状态筛选：open / closed / all（仅 issues 和 pull_requests）")] string s = "open",
        [Description("分支名（仅 commits 用，留空用默认分支）")] string b = "",
        [Description("每页结果数，默认 20，最大 100")] int n = 20)
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }
        if (n < 1) n = 20;
        if (n > 100) n = 100;
        string ep;
        if (t == "commits")
        {
            ep = $"/repos/{o}/{r}/commits?per_page={n}";
            if (!string.IsNullOrEmpty(b))
                ep += $"&sha={Uri.EscapeDataString(b)}";
        }
        else if (t == "issues")
            ep = $"/repos/{o}/{r}/issues?state={Uri.EscapeDataString(s)}&per_page={n}";
        else if (t == "pull_requests")
            ep = $"/repos/{o}/{r}/pulls?state={Uri.EscapeDataString(s)}&per_page={n}";
        else { interactor.Poke($"Unknown target: {t}"); return; }

        var (code, outBody) = await ReqAsync("GET", ep);
        interactor.Poke(code == 200 ? Fmt(outBody) : $"请求失败 (HTTP {code}): {Fmt(outBody)}");
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_create")]
    [Description("创建 GitHub 资源：仓库（act=repository）、文件（act=file，自动判断新建/更新，无需先取 SHA）、Issue（act=issue）、PR（act=pull_request，head 分支须领先 base 至少一个 commit）、分支（act=branch）、PR Review（act=pull_request_review）、Star（act=star）、取消 Star（act=unstar）、Release（act=release）、删除文件（act=delete_file）、删除仓库（act=delete_repository，需插件开关且不可逆）、删除分支（act=delete_branch）、删除 Release（act=delete_release，支持 nm=tag 名）、删除评论（act=delete_comment，cid 评论 ID，k=issue/review）。lb/as 参数为 JSON 数组字符串，如 [\"bug\",\"enhancement\"]。")]
    public async Task GithubCreate(
        [Description("操作类型：repository / file / issue / pull_request / branch / pull_request_review / star / unstar / release / delete_file / delete_repository / delete_branch / delete_release / delete_comment")] string act,
        [Description("仓库所有者")] string o = "",
        [Description("仓库名")] string r = "",
        [Description("名称（仓库名/新分支名/Release tag 名）")] string nm = "",
        [Description("文件路径（act=file 或 delete_file 时必填，如 README.md）")] string p = "",
        [Description("文件内容（act=file）或评论/PR/Release 正文")] string ct = "",
        [Description("提交信息（act=file）")] string msg = "",
        [Description("分支名，留空自动用默认分支")] string br = "",
        [Description("标题（Issue/PR/Release）")] string ti = "",
        [Description("正文（Issue/PR/Release/Review）")] string bd = "",
        [Description("head 分支（act=pull_request 必填，格式 my-branch 或 MyUser:my-branch）")] string hd = "",
        [Description("base 分支（act=pull_request 必填）")] string ba = "",
        [Description("PR 编号（act=pull_request_review 必填）")] int pn = 0,
        [Description("仓库描述（act=repository）")] string desc = "",
        [Description("是否私有仓库（act=repository，默认 true）")] bool pv = true,
        [Description("已有文件 SHA（可选，省略自动获取）")] string sh = "",
        [Description("是否为草稿 PR（act=pull_request）")] bool dr = false,
        [Description("Review 事件：APPROVE / REQUEST_CHANGES / COMMENT")] string ev = "",
        [Description("标签列表，JSON 数组字符串，如 [\"bug\"]")] string lb = "",
        [Description("指派用户列表，JSON 数组字符串，如 [\"octocat\"]")] string as_ = "",
        [Description("源分支（act=branch，省略用默认分支）")] string fb = "",
        [Description("Release 目标提交（act=release，默认默认分支）")] string target = "",
        [Description("Release ID（act=delete_release，省略则用 nm 查）")] int rid = 0,
        [Description("评论 ID（act=delete_comment 必填）")] int cid = 0,
        [Description("评论类型（act=delete_comment）：issue / review")] string k = "issue",
        [Description("是否为预发布（act=release）")] bool prerelease = false)
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }

        JArray? ParseArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JArray.Parse(json); }
            catch (JsonException) { return null; }
        }

        switch (act)
        {
            case "repository":
            {
                if (string.IsNullOrEmpty(nm)) { interactor.Poke("act=repository 缺少必填参数: nm (仓库名)"); return; }
                var d = new { name = nm, description = desc ?? "", @private = pv };
                var (code, outBody) = await ReqAsync("POST", "/user/repos", d);
                interactor.Poke(code == 201 ? Fmt(outBody) : $"创建仓库失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "file":
            {
                if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(r) || string.IsNullOrEmpty(p))
                { interactor.Poke("act=file 缺少必填参数: o (owner), r (repo), p (文件路径，如 README.md)"); return; }
                string brEff = await ResolveBranchAsync(o, r, br);
                string sha = !string.IsNullOrEmpty(sh) ? sh : await GetFileShaAsync(o, r, p, brEff);
                var d = new JObject
                {
                    ["message"] = msg ?? $"Update {p}",
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ct ?? "")),
                    ["branch"] = brEff,
                };
                if (!string.IsNullOrEmpty(sha)) d["sha"] = sha;
                var (code, outBody) = await ReqAsync("PUT", $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(p)}", d);
                if (code != 200 && code != 201) { interactor.Poke($"写入文件失败 (HTTP {code}): {Fmt(outBody)}"); return; }
                string res = Fmt(outBody);
                string action = !string.IsNullOrEmpty(sha) ? "更新" : "新建";
                interactor.Poke(res.StartsWith("Error") ? res : $"✅ {action}文件成功 ({o}/{r}:{brEff}:{p})\n{res}");
                return;
            }
            case "issue":
            {
                if (string.IsNullOrEmpty(ti)) { interactor.Poke("act=issue 缺少必填参数: ti (标题)"); return; }
                var d = new JObject { ["title"] = ti };
                if (!string.IsNullOrEmpty(bd)) d["body"] = bd;
                if (ParseArray(lb) is JArray lbArr) d["labels"] = lbArr;
                if (ParseArray(as_) is JArray asArr) d["assignees"] = asArr;
                var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/issues", d);
                interactor.Poke(code == 201 ? Fmt(outBody) : $"创建 Issue 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "pull_request":
            {
                if (string.IsNullOrEmpty(ti) || string.IsNullOrEmpty(hd) || string.IsNullOrEmpty(ba))
                { interactor.Poke("act=pull_request 缺少必填参数: ti (标题), hd (head 分支), ba (base 分支)。注意：head 分支必须至少领先 base 一个 commit。"); return; }
                var d = new JObject { ["title"] = ti, ["head"] = hd, ["base"] = ba };
                if (!string.IsNullOrEmpty(bd)) d["body"] = bd;
                if (dr) d["draft"] = true;
                var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/pulls", d);
                interactor.Poke(code == 201 ? Fmt(outBody) : $"创建 PR 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "branch":
            {
                if (string.IsNullOrEmpty(nm)) { interactor.Poke("act=branch 缺少必填参数: nm (新分支名)"); return; }
                string src = !string.IsNullOrEmpty(fb) ? fb : (!string.IsNullOrEmpty(br) ? br : await DefaultBranchAsync(o, r));
                if (string.IsNullOrEmpty(src)) { interactor.Poke("无法确定源分支，请用 fb 参数显式指定"); return; }
                var (code2, refOut) = await ReqAsync("GET", $"/repos/{o}/{r}/git/refs/heads/{Uri.EscapeDataString(src)}");
                if (code2 != 200) { interactor.Poke($"获取源分支失败: {refOut[..Math.Min(200, refOut.Length)]}"); return; }
                string shaVal;
                try { shaVal = JObject.Parse(refOut)["object"]!["sha"]!.ToString(); }
                catch (Exception) { interactor.Poke($"源分支 {src} 不存在或解析失败: {refOut[..Math.Min(200, refOut.Length)]}"); return; }
                var d = new { @ref = $"refs/heads/{nm}", sha = shaVal };
                var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/git/refs", d);
                interactor.Poke(code == 201 ? Fmt(outBody) : $"创建分支失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "pull_request_review":
            {
                if (pn <= 0) { interactor.Poke("act=pull_request_review 缺少必填参数: pn (PR 编号)"); return; }
                var d = new { body = bd ?? "", @event = string.IsNullOrEmpty(ev) ? "COMMENT" : ev };
                var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/pulls/{pn}/reviews", d);
                interactor.Poke(code == 200 || code == 201 ? Fmt(outBody) : $"提交 Review 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "star":
            {
                var (code, outBody) = await ReqAsync("PUT", $"/user/starred/{o}/{r}");
                interactor.Poke(code == 204 ? $"⭐ Starred {o}/{r} successfully" : $"Star failed (HTTP {code}): {outBody[..Math.Min(200, outBody.Length)]}");
                return;
            }
            case "unstar":
            {
                var (code, outBody) = await ReqAsync("DELETE", $"/user/starred/{o}/{r}");
                interactor.Poke(code == 204 ? $"★ Unstarred {o}/{r} successfully" : $"Unstar failed (HTTP {code}): {outBody[..Math.Min(200, outBody.Length)]}");
                return;
            }
            case "release":
            {
                if (string.IsNullOrEmpty(nm)) { interactor.Poke("Missing required parameter: nm (tag name)"); return; }
                var d = new JObject
                {
                    ["tag_name"] = nm,
                    ["name"] = string.IsNullOrEmpty(ti) ? nm : ti,
                    ["body"] = bd ?? "",
                    ["draft"] = false,
                    ["prerelease"] = prerelease,
                };
                if (!string.IsNullOrEmpty(target)) d["target_commitish"] = target;
                var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/releases", d);
                interactor.Poke(code == 201 ? Fmt(outBody) : $"创建 Release 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "delete_file":
            {
                if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(r) || string.IsNullOrEmpty(p))
                { interactor.Poke("act=delete_file 缺少必填参数: o (owner), r (repo), p (文件路径)"); return; }
                string brEff = await ResolveBranchAsync(o, r, br);
                string sha = !string.IsNullOrEmpty(sh) ? sh : await GetFileShaAsync(o, r, p, brEff);
                if (string.IsNullOrEmpty(sha)) { interactor.Poke($"未找到文件 {p}（分支 {brEff}），无法删除"); return; }
                var d = new { message = msg ?? $"Delete {p}", sha, branch = brEff };
                var (code, outBody) = await ReqAsync("DELETE", $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(p)}", d);
                if (code != 200) { interactor.Poke($"删除文件失败 (HTTP {code}): {Fmt(outBody)}"); return; }
                string res = Fmt(outBody);
                interactor.Poke(res.StartsWith("Error") ? res : $"✅ 文件已删除 ({o}/{r}:{brEff}:{p})\n{res}");
                return;
            }
            case "delete_repository":
            {
                if (!Configuration.EnableDeleteRepository)
                { interactor.Poke("⚠️ 删除仓库功能已被插件开关禁用（默认关闭）。请在插件配置中开启「允许删除仓库」后再试。此操作不可逆，请谨慎开启。"); return; }
                if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(r))
                { interactor.Poke("act=delete_repository 缺少必填参数: o (owner), r (repo)"); return; }
                var (code, outBody) = await ReqAsync("DELETE", $"/repos/{o}/{r}");
                interactor.Poke(code == 204 ? $"✅ 仓库 {o}/{r} 已删除（不可逆）" : $"删除仓库失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "delete_release":
            {
                int relId = rid;
                if (relId == 0 && !string.IsNullOrEmpty(nm))
                {
                    var (code2, out2) = await ReqAsync("GET", $"/repos/{o}/{r}/releases/tags/{Uri.EscapeDataString(nm)}");
                    if (code2 == 200)
                    {
                        try { relId = JObject.Parse(out2)["id"]?.ToObject<int>() ?? 0; } catch (JsonException) { relId = 0; }
                    }
                }
                if (relId == 0) { interactor.Poke("act=delete_release 需要 rid（release ID）或 nm（tag 名，自动查 ID）"); return; }
                var (code, outBody) = await ReqAsync("DELETE", $"/repos/{o}/{r}/releases/{relId}");
                interactor.Poke(code == 204 ? $"✅ Release 已删除（{o}/{r}，id={relId}）。注意：对应的 git tag 不会被删除。" : $"删除 Release 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "delete_comment":
            {
                if (cid == 0) { interactor.Poke("act=delete_comment 缺少必填参数: cid（评论 ID）"); return; }
                string path = k == "review"
                    ? $"/repos/{o}/{r}/pulls/comments/{cid}"
                    : $"/repos/{o}/{r}/issues/comments/{cid}";
                var (code, outBody) = await ReqAsync("DELETE", path);
                interactor.Poke(code == 204 ? $"✅ {(k == \"review\" ? \"PR review\" : \"Issue\")}评论 {cid} 已删除" : $"删除评论失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "delete_branch":
            {
                if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(r) || string.IsNullOrEmpty(nm))
                { interactor.Poke("Missing required parameters: o (owner), r (repo), nm (branch name)"); return; }
                var (code, outBody) = await ReqAsync("DELETE", $"/repos/{o}/{r}/git/refs/heads/{Uri.EscapeDataString(nm)}");
                interactor.Poke(code == 204 ? $"✅ Branch '{nm}' deleted successfully from {o}/{r}" : $"Delete failed (HTTP {code}): {outBody[..Math.Min(200, outBody.Length)]}");
                return;
            }
            default:
                interactor.Poke($"Unknown action: {act}");
                return;
        }
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_update")]
    [Description("更新 GitHub 资源：Issue（act=issue，改标题/正文/状态/标签/指派人/里程碑）、PR（act=pull_request，改标题/正文/状态）、更新 PR 分支（act=pull_request_branch）。lb/as 为 JSON 数组字符串。")]
    public async Task GithubUpdate(
        [Description("更新类型：issue / pull_request / pull_request_branch")] string act,
        [Description("仓库所有者")] string o,
        [Description("仓库名")] string r,
        [Description("Issue 编号（act=issue）")] int inn = 0,
        [Description("PR 编号（act=pull_request*）")] int pn = 0,
        [Description("新标题")] string ti = "",
        [Description("新正文")] string bd = "",
        [Description("新状态：open / closed")] string st = "",
        [Description("新标签列表，JSON 数组字符串")] string lb = "",
        [Description("新指派列表，JSON 数组字符串")] string as_ = "",
        [Description("里程碑编号")] int ms = 0,
        [Description("期望的 head SHA（act=pull_request_branch，可选）")] string sh = "")
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }

        JArray? ParseArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JArray.Parse(json); }
            catch (JsonException) { return null; }
        }

        switch (act)
        {
            case "issue":
            {
                var d = new JObject();
                if (!string.IsNullOrEmpty(ti)) d["title"] = ti;
                if (!string.IsNullOrEmpty(bd)) d["body"] = bd;
                if (!string.IsNullOrEmpty(st)) d["state"] = st;
                if (ParseArray(lb) is JArray lbArr) d["labels"] = lbArr;
                if (ParseArray(as_) is JArray asArr) d["assignees"] = asArr;
                if (ms > 0) d["milestone"] = ms;
                if (d.Count == 0) { interactor.Poke("No fields to update"); return; }
                if (inn <= 0) { interactor.Poke("act=issue 缺少必填参数: inn (Issue 编号)"); return; }
                var (code, outBody) = await ReqAsync("PATCH", $"/repos/{o}/{r}/issues/{inn}", d);
                interactor.Poke(code == 200 ? Fmt(outBody) : $"更新 Issue 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "pull_request":
            {
                if (pn <= 0) { interactor.Poke("act=pull_request 缺少必填参数: pn (PR 编号)"); return; }
                var d = new JObject();
                if (!string.IsNullOrEmpty(ti)) d["title"] = ti;
                if (!string.IsNullOrEmpty(bd)) d["body"] = bd;
                if (!string.IsNullOrEmpty(st)) d["state"] = st;
                if (d.Count == 0) { interactor.Poke("No fields to update"); return; }
                var (code, outBody) = await ReqAsync("PATCH", $"/repos/{o}/{r}/pulls/{pn}", d);
                interactor.Poke(code == 200 ? Fmt(outBody) : $"更新 PR 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "pull_request_branch":
            {
                if (pn <= 0) { interactor.Poke("act=pull_request_branch 缺少必填参数: pn (PR 编号)"); return; }
                var d = new JObject();
                if (!string.IsNullOrEmpty(sh)) d["expected_head_sha"] = sh;
                var (code, outBody) = await ReqAsync("PUT", $"/repos/{o}/{r}/pulls/{pn}/update-branch", d);
                interactor.Poke(code == 200 || code == 202 ? Fmt(outBody) : $"更新 PR 分支失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            default:
                interactor.Poke($"Unknown action: {act}");
                return;
        }
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_mutation")]
    [Description("批量操作：act=files 一次提交多个文件（fs 为 JSON 数组字符串，元素格式 {\"p\":\"路径\",\"c\":\"内容\"}，自动获取 SHA 判断新建/更新，逐个反馈真实成败）；act=issue_comment 发 Issue 评论（需 in 编号、bd 内容）；act=pull_request 合并 PR（需 pn 编号、mm 合并方式 merge/squash/rebase）。")]
    public async Task GithubMutation(
        [Description("批量类型：files / issue_comment / pull_request")] string act,
        [Description("仓库所有者")] string o,
        [Description("仓库名")] string r,
        [Description("分支名，留空自动用默认分支")] string br = "",
        [Description("提交信息（act=files）")] string msg = "",
        [Description("文件列表，JSON 数组字符串，如 [{\"p\":\"a.txt\",\"c\":\"content\"}]")] string fs = "",
        [Description("Issue 编号（act=issue_comment）")] int inn = 0,
        [Description("评论内容（act=issue_comment）或合并信息（act=pull_request）")] string bd = "",
        [Description("PR 编号（act=pull_request）")] int pn = 0,
        [Description("合并方式：merge / squash / rebase（act=pull_request）")] string mm = "merge",
        [Description("合并提交标题（act=pull_request，可选）")] string ct = "")
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }

        switch (act)
        {
            case "files":
            {
                if (string.IsNullOrWhiteSpace(fs)) { interactor.Poke("No files specified"); return; }
                JArray files;
                try { files = JArray.Parse(fs); }
                catch (JsonException) { interactor.Poke("fs 不是合法的 JSON 数组"); return; }
                string brEff = await ResolveBranchAsync(o, r, br);
                var results = new StringBuilder($"Files (branch: {brEff}):");
                foreach (var f in files)
                {
                    string fp = f["p"]?.ToString() ?? "";
                    string fc = f["c"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(fp))
                    {
                        results.Append("\n  (unknown): ❌ 缺少文件路径 p");
                        continue;
                    }
                    string sha = await GetFileShaAsync(o, r, fp, brEff);
                    var d = new JObject
                    {
                        ["message"] = msg ?? $"Update {fp}",
                        ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fc)),
                        ["branch"] = brEff,
                    };
                    if (!string.IsNullOrEmpty(sha)) d["sha"] = sha;
                    var (code, outBody) = await ReqAsync("PUT", $"/repos/{o}/{r}/contents/{Uri.EscapeDataString(fp)}", d);
                    try
                    {
                        var od = JObject.Parse(outBody);
                        if (od["content"]?["sha"] != null)
                        {
                            string newSha = od["content"]!["sha"]!.ToString();
                            results.Append($"\n  {fp}: ✅ {(string.IsNullOrEmpty(sha) ? \"新建\" : \"更新\")}成功 sha={newSha[..Math.Min(8, newSha.Length)]}");
                        }
                        else if (od["message"] != null)
                        {
                            string detail = od["message"]!.ToString();
                            if (od["errors"] is JArray errs2 && errs2.Count > 0)
                            {
                                string errText = errs2.ToString(Formatting.None);
                                detail += " — " + (errText.Length > 200 ? errText[..200] : errText);
                            }
                            results.Append($"\n  {fp}: ❌ {detail}");
                        }
                        else
                            results.Append($"\n  {fp}: ❌ {outBody[..Math.Min(120, outBody.Length)]}");
                    }
                    catch (JsonException)
                    {
                        results.Append($"\n  {fp}: ❌ {outBody[..Math.Min(120, outBody.Length)]}");
                    }
                }
                interactor.Poke(results.ToString());
                return;
            }
            case "issue_comment":
            {
                if (inn <= 0) { interactor.Poke("act=issue_comment 缺少必填参数: in (Issue 编号)"); return; }
                var d = new { body = bd ?? "" };
                var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/issues/{inn}/comments", d);
                interactor.Poke(code == 201 ? Fmt(outBody) : $"发评论失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            case "pull_request":
            {
                if (pn <= 0) { interactor.Poke("act=pull_request 缺少必填参数: pn (PR 编号)"); return; }
                var d = new JObject { ["merge_method"] = mm ?? "merge" };
                if (!string.IsNullOrEmpty(ct)) d["commit_title"] = ct;
                if (!string.IsNullOrEmpty(bd)) d["commit_message"] = bd;
                var (code, outBody) = await ReqAsync("PUT", $"/repos/{o}/{r}/pulls/{pn}/merge", d);
                interactor.Poke(code == 200 ? Fmt(outBody) : $"合并 PR 失败 (HTTP {code}): {Fmt(outBody)}");
                return;
            }
            default:
                interactor.Poke($"Unknown action: {act}");
                return;
        }
    }

    [XmlFunction(FunctionMode.OneShot, name: "github_fork")]
    [Description("Fork 仓库到自己的账号或指定组织。注意：GitHub Fork 是异步的，新仓库可能需要几秒才完全就绪；紧接着写文件失败就稍等重试。")]
    public async Task GithubFork(
        [Description("源仓库所有者")] string o,
        [Description("源仓库名")] string r,
        [Description("目标组织（可选，默认 fork 到自己账号）")] string org = "")
    {
        string err = CheckTokenError();
        if (err.Length > 0) { interactor.Poke(err); return; }
        var d = new JObject();
        if (!string.IsNullOrEmpty(org)) d["organization"] = org;
        var (code, outBody) = await ReqAsync("POST", $"/repos/{o}/{r}/forks", d);
        if (code != 202) { interactor.Poke($"Fork 失败 (HTTP {code}): {Fmt(outBody)}"); return; }
        string res = Fmt(outBody);
        if (!res.StartsWith("Error"))
            res += "\n\n💡 Fork 为异步操作，仓库可能需要几秒才完全就绪；若紧接着写文件失败，请稍后重试。";
        interactor.Poke(res);
    }

    // ==================== 生命周期 ====================

    protected override Task OnAwake()
    {
        if (Configuration.EnableDeleteRepository)
            logger.LogWarning("GitHubTool: 「允许删除仓库」已开启，AI 可调用 delete_repository 删除仓库（不可逆），请注意风险！");
        if (Configuration.ExposeTokenInCheck)
            logger.LogWarning("GitHubTool: 「检查Token时返回明文」已开启，github_check_token 将返回明文 Token，请注意日志安全！");

        var handler = new XmlHandler(this)
        {
            Description = "GitHub 工具：提供对 GitHub REST API 的完整操作能力（搜索仓库/代码/Issue/用户、读取文件内容、创建/更新 Issue/PR/Release/分支、批量提交文件、合并 PR、Fork 等）。Token 已在插件配置中设置，调用时无需传认证参数。",
            Explanation = """
                GitHub 工具使用说明
                - 所有函数无需传 token/认证参数，插件自动携带
                - 分支参数（b/br）留空即自动使用仓库默认分支（main/master 均可），无需手动指定
                - 写文件（github_create act=file / github_mutation act=files）无需先获取文件 SHA，插件自动判断新建还是更新
                - 搜索 query 中的空格/特殊字符会自动 URL 编码
                - 大文件读取用 github_read_file 的 offset/limit 分页
                - 删除仓库（act=delete_repository）默认被插件开关禁用，需在配置中开启（不可逆，谨慎）
                - 状态检查可随时调用 github_check_token
                """,
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

        if (TokenConfigured)
            _ = ValidateTokenAsync();
        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        try
        {
            ChatBot.EditChatHistory(thread =>
            {
                thread.ChatHistory.AddSystemMessage(BuildTokenHint());
            }, "GitHubTool token 状态提示");
        }
        catch (Exception e)
        {
            logger.LogWarning("注入 GitHub token 提示失败: {Message}", e.Message);
        }
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        DefaultBranchCache.Clear();
        return Task.CompletedTask;
    }

    private async Task ValidateTokenAsync()
    {
        try
        {
            var (code, body) = await ReqAsync("GET", "/user");
            if (code >= 200 && code < 300)
            {
                _githubUser = JObject.Parse(body)["login"]?.ToString() ?? "";
                logger.LogInformation("GitHub Tool ready, logged in as {User}", _githubUser);
            }
            else
            {
                logger.LogWarning("GitHub Token 可能无效，/user 返回 HTTP {Code}", code);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("GitHub Token 验证失败: {Message}", e.Message);
        }
    }

    private string BuildTokenHint()
    {
        if (!TokenConfigured)
            return "【GitHub Token 状态】GitHub Personal Access Token 未配置。请提醒用户到插件配置中填写（需要 repo 权限），否则所有 GitHub 工具不可用。";
        string hint = "【GitHub Token 状态】GitHub Personal Access Token 已在插件配置中设置完毕且有效。";
        if (!string.IsNullOrEmpty(_githubUser))
            hint += $" 当前登录的 GitHub 账号为: {_githubUser}。";
        else
            hint += " 但未能获取用户名，请检查 Token 是否有效。";
        hint += " 调用 GitHub 工具时无需传 token 参数，插件会自动携带认证。所有工具的分支参数留空即自动使用仓库默认分支。写文件无需先获取文件 SHA，插件自动判断新建还是更新。对 token 状态有疑问可调用 github_check_token 确认。";
        return hint;
    }
}
