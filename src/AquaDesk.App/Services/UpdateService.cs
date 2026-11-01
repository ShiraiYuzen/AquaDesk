using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
#if VELOPACK
using Velopack;
#endif

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
    long Size,
    string? ZipDownloadUrl = null,
    string? ZipDigest = null,
    long ZipSize = 0);

public sealed record UpdateCheckResult(
    UpdateStatus Status,
    UpdateInfo? Info,
    string Message,
    string? Diagnostics = null);

/// <summary>
/// 检查 / 下载 / 应用更新。
///
/// 版本号用 csproj 里的 <c>InformationalVersion</c>（0.7.0 这种），不是
/// <c>AssemblyVersion</c>（那个是给 Win32 文件版本用的，一直没跟着发版改）。
///
/// 两种模式，行为不同：
/// - 便携版（默认，未定义 VELOPACK）：只检查更新，把新版本说明 + SHA256 展示给
///   用户，用户自己去发布页手动下载，不自动下载、不自动替换。
/// - 安装版（定义 VELOPACK）：走 Velopack 框架自动下载 + 应用 + 重启。
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

#if VELOPACK
    /// <summary>Velopack 安装版的更新源：GitHub Releases 的 latest download 目录。</summary>
    private const string VelopackFeedUrl = $"https://github.com/{Owner}/{Repo}/releases/latest/download/";
#endif

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

#if VELOPACK
    /// <summary>Velopack 安装版：直接问 Velopack 更新源有没有新版本。</summary>
    public static async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            var latest = await CreateManager().CheckForUpdatesAsync().ConfigureAwait(false);

            if (latest is null)
            {
                App.Log($"检查更新(Velopack)：本地 {CurrentVersionText}，已是最新", null);
                return new UpdateCheckResult(UpdateStatus.UpToDate, null, string.Empty);
            }

            var remoteVersion = latest.TargetFullRelease.Version.ToString();

            var info = new UpdateInfo(
                remoteVersion,
                remoteVersion,
                string.Empty,
                $"https://github.com/{Owner}/{Repo}/releases/latest",
                null,
                null,
                latest.TargetFullRelease.Size);

            PendingUpdate = info;
            App.Log($"检查更新(Velopack)：本地 {CurrentVersionText}，远端 {remoteVersion}，有新版", null);

            return new UpdateCheckResult(UpdateStatus.Available, info, string.Empty);
        }
        catch (Exception ex)
        {
            App.Log("检查更新(Velopack)失败", ex);

            var diagnostics =
                $"AquaDesk {CurrentVersionText}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"系统：{Environment.OSVersion}\n运行时：.NET {Environment.Version}\n\n" +
                $"Velopack 更新源：{VelopackFeedUrl}\n异常：{ex.Message}";

            return new UpdateCheckResult(UpdateStatus.Failed, null, ex.Message, diagnostics);
        }
    }
#else
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
#endif

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

    // ---------------- 下载 + 应用（统一入口） ----------------

    /// <summary>
    /// 下载并应用更新，一步到位。返回 null 表示成功；非 null 是错误信息。
    /// 安装版（VELOPACK）走 Velopack 框架；便携版（net48）下载 zip → 原地替换 → 自动重启。
    /// </summary>
    public static async Task<string?> DownloadAndApplyAsync(UpdateInfo info, IProgress<double> progress)
    {
#if VELOPACK
        try
        {
            var mgr = CreateManager();
            var latest = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);

            if (latest is null) return "没有可用的更新";

            await mgr.DownloadUpdatesAsync(latest, p => progress.Report(NetFxShims.Clamp(p / 100.0, 0, 1)))
                .ConfigureAwait(false);

            mgr.ApplyUpdatesAndRestart(latest);
            return null; // 实际不会返回：ApplyUpdatesAndRestart 内部退出并重启
        }
        catch (Exception ex)
        {
            App.Log("Velopack：下载并应用更新失败", ex);
            return ex.Message;
        }
#else
        if (info?.ZipDownloadUrl is null)
        {
            return "发布包中未找到可用的 net48 更新包（.zip），请点击「打开发布页」手动下载。";
        }

        try
        {
            var tempZip = Path.Combine(Path.GetTempPath(), $"AquaDesk-update-{info.Version}.zip");
            var extractDir = Path.Combine(Path.GetTempPath(), $"AquaDesk-update-{info.Version}");

            await DownloadZipAsync(info.ZipDownloadUrl, tempZip, progress, info.ZipSize).ConfigureAwait(false);

            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(tempZip, extractDir);
            File.Delete(tempZip);

            InstallAndRestart(extractDir);
            return null; // InstallAndRestart 已退出并重启，正常不会返回
        }
        catch (Exception ex)
        {
            App.Log("net48 更新：下载并应用失败", ex);
            return ex.Message;
        }
#endif
    }

    // ---------------- net48 / 便携版：一键自更新（下载 zip → 原地替换 → 重启） ----------------

    // 国内可达的加速镜像前缀；下载直连失败时逐个尝试。
    private static readonly string[] DownloadMirrors =
    {
        "https://ghfast.top/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/"
    };

    private static IEnumerable<string> DownloadCandidates(string url)
    {
        yield return url;
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mirror in DownloadMirrors) yield return mirror + url;
        }
    }

    private static async Task DownloadZipAsync(string url, string dest, IProgress<double> progress, long expectedSize)
    {
        Exception? lastError = null;

        foreach (var candidate in DownloadCandidates(url))
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AquaDesk-Updater/1.0");

                using var response = await client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? expectedSize;
                using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress.Report(NetFxShims.Clamp(done * 1.0 / total, 0, 1));
                }

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            }
        }

        throw new Exception("下载更新包失败，网络可能受限。", lastError);
    }

    /// <summary>
    /// 写入重启脚本：等本进程退出 → robocopy 覆盖程序文件 → 重新启动 → 自删。
    /// 用户配置在 %AppData% 不在程序目录，robocopy 只覆盖 exe/dll，不影响配置。
    /// </summary>
    private static void InstallAndRestart(string extractedDir)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        // 目录不可写（Program Files 等）：直接引导去发布页手动更新
        try
        {
            var probe = Path.Combine(appDir, "AquaDesk-write-test.tmp");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://github.com/{Owner}/{Repo}/releases")
                {
                    UseShellExecute = true
                });
            }
            catch { }

            return;
        }

        var script = Path.Combine(Path.GetTempPath(), "AquaDesk-update-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");
        var lines = new[]
        {
            "@echo off",
            ":wait",
            "tasklist /FI \"IMAGENAME eq AquaDesk.exe\" 2>nul | find /I \"AquaDesk.exe\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            "robocopy \"" + extractedDir + "\" \"" + appDir + "\" /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS >nul",
            "start \"\" \"" + Path.Combine(appDir, "AquaDesk.exe") + "\"",
            "rmdir /S /Q \"" + extractedDir + "\"",
            "del \"%~f0\""
        };
        File.WriteAllLines(script, lines, Encoding.ASCII);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });

        // 正常退出，把覆盖 + 重启交给脚本；配置已在日常操作中保存，不会被清。
        System.Windows.Application.Current.Shutdown();
    }

#if VELOPACK
    private static UpdateManager CreateManager() => new(VelopackFeedUrl);
#endif

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
            string? zipUrl = null;
            string? zipDigest = null;
            long zipSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                string? setupUrl = null;
                string? setupDigest = null;
                long setupSize = 0;

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var url = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    var dig = asset.TryGetProperty("digest", out var g) ? g.GetString() : null;
                    var sz = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                    var isExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    var isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

                    if (!isExe && !isZip) continue;

                    if (isExe)
                    {
                        // 同一个 release 里可能挂多个 exe：便携版 AquaDesk-x.y.z.exe、Velopack 的
                        // Setup.exe / win-* 包。便携版要的是前者，排除掉 Setup / win- 前缀的。
                        if (!name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("-win-", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = url;
                            digest = dig;
                            size = sz;
                            break;
                        }

                        setupUrl ??= url;
                        setupDigest ??= dig;
                        setupSize = sz;
                        continue;
                    }

                    // .zip：net48 自更新要的就是它；优先 net48 命名的包，否则第一个非 -src 的 zip
                    if (!name.Contains("-src", StringComparison.OrdinalIgnoreCase))
                    {
                        var isNet48 = name.Contains("net48", StringComparison.OrdinalIgnoreCase);
                        if (zipUrl is null || isNet48)
                        {
                            zipUrl = url;
                            zipDigest = dig;
                            zipSize = sz;
                        }
                    }
                }

                // 兜底：万一只有 Setup.exe（理论上不会），至少别让 digest 空着
                if (downloadUrl is null)
                {
                    downloadUrl = setupUrl;
                    digest = setupDigest;
                    size = setupSize;
                }
            }

            return new UpdateInfo(tag.TrimStart('v', 'V'), title, notes, pageUrl, downloadUrl, digest, size, zipUrl, zipDigest, zipSize);
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
