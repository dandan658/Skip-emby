using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using InfuseSync.Models;
using System.Text.Json;

namespace SkipEmby.HostTests;
// This harness is never shipped as an installation DLL and refuses non-fixture roots.
public sealed class Harness(ILibraryManager library, IItemRepository repository, IFfmpegManager ffmpeg, ILogManager logs) : IServerEntryPoint
{
    readonly List<string> checks = new();
    public void Run()
    {
        var root = Environment.GetEnvironmentVariable("SKIPEMBY_FIXTURE");
        if (string.IsNullOrWhiteSpace(root)) return;
        if (!Path.GetFileName(root).StartsWith("skip-emby-host-", StringComparison.Ordinal) || !File.Exists(Path.Combine(root, ".fixture-only"))) throw new InvalidOperationException("Fixture guard missing");
        _ = Execute(root);
    }
    void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); logs.GetLogger("SkipEmbyTests").Info("PASS " + text); }
    public void Dispose() { }
    async Task Execute(string root)
    {
        try
        {
            Check(Plugin.Instance is not null, "Skip-emby instantiated inside actual Emby host");
            Check(InfuseSync.Plugin.Instance is not null, "Official InfuseSync 1.5.3.0 instantiated in the same host");
            Check(InfuseSync.Plugin.Instance!.Id != Plugin.Instance!.Id, "Both plugins retain independent identities");
            Check(typeof(Episode).Assembly.GetName().Version == new Version(4, 10, 1, 0), "Actual host is Emby 4.10.1.0");
            var c = Plugin.Instance.Configuration;
            Check(!c.Enabled && c.DryRun, "Plugin loads without triggering automatic scans or writes");
            var checkpoint = InfuseSync.Plugin.Instance.Db.CreateCheckpoint("skip-emby-isolated-test", Guid.NewGuid().ToString("N"));
            Check(InfuseSync.Plugin.Instance.Db.HasCheckpoints(), "InfuseSync checkpoint database remains operational");
            var tvPath = Path.Combine(root, "television"); Directory.CreateDirectory(tvPath);
            var tv = library.AddVirtualFolder("Skip-emby isolated TV", "tvshows", new LibraryOptions { EnableRealtimeMonitor = false, PathInfos = new[] { new MediaPathInfo { Path = tvPath } } }, false);
            var seriesPath = Path.Combine(tvPath, "Fixture"); Directory.CreateDirectory(seriesPath);
            var series = new Series { Id = Guid.NewGuid(), Name = "Fixture", Path = seriesPath }; series.SetParent(tv); library.CreateItem(series, tv);
            var seasonPath = Path.Combine(seriesPath, "Season 1"); Directory.CreateDirectory(seasonPath);
            var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", Path = seasonPath, IndexNumber = 1 }; season.SetParent(series); library.CreateItem(season, series);
            var episodes = new List<Episode>();
            for (int i = 0; i < 2; i++)
            {
                string filename = i == 0 ? "episode-a.wav" : "episode-b.wav";
                var path = Path.Combine(seasonPath, "Fixture-S01E0" + (i + 1) + ".wav"); File.Copy(Path.Combine(root, filename), path, true);
                if (i == 0) { var shortcut = Path.ChangeExtension(path, ".strm"); File.WriteAllText(shortcut, path); path = shortcut; }
                var item = new Episode { Id = Guid.NewGuid(), Name = "Episode " + (i + 1), Path = path, Container = "wav", IndexNumber = i + 1, ParentIndexNumber = 1, RunTimeTicks = TimeSpan.FromSeconds(i == 0 ? 166 : 172).Ticks };
                item.SetParent(season); item.SeriesId = series.InternalId; library.CreateItem(item, season);
                repository.SaveMediaStreams(item.InternalId, new List<MediaStream> { new() { Type = MediaStreamType.Audio, Codec = "pcm_s16le", SampleRate = 11025, Channels = 1, Index = 0 } }, CancellationToken.None);
                repository.SaveChapters(item.InternalId, new List<ChapterInfo> { new() { Name = "Original chapter", MarkerType = MarkerType.Chapter, StartPositionTicks = 0 } });
                episodes.Add(item);
            }
            c.Enabled = true; c.DryRun = true; c.FullLibraryRun = true; c.MaxEpisodesPerSeason = 2; c.LibraryIds = tv.InternalId.ToString(); c.MinimumMatchSeconds = 18; c.MaximumMatchSeconds = 60; c.IntroWindowSeconds = 75; c.CreditsWindowSeconds = 60;
            c.FfmpegPath = Path.Combine(root, "ffmpeg.exe"); Plugin.Instance.SaveConfiguration();
            File.WriteAllText(Path.Combine(root, "query-debug.json"), JsonSerializer.Serialize(new {
                Library = tv.InternalId, Season = season.InternalId, SeasonParent = season.ParentId,
                AllSeasons = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Season) }, Recursive = true }).Select(s => new { s.InternalId, s.ParentId, s.Name }).ToArray(),
                ScopedSeasons = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Season) }, ParentIds = new[] { tv.InternalId }, Recursive = true }).Length,
                Episodes = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Episode) }, ParentIds = new[] { season.InternalId }, Recursive = false }).Select(e => new { e.InternalId, e.RunTimeTicks, e.ParentId }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            var task = new DetectionTask(library, repository, ffmpeg, logs);
            await task.Execute(default, new Progress<double>());
            Check(episodes.All(e => repository.GetChapters(e).Count == 1), "Dry run performs detection without changing stored chapters");
            c.DryRun = false; Plugin.Instance.SaveConfiguration();
            await task.Execute(default, new Progress<double>());
            foreach (var e in episodes)
            {
                var ch = repository.GetChapters(e);
                Check(ch.Any(x => x.MarkerType == MarkerType.IntroStart) && ch.Any(x => x.MarkerType == MarkerType.IntroEnd) && ch.Any(x => x.MarkerType == MarkerType.CreditsStart), "Actual repository contains standard intro and credits markers for episode " + e.IndexNumber);
                Check(ch.Any(x => x.Name == "Original chapter"), "Original chapter preserved for episode " + e.IndexNumber);
                Check(ch.Where(x => x.MarkerType != MarkerType.Chapter).All(x => x.StartPositionTicks >= 0 && x.StartPositionTicks < e.RunTimeTicks), "All persisted markers stay inside runtime for episode " + e.IndexNumber);
            }
            Check(Directory.EnumerateFiles(Path.Combine(Plugin.Instance.StorageDirectory, "marker-backups"), "*.json").Any(), "Chapter backup files created before writes");
            await task.Execute(default, new Progress<double>());
            Check(episodes.All(e => repository.GetChapters(e).Count == 4), "Repeated task leaves completed episodes and marker counts unchanged");
            c.DryRun = true; Plugin.Instance.SaveConfiguration();
            await task.Execute(default, new Progress<double>());
            using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(Plugin.Instance.StorageDirectory, "last-report.json"))))
            {
                Check(report.RootElement.GetProperty("Results").GetArrayLength() == 2, "Dry run still probes episodes that already have markers");
                Check(report.RootElement.GetProperty("Errors").GetArrayLength() == 0 && report.RootElement.GetProperty("SeasonsProcessed").GetInt32() == 1, "Diagnostic report distinguishes successful extraction from an empty task");
            }
            Check(episodes.All(e => repository.GetChapters(e).Count == 4), "Dry run on marked episodes preserves every stored marker");
            c.DryRun = false; Plugin.Instance.SaveConfiguration();
            for (int i = 0; i < 25; i++)
            {
                if (InfuseSync.Plugin.Instance.Db.GetItems(0, long.MaxValue, ItemStatus.Updated, new[] { "Episode" }, 0, 100).Count >= 2) break;
                await Task.Delay(500);
            }
            var syncItems = InfuseSync.Plugin.Instance.Db.GetItems(0, long.MaxValue, ItemStatus.Updated, new[] { "Episode" }, 0, 100);
            Check(episodes.All(e => syncItems.Any(s => s.Guid == e.Id)), "InfuseSync records both episode updates after Skip-emby writes markers");
            Check(InfuseSync.Plugin.Instance.Db.GetCheckpoint(checkpoint.Guid) is not null, "InfuseSync checkpoint survives detection and chapter writes");
            var foreign = repository.GetChapters(episodes[0]); foreign.Add(new() { Name = "ForeignCredits", MarkerType = MarkerType.CreditsStart, StartPositionTicks = TimeSpan.FromSeconds(160).Ticks }); repository.SaveChapters(episodes[0].InternalId, foreign);
            await new RemoveTask(library, repository, logs).Execute(default, new Progress<double>());
            Check(repository.GetChapters(episodes[0]).Count == 2 && repository.GetChapters(episodes[0]).Any(x => x.Name == "ForeignCredits"), "Cleanup preserves another detector's credits marker");
            Check(repository.GetChapters(episodes[1]).Count == 1, "Cleanup returns unshared episode to its original chapter");
            InfuseSync.Plugin.Instance.Db.RemoveCheckpoint(checkpoint.Guid);
            File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { Success = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { Success = false, Checks = checks, Error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true })); }
    }
}

