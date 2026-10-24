using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace SkipEmby;
public sealed class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string LibraryIds { get; set; } = "";
    public bool DetectIntro { get; set; } = true;
    public bool DetectCredits { get; set; } = true;
    public bool AllowRemoteSources { get; set; }
    public bool DryRun { get; set; } = true;
    public int IntroWindowSeconds { get; set; } = 180;
    public int CreditsWindowSeconds { get; set; } = 180;
    public int MinimumMatchSeconds { get; set; } = 20;
    public int MaximumMatchSeconds { get; set; } = 150;
    public int MinimumEpisodes { get; set; } = 2;
    public int MaxEpisodesPerSeason { get; set; } = 12;
    public int MaxSeasonsPerRun { get; set; } = 20;
    public int ProcessTimeoutSeconds { get; set; } = 180;
    public string FfmpegPath { get; set; } = "";
}
public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public const string PluginId = "b56f2e37-909b-4439-8b31-bb51ca4acdc1";
    public static readonly SemaphoreSlim WorkLock = new(1, 1);
    public Plugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml) { Instance = this; }
    public override string Name => "Skip-emby";
    public override string Description => "独立片头片尾音频探测，只写标准章节标记，不干预 InfuseSync 同步和播放接口。";
    public override Guid Id => Guid.Parse(PluginId);
    public string StorageDirectory => Path.Combine(ApplicationPaths.DataPath, "Skip-emby");
    public IEnumerable<PluginPageInfo> GetPages() => new[] { new PluginPageInfo { Name = "SkipEmbyConfiguration", EmbeddedResourcePath = "SkipEmby.Configuration.html" }, new PluginPageInfo { Name = "SkipEmbyConfiguration.js", EmbeddedResourcePath = "SkipEmby.Configuration.js" } };
}
