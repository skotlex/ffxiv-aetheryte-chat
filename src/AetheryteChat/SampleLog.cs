using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Game.Text;

namespace AetheryteChat;

/// <summary>
/// 번역 품질을 높이려고 개발자 본인의 게임 PC 에서만 켜는 표본 기록 (게임 용어 후보를 뽑고 번역을 비교하는 데 쓴다).
/// 설정 파일의 <see cref="Configuration.SampleLogDirectory"/> 를 직접 적었을 때만 켜지고, 설정 화면에는 없다 —
/// 공개 플러그인이라 다른 사용자 PC 에 남의 대화가 쌓이면 안 된다. 값이 없으면 아무것도 하지 않고 폴더도 만들지 않는다.
/// <para>
/// 그 폴더에 날짜별 samples-YYYY-MM-DD.jsonl (UTF-8, BOM 없음) 로 한 줄에 사건 하나를 덧붙인다.
/// 보낸 사람은 남기지 않고, 본문 속 플레이어 이름은 이 세션에서 본 이름을 &lt;이름&gt; 으로 가린다
/// (가타카나 애칭이나 한글로 옮긴 이름은 가리지 못한다).
/// 메모리에 모았다가 30초마다, 그리고 플러그인을 끌 때 쓴다. 파일이 잠겨(OneDrive 동기화 등) 쓰지 못하면 다음 차례에 다시 쓴다
/// </para>
/// </summary>
internal sealed class SampleLog : IDisposable
{
    /// <summary>기록 형식 버전. 필드의 뜻을 바꾸면 올린다</summary>
    private const int FormatVersion = 1;

    /// <summary>쓰지 못한 채 쌓인 기록이 이보다 많으면 오래된 것부터 버린다</summary>
    private const int MaxPending = 2000;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    /// <summary>이보다 짧은 이름·성은 따로 가리지 않는다 (흔한 글자열까지 가려 문장이 망가지지 않게). 전체 이름은 늘 가린다</summary>
    private const int MinNamePartLength = 3;

    private const string NameMask = "<이름>";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 일본어·한국어를 \uXXXX 로 바꾸지 않고 그대로 적는다 (사람이 읽고 grep 하기 쉽게)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string? _directory;
    private readonly Timer? _timer;
    private readonly Lock _lock = new();
    private readonly List<(DateTime Time, string Json)> _pending = [];
    private readonly SemaphoreSlim _writing = new(1, 1);
    private bool _warned; // 쓰기 실패 경고는 다시 성공할 때까지 한 번만 남긴다

    // 가릴 이름. 긴 것부터 바꿔야 'Taro Yamada' 를 'Taro' 보다 먼저 가린다
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private string[] _namesLongestFirst = [];

    public SampleLog(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        _directory = directory.Trim();
        _timer = new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
    }

    /// <summary>기록을 남기는지 (개발용 값을 설정했는지). '오역 제보' 메뉴도 이때만 보인다</summary>
    public bool Enabled => _directory is not null;

    /// <summary>플레이어 이름 ('이름 성') 을 기억해 둔다. 메모리에만 두고 파일에는 남기지 않는다</summary>
    public void RememberName(string? playerName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(playerName))
            return;
        var name = playerName.Trim();
        lock (_lock)
        {
            if (!_names.Add(name))
                return;
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= MinNamePartLength);
            _namesLongestFirst = _names.Concat(parts).Concat(_namesLongestFirst)
                .Distinct(StringComparer.Ordinal).OrderByDescending(n => n.Length).ToArray();
        }
    }

    /// <summary>받은 채팅을 번역한 결과 하나. 같은 줄을 다른 도구로 다시 번역하면 한 건 더 남는다</summary>
    public void Incoming(XivChatType channel, string source, string engine, bool done, string? output)
    {
        if (!Enabled)
            return;
        Add(new JsonObject
        {
            ["kind"] = "in",
            ["ch"] = ChannelName(channel),
            ["src"] = Mask(source),
            ["engine"] = engine,
            ["state"] = done ? "done" : "failed",
            ["out"] = done && output is not null ? Mask(output) : null,
        });
    }

    /// <summary>보낸 글: 쓴 한국어, 보낸 일본어, 다시 옮긴 한국어 (아직 안 나왔으면 null)</summary>
    public void Outgoing(string channel, string korean, string japanese, string? back, string engine, bool sent)
    {
        if (!Enabled)
            return;
        Add(new JsonObject
        {
            ["kind"] = "out",
            ["ch"] = channel,
            ["ko"] = Mask(korean),
            ["ja"] = Mask(japanese),
            ["back"] = back is null ? null : Mask(back),
            ["engine"] = engine,
            ["sent"] = sent,
        });
    }

    /// <summary>받은 채팅 창의 '오역 제보'</summary>
    public void Flag(XivChatType channel, string source, string engine, string output)
    {
        if (!Enabled)
            return;
        Add(new JsonObject
        {
            ["kind"] = "flag",
            ["ch"] = ChannelName(channel),
            ["src"] = Mask(source),
            ["engine"] = engine,
            ["out"] = Mask(output),
            ["reason"] = "user",
        });
    }

    /// <summary>번역할 채널 묶음의 이름 (Party, Tell 등). 링크셸 번호·이름은 남기지 않는다</summary>
    private static string ChannelName(XivChatType channel) =>
        TranslateChannels.GroupFor(channel)?.ToString() ?? "Other";

    /// <summary>보내기 채널 키(SendChannel.Key)를 받은 채팅과 같은 묶음 이름으로. '자동' 은 실제 채널을 몰라 Auto</summary>
    public static string SendChannelName(string key) => key switch
    {
        SendChannel.AutoKey => "Auto",
        SendChannel.TellKey => nameof(TranslateChannelGroup.Tell),
        "say" => nameof(TranslateChannelGroup.Say),
        "party" => nameof(TranslateChannelGroup.Party),
        "alliance" => nameof(TranslateChannelGroup.Alliance),
        "yell" => nameof(TranslateChannelGroup.Yell),
        "shout" => nameof(TranslateChannelGroup.Shout),
        "fc" => nameof(TranslateChannelGroup.FreeCompany),
        "echo" => nameof(TranslateChannelGroup.Echo),
        _ when key.StartsWith("cwls", StringComparison.Ordinal) => nameof(TranslateChannelGroup.CrossLinkshell),
        _ when key.StartsWith("ls", StringComparison.Ordinal) => nameof(TranslateChannelGroup.Linkshell),
        _ => "Other",
    };

    private string Mask(string text)
    {
        string[] names;
        lock (_lock)
            names = _namesLongestFirst;
        foreach (var name in names)
            text = text.Replace(name, NameMask, StringComparison.Ordinal);
        return text;
    }

    private void Add(JsonObject fields)
    {
        var now = DateTime.Now;
        var record = new JsonObject { ["v"] = FormatVersion, ["t"] = now.ToString("yyyy-MM-ddTHH:mm:ss") };
        foreach (var (key, value) in fields.ToList())
        {
            fields.Remove(key);
            if (value is not null)
                record[key] = value;
        }
        var json = record.ToJsonString(JsonOptions);
        lock (_lock)
        {
            _pending.Add((now, json));
            if (_pending.Count > MaxPending)
                _pending.RemoveRange(0, _pending.Count - MaxPending);
        }
    }

    /// <summary>쌓인 기록을 날짜별 파일에 덧붙인다. 쓰지 못한 것은 버리지 않고 남겨 다음 차례에 다시 쓴다</summary>
    private async Task FlushAsync()
    {
        if (_directory is null || !await _writing.WaitAsync(0))
            return; // 앞의 쓰기가 아직 끝나지 않았다
        try
        {
            List<(DateTime Time, string Json)> batch;
            lock (_lock)
            {
                if (_pending.Count == 0)
                    return;
                batch = [.. _pending];
                _pending.Clear();
            }

            var failed = new List<(DateTime Time, string Json)>();
            foreach (var day in batch.GroupBy(r => r.Time.Date))
            {
                var path = Path.Combine(_directory, $"samples-{day.Key:yyyy-MM-dd}.jsonl");
                var text = string.Concat(day.Select(r => r.Json + "\n"));
                try
                {
                    Directory.CreateDirectory(_directory);
                    await File.AppendAllTextAsync(path, text, Utf8NoBom);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    failed.AddRange(day);
                    if (!_warned)
                    {
                        _warned = true;
                        // 채팅 내용·경로가 로그에 남지 않게 종류만 남긴다
                        Plugin.Log.Warning($"표본 기록을 쓰지 못했습니다 ({e.GetType().Name}) — 다음 차례에 다시 씁니다");
                    }
                }
            }

            if (failed.Count == 0)
            {
                _warned = false;
                return;
            }
            lock (_lock)
            {
                _pending.InsertRange(0, failed);
                if (_pending.Count > MaxPending)
                    _pending.RemoveRange(0, _pending.Count - MaxPending);
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Dispose()
    {
        if (_timer is null)
            return;
        _timer.Dispose();
        // 플러그인을 끌 때 남은 기록을 쓴다. 파일이 잠겨 있으면 오래 기다리지 않고 버린다
        try
        {
            // 타이머가 시작한 쓰기가 끝나기를 기다린 뒤 마지막으로 한 번 더 쓴다
            if (!_writing.Wait(TimeSpan.FromSeconds(2)))
                return;
            _writing.Release();
            FlushAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}
