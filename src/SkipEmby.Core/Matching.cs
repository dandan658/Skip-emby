using System.Buffers.Binary;
using System.Numerics;

namespace SkipEmby.Core;

public sealed record Fingerprint(uint[] Words, double Offset, double WindowLength)
{
    // Chromaprint algorithm 1 (TEST2): 4096/3 samples per item at 11025 Hz.
    public const double Step = 1365d / 11025;
    public const double Delay = (19d * 1365 + 2731) / 11025;
    public static Fingerprint FromRaw(byte[] bytes, double offset, double length)
    {
        if (bytes.Length % 4 != 0 || bytes.Length > 256000) throw new InvalidDataException("Invalid fingerprint size");
        var words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        return new(words, offset, length);
    }
}
public sealed record Match(double StartA, double EndA, double StartB, double EndB, double Confidence)
{
    public double Duration => EndA - StartA;
}
public static class Matcher
{
    public static Match? Find(Fingerprint a, Fingerprint b, double minimumSeconds = 20, double maximumSeconds = 180,
        CancellationToken cancellationToken = default)
    {
        int minFrames = (int)Math.Ceiling(minimumSeconds / Fingerprint.Step);
        if (a.Words.Length < minFrames || b.Words.Length < minFrames) return null;
        var index = new Dictionary<int, List<int>>();
        for (int j = 0; j < b.Words.Length; j += 3)
            for (int part = 0; part < 4; part++)
            {
                int key = (part << 8) | (int)((b.Words[j] >> (part * 8)) & 255);
                if (!index.TryGetValue(key, out var list)) index[key] = list = new();
                if (list.Count <= 96) list.Add(j);
            }
        var votes = new Dictionary<int, int>();
        for (int i = 0; i < a.Words.Length; i += 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int part = 0; part < 4; part++)
            {
                int key = (part << 8) | (int)((a.Words[i] >> (part * 8)) & 255);
                if (!index.TryGetValue(key, out var list) || list.Count > 96) continue;
                foreach (int j in list)
                    if (BitOperations.PopCount(a.Words[i] ^ b.Words[j]) <= 6)
                        votes[j - i] = votes.GetValueOrDefault(j - i) + 1;
            }
        }
        var offsets = votes.OrderByDescending(v => v.Value).Take(12).SelectMany(v => new[] { v.Key - 1, v.Key, v.Key + 1 }).Distinct();
        Match? best = null;
        foreach (int shift in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int begin = Math.Max(0, -shift), limit = Math.Min(a.Words.Length, b.Words.Length - shift);
            int start = -1, lastGood = -1, good = 0, distance = 0;
            void Consider()
            {
                if (start < 0 || lastGood - start + 1 < minFrames) return;
                int frames = lastGood - start + 1;
                if (good / (double)frames < .86 || distance / (double)Math.Max(good, 1) > 3.5) return;
                if (a.Words.Skip(start).Take(frames).Distinct().Take(16).Count() < 16) return;
                double sa = a.Offset + start * Fingerprint.Step, ea = Math.Min(a.Offset + a.WindowLength, a.Offset + (lastGood + 1) * Fingerprint.Step + Fingerprint.Delay);
                double sb = b.Offset + (start + shift) * Fingerprint.Step, eb = Math.Min(b.Offset + b.WindowLength, b.Offset + (lastGood + shift + 1) * Fingerprint.Step + Fingerprint.Delay);
                if (ea - sa > maximumSeconds || eb - sb > maximumSeconds) return;
                var m = new Match(sa, ea, sb, eb, good / (double)frames);
                if (best is null || m.Duration > best.Duration) best = m;
            }
            for (int i = begin; i < limit; i++)
            {
                int d = BitOperations.PopCount(a.Words[i] ^ b.Words[i + shift]);
                if (d <= 6) { if (start < 0) start = i; lastGood = i; good++; distance += d; }
                else if (start >= 0 && i - lastGood > 8) { Consider(); start = -1; lastGood = -1; good = 0; distance = 0; }
            }
            Consider();
        }
        return best;
    }
    public static (double Start, double End, int Support)? Consensus(IEnumerable<(double Start, double End, long Peer)> candidates, int minimumPeers)
    {
        var values = candidates.ToArray();
        foreach (var candidate in values.OrderByDescending(x => x.End - x.Start))
        {
            var overlaps = values.Where(x => Math.Min(x.End, candidate.End) - Math.Max(x.Start, candidate.Start) >= .65 * Math.Min(x.End - x.Start, candidate.End - candidate.Start))
                .GroupBy(x => x.Peer).Select(g => g.First()).ToArray();
            if (overlaps.Length < minimumPeers) continue;
            // Intersection is conservative: avoid extending across unmatched story audio.
            double start = overlaps.Max(x => x.Start), end = overlaps.Min(x => x.End);
            if (end > start) return (start, end, overlaps.Length);
        }
        return null;
    }
}
