using System.Windows.Media;
using Windows.Media.Control;

namespace AquaDesk.Services;

/// <summary>当前正在播放的一条媒体会话快照。</summary>
public sealed class MediaSnapshot
{
    /// <summary>播放它的应用（AUMID，可能是 exe 名、完整路径或 UWP 的 <c>包族名!应用ID</c>）。</summary>
    public string AppId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;

    public bool IsPlaying { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }

    /// <summary>左侧那个应用图标，取不到就是 null（界面退回音符字形）。</summary>
    public ImageSource? Icon { get; init; }

    /// <summary>用来判断「内容有没有变」，避免每轮轮询都刷一遍界面。</summary>
    public string Key =>
        $"{AppId}\u0001{Title}\u0001{Artist}\u0001{IsPlaying}\u0001{CanPrevious}\u0001{CanPlayPause}\u0001{CanNext}";
}

/// <summary>
/// 读系统正在播放的音乐，并能控制上一首 / 播放暂停 / 下一首。
///
/// 走的是 Windows 自己的 SMTC（System Media Transport Controls）：
/// 凡是接了媒体会话的应用（Spotify、网易云、QQ 音乐、浏览器里放歌……）都会被系统汇总到一个会话管理器里，
/// 我们只认「当前会话」，不用一个个去适配播放器、也不用读窗口标题。
///
/// **轮询而不是订阅事件**：SMTC 的事件会从 COM 的其它线程回调过来，
/// 每条都要 Dispatcher 转一次，还要在切歌 / 关应用 / 切播放器之间处理竞态；
/// 内容变化本来就慢（几百毫秒级别），600ms 轮询一次、比对一下 Key 更省心，也更不容易出怪问题。
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private const int PollMs = 600;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public MediaSnapshot? Current { get; private set; }

    /// <summary>内容变了（含从「在播」变成「没在播」，这时传 null）。在后台线程上触发。</summary>
    public event Action<MediaSnapshot?>? Changed;

    /// <summary>
    /// 排查用：把当前所有媒体会话打到日志里（<c>--media-info</c> 启动）。
    /// 看得到会话但胶囊没反应，就往界面那边查；一个会话都没有，就是播放器没接 SMTC。
    /// </summary>
    public static async Task DumpSessionsAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            App.Log($"媒体会话：共 {sessions.Count} 个", null);

            foreach (var session in sessions)
            {
                var info = session.GetPlaybackInfo();
                var props = await session.TryGetMediaPropertiesAsync();
                App.Log($"  · {session.SourceAppUserModelId} | {props?.Title} / {props?.Artist} | {info.PlaybackStatus}", null);
            }

            var current = manager.GetCurrentSession();
            App.Log($"媒体会话：当前 = {current?.SourceAppUserModelId ?? "（无）"}", null);
        }
        catch (Exception ex)
        {
            App.Log("媒体会话：枚举失败", ex);
        }
    }

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ---------------- 控制 ----------------

    public void TogglePlayPause() => Run(s => s.TryTogglePlayPauseAsync());

    public void Next() => Run(s => s.TrySkipNextAsync());

    public void Previous() => Run(s => s.TrySkipPreviousAsync());

    /// <summary>把控制请求丢到线程池，失败就静默 —— 播放器不支持时 SMTC 会返回 false。</summary>
    private void Run(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
    {
        var session = _session;
        if (session is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await action(session);
            }
            catch (Exception ex)
            {
                App.Log("媒体控制：发送指令失败（播放器可能不支持）", ex);
            }

            // 让主循环立刻再读一次，按钮状态不用等下一轮
            Wake();
        });
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已经有人叫醒过了，忽略
        }
    }

    // ---------------- 轮询 ----------------

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch (Exception ex)
            {
                App.Log("媒体会话：读取失败", ex);
            }

            try
            {
                await _wake.WaitAsync(PollMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync()
    {
        if (_manager is null)
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            App.Trace("媒体会话：管理器已就绪");
        }

        var session = _manager.GetCurrentSession();
        _session = session;

        MediaSnapshot? snapshot = null;

        if (session is not null)
        {
            var info = session.GetPlaybackInfo();
            var status = info.PlaybackStatus;

            // 只有「正在播」或「暂停」才算有内容；Closed/Stopped 就退回时钟
            if (status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var title = props?.Title ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(title))
                {
                    var controls = info.Controls;

                    snapshot = new MediaSnapshot
                    {
                        AppId = session.SourceAppUserModelId ?? string.Empty,
                        Title = title.Trim(),
                        Artist = (props?.Artist ?? string.Empty).Trim(),
                        IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        CanPrevious = controls is null || controls.IsPreviousEnabled,
                        // SMTC 没有「播放/暂停」这个合成开关，只有 play 和 pause 两个，
                        // 任何一个可用就说明能控制
                        CanPlayPause = controls is null || controls.IsPlayEnabled || controls.IsPauseEnabled,
                        CanNext = controls is null || controls.IsNextEnabled,
                        Icon = ResolveIcon(session.SourceAppUserModelId)
                    };
                }
            }
        }

        if (snapshot?.Key == Current?.Key) return;

        App.Trace($"媒体会话：{(snapshot is null ? "无" : $"{snapshot.Title} / {snapshot.Artist} · {snapshot.IsPlaying}")}");
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private ImageSource? ResolveIcon(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        if (_iconCache.TryGetValue(appId, out var cached)) return cached;

        var icon = IconExtractor.FromAppUserModelId(appId);
        _iconCache[appId] = icon;
        return icon;
    }
}
