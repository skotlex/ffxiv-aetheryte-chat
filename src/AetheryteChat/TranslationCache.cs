using AetheryteChat.Translation;

namespace AetheryteChat;

/// <summary>
/// 받은 채팅의 같은 원문 번역을 기억한다. 레이드에서 파티장이 전멸할 때마다 같은 공략 매크로를 다시 올려도
/// 번역 도구를 다시 부르지 않고 바로 보여 준다 (사용량도 아낀다).
/// 번역 도구 하나(<see cref="Owner"/>)에 묶인다 — 도구나 그 설정(모델·용어집 등)을 바꾸면 도구를 새로 만들므로 캐시도 새로 쓴다.
/// 가득 차면 먼저 넣은 것부터 버린다. 메모리에만 두고 파일에 남기지 않는다
/// </summary>
internal sealed class TranslationCache(ITranslator owner, int capacity = TranslationCache.DefaultCapacity)
{
    public const int DefaultCapacity = 500;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public ITranslator Owner { get; } = owner;

    public bool TryGet(string text, out string translation)
    {
        lock (_lock)
            return _map.TryGetValue(text, out translation!);
    }

    public void Add(string text, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
            return;
        lock (_lock)
        {
            if (!_map.TryAdd(text, translation))
                return;
            _order.Enqueue(text);
            if (_map.Count > capacity)
                _map.Remove(_order.Dequeue());
        }
    }
}
