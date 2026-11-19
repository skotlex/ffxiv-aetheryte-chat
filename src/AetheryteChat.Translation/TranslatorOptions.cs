namespace AetheryteChat.Translation;

/// <summary>모든 LLM 엔진이 같이 쓰는 옵션</summary>
public abstract class TranslatorOptions
{
    /// <summary>
    /// 용어집. 번역할 때 그 줄에 나온 용어만 사용자 메시지에 넣는다. <see cref="Translation.Glossary.Empty"/> 면 용어집 없이 번역한다.
    /// 한→일 때는 한국어 쪽으로 찾아 '한국어 → 일본어' 로 넣는다
    /// </summary>
    public Glossary Glossary { get; set; } = Glossary.Default;

    public abstract TimeSpan Timeout { get; set; }
}
