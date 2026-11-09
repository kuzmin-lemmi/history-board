using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HistoryBoard;

public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);
public sealed record AppRelease(Version Version, string Tag, string Notes, Uri Page, ReleaseAsset? Installer, ReleaseAsset? Checksum);
public sealed record DownloadProgress(long Received, long Total)
{
    public double Percent => Total > 0 ? Received * 100.0 / Total : 0;
}

public sealed class UpdateService : IDisposable
{
    public const string Repository = "kuzmin-lemmi/history-board";
    public const string InstallerName = "HistoryBoard-Setup-win-x64.exe";
    public const long MaxDownloadBytes = 300L * 1024 * 1024;
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");
    public static Version CurrentVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version!);
    public static string DisplayVersion => CurrentVersion.ToString(3);
    private readonly HttpClient client;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"HistoryBoard/{DisplayVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AppRelease?> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub временно ограничил проверку обновлений. Попробуйте позже.");
        response.EnsureSuccessStatusCode();
        return ParseRelease(await ReadSmallAsync(response, 2 * 1024 * 1024, cancellationToken));
    }

    internal static AppRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) throw new InvalidDataException("У релиза нет номера версии вида v1.2.0.");
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        if (notes.Length > 8000) notes = notes[..8000] + "\n…";
        ReleaseAsset? installer = null, checksum = null;
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString();
                if (name != InstallerName && name != InstallerName + ".sha256") continue;
                if (entry.TryGetProperty("state", out var state) && state.GetString() != "uploaded") continue;
                var size = entry.GetProperty("size").GetInt64();
                var link = entry.GetProperty("browser_download_url").GetString();
                if (size < 1 || size > MaxDownloadBytes || !Uri.TryCreate(link, UriKind.Absolute, out var url) || !IsRepositoryAssetUrl(url)) continue;
                var digest = entry.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String ? digestValue.GetString() : null;
                var hash = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
                if (!ValidHash(hash)) hash = null;
                var asset = new ReleaseAsset(name!, url, size, hash);
                if (name == InstallerName) installer = asset; else if (size <= 4096) checksum = asset;
            }
        return new AppRelease(version, tag, notes, new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}"), installer, checksum);
    }

    public async Task<string> DownloadAsync(AppRelease release, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var asset = release.Installer ?? throw new InvalidOperationException("Установщик ещё не прикреплён к релизу. Повторите проверку позже.");
        if (!IsRepositoryAssetUrl(asset.Url) || asset.Size < 1 || asset.Size > MaxDownloadBytes) throw new InvalidDataException("Некорректный файл обновления.");
        var expectedHash = asset.Sha256;
        if (!ValidHash(expectedHash) && release.Checksum is { } checksum)
        {
            using var response = await GetAssetResponseAsync(checksum.Url, cancellationToken);
            var text = await ReadSmallAsync(response, 4096, cancellationToken);
            var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 1 or 2 && (parts.Length == 1 || parts[1].TrimStart('*') == InstallerName)) expectedHash = parts[0];
        }
        if (!ValidHash(expectedHash)) throw new InvalidDataException("GitHub ещё не предоставил контрольную сумму установщика. Повторите проверку позже.");
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, "Установить доску.exe");
        var expectedBytes = Convert.FromHexString(expectedHash!);
        if (File.Exists(final) && new FileInfo(final).Length == asset.Size)
        {
            await using var cached = File.OpenRead(final);
            if (CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(cached, cancellationToken), expectedBytes))
            { progress?.Report(new(asset.Size, asset.Size)); return final; }
        }
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.download");
        try
        {
            using var response = await GetAssetResponseAsync(asset.Url, cancellationToken);
            if (response.Content.Headers.ContentLength is { } size && size != asset.Size) throw new InvalidDataException("Размер загрузки не совпадает с данными релиза.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var lastProgress = System.Diagnostics.Stopwatch.StartNew();
            var buffer = new byte[65536];
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > asset.Size || received > MaxDownloadBytes) throw new InvalidDataException("Файл обновления превышает ожидаемый размер.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    if (lastProgress.ElapsedMilliseconds > 80) { progress?.Report(new(received, asset.Size)); lastProgress.Restart(); }
                }
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (received != asset.Size) throw new InvalidDataException("Обновление скачалось не полностью. Попробуйте ещё раз.");
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedBytes)) throw new InvalidDataException("Контрольная сумма не совпала. Повреждённый файл не будет установлен.");
            using (var executable = File.OpenRead(temporary))
                if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z') throw new InvalidDataException("Скачанный файл не является установщиком Windows.");
            File.Move(temporary, final, true);
            progress?.Report(new(received, asset.Size));
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage> GetAssetResponseAsync(Uri url, CancellationToken token)
    {
        if (!IsRepositoryAssetUrl(url)) throw new InvalidDataException("Обновления можно скачивать только из репозитория программы.");
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear(); request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null) throw new InvalidDataException("GitHub не предоставил адрес загрузки.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                    !(IsRepositoryAssetUrl(url) || url.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub перенаправил загрузку на неизвестный адрес.");
                continue;
            }
            if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException($"Не удалось скачать обновление (HTTP {(int)code})."); }
            return response;
        }
        throw new HttpRequestException("Слишком много перенаправлений при загрузке обновления.");
    }

    private static async Task<string> ReadSmallAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > limit) throw new InvalidDataException("Ответ сервера слишком большой.");
            memory.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    internal static bool IsRepositoryAssetUrl(Uri url) => url.Scheme == "https" && url.IsDefaultPort && url.UserInfo.Length == 0 && url.Host == "github.com" && url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal);
    internal static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V').Split('+')[0];
        if (!Version.TryParse(text, out var parsed) || parsed.Build < 0) { version = new Version(0, 0, 0, 0); return false; }
        version = Normalize(parsed); return true;
    }
    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public void Dispose() => client.Dispose();
}
