using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HistoryBoard;

internal static class UpdateServiceTests
{
    internal static async Task RunAsync(string directory, Action<bool, string> check)
    {
        var bytes = new byte[180000]; new Random(42).NextBytes(bytes); bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var tag = $"v{UpdateService.CurrentVersion.Major + 1}.0.0";
        var assetUrl = $"https://github.com/kuzmin-lemmi/history-board/releases/download/{tag}/HistoryBoard-Setup-win-x64.exe";
        string Json(string? digest = null, string? url = null, bool prerelease = false) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease, body = "Новая версия: карты и уроки.",
            assets = new object[]
            {
                new { name = UpdateService.InstallerName, size = bytes.Length, state = "uploaded", browser_download_url = url ?? assetUrl, digest },
                new { name = UpdateService.InstallerName + ".sha256", size = 100, state = "uploaded", browser_download_url = assetUrl + ".sha256" }
            }
        });
        var json = Json("sha256:" + hash);
        var release = UpdateService.ParseRelease(json)!;
        check(release.Version > UpdateService.CurrentVersion && release.Installer?.Sha256 == hash, "Updater detects a newer stable version and its GitHub SHA-256 digest");
        check(UpdateService.TryParseVersion("v" + UpdateService.DisplayVersion, out var current) && current == UpdateService.CurrentVersion, "Version comparison normalizes three-part release tags against assembly versions");
        check(UpdateService.ParseRelease(Json(prerelease: true)) == null && !UpdateService.TryParseVersion("v2.0.0-beta", out _), "Prereleases and non-stable version tags are excluded");
        check(UpdateService.ParseRelease(Json("sha256:" + hash, "https://github.com/other/repo/releases/download/v9/setup.exe"))?.Installer == null, "Updater rejects assets from a different repository");

        var requests = 0;
        using var service = new UpdateService(new Handler((request, token) =>
        {
            requests++;
            if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        var found = await service.CheckLatestAsync();
        check(found?.Tag == tag, "Release check uses the GitHub latest-release API");
        var success = Path.Combine(directory, "updates-success");
        var reports = new List<DownloadProgress>();
        var output = await service.DownloadAsync(release, success, new Recorder(reports));
        check(File.Exists(output) && SHA256.HashData(await File.ReadAllBytesAsync(output)).SequenceEqual(Convert.FromHexString(hash)) && reports[^1].Percent == 100, "Update download verifies the complete file and reports completion");
        var beforeCache = requests; await service.DownloadAsync(release, success);
        check(requests == beforeCache, "A cached update is reused only after verifying its size and hash");

        var corrupt = bytes.ToArray(); corrupt[20] ^= 1;
        using var damagedService = new UpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt) })));
        var damageDirectory = Path.Combine(directory, "updates-damaged");
        var rejected = false; try { await damagedService.DownloadAsync(release, damageDirectory); } catch (InvalidDataException) { rejected = true; }
        check(rejected && !Directory.EnumerateFiles(damageDirectory).Any(), "Corrupted updates are rejected and temporary downloads are removed");

        using var partialService = new UpdateService(new Handler((_, _) =>
        {
            var content = new ByteArrayContent(bytes[..9000]); content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        rejected = false; var partial = Path.Combine(directory, "updates-partial");
        try { await partialService.DownloadAsync(release, partial); } catch (InvalidDataException) { rejected = true; }
        check(rejected && !Directory.EnumerateFiles(partial).Any(), "Incomplete downloads never become installable updates");

        using var checksumService = new UpdateService(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new StringContent(hash + "  " + UpdateService.InstallerName) : new ByteArrayContent(bytes) })));
        var fallback = await checksumService.DownloadAsync(UpdateService.ParseRelease(Json())!, Path.Combine(directory, "updates-checksum"));
        check(File.Exists(fallback), "Release checksum attachments are supported when GitHub has no asset digest");

        using var unsafeRedirect = new UpdateService(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://example.com/untrusted.exe"); return Task.FromResult(response);
        }));
        rejected = false;
        try { await unsafeRedirect.DownloadAsync(release, Path.Combine(directory, "updates-redirect")); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Update downloads reject redirects outside GitHub's delivery hosts");

        using var validRedirect = new UpdateService(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "github.com") { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/test/installer"); return Task.FromResult(response); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        check(File.Exists(await validRedirect.DownloadAsync(release, Path.Combine(directory, "updates-cdn"))), "GitHub release delivery redirects download and verify successfully");

        using var cancel = new CancellationTokenSource();
        using var canceledService = new UpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelingStream(bytes, cancel)) })));
        var canceledDirectory = Path.Combine(directory, "updates-canceled"); var canceled = false;
        try { await canceledService.DownloadAsync(release, canceledDirectory, cancellationToken: cancel.Token); } catch (OperationCanceledException) { canceled = true; }
        check(canceled && !Directory.EnumerateFiles(canceledDirectory).Any(), "Canceling an active download cleans up partial files");

        using var noRelease = new UpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        check(await noRelease.CheckLatestAsync() == null, "An empty release list is handled without treating it as an error");
        using var throttled = new UpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        rejected = false; try { await throttled.CheckLatestAsync(); } catch (HttpRequestException) { rejected = true; }
        check(rejected, "GitHub API rate limits produce a recoverable check failure");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class Recorder(List<DownloadProgress> list) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => list.Add(value); }
    private sealed class CancelingStream(byte[] data, CancellationTokenSource source) : MemoryStream(data, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Read(buffer.Span[..Math.Min(buffer.Length, 4096)]); source.Cancel(); return ValueTask.FromResult(count);
        }
    }
}
