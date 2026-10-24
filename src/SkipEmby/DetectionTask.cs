using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using SkipEmby.Core;
using System.Text.Json;

namespace SkipEmby;
public sealed class DetectionTask(ILibraryManager library, IItemRepository repository, IFfmpegManager ffmpeg, ILogManager logs) : IScheduledTask
{
    public string Name => "Skip-emby 片头片尾探测";
    public string Key => "SkipEmby.Detect.v1";
    public string Description => "比较同季多集首尾音频；保留已有标记。默认试运行，不自动扫描全库。";
    public string Category => "Skip-emby";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        if (!await Plugin.WorkLock.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Skip-emby 已有任务运行中");
        try { await Run(cancellationToken, progress); } finally { Plugin.WorkLock.Release(); }
    }
    async Task Run(CancellationToken token, IProgress<double> progress)
    {
        var plugin = Plugin.Instance; var c = plugin.Configuration; var logger = logs.GetLogger("Skip-emby");
        var ids = Scope.Parse(c.LibraryIds);
        if (!c.Enabled || ids.Length == 0) { logger.Info("Skip-emby 未启用或未选择媒体库，不执行探测。"); progress.Report(100); return; }
        int head = Math.Clamp(c.IntroWindowSeconds, 30, 600), tail = Math.Clamp(c.CreditsWindowSeconds, 30, 600);
        double minimum = Math.Clamp(c.MinimumMatchSeconds, 10, 90), maximum = Math.Clamp(c.MaximumMatchSeconds, minimum, 300);
        int minEpisodes = Math.Clamp(c.MinimumEpisodes, 2, 5), maxEpisodes = Math.Max(Math.Clamp(c.MaxEpisodesPerSeason, 2, 30), minEpisodes);
        var executable = string.IsNullOrWhiteSpace(c.FfmpegPath) ? ffmpeg.FfmpegConfiguration.EncoderPath : c.FfmpegPath;
        var foundSeasons = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Season) }, Recursive = true }).OfType<Season>().ToArray();
        var seasons = foundSeasons.Where(s => s.IndexNumber != 0 && Scope.Contains(s, ids, library)).OrderBy(s => s.InternalId).ToArray();
        var extractor = new FfmpegFingerprint(); var store = new MarkerStore(repository, plugin.StorageDirectory); var results = new List<object>();
        int done = 0, eligibleEpisodes = 0, skippedMarked = 0, skippedShortSeasons = 0;
        var errors = new List<object>();
        logger.Info("Skip-emby 开始，所选库 " + string.Join(",", ids) + "，范围内季数 " + seasons.Length + "，试运行 " + c.DryRun);
        foreach (var season in seasons)
        {
            token.ThrowIfCancellationRequested();
            if (library.IsScanRunning) throw new InvalidOperationException("媒体库正在扫描，请在扫描结束后运行探测。");
            var episodes = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Episode) }, ParentIds = new[] { season.InternalId }, Recursive = false })
                .OfType<Episode>().Where(e => e.RunTimeTicks > TimeSpan.FromMinutes(2).Ticks).OrderBy(e => e.IndexNumber).ToArray();
            eligibleEpisodes += episodes.Length;
            bool NeedsMarker(Episode e)
            {
                var ch = repository.GetChapters(e);
                return (c.DetectIntro && !ch.Any(x => x.MarkerType is MediaBrowser.Model.Entities.MarkerType.IntroStart or MediaBrowser.Model.Entities.MarkerType.IntroEnd)) ||
                    (c.DetectCredits && !ch.Any(x => x.MarkerType == MediaBrowser.Model.Entities.MarkerType.CreditsStart));
            }
            // Dry runs must still exercise source access and matching when old detectors already
            // supplied markers. The write path continues to preserve all existing markers.
            var targets = episodes.Where(e => c.DryRun || NeedsMarker(e)).Take(Math.Max(1, maxEpisodes - minEpisodes + 1)).ToArray();
            if (!c.DryRun) skippedMarked += episodes.Count(e => !NeedsMarker(e));
            if (targets.Length == 0) continue;
            var selected = targets.Concat(episodes.Except(targets)).Take(maxEpisodes).ToArray();
            if (selected.Length < minEpisodes) { skippedShortSeasons++; continue; }
            if (done >= Math.Clamp(c.MaxSeasonsPerRun, 1, 100)) break;
            var all = new List<(Episode Item, Fingerprint? Head, Fingerprint? Tail)>();
            bool failed = false;
            // Read each episode once at a time; no simultaneous probes or background playback listeners.
            foreach (var e in selected)
            {
                token.ThrowIfCancellationRequested();
                // Episodes with existing markers can still be references for an unmarked neighbour.
                if (!c.DetectIntro && !c.DetectCredits) break;
                try
                {
                    logger.Info("Skip-emby 读取首尾音频，条目 ID " + e.InternalId);
                    var source = e.GetMediaSources(false, false, library.GetLibraryOptions(e)).FirstOrDefault();
                    var input = await MediaInput.Resolve(string.IsNullOrWhiteSpace(source?.Path) ? e.Path : source.Path, token);
                    bool remote = Uri.TryCreate(input, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
                    if (u is not null && u.IsAbsoluteUri && !u.IsFile && !remote) throw new IOException("不支持的远程来源协议");
                    if (remote && !c.AllowRemoteSources) { logger.Info("Skip-emby 跳过远程来源，条目 ID " + e.InternalId); continue; }
                    double duration = TimeSpan.FromTicks(e.RunTimeTicks!.Value).TotalSeconds;
                    logger.Info("Skip-emby 来源已解析，条目 ID " + e.InternalId + "，远程 " + remote);
                    var h = c.DetectIntro ? await extractor.Extract(executable, input, 0, Math.Min(head, duration / 3), source?.RequiredHttpHeaders, TimeSpan.FromSeconds(Math.Clamp(c.ProcessTimeoutSeconds, 30, 900)), token) : null;
                    double length = Math.Min(tail, duration / 3);
                    var t = c.DetectCredits ? await extractor.Extract(executable, input, duration - length, length, source?.RequiredHttpHeaders, TimeSpan.FromSeconds(Math.Clamp(c.ProcessTimeoutSeconds, 30, 900)), token) : null;
                    all.Add((e, h, t));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { logger.Warn("Skip-emby 片段读取超时，条目 ID " + e.InternalId); errors.Add(new { ItemId = e.InternalId, Kind = "Timeout" }); failed = true; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.Warn("Skip-emby 探测跳过，条目 ID " + e.InternalId + "，错误类型 " + ex.GetType().Name); errors.Add(new { ItemId = e.InternalId, Kind = ex.GetType().Name }); failed = true; }
            }
            var intros = new Dictionary<long, List<(double Start, double End, long Peer)>>(); var creditsMap = new Dictionary<long, List<(double Start, double End, long Peer)>>();
            void Add(Dictionary<long, List<(double Start, double End, long Peer)>> map, long id, long peer, double start, double end)
            { if (!map.TryGetValue(id, out var list)) map[id] = list = new(); list.Add((start, end, peer)); }
            for (int i = 0; i < all.Count; i++) for (int j = i + 1; j < all.Count; j++)
            {
                token.ThrowIfCancellationRequested();
                if (all[i].Head is not null && all[j].Head is not null)
                { var m = Matcher.Find(all[i].Head!, all[j].Head!, minimum, maximum, token); if (m is not null) { Add(intros, all[i].Item.InternalId, all[j].Item.InternalId, m.StartA, m.EndA); Add(intros, all[j].Item.InternalId, all[i].Item.InternalId, m.StartB, m.EndB); } }
                if (all[i].Tail is not null && all[j].Tail is not null)
                { var m = Matcher.Find(all[i].Tail!, all[j].Tail!, minimum, maximum, token); if (m is not null) { Add(creditsMap, all[i].Item.InternalId, all[j].Item.InternalId, m.StartA, m.EndA); Add(creditsMap, all[j].Item.InternalId, all[i].Item.InternalId, m.StartB, m.EndB); } }
            }
            foreach (var entry in all)
            {
                token.ThrowIfCancellationRequested();
                var intro = Matcher.Consensus(intros.GetValueOrDefault(entry.Item.InternalId) ?? new(), minEpisodes - 1);
                var credits = Matcher.Consensus(creditsMap.GetValueOrDefault(entry.Item.InternalId) ?? new(), minEpisodes - 1);
                // Chromaprint frames cover a window, not an exact edit boundary. Trim inward
                // instead of advancing the skip target into potentially unmatched story audio.
                if (intro.HasValue)
                {
                    var candidate = intro.Value;
                    intro = candidate.End - Fingerprint.Delay - candidate.Start - .5 >= minimum
                        ? (candidate.Start + .5, candidate.End - Fingerprint.Delay, candidate.Support) : null;
                }
                if (credits.HasValue) credits = (credits.Value.Start + .5, credits.Value.End, credits.Value.Support);
                bool saved = false;
                if (!c.DryRun && (intro.HasValue || credits.HasValue))
                {
                    // Re-read chapters immediately before merge; do not overwrite another detector's markers.
                    saved = store.Save(entry.Item, intro?.Start, intro?.End, credits?.Start);
                    if (saved) library.UpdateItem(entry.Item, entry.Item.GetParent(), ItemUpdateType.MetadataImport);
                }
                results.Add(new { ItemId = entry.Item.InternalId, IntroStart = intro?.Start, IntroEnd = intro?.End, CreditsStart = credits?.Start, Saved = saved, DryRun = c.DryRun });
            }
            logger.Info("Skip-emby 季 ID " + season.InternalId + " 完成，参考集数 " + all.Count + (failed ? "，有读取失败" : "")); progress.Report(++done * 100d / Math.Max(seasons.Length, 1));
        }
        Directory.CreateDirectory(plugin.StorageDirectory);
        var report = Path.Combine(plugin.StorageDirectory, "last-report.json"); var tmp = report + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { Version = typeof(Plugin).Assembly.GetName().Version?.ToString(), CreatedUtc = DateTime.UtcNow, DryRun = c.DryRun, LibraryIds = ids, SeasonsFound = foundSeasons.Length, SeasonsInScope = seasons.Length, SeasonsProcessed = done, EligibleEpisodes = eligibleEpisodes, SkippedAlreadyMarked = skippedMarked, SkippedInsufficientEpisodes = skippedShortSeasons, Errors = errors, Results = results }, new JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, report, true);
        logger.Info("Skip-emby 结束，处理季数 " + done + "，结果集数 " + results.Count + "，读取失败 " + errors.Count + (c.DryRun ? "，试运行未写入标记" : ""));
        progress.Report(100);
    }
}
public static class Scope
{
    public static long[] Parse(string? value) => (value ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var id) ? id : 0).Where(x => x > 0).Distinct().ToArray();
    public static bool Contains(BaseItem item, long[] libraries, ILibraryManager manager)
    {
        if (libraries.Length == 0) return false;
        // A virtual media library can contain multiple physical roots. Its collection ID
        // need not occur in the Series/Season physical ParentId chain.
        if (manager.GetCollectionFolders(item).Any(folder => libraries.Contains(folder.InternalId))) return true;
        var visited = new HashSet<long>(); BaseItem? current = item;
        for (int depth = 0; depth < 64 && current is not null; depth++)
        {
            if (libraries.Contains(current.InternalId)) return true;
            if (current.ParentId <= 0 || !visited.Add(current.InternalId)) return false;
            current = manager.GetItemById(current.ParentId);
        }
        return false;
    }
}
