using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using System.Text.Json;

namespace SkipEmby;
public static class MarkerPolicy
{
    public const string Prefix = "Skip-emby:";
    public static List<ChapterInfo> Add(IReadOnlyList<ChapterInfo> original, double? introStart, double? introEnd, double? creditsStart, double runtime)
    {
        var next = original.ToList();
        if (introStart is >= 0 && introEnd > introStart && introEnd < runtime &&
            !original.Any(x => x.MarkerType is MarkerType.IntroStart or MarkerType.IntroEnd))
        {
            next.Add(new() { Name = Prefix + "IntroStart", MarkerType = MarkerType.IntroStart, StartPositionTicks = TimeSpan.FromSeconds(introStart.Value).Ticks });
            next.Add(new() { Name = Prefix + "IntroEnd", MarkerType = MarkerType.IntroEnd, StartPositionTicks = TimeSpan.FromSeconds(introEnd.Value).Ticks });
        }
        var effectiveIntroEnd = introEnd ?? original.Where(x => x.MarkerType == MarkerType.IntroEnd).Select(x => (double?)TimeSpan.FromTicks(x.StartPositionTicks).TotalSeconds).Max();
        if (creditsStart > 0 && creditsStart < runtime && (!effectiveIntroEnd.HasValue || creditsStart > effectiveIntroEnd) &&
            !original.Any(x => x.MarkerType == MarkerType.CreditsStart))
            next.Add(new() { Name = Prefix + "CreditsStart", MarkerType = MarkerType.CreditsStart, StartPositionTicks = TimeSpan.FromSeconds(creditsStart.Value).Ticks });
        return next.OrderBy(x => x.StartPositionTicks).ToList();
    }
    public static List<ChapterInfo> RemoveOwn(IEnumerable<ChapterInfo> original) => original.Where(x =>
        !(x.Name?.StartsWith(Prefix, StringComparison.Ordinal) == true && x.MarkerType is MarkerType.IntroStart or MarkerType.IntroEnd or MarkerType.CreditsStart)).ToList();
}
public sealed class MarkerStore(IItemRepository repository, string directory)
{
    public bool Save(BaseItem item, double? introStart, double? introEnd, double? creditsStart)
    {
        var old = repository.GetChapters(item).ToList();
        var next = MarkerPolicy.Add(old, introStart, introEnd, creditsStart, TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds);
        if (next.Count == old.Count) return false;
        Backup(item, old);
        repository.SaveChapters(item.InternalId, next);
        return true;
    }
    public bool Remove(BaseItem item)
    {
        var old = repository.GetChapters(item).ToList(); var next = MarkerPolicy.RemoveOwn(old);
        if (next.Count == old.Count) return false;
        Backup(item, old); repository.SaveChapters(item.InternalId, next); return true;
    }
    void Backup(BaseItem item, List<ChapterInfo> chapters)
    {
        var folder = Path.Combine(directory, "marker-backups"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, item.InternalId + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { ItemId = item.InternalId, CreatedUtc = DateTime.UtcNow, Chapters = chapters }));
    }
}
