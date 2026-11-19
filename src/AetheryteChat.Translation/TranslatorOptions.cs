namespace AetheryteChat.Translation;

/// <summary>모든 LLM 엔진이 같이 쓰는 옵션</summary>
public abstract class TranslatorOptions
{
    /// <summary>
    /// 번역 지시문에 넣는 용어집. 한 줄에 '일본어 → 한국어' 하나. 비우면 용어집 없이 번역한다.
    /// 한→일 때는 같은 표를 거꾸로 읽게 한다
    /// </summary>
    public string Glossary { get; set; } = Prompts.DefaultGlossary;

    public abstract TimeSpan Timeout { get; set; }
}
