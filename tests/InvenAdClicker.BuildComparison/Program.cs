using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Measures the actual published executable, not a reimplemented application path.
if (args.Length is < 3 or > 4) throw new ArgumentException("Expected executable, isolated working directory, report path, and optional --explicit-data-directory.");
string executable = Path.GetFullPath(args[0]), working = Path.GetFullPath(args[1]), report = Path.GetFullPath(args[2]);
using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(working, "appsettings.json")));
var settings = config.RootElement.GetProperty("AppSettings");
if (!settings.GetProperty("DryRun").GetBoolean()) throw new InvalidOperationException("Only DryRun comparisons are permitted.");
var start = new ProcessStartInfo(executable)
{
    WorkingDirectory = working, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
};
foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("AppSettings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AppSettings:", StringComparison.OrdinalIgnoreCase)).ToArray())
    start.Environment.Remove(key);
start.Environment["DOTNET_ENVIRONMENT"] = "Production";
if (args.Length == 4)
{
    if (args[3] != "--explicit-data-directory") throw new ArgumentException("Unknown controller option.");
    start.ArgumentList.Add("--data-directory"); start.ArgumentList.Add(working);
}
var clock = Stopwatch.StartNew();
using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
process.StandardInput.Close(); // Suppress interactive credential/key prompts.
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
using var meter = new ProcessTreeMeter(process.Id, clock);
bool timedOut = false;
try
{
    while (!process.HasExited)
    {
        meter.SampleNow();
        if (clock.Elapsed > TimeSpan.FromMinutes(5)) { timedOut = true; process.Kill(entireProcessTree: true); break; }
        await Task.Delay(150);
    }
    await process.WaitForExitAsync();
    clock.Stop();
    meter.SampleNow();
    // Allow short-lived children a bounded period to complete their shutdown.
    for (int i = 0; i < 10 && meter.Samples.Last().Processes != 0; i++)
    { await Task.Delay(150); meter.SampleNow(); }
}
catch
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    throw;
}
string output = await stdout, error = await stderr;
// Raw output and logs stay in ignored, isolated run directories.
await File.WriteAllTextAsync(Path.Combine(working, "stdout.txt"), output);
await File.WriteAllTextAsync(Path.Combine(working, "stderr.txt"), error);
string logs = string.Join("\n", Directory.EnumerateFiles(working, "*.log", SearchOption.AllDirectories).Select(File.ReadAllText));
var rows = Regex.Matches(logs, @"\[perf\]\[collect\] url='(?<url>[^']+)' done attempts=(?<attempts>\d+) total=(?<ms>\d+)ms links=(?<links>\d+)/(?<all>\d+)")
    .Select(m => new { Url = m.Groups["url"].Value, Attempts = int.Parse(m.Groups["attempts"].Value), Milliseconds = int.Parse(m.Groups["ms"].Value), Links = int.Parse(m.Groups["links"].Value) }).ToArray();
var result = new
{
    StartedUtc = DateTime.UtcNow - clock.Elapsed,
    ExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))),
    Version = FileVersionInfo.GetVersionInfo(executable).ProductVersion,
    ExitCode = process.ExitCode, TimedOut = timedOut, WallMilliseconds = clock.ElapsedMilliseconds,
    Settings = settings.Clone(), Collections = rows, CollectedLinks = rows.Sum(r => r.Links),
    CollectedUrls = rows.Length, EmptyUrls = rows.Count(r => r.Links == 0),
    // Timing-only lines exclude login values, cookies, and ad click URLs.
    Performance = logs.Split('\n').Where(l => l.Contains("[perf]")).Select(l => l.Trim()).ToArray(),
    Resources = meter.Result(), Samples = meter.Samples,
    ApplicationReportedSuccess = logs.Contains("모든 작업이 완료되었습니다.") || logs.Contains("작업이 성공적으로 완료되었습니다."),
    ErrorOutputPresent = !string.IsNullOrWhiteSpace(error)
};
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{Path.GetFileName(working)}: exit={process.ExitCode} wall={clock.ElapsedMilliseconds}ms urls={rows.Length} links={rows.Sum(r => r.Links)}");
return process.ExitCode == 0 && !timedOut && rows.Length == settings.GetProperty("TargetUrls").GetArrayLength() ? 0 : 1;
