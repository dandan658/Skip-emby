using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using SkipEmby;
using SkipEmby.Core;

if (args.Length < 3) throw new ArgumentException("Usage: Tests <ffmpeg> <fixture-directory> <official-InfuseSync.dll>");
var checks = new List<string>();
void Check(bool b, string message) { if (!b) throw new InvalidOperationException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
void Throws(Action fn, string message) { try { fn(); } catch { Check(true, message); return; } throw new Exception(message); }
var exe = Path.GetFullPath(args[0]); var root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
var configuration = new Configuration();
Check(!configuration.Enabled && configuration.DryRun && !configuration.AllowRemoteSources, "Safe defaults: explicit enable, selected scope, dry run and remote opt-in");
Check(Scope.Parse("1,2;2 -1 invalid 0").SequenceEqual(new long[] { 1, 2 }), "Scope rejects invalid and negative IDs and removes duplicates");
Check(Scope.Parse("").Length == 0, "Empty scope never means all libraries");
var random = new Random(4545); uint[] Words(int n) => Enumerable.Range(0, n).Select(_ => (uint)random.NextInt64(0, 1L << 32)).ToArray();
var sequence = Words(400); var a = Words(120).Concat(sequence).Concat(Words(100)).ToArray(); var b = Words(165).Concat(sequence).Concat(Words(80)).ToArray();
var m = Matcher.Find(new(a, 0, 100), new(b, 0, 100), 20, 80);
Check(m is not null && Math.Abs(m.StartA - 120 * Fingerprint.Step) < .5 && Math.Abs(m.StartB - 165 * Fingerprint.Step) < .5, "Offset matching identifies common intro after different cold opens");
Check(Matcher.Find(new(Words(700), 0, 100), new(Words(700), 0, 100), 20, 80) is null, "Unrelated fingerprints produce no marker");
Check(Matcher.Find(new(Enumerable.Repeat(123u, 700).ToArray(), 0, 100), new(Enumerable.Repeat(123u, 700).ToArray(), 0, 100), 20, 80) is null, "Low-information constant audio does not create markers");
using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Throws(() => Matcher.Find(new(a, 0, 100), new(b, 0, 100), 20, 80, cancel.Token), "Matching responds to cancellation"); }
Check(Matcher.Consensus(new[] { (10d, 50d, 1L), (11d, 49d, 2L) }, 2) is { Start: 11, End: 49, Support: 2 }, "Consensus conservatively intersects two independent references");
Check(Matcher.Consensus(new[] { (10d, 50d, 1L), (11d, 49d, 1L) }, 2) is null, "Duplicate references cannot inflate support");
var other = new ChapterInfo { Name = "Original chapter", MarkerType = MarkerType.Chapter, StartPositionTicks = 0 };
var preserved = new ChapterInfo { Name = "Other detector intro", MarkerType = MarkerType.IntroStart, StartPositionTicks = TimeSpan.FromSeconds(15).Ticks };
var next = MarkerPolicy.Add(new[] { other }, 12, 42, 130, 160);
Check(next.Count == 4 && next.Contains(other), "Add standard markers while preserving original chapter");
Check(MarkerPolicy.Add(next, 3, 40, 125, 160).Count == next.Count, "Reruns never duplicate or overwrite existing markers");
Check(MarkerPolicy.Add(new[] { preserved }, 3, 40, null, 160).Count == 1, "Partial marker from another detector is preserved");
Check(MarkerPolicy.RemoveOwn(next).SequenceEqual(new[] { other }), "Cleanup removes only Skip-emby marker names");
Check(MarkerPolicy.RemoveOwn(new[] { preserved, other }).Count == 2, "Cleanup preserves foreign markers and chapter metadata");
Check(MarkerPolicy.RemoveOwn(new[] { new ChapterInfo { Name = "Skip-emby: ordinary chapter", MarkerType = MarkerType.Chapter } }).Count == 1, "Cleanup cannot delete an ordinary chapter merely sharing the name prefix");
Check(MarkerPolicy.Add(new[] { other }, -1, 500, 200, 160).Count == 1, "Out-of-bounds markers are rejected");
var end = new ChapterInfo { Name = "Foreign end", MarkerType = MarkerType.IntroEnd, StartPositionTicks = TimeSpan.FromSeconds(60).Ticks };
Check(MarkerPolicy.Add(new[] { end }, null, null, 40, 160).Count == 1, "Credits cannot precede a retained foreign intro end");
var argv = FfmpegFingerprint.Arguments("/media/a;bad name.mp4", 1, 30, null).ToArray();
Check(argv.Contains("/media/a;bad name.mp4") && argv.Contains("chromaprint"), "FFmpeg arguments preserve filenames as a single argument without a shell");
Throws(() => FfmpegFingerprint.Arguments("https://own.example/video", 0, 30, new Dictionary<string, string> { ["Cookie"] = "test\r\nInjected: x" }).ToArray(), "Remote header injection is rejected");
var infuse = Assembly.LoadFrom(Path.GetFullPath(args[2])); var infuseType = infuse.GetType("InfuseSync.Plugin", true)!;
var infusePlugin = RuntimeHelpers.GetUninitializedObject(infuseType);
Check((Guid)infuseType.GetProperty("Id")!.GetValue(infusePlugin)! != Guid.Parse(Plugin.PluginId), "Skip-emby has a distinct plugin identity from official InfuseSync");
Check(infuse.GetName().Version == new Version(1, 5, 3, 0), "Official InfuseSync 1.5.3.0 assembly loads alongside Skip-emby");
var built = typeof(Plugin).Assembly;
Check(built.GetReferencedAssemblies().All(x => x.Name is not "0Harmony" and not "InfuseSync" and not "FilmIndex.Emby"), "Plugin has no Harmony, InfuseSync or old Film Index dependencies");
Check(built.GetTypes().All(t => t.GetInterfaces().All(i => i.Name != "IService" && i.Name != "IServerEntryPoint" && i.Name != "IMediaSourceProvider")), "No API routes, playback hooks or automatic library listeners are registered");
Check(built.GetManifestResourceNames().Contains("SkipEmby.Configuration.html"), "Independent configuration page is embedded");
Check(built.GetManifestResourceNames().Contains("SkipEmby.Configuration.js"), "Emby 4.10 page controller is embedded");
using (var reader = new StreamReader(built.GetManifestResourceStream("SkipEmby.Configuration.html")!)) { var html = reader.ReadToEnd(); Check(html.Contains("data-controller=\"__plugin/SkipEmbyConfiguration.js\"") && !html.Contains("<script>"), "Configuration uses the Emby controller lifecycle instead of inert inline scripts"); }
var taskWithoutHost = new DetectionTask(null!, null!, null!, null!); var cleanupWithoutHost = new RemoveTask(null!, null!, null!);
Check(!taskWithoutHost.GetDefaultTriggers().Any() && !cleanupWithoutHost.GetDefaultTriggers().Any(), "Both tasks have no automatic default schedule");
await Plugin.WorkLock.WaitAsync();
try
{
    bool detectBlocked = false, cleanupBlocked = false;
    try { await taskWithoutHost.Execute(default, new Progress<double>()); } catch (InvalidOperationException) { detectBlocked = true; }
    try { await cleanupWithoutHost.Execute(default, new Progress<double>()); } catch (InvalidOperationException) { cleanupBlocked = true; }
    Check(detectBlocked && cleanupBlocked, "Concurrent detection and cleanup are blocked by the same gate");
}
finally { Plugin.WorkLock.Release(); }

// Reproducible generated audio, never user media: common intro and outro,
// distinct episode bodies and a six-second cold-open difference.
const int sr = 11025;
short[] Music(int seconds, int seed)
{
    var rng = new Random(seed); var pcm = new short[seconds * sr]; var frequencies = new double[8];
    for (int i = 0; i < pcm.Length; i++)
    {
        if (i % (sr / 2) == 0) for (int j = 0; j < frequencies.Length; j++) frequencies[j] = 90 * Math.Pow(2, rng.Next(0, 48) / 12d);
        double value = 0; for (int j = 0; j < frequencies.Length; j++) value += Math.Sin(2 * Math.PI * frequencies[j] * i / sr) / (j + 2);
        pcm[i] = (short)Math.Clamp(value * 5500, short.MinValue, short.MaxValue);
    }
    return pcm;
}
void Wav(string file, short[] pcm)
{
    using var w = new BinaryWriter(File.Create(file)); w.Write("RIFF"u8.ToArray()); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8.ToArray()); w.Write(pcm.Length * 2); foreach (short s in pcm) w.Write(s);
}
short[] Episode(int length, int seed, int intro, int credits)
{
    var pcm = Music(length, seed); Music(32, 91).CopyTo(pcm, intro * sr); Music(28, 201).CopyTo(pcm, credits * sr); return pcm;
}
var fileA = Path.Combine(root, "episode-a.wav"); var fileB = Path.Combine(root, "episode-b.wav"); var fileC = Path.Combine(root, "episode-b.m4a");
Wav(fileA, Episode(166, 311, 12, 130)); Wav(fileB, Episode(172, 477, 18, 136));
var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", fileB, "-c:a", "aac", "-b:a", "80k", fileC }) psi.ArgumentList.Add(arg);
using (var p = Process.Start(psi)!) { var err = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); await err; Check(p.ExitCode == 0, "Generated AAC fixture encodes successfully"); }
var shortcut = Path.Combine(root, "fixture.strm");
File.WriteAllText(shortcut, "#EXTM3U\n" + fileA + "\n");
Check(await MediaInput.Resolve(shortcut, default) == fileA, "STRM resolves a bounded single local reference before extraction");
File.WriteAllText(shortcut, "https://example.invalid/video.mp4?sign=private");
Check(await MediaInput.Resolve(shortcut, default) == "https://example.invalid/video.mp4?sign=private", "STRM preserves encoded remote references without modifying signatures");
File.WriteAllText(shortcut, "https://example.invalid/a\nhttps://example.invalid/b");
try { await MediaInput.Resolve(shortcut, default); throw new Exception("Multiple STRM references accepted"); } catch (InvalidDataException) { Check(true, "Ambiguous multi-source STRM is rejected"); }
File.WriteAllText(shortcut, new string('x', 65537));
try { await MediaInput.Resolve(shortcut, default); throw new Exception("Oversized STRM accepted"); } catch (InvalidDataException) { Check(true, "Oversized STRM is rejected before FFmpeg"); }
File.WriteAllText(shortcut, "javascript:unexpected");
try { await MediaInput.Resolve(shortcut, default); throw new Exception("Unsupported protocol accepted"); } catch (InvalidDataException) { Check(true, "Unsupported STRM protocols are rejected"); }
var extract = new FfmpegFingerprint();
var ha = await extract.Extract(exe, fileA, 0, 75, null, TimeSpan.FromSeconds(30), default);
var hb = await extract.Extract(exe, fileC, 0, 75, null, TimeSpan.FromSeconds(30), default);
Check(ha.Words.Length > 400 && hb.Words.Length > 400, "Real FFmpeg Chromaprint extraction returns fingerprints");
var introMatch = Matcher.Find(ha, hb, 20, 50);
Console.WriteLine("INTRO_RESULT " + JsonSerializer.Serialize(introMatch));
Check(introMatch is not null && Math.Abs(introMatch.StartA - 12) < 5 && Math.Abs(introMatch.StartB - 18) < 5 && Math.Abs(introMatch.EndA - 44) < 5, "Real generated intro detected across AAC and WAV within five-second boundary tolerance");
var ta = await extract.Extract(exe, fileA, 106, 60, null, TimeSpan.FromSeconds(30), default); var tb = await extract.Extract(exe, fileC, 112, 60, null, TimeSpan.FromSeconds(30), default);
var outroMatch = Matcher.Find(ta, tb, 18, 45); Console.WriteLine("CREDITS_RESULT " + JsonSerializer.Serialize(outroMatch));
Check(outroMatch is not null && Math.Abs(outroMatch.StartA - 130) < 5 && Math.Abs(outroMatch.StartB - 136) < 5, "Real generated credits detected using absolute episode offsets");
Wav(Path.Combine(root, "silence.wav"), new short[60 * sr]); var silence = await extract.Extract(exe, Path.Combine(root, "silence.wav"), 0, 60, null, TimeSpan.FromSeconds(30), default);
Check(Matcher.Find(silence, silence, 20, 60) is null, "Actual silent FFmpeg output does not cause false intro detection");
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks, Intro = introMatch, Credits = outroMatch }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("TOTAL_PASSED " + checks.Count);
