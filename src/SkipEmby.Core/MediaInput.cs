namespace SkipEmby.Core;

public static class MediaInput
{
    public static async Task<string> Resolve(string? source, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new IOException("Missing media source");
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) return source;
        if (uri?.IsFile == true) source = uri.LocalPath;
        if (!Path.GetExtension(source).Equals(".strm", StringComparison.OrdinalIgnoreCase)) return source;
        // Never pass a shortcut text file to FFmpeg as if it were a video. Bound the read
        // and accept exactly one reference; URLs and signatures remain memory-only.
        using var reader = new StreamReader(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var buffer = new char[65537];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        if (count > 65536) throw new InvalidDataException("STRM exceeds size limit");
        var refs = new string(buffer, 0, count).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimStart('\uFEFF')).Where(x => x.Length > 0 && !x.StartsWith('#')).ToArray();
        if (refs.Length != 1) throw new InvalidDataException("STRM must contain exactly one media reference");
        var value = refs[0];
        if (Uri.TryCreate(value, UriKind.Absolute, out var target))
        {
            if (target.Scheme == "http" || target.Scheme == "https") return value;
            if (target.IsFile) value = target.LocalPath;
            else throw new InvalidDataException("Unsupported STRM protocol");
        }
        if (!Path.IsPathFullyQualified(value)) value = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, value));
        if (Path.GetExtension(value).Equals(".strm", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Nested STRM is unsupported");
        return value;
    }
}
