using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Logging;

namespace SkipEmby;
public sealed class RemoveTask(ILibraryManager library, IItemRepository repository, ILogManager logs) : IScheduledTask
{
    public string Name => "Skip-emby 清除本插件标记";
    public string Key => "SkipEmby.RemoveOwn.v1";
    public string Category => "Skip-emby";
    public string Description => "只处理所选库中以 Skip-emby: 命名的片头片尾标记，保留其他插件和原始章节。试运行模式下不删除。";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        if (!await Plugin.WorkLock.WaitAsync(0, token)) throw new InvalidOperationException("已有 Skip-emby 任务运行中");
        try
        {
            var p = Plugin.Instance; var ids = Scope.Parse(p.Configuration.LibraryIds);
            if (ids.Length == 0) { progress.Report(100); return; }
            var items = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Episode) }, Recursive = true }).Where(x => Scope.Contains(x, ids, library)).ToArray();
            var store = new MarkerStore(repository, p.StorageDirectory); int done = 0;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (!p.Configuration.DryRun && store.Remove(item)) library.UpdateItem(item, item.GetParent(), ItemUpdateType.MetadataImport);
                progress.Report(++done * 100d / Math.Max(items.Length, 1));
            }
            logs.GetLogger("Skip-emby").Info(p.Configuration.DryRun ? "清理试运行完成，没有写入。" : "只清除了 Skip-emby 自己的标记。"); progress.Report(100);
        }
        finally { Plugin.WorkLock.Release(); }
    }
}
