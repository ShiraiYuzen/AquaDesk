using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AquaDesk.Services;

public enum UpdateStatus
{
    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>远端有新版本。</summary>
    Available,

    /// <summary>没检查成（网络、限流、解析失败…）。</summary>
    Failed,
}

/// <summary>远端 release 的信息。</summary>
public sealed record UpdateInfo(
    string Version,
    string Title,
    string Notes,
    string PageUrl,
    string? DownloadUrl,
    string? Digest,
    long Size);

public sealed record UpdateCheckResult(
    UpdateStatus Status,
    UpdateInfo? Info,
    string Message,
    string? Diagnostics = null);

/// <summary>下载完成、校验通过的安装包。</summary>
public sealed record UpdatePackage(string Path, long Size);

/// <summary>
/// 检查 / 下载 / 应用更新。
///
/// 版本号用 csproj 里的 <c>InformationalVersion</c>（0.7.0 这种），不是
/// <c>AssemblyVersion</c>（那个是给 Win32 文件版本用的，一直没跟着发版改）。
///
/// 三道防线，缺一不可：
/// 1. **多源**：GitHub API 在国内时通时不通，失败就换镜像前缀再试。
/// 2. **验签**：下载下来的 exe 必须带我们这把证书的 Authenticode 签名
///    （指纹见 <see cref="PublisherThumbprint"/>），有 SHA256 摘要就一起比对。
///    只信 API 返回的链接是不够的 —— 镜像被投毒一样会把假 exe 装上。
/// 3. **替换**：运行中的 exe 不能覆盖自己，所以要起一个等进程退出的 PowerShell
///    去做 copy，再拉起新版本。脚本用 <c>-EncodedCommand</c>（UTF-16 Base64）传，
///    避开中文路径在批处理里的编码坑。
/// </summary>
public static class UpdateService
{
    private const string Owner = "ShiraiYuzen";
    private const string Repo = "AquaDesk";

    private const string ApiPath = $"repos/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// 请求源，按顺序试。第一个是官方 API，后面是常见镜像前缀 ——
    /// 国内直连 api.github.com 经常超时，镜像能救回来不少。
    /// </summary>
    private static readonly string[] Sources =
    {
        $"https://api.github.com/{ApiPath}",
        $"https://ghproxy.net/https://api.github.com/{ApiPath}",
        $"https://gh-proxy.com/https://api.github.com/{ApiPath}",
    };

    /// <summary>发布用的自签证书指纹（和 verify_release.ps1 里那个白名单一致）。</summary>
    private const string PublisherThumbprint = "62DF52A960B467FE81D1B471A08A8447A86FFA5B";

    /// <summary>当前版本的可读文本，比如 0.7.0。</summary>
    public static string CurrentVersionText { get; } = ReadInformationalVersion();

    /// <summary>后台静默检查的结果，设置页打开时直接拿来用；null 表示还没查过或没新版。</summary>
    public static UpdateInfo? PendingUpdate { get; private set; }

    private static string _skippedVersion = string.Empty;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // GitHub 的 API 强制要求 User-Agent，缺了直接 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AquaDesk-UpdateChecker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    // ---------------- 检查 ----------------

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var errors = new List<string>();
        var trace = new List<string>();

        foreach (var source in Sources)
        {
            try
            {
                App.Log($"检查更新：请求 {source}", null);

                using var response = await Http.GetAsync(source).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var hint = (int)response.StatusCode switch
                    {
                        403 => "接口限流",
                        404 => "仓库还没有发布版本",
                        _ => $"HTTP {(int)response.StatusCode}",
                    };

                    errors.Add(hint);
                    trace.Add($"{(int)response.StatusCode}  {source}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var info = ParseRelease(json);

                if (info is null)
                {
                    errors.Add("返回内容无法解析");
                    trace.Add($"200（解析失败）  {source}");
                    continue;
                }

                var available = IsNewer(info.Version, CurrentVersionText);

                App.Log($"检查更新：本地 {CurrentVersionText}，远端 {info.Version}，" +
                        (available ? "有新版" : "已是最新"), null);

                if (available) PendingUpdate = info;

                return new UpdateCheckResult(
                    available ? UpdateStatus.Available : UpdateStatus.UpToDate,
                    info,
                    string.Empty);
            }
            catch (TaskCanceledException)
            {
                errors.Add("超时");
                trace.Add($"超时  {source}");
                App.Log($"检查更新：{source} 超时", null);
            }
            catch (HttpRequestException ex)
            {
                errors.Add("连不上");
                trace.Add($"连不上  {source}");
                App.Log($"检查更新：{source} 连不上", ex);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                trace.Add($"异常  {source}：{ex.Message}");
                App.Log($"检查更新：{source} 出错", ex);
            }
        }

        var message = errors.Count > 0
            ? $"所有源都没成功（{string.Join("、", errors.Distinct())}），检查一下网络或代理"
            : "检查失败";

        return new UpdateCheckResult(UpdateStatus.Failed, null, message, BuildDiagnostics(trace));
    }

    /// <summary>
    /// 拼一段人可读的诊断信息，用户反馈时直接贴过来，不用去翻日志。
    /// 只含版本、时间、系统和每个源的请求结果，不含任何隐私数据。
    /// </summary>
    private static string BuildDiagnostics(List<string> trace)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AquaDesk {CurrentVersionText}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"系统：{Environment.OSVersion}");
        sb.AppendLine($"运行时：.NET {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("请求源（按顺序尝试）：");

        foreach (var line in trace) sb.AppendLine("  " + line);

        return sb.ToString().TrimEnd();
    }

    /// <summary>启动后台静默检查：不弹窗，只把结果记在 <see cref="PendingUpdate"/> 里。</summary>
    public static async void CheckQuietly()
    {
        try
        {
            var result = await CheckAsync().ConfigureAwait(false);

            if (result.Status != UpdateStatus.Available || result.Info is null) return;

            if (IsSkipped(result.Info.Version))
            {
                App.Log($"检查更新：{result.Info.Version} 已被用户跳过", null);
                PendingUpdate = null;
                return;
            }

            App.Log($"检查更新：发现 {result.Info.Version}（静默）", null);
        }
        catch (Exception ex)
        {
            App.Log("后台检查更新失败", ex);
        }
    }

    // ---------------- 跳过某版本 ----------------

    /// <summary>
    /// 跳过状态单独存一个文件，不塞进 settings.json —— 它不属于「外观设置」，
    /// 而且删掉 settings.json 重置外观时，不该连带把「跳过哪个版本」也忘了。
    /// </summary>
    private static string StatePath => Path.Combine(SettingsStore.RootDirectory, "update-state.json");

    public static string SkippedVersion => _skippedVersion;

    public static bool IsSkipped(string version) =>
        string.Equals(_skippedVersion.TrimStart('v', 'V'), version.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    public static void SkipVersion(string version) => _skippedVersion = (version ?? string.Empty).TrimStart('v', 'V');

    public static void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));

            if (document.RootElement.TryGetProperty("SkippedVersion", out var value))
            {
                _skippedVersion = (value.GetString() ?? string.Empty).TrimStart('v', 'V');
            }
        }
        catch (Exception ex)
        {
            App.Log("更新：读取跳过状态失败", ex);
        }
    }

    public static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.RootDirectory);

            var json = JsonSerializer.Serialize(
                new { SkippedVersion = _skippedVersion },
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(StatePath, json);
        }
        catch (Exception ex)
        {
            App.Log("更新：保存跳过状态失败", ex);
        }
    }

    // ---------------- 下载 + 验签 ----------------

    /// <summary>
    /// 把安装包下载到临时目录并校验。Package 为 null 时 <paramref name="error"/> 里是原因。
    /// </summary>
    public static async Task<(UpdatePackage? Package, string Error)> DownloadAsync(
        UpdateInfo info,
        IProgress<double>? progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            return (null, "这个 release 没有附带 exe，只能去下载页手动取");
        }

        var directory = Path.Combine(Path.GetTempPath(), "AquaDesk-update");
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"AquaDesk-{info.Version}.exe";

        var target = Path.Combine(directory, fileName);

        try
        {
            using var response = await Http
                .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;

                    if (total > 0) progress?.Report(received / (double)total);
                }
            }

            progress?.Report(1);

            var length = new FileInfo(target).Length;

            // 1) 大小：对不上说明下载被截断
            if (info.Size > 0 && length != info.Size)
            {
                File.Delete(target);
                return (null, $"文件大小不符（{length} / {info.Size}），下载可能被中断");
            }

            // 2) SHA256：GitHub 现在会给 asset 的 digest，有就比对
            if (!string.IsNullOrWhiteSpace(info.Digest)
                && info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var expected = info.Digest["sha256:".Length..].Trim();

                await using var stream = File.OpenRead(target);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(target);
                    return (null, "SHA256 和发布清单对不上，已丢弃");
                }
            }

            // 3) Authenticode：必须是发布证书签的
            var thumbprint = ReadSignerThumbprint(target);

            if (!string.Equals(thumbprint, PublisherThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);

                return (null, thumbprint.Length == 0
                    ? "下载的 exe 没有数字签名，已丢弃"
                    : $"签名证书指纹不符（{thumbprint}），已丢弃");
            }

            App.Log($"更新：已下载并验签通过 {fileName}（{length / 1024 / 1024} MB）", null);

            return (new UpdatePackage(target, length), string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryDelete(target);
            return (null, "下载已取消");
        }
        catch (Exception ex)
        {
            TryDelete(target);
            App.Log("更新：下载失败", ex);
            return (null, $"下载失败：{ex.Message}");
        }
    }

    /// <summary>读文件签名证书的指纹；没签名返回空串。</summary>
    private static string ReadSignerThumbprint(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate.GetCertHashString();
        }
        catch
        {
            // 没签名、或者签名结构损坏，都走这里
            return string.Empty;
        }
    }

    // ---------------- 应用（替换 + 重启） ----------------

    /// <summary>
    /// 把新版本覆盖到当前 exe 上并重启。
    ///
    /// 自己不能覆盖自己，所以起一个隐藏的 PowerShell：等本进程退出 → 重试复制 →
    /// 拉起新版本。脚本用 -EncodedCommand 传（UTF-16 Base64），中文字符串不会被
    /// 代码页毁掉 —— 用 .bat 走 chcp 那套在中文路径上很容易翻车。
    /// </summary>
    public static bool ApplyAndRestart(UpdatePackage package)
    {
        try
        {
            var target = Environment.ProcessPath;

            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                App.Log("更新：找不到当前 exe 路径，放弃替换", null);
                return false;
            }

            var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {Environment.ProcessId} -Timeout 30

for ($i = 0; $i -lt 30; $i++) {{
    try {{
        Copy-Item -LiteralPath '{Escape(package.Path)}' -Destination '{Escape(target)}' -Force -ErrorAction Stop
        break
    }} catch {{
        Start-Sleep -Milliseconds 400
    }}
}}

Start-Process -FilePath '{Escape(target)}'
";

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            App.Log($"更新：已安排替换并重启（{package.Path} → {target}）", null);

            return true;
        }
        catch (Exception ex)
        {
            App.Log("更新：安排替换失败", ex);
            return false;
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就留着，下次下载会覆盖
        }
    }

    // ---------------- 解析 ----------------

    private static UpdateInfo? ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;

            if (tag.Length == 0) return null;

            var title = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            var notes = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? string.Empty
                : string.Empty;

            var pageUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : $"https://github.com/{Owner}/{Repo}/releases";

            string? downloadUrl = null;
            string? digest = null;
            long size = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;

                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    digest = asset.TryGetProperty("digest", out var g) ? g.GetString() : null;
                    size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    break;
                }
            }

            return new UpdateInfo(tag.TrimStart('v', 'V'), title, notes, pageUrl, downloadUrl, digest, size);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 解析 release 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>读 csproj 的 InformationalVersion；读不到就退回 AssemblyVersion。</summary>
    private static string ReadInformationalVersion()
    {
        try
        {
            var assembly = typeof(App).Assembly;

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.InformationalVersion))
            {
                var text = attribute.InformationalVersion;
                var plus = text.IndexOf('+');

                return plus > 0 ? text[..plus] : text;
            }

            var version = assembly.GetName().Version ?? new Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// semver 比较：主.次.修订[-预发布]。
    /// 有预发布标记的**小于**同号正式版（0.8.0-rc1 &lt; 0.8.0），
    /// 两边都有标记就按字典序（rc1 &lt; rc2）。
    /// </summary>
    private static bool IsNewer(string remote, string local)
    {
        var r = Split(remote);
        var l = Split(local);

        for (var i = 0; i < 3; i++)
        {
            if (r.Numbers[i] != l.Numbers[i]) return r.Numbers[i] > l.Numbers[i];
        }

        if (r.Pre.Length == 0 && l.Pre.Length == 0) return false;
        if (r.Pre.Length == 0) return true;      // 正式版 > 预发布
        if (l.Pre.Length == 0) return false;

        return string.Compare(r.Pre, l.Pre, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static (int[] Numbers, string Pre) Split(string text)
    {
        var cleaned = (text ?? string.Empty).Trim().TrimStart('v', 'V');

        var pre = string.Empty;
        var dash = cleaned.IndexOf('-');

        if (dash >= 0)
        {
            pre = cleaned[(dash + 1)..];
            cleaned = cleaned[..dash];
        }

        var plus = cleaned.IndexOf('+');
        if (plus > 0) cleaned = cleaned[..plus];

        var parts = cleaned.Split('.');
        var numbers = new int[3];

        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            int.TryParse(parts[i], out numbers[i]);
        }

        return (numbers, pre);
    }
}
