using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AetheryteChat.Translation;

public sealed record OllamaAsset(string Name, long Size, string Url, string? Sha256);

public sealed record OllamaRelease(string Tag, IReadOnlyList<OllamaAsset> Assets)
{
    public OllamaAsset? Find(string name) => Assets.FirstOrDefault(a => a.Name == name);
}

/// <summary>설치 진행 상황. Completed·Total 은 그 단계의 바이트</summary>
public sealed record OllamaInstallProgress(string Stage, long Completed, long Total)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : null;
}

/// <summary>
/// Ollama 를 설치 프로그램 없이 플러그인 폴더에 설치한다. 공식 GitHub 릴리스의 zip 을 받아 GitHub 가 알려 주는 SHA-256 으로
/// 검증한 뒤에만 푼다. 공식 설치 프로그램과 달리 Ollama 앱(환영 화면·작업 표시줄 아이콘·시작프로그램)이 생기지 않는다.
/// 폴더: root\current (실행 파일), root\version.txt (설치한 릴리스 태그), root\download (받는 중인 zip — 이어 받기)
/// </summary>
public static class OllamaInstaller
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/ollama/ollama/releases/latest";
    public const string RocmAssetName = "ollama-windows-amd64-rocm.zip";

    /// <summary>이 PC 의 CPU 에 맞는 본체 zip</summary>
    public static string MainAssetName => RuntimeInformation.OSArchitecture == Architecture.Arm64
        ? "ollama-windows-arm64.zip"
        : "ollama-windows-amd64.zip";

    // GitHub API 는 User-Agent 가 없으면 거절한다. 사용자 정보는 넣지 않는다
    private static readonly HttpClient Api = CreateApiClient();

    // 받기는 수 분 걸릴 수 있어 제한 시간을 두지 않는다. 멈추는 것은 취소로 한다
    private static readonly HttpClient Download = CreateDownloadClient();

    public static string ExecutablePath(string root) => Path.Combine(root, "current", "ollama.exe");

    public static bool IsInstalled(string root) => File.Exists(ExecutablePath(root));

    /// <summary>설치한 릴리스 태그 (예: v0.40.16). 설치하지 않았으면 null</summary>
    public static string? InstalledVersion(string root)
    {
        var file = Path.Combine(root, "version.txt");
        return IsInstalled(root) && File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>최신 릴리스 (GitHub API)</summary>
    /// <exception cref="TranslationException">GitHub 에 닿지 못함</exception>
    public static async Task<OllamaRelease> GetLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Api.GetStringAsync(LatestReleaseUrl, ct));
            var root = doc.RootElement;
            var assets = root.GetProperty("assets").EnumerateArray()
                .Select(a => new OllamaAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("size").GetInt64(),
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal)
                        ? digest["sha256:".Length..]
                        : null))
                .ToList();
            return new OllamaRelease(root.GetProperty("tag_name").GetString() ?? "", assets);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new TranslationException($"Ollama 최신 버전을 확인하지 못했습니다: {e.Message}", e);
        }
    }

    /// <summary>
    /// AMD 그래픽카드(외장)가 있는지 — 있으면 AMD 용 구성 요소(rocm)도 받는 것이 좋다.
    /// 내장 그래픽(Radeon 760M Graphics, Radeon(TM) Graphics 등)은 Ollama 가 대부분 쓰지 못해(2026-11-19 760M 확인) 빼고,
    /// 이름에 RX·PRO 가 든 외장 카드만 센다
    /// </summary>
    public static bool HasAmdGpu() => GpuInfo.Adapters.Any(a => a.IsAmdDiscrete);

    /// <summary>
    /// 받기·검증·풀기까지 하고 새 폴더를 돌려준다. 아직 쓰지는 않는다 — 서버를 끈 뒤 <see cref="Activate"/> 로 바꿔 끼운다
    /// (실행 중인 ollama.exe 는 지울 수 없어서). 끊어도 받은 부분은 남아 다음에 이어 받는다
    /// </summary>
    /// <exception cref="TranslationException">받기·검증·풀기 실패</exception>
    public static async Task<string> PrepareAsync(
        string root, OllamaRelease release, bool includeRocm, Action<OllamaInstallProgress> onProgress, CancellationToken ct)
    {
        var assets = new List<OllamaAsset>
        {
            release.Find(MainAssetName) ?? throw new TranslationException($"{release.Tag} 릴리스에 {MainAssetName} 가 없습니다."),
        };
        if (includeRocm && release.Find(RocmAssetName) is { } rocm)
            assets.Add(rocm);

        var downloadDir = Directory.CreateDirectory(Path.Combine(root, "download")).FullName;
        var newDir = Path.Combine(root, "new");
        if (Directory.Exists(newDir))
            Directory.Delete(newDir, recursive: true);
        Directory.CreateDirectory(newDir);

        try
        {
            foreach (var asset in assets)
            {
                var sha256 = asset.Sha256 ?? await FindChecksumAsync(release, asset.Name, ct)
                    ?? throw new TranslationException($"{asset.Name} 의 검증 값(SHA-256)을 찾지 못해 설치하지 않았습니다.");
                var file = Path.Combine(downloadDir, asset.Name);
                await DownloadAsync(asset, file, (done, total) => onProgress(new($"받는 중: {asset.Name}", done, total)), ct);

                onProgress(new($"검증 중: {asset.Name}", 0, 0));
                await VerifyAsync(file, sha256, ct);

                await Task.Run(() => Extract(file, newDir, (done, total) => onProgress(new($"푸는 중: {asset.Name}", done, total)), ct), ct);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new TranslationException($"Ollama를 설치하지 못했습니다: {e.Message}", e);
        }

        if (!File.Exists(Path.Combine(newDir, "ollama.exe")))
            throw new TranslationException("받은 파일에 ollama.exe 가 없습니다.");
        return newDir;
    }

    /// <summary>
    /// 준비한 새 폴더로 바꿔 끼운다. 이 폴더의 ollama.exe 가 실행 중이면 실패하므로 서버를 먼저 끈다.
    /// 끝나면 받은 zip 은 지운다
    /// </summary>
    /// <exception cref="TranslationException">이전 폴더를 지우지 못함 (실행 중 등)</exception>
    public static void Activate(string root, string newDir, string tag)
    {
        var current = Path.Combine(root, "current");
        var old = Path.Combine(root, "old");
        try
        {
            // 지우기부터 하면 쓰는 중인 파일에서 멈춰 이전 설치가 반쯤 지워진 채 남는다 (2026-11-19 확인).
            // 폴더 이름 바꾸기는 안의 파일을 누가 쓰고 있으면 통째로 실패하므로, 먼저 옮겨 보고 실패하면 이전 설치를 그대로 둔다
            DeleteQuietly(old);
            if (Directory.Exists(current))
                Directory.Move(current, old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TranslationException($"이전 Ollama를 바꾸지 못했습니다 (실행 중일 수 있습니다): {e.Message}", e);
        }

        try
        {
            Directory.Move(newDir, current);
            File.WriteAllText(Path.Combine(root, "version.txt"), tag);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 새 것을 넣지 못했으면 이전 것을 되돌린다
            if (!Directory.Exists(current) && Directory.Exists(old))
                Directory.Move(old, current);
            throw new TranslationException($"새 Ollama를 넣지 못했습니다: {e.Message}", e);
        }

        DeleteQuietly(old);
        DeleteQuietly(Path.Combine(root, "download"));
    }

    /// <summary>지워 보고 안 되면 둔다 (다음 설치 때 다시 지운다)</summary>
    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>플러그인이 설치한 Ollama 를 지운다 (받아 둔 모델은 Ollama 모델 폴더에 남는다). 서버를 먼저 끈다</summary>
    /// <exception cref="TranslationException">지우지 못함</exception>
    public static void Uninstall(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TranslationException($"Ollama를 지우지 못했습니다 (실행 중일 수 있습니다): {e.Message}", e);
        }
    }

    /// <summary>받다 만 파일이 있으면 이어 받는다 (Range). 서버가 이어 받기를 거절하면 처음부터</summary>
    private static async Task DownloadAsync(OllamaAsset asset, string file, Action<long, long> onProgress, CancellationToken ct)
    {
        var existing = File.Exists(file) ? new FileInfo(file).Length : 0;
        if (existing > asset.Size)
        {
            File.Delete(file);
            existing = 0;
        }
        if (existing == asset.Size)
        {
            onProgress(existing, asset.Size);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        try
        {
            using var response = await Download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed)
                existing = 0;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(file, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            var done = existing;
            var lastReport = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (done - lastReport >= 4 << 20 || done == asset.Size)
                {
                    onProgress(done, asset.Size);
                    lastReport = done;
                }
            }
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException($"{asset.Name} 을 받지 못했습니다: {e.Message}", e);
        }
    }

    private static async Task VerifyAsync(string file, string expectedSha256, CancellationToken ct)
    {
        string actual;
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file); // 다음에는 처음부터 받는다
            throw new TranslationException($"{Path.GetFileName(file)} 의 검증 값이 맞지 않아 설치하지 않았습니다 (받는 중 손상되었을 수 있습니다). 다시 시도해 주세요.");
        }
    }

    /// <summary>릴리스에 digest 가 없으면 sha256sum.txt ("해시  ./이름") 에서 찾는다</summary>
    private static async Task<string?> FindChecksumAsync(OllamaRelease release, string assetName, CancellationToken ct)
    {
        if (release.Find("sha256sum.txt") is not { } sums)
            return null;
        try
        {
            var text = await Download.GetStringAsync(sums.Url, ct);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*').Replace("./", "") == assetName)
                    return parts[0];
            }
        }
        catch (HttpRequestException)
        {
        }
        return null;
    }

    /// <summary>zip 을 푼다. 폴더 밖을 가리키는 항목(zip slip)은 거절한다</summary>
    private static void Extract(string zipFile, string targetDir, Action<long, long> onProgress, CancellationToken ct)
    {
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);
        var total = zip.Entries.Sum(e => e.Length);
        long done = 0, lastReport = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"잘못된 zip 항목: {entry.FullName}");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            done += entry.Length;
            if (done - lastReport >= 32 << 20 || done == total)
            {
                onProgress(done, total);
                lastReport = done;
            }
        }
    }

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AetheryteChat");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AetheryteChat");
        return client;
    }
}
