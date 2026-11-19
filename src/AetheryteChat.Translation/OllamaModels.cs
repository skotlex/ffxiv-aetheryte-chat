using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetheryteChat.Translation;

/// <summary>받기 진행 상황. Completed·Total 은 모델 전체(조각을 합친) 바이트</summary>
public sealed record OllamaPullProgress(string Status, long Completed, long Total)
{
    /// <summary>0~1. 크기를 아직 모르면 null</summary>
    public double? Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : null;
}

/// <summary>GPU(VRAM)에 올라가 있는 모델 (/api/ps)</summary>
/// <param name="Size">모델이 차지하는 메모리 전체</param>
/// <param name="SizeVram">그중 GPU 메모리에 올라간 크기. 나머지는 일반 메모리에서 CPU 로 돈다</param>
/// <param name="ExpiresAt">이때 Ollama 가 모델을 내린다 (keep_alive)</param>
public sealed record OllamaLoadedModel(long Size, long SizeVram, DateTimeOffset? ExpiresAt)
{
    /// <summary>GPU 에 올라간 비율 (0~1)</summary>
    public double GpuFraction => Size > 0 ? Math.Clamp((double)SizeVram / Size, 0, 1) : 0;

    /// <summary>일부라도 CPU 에서 돌면 몇 배 느려진다. 반올림 오차를 생각해 95% 미만부터 본다</summary>
    public bool PartlyOnCpu => GpuFraction < 0.95;
}

/// <summary>
/// 번역과 별개로 Ollama 모델을 다루는 일: 받기 전 크기·디스크 여유, 받기, GPU 에 올라간 상태, 내리기.
/// 모델이 수 GB 라 받기 전에 크기를 알리고, 게임과 GPU 를 나눠 쓰므로 GPU 에 다 올라갔는지 보여 준다
/// </summary>
public static class OllamaModels
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // 모델 받기는 수 분 걸릴 수 있어 HttpClient 기본 제한 시간(100초)을 쓰지 않는다. 멈추는 것은 취소로 한다
    private static readonly HttpClient PullHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private const string RegistryManifestAccept = "application/vnd.docker.distribution.manifest.v2+json";

    /// <summary>
    /// 모델을 받으면 내려받을 크기(바이트). ollama.com 레지스트리의 매니페스트에서 조각 크기를 더한다.
    /// 다른 레지스트리(hf.co/… 등)이거나 레지스트리에 닿지 못하면 null
    /// </summary>
    /// <exception cref="TranslationException">레지스트리에 없는 모델 (이름이 틀림)</exception>
    public static async Task<long?> GetDownloadSizeAsync(string model, CancellationToken ct = default)
    {
        if (ManifestPath(model) is not { } manifest)
            return null;
        var (path, tag) = manifest;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://registry.ollama.ai/v2/{path}/manifests/{tag}");
        request.Headers.Accept.ParseAdd(RegistryManifestAccept);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new TranslationException($"'{model}' 모델을 찾지 못했습니다 — 이름을 확인해 주세요 (ollama.com/library).");
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            long size = root.TryGetProperty("config", out var config) && config.TryGetProperty("size", out var cs) ? cs.GetInt64() : 0;
            foreach (var layer in root.GetProperty("layers").EnumerateArray())
                size += layer.GetProperty("size").GetInt64();
            return size;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null; // 크기는 안내일 뿐이라 모르면 넘어간다
        }
    }

    /// <summary>
    /// 'gemma3:12b' → (library/gemma3, 12b), 'user/model' → (user/model, latest).
    /// 첫 부분에 '.' 이 있으면(hf.co/… 같은 다른 레지스트리) null
    /// </summary>
    internal static (string Path, string Tag)? ManifestPath(string model)
    {
        var name = model.Trim();
        var tag = "latest";
        var colon = name.LastIndexOf(':');
        if (colon > name.LastIndexOf('/'))
        {
            tag = name[(colon + 1)..];
            name = name[..colon];
        }
        if (name.Length == 0 || tag.Length == 0)
            return null;

        var parts = name.Split('/');
        return parts switch
        {
            [var single] => ($"library/{single}", tag),
            [var ns, _] when !ns.Contains('.') => (name, tag),
            _ => null,
        };
    }

    /// <summary>주소가 이 PC 의 Ollama 인지 — 그럴 때만 이 PC 의 디스크 여유를 볼 수 있다</summary>
    public static bool IsLocal(string endpoint)
    {
        if (!Uri.TryCreate(OllamaTranslator.NormalizeEndpoint(endpoint), UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ollama 가 모델을 두는 폴더. OLLAMA_MODELS 환경 변수가 없으면 %USERPROFILE%\.ollama\models</summary>
    public static string ModelsDirectory =>
        Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

    /// <summary>모델 폴더가 있는 드라이브의 여유 공간. 이 PC 의 Ollama 가 아니거나 알 수 없으면 null</summary>
    public static long? FreeDiskBytes(string endpoint)
    {
        if (!IsLocal(endpoint))
            return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(ModelsDirectory));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 모델을 받는다 (터미널의 ollama pull 과 같다). Ollama 는 모델을 조각(digest)으로 나눠 받고 진행률도 조각마다 알려 주므로,
    /// 조각을 합쳐 전체 진행률로 알린다. expectedTotal(레지스트리에서 읽은 크기)을 주면 처음부터 전체 크기로 나눈다
    /// </summary>
    /// <exception cref="TranslationException">연결 실패 또는 Ollama 가 거절 (없는 모델 이름 등)</exception>
    public static async Task PullAsync(
        string endpoint, string model, long? expectedTotal, Action<OllamaPullProgress> onProgress, CancellationToken ct)
    {
        var url = $"{OllamaTranslator.NormalizeEndpoint(endpoint)}/api/pull";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new JsonObject { ["model"] = model, ["stream"] = true }),
        };

        var layers = new Dictionary<string, (long Completed, long Total)>();
        long completed = 0, total = expectedTotal ?? 0;
        try
        {
            // 응답 머리글만 받고 돌아와, 한 줄에 하나씩 오는 진행 상황을 읽는다
            using var response = await PullHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"모델을 받지 못했습니다 (HTTP {(int)response.StatusCode}): {Trim(await response.Content.ReadAsStringAsync(ct))}");

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new TranslationException($"모델을 받지 못했습니다: {error.GetString()}");

                var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                if (root.TryGetProperty("digest", out var d) && d.GetString() is { } digest
                    && root.TryGetProperty("total", out var t) && t.TryGetInt64(out var layerTotal))
                {
                    var layerDone = root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                    layers[digest] = (layerDone, layerTotal);
                    completed = layers.Values.Sum(l => l.Completed);
                    total = Math.Max(layers.Values.Sum(l => l.Total), expectedTotal ?? 0);
                }
                onProgress(new OllamaPullProgress(status, completed, total));
            }
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException($"Ollama에 연결하지 못했습니다 ({url}) — Ollama를 켜 주세요.", e);
        }
        catch (JsonException e)
        {
            throw new TranslationException($"{url} 의 응답이 Ollama 형식이 아닙니다.", e);
        }
    }

    /// <summary>이 모델이 지금 메모리에 올라가 있으면 그 상태, 아니면 null (/api/ps)</summary>
    /// <exception cref="TranslationException">Ollama 에 연결하지 못함</exception>
    public static async Task<OllamaLoadedModel?> GetLoadedAsync(string endpoint, string model, CancellationToken ct = default)
    {
        var url = $"{OllamaTranslator.NormalizeEndpoint(endpoint)}/api/ps";
        try
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !OllamaTranslator.SameModel(name, model))
                    continue;
                var size = m.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szv) ? szv : 0;
                var vram = m.TryGetProperty("size_vram", out var sv) && sv.TryGetInt64(out var svv) ? svv : 0;
                DateTimeOffset? expires = m.TryGetProperty("expires_at", out var e) && e.TryGetDateTimeOffset(out var ev) ? ev : null;
                return new OllamaLoadedModel(size, vram, expires);
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new TranslationException("Ollama에 연결하지 못했습니다.", e);
        }
    }

    /// <summary>모델을 GPU·메모리에서 바로 내린다 (keep_alive 0). 다음 번역 때 다시 올라간다</summary>
    /// <exception cref="TranslationException">Ollama 에 연결하지 못함</exception>
    public static async Task UnloadAsync(string endpoint, string model, CancellationToken ct = default)
    {
        var url = $"{OllamaTranslator.NormalizeEndpoint(endpoint)}/api/generate";
        try
        {
            using var response = await Http.PostAsJsonAsync(url, new JsonObject { ["model"] = model, ["keep_alive"] = 0 }, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException
                                       || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new TranslationException("Ollama에 연결하지 못했습니다.", e);
        }
    }

    /// <summary>바이트를 '8.2GB' 처럼</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0}GB" : $"{bytes / 1e6:0}MB";

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
