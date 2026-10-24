namespace SkipEmby.Core;

public sealed record EpisodeBatch(int[] Targets, int[] Selected);
public static class BatchPlanner
{
    public static IEnumerable<EpisodeBatch> Plan(int count, int[] targets, int size, int minimum, bool all)
    {
        if (minimum < 2 || size < minimum) throw new ArgumentException("Invalid batch size");
        if (count < minimum) yield break;
        var pending = targets.Where(x => x >= 0 && x < count).Distinct().ToArray();
        int capacity = size - minimum + 1;
        foreach (var group in pending.Chunk(capacity))
        {
            var owned = group.ToHashSet();
            // Include nearby episodes as references, retaining bounded memory per batch.
            var selected = group.Concat(Enumerable.Range(0, count).Where(x => !owned.Contains(x))
                .OrderBy(x => Math.Abs(x - group[0])).Take(size - group.Length)).ToArray();
            yield return new(group, selected);
            if (!all) yield break;
        }
    }
}
