using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteGallery.Services;

/// <summary>
/// 「检查更新」的纯逻辑部分：读 GitHub Releases latest 接口、比较版本号、
/// 在发布资产里挑出安装包。界面只管调，不掺和解析细节。
///
/// 刻意和界面分开的原因和 PhotoOcr 一样：**能拉到控制台里压测**。
/// 版本比较、资产匹配这两块是最容易写错又最难靠点鼠标测出来的地方
/// （比如 26.9.13 和 26.9.13.1 谁新、release 里挂了五个资产该挑哪个），
/// 分开之后可以用 `_cvupd` 探针批量喂 JSON 验，不用手动发版。
///
/// 仓库地址见 <see cref="GithubReleasesApi"/>。发版流程：
/// 在 GitHub 上打 tag（如 v26.9.14）并发布 Release，
/// 把名字里带 Setup 的安装包（CelesteGallery-Setup-*.exe）挂到资产里即可。
/// 没有安装包资产时只提示"有新版本"，不给下载按钮。
/// </summary>
public static class UpdateChecker
{
    /// <summary>GitHub Releases latest 接口。</summary>
    public const string GithubReleasesApi =
        "https://api.github.com/repos/Celeste-114514/CelesteGallery/releases/latest";

    /// <summary>Release 页面的地址（人工下载用，界面上的「GitHub Releases ↗」）。</summary>
    public const string ReleasesPageUrl =
        "https://github.com/Celeste-114514/CelesteGallery/releases";

    /// <summary>最近一次发现的新版本（仅当比当前版本新才非空）。</summary>
    public sealed class UpdateInfo
    {
        public UpdateInfo(string tag, string? setupUrl, string? notes)
        {
            Tag = tag;
            SetupUrl = setupUrl;
            Notes = notes;
        }

        /// <summary>版本 tag，如 "v26.9.14"。</summary>
        public string Tag { get; }

        /// <summary>安装包（Setup-*.exe）下载地址；无匹配资产为 null。</summary>
        public string? SetupUrl { get; }

        /// <summary>Release 说明正文（Markdown 原文），可为空。</summary>
        public string? Notes { get; }
    }

    /// <summary>启动期自动检查发现的新版本；未发现有更新时为 null。</summary>
    public static UpdateInfo? LatestAvailable { get; private set; }

    /// <summary>最近一次操作的失败原因。成功为 null。界面想记日志就读它。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 当前程序集版本号（如 "26.9.13" 或 "26.9.13.1"）。
    ///
    /// 用 <c>GetEntryAssembly</c>**不是**随手写的：这个类住在 CelesteGallery.Core 里，
    /// <c>GetExecutingAssembly()</c> 在这个类里拿到的是 Core.dll 的版本号，
    /// 而 csproj 里配的 <c>&lt;Version&gt;</c> 只写进主程序 exe —— 两者对不上，
    /// 结果就是"永远检查不到更新"或者"永远提示有更新"。
    /// 控制台探针里没有 exe 入口，那时退回 GetExecutingAssembly，不影响压测。
    /// </summary>
    public static string CurrentVersionText()
    {
        Assembly asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        Version? v = asm.GetName().Version;
        if (v is null) return "未知";

        // 完整输出 4 段（含修订号），否则 26.9.13.1 会显示成 26.9.13，
        // 和最初的 26.9.13 分不开，用户会以为"更新了个寂寞"。
        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// 比较两个版本字符串（支持 v 前缀与 -beta/-rc 后缀）。
    /// 返回值：&lt;0 / 0 / &gt;0 表示 a 比 b 旧/相等/新。
    /// </summary>
    public static int CompareVersionStrings(string a, string b)
        => ParseLoose(a).CompareTo(ParseLoose(b));

    private static Version ParseLoose(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Version(0, 0, 0, 0);

        string trimmed = raw.Trim().TrimStart('v', 'V');
        int dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];

        if (!Version.TryParse(trimmed, out Version? parsed) || parsed is null)
            return new Version(0, 0, 0, 0);

        // 规范化到 4 段、缺失补 0。Version 的 -1 会被 CompareTo 当成"更小"，
        // 不补零会让 26.9.13（Revision=-1）被判成比 26.9.13.1 还旧。
        return new Version(
            parsed.Major < 0 ? 0 : parsed.Major,
            parsed.Minor < 0 ? 0 : parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
    }

    /// <summary>
    /// 从一段 GitHub Release 的 JSON 里挑出安装包地址。
    /// 单独拆出来就是为了能压测 —— 这一段不联网，纯字符串处理。
    ///
    /// 挑选顺序：名字里带 Setup/Install 的 .exe → 退而求其次第一个 .exe。
    /// 没有 .exe 就返回 null（比如 release 只挂了源码包）。
    /// </summary>
    public static string? FindSetupAsset(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        string? fallback = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u)
                    ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;

                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Install", StringComparison.OrdinalIgnoreCase))
                    return url;

                fallback ??= url;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return fallback;
    }

    /// <summary>从一个 Release JSON 里读 tag_name。读不到返回 null。</summary>
    public static string? ReadTag(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out JsonElement el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读 Release 说明正文（更新日志用）。没有就是 null。</summary>
    public static string? ReadNotes(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("body", out JsonElement el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 联网拉最新 Release。成功返回原始 JSON（失败时 tag/安装包都为 null，
    /// 失败原因写进 <see cref="LastError"/>）。
    ///
    /// 超时压到 15 秒是有意的：这是"点一下看看有没有新版"的操作，
    /// 不是下载，卡住半分钟让人干等不如早点告诉用户网络不通。
    /// </summary>
    public static async Task<(string? Tag, string? SetupUrl, string? Notes)> FetchLatestAsync()
    {
        LastError = null;
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteGallery/" + CurrentVersionText());

            string json = await http.GetStringAsync(GithubReleasesApi);
            return (ReadTag(json), FindSetupAsset(json), ReadNotes(json));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StartupLog.Write("UpdateChecker.FetchLatest", ex);
            return (null, null, null);
        }
    }

    /// <summary>
    /// 拉取 + 比较。比当前版本新就写进 <see cref="LatestAvailable"/> 并返回它，
    /// 否则清空并返回 null。
    ///
    /// 注意：返回 null **既可能是"已是最新"，也可能是"网络不通"**，
    /// 界面要区分这两种情况得自己看 <see cref="LastError"/> —— 别把"检查失败"
    /// 显示成"已是最新版本"，那是在骗用户。
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        (string? tag, string? setupUrl, string? notes) = await FetchLatestAsync();

        if (string.IsNullOrEmpty(tag) || CompareVersionStrings(tag, CurrentVersionText()) <= 0)
        {
            LatestAvailable = null;
            return null;
        }

        LatestAvailable = new UpdateInfo(tag!, setupUrl, notes);
        return LatestAvailable;
    }
}
