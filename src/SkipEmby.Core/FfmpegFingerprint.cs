using System.Diagnostics;
using System.Globalization;

namespace SkipEmby.Core;
public sealed class FfmpegFingerprint
{
    public async Task<Fingerprint> Extract(string executable, string input, double start, double seconds,
        IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new FileNotFoundException("FFmpeg executable not found");
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Arguments(input, start, seconds, headers)) psi.ArgumentList.Add(a);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token); linked.CancelAfter(timeout);
        using var process = new Process { StartInfo = psi };
        process.Start();
        using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        // Never expose FFmpeg stderr: remote paths can contain private URL signatures.
        var stderr = Drain(process.StandardError, linked.Token);
        using var output = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            int n;
            while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, linked.Token)) > 0)
            { if (output.Length + n > 256000) throw new InvalidDataException("Fingerprint output exceeded limit"); await output.WriteAsync(buffer.AsMemory(0, n), linked.Token); }
            await process.WaitForExitAsync(linked.Token); await stderr;
            if (process.ExitCode != 0) throw new IOException("FFmpeg fingerprint extraction failed; check Chromaprint support, audio stream and source access");
            linked.Token.ThrowIfCancellationRequested();
            return Fingerprint.FromRaw(output.ToArray(), start, seconds);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }
    static async Task Drain(StreamReader reader, CancellationToken token)
    { var buffer = new char[4096]; while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { } }
    public static IEnumerable<string> Arguments(string input, double start, double seconds, IReadOnlyDictionary<string, string>? headers)
    {
        if (start < 0 || seconds < 10 || seconds > 600 || string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Invalid extraction window");
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-protocol_whitelist", "file,http,https,tcp,tls" };
        bool remote = Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
        if (remote)
        {
            args.AddRange(new[] { "-rw_timeout", "30000000", "-user_agent", headers is not null && headers.TryGetValue("User-Agent", out var ua) ? ua : "Infuse-Direct/8.5.9" });
            if (headers is not null)
            {
                var lines = new List<string>();
                foreach (var h in headers)
                {
                    if (h.Key.Contains('\r') || h.Key.Contains('\n') || h.Value.Contains('\r') || h.Value.Contains('\n')) throw new ArgumentException("Invalid source headers");
                    if (h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) lines.Add(h.Key + ": " + h.Value + "\r\n");
                }
                if (lines.Count > 0) args.AddRange(new[] { "-headers", string.Join("", lines) });
            }
        }
        args.AddRange(new[] { "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", input, "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", "11025", "-f", "chromaprint", "-algorithm", "1", "-fp_format", "raw", "pipe:1" });
        return args;
    }
}
