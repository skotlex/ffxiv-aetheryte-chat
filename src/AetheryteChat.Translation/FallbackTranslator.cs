namespace AetheryteChat.Translation;

/// <summary>순서대로 쓸 도구 하나의 상태</summary>
public enum FallbackMemberStatus
{
    Checking,
    Ready,

    /// <summary>로그인이 안 됐거나 풀림 (Ollama 는 모델이 없음). 다시 확인하거나 로그인할 때까지 건너뛴다</summary>
    NotLoggedIn,

    /// <summary>실행 파일이 없거나 연결하지 못함. 다시 확인할 때까지 건너뛴다</summary>
    Unavailable,
}

/// <summary>
/// 여러 번역 도구를 우선순위대로 쓴다. 앞의 도구가 준비되지 않았거나 번역에 실패하면(시간 초과·연결 실패·로그인 풀림 등)
/// 다음 도구로 같은 줄을 번역한다. 실패한 도구는 <see cref="FailureCooldown"/> 동안 건너뛰어, 막힌 도구의 제한 시간을
/// 줄마다 기다리지 않게 한다. 로그인이 풀린 도구는 다시 확인(<see cref="IsLoggedInAsync"/>)할 때까지 건너뛴다.
/// 도구가 하나뿐이면 그 도구를 그대로 쓰는 것과 같다
/// </summary>
public sealed class FallbackTranslator : ITranslator
{
    /// <summary>번역에 실패한 도구를 건너뛰는 시간. 지나면 다시 앞 순위부터 쓴다</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    public sealed class Member(string key, ITranslator translator)
    {
        /// <summary>부르는 쪽이 도구를 알아보는 이름 (플러그인은 TranslationEngine 이름)</summary>
        public string Key { get; } = key;
        public ITranslator Translator { get; } = translator;

        private volatile FallbackMemberStatus _status = FallbackMemberStatus.Checking;
        public FallbackMemberStatus Status { get => _status; internal set => _status = value; }

        /// <summary>쓸 수 없는 이유 또는 마지막 실패 이유. 없으면 null</summary>
        public string? Detail { get; internal set; }

        private long _skipUntilTicks; // UTC. 실패한 뒤 건너뛰는 끝 시각

        /// <summary>최근에 실패해 잠시 건너뛰는 중인지</summary>
        public bool CoolingDown => DateTime.UtcNow.Ticks < Interlocked.Read(ref _skipUntilTicks);

        internal void CoolDown() => Interlocked.Exchange(ref _skipUntilTicks, (DateTime.UtcNow + FailureCooldown).Ticks);
        internal void ClearCoolDown() => Interlocked.Exchange(ref _skipUntilTicks, 0);
    }

    private readonly Member[] _members;

    /// <param name="members">우선순위 순서. 하나 이상</param>
    public FallbackTranslator(IEnumerable<(string Key, ITranslator Translator)> members)
    {
        _members = members.Select(m => new Member(m.Key, m.Translator)).ToArray();
        if (_members.Length == 0)
            throw new ArgumentException("번역 도구가 하나 이상 있어야 합니다.", nameof(members));
    }

    public IReadOnlyList<Member> Members => _members;

    public Member Primary => _members[0];

    /// <summary>지금 번역에 쓸 도구: 준비됐고 건너뛰는 중이 아닌 첫 도구. 모두 건너뛰는 중이면 준비된 첫 도구. 없으면 null</summary>
    public Member? Active =>
        _members.FirstOrDefault(m => m.Status == FallbackMemberStatus.Ready && !m.CoolingDown)
        ?? _members.FirstOrDefault(m => m.Status == FallbackMemberStatus.Ready);

    /// <summary>상태 줄에 보이는 이름: 지금 쓰는 도구 (없으면 1순위)</summary>
    public string Name => (Active ?? Primary).Translator.Name;

    /// <summary>
    /// 번역을 시작할 때 붙이는 이름표는 1순위 도구. 실제로 번역한 도구는 결과의 <see cref="TranslationResult.Engine"/> 에 적는다
    /// </summary>
    public string ShortName => Primary.Translator.ShortName;

    public string NotReadyHint => Primary.Translator.NotReadyHint;

    /// <summary>로그인 버튼으로 로그인할 도구: 로그인이 안 된 첫 도구 가운데 플러그인에서 로그인할 수 있는 것</summary>
    public Member? LoginTarget =>
        _members.FirstOrDefault(m => m.Status == FallbackMemberStatus.NotLoggedIn && m.Translator.CanLogin);

    public bool CanLogin => LoginTarget is not null;

    public Task LoginAsync(CancellationToken ct = default) =>
        LoginTarget?.Translator.LoginAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// 1순위 도구를 쓰지 못하고 다음 도구로 번역하는 중이면 그 이유 (예: '1순위 Claude CLI: 로그인 안 됨'). 1순위를 쓰면 null
    /// </summary>
    public string? PrimaryIssue
    {
        get
        {
            var active = Active;
            if (active is null || active == Primary)
                return null;
            var p = Primary;
            var reason = p.Status switch
            {
                FallbackMemberStatus.Checking => "확인 중",
                FallbackMemberStatus.NotLoggedIn => p.Translator.NotReadyHint,
                FallbackMemberStatus.Unavailable => "사용 불가",
                _ => "실패해 잠시 건너뜀",
            };
            return $"1순위 {p.Translator.Name}: {reason}";
        }
    }

    /// <summary>
    /// 모든 도구의 상태를 함께 확인한다. 하나라도 준비됐으면 true. 모두 쓸 수 없으면 — 로그인이 안 된 도구가 있으면 false
    /// (로그인 안내를 보이게), 아니면 1순위 도구가 쓸 수 없는 이유로 예외.
    /// 1순위가 준비되면 나머지 확인(Ollama 켜기 등 수십 초 걸릴 수 있다)을 기다리지 않고 돌아온다 — 나머지는 뒤에서 끝난다
    /// </summary>
    /// <exception cref="TranslationException">어느 도구도 쓸 수 없고 로그인할 도구도 없음</exception>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        var checks = _members.Select(m => CheckAsync(m, ct)).ToArray();
        await checks[0];
        if (Primary.Status == FallbackMemberStatus.Ready)
            return true;
        await Task.WhenAll(checks);
        if (_members.Any(m => m.Status == FallbackMemberStatus.Ready))
            return true;
        if (_members.Any(m => m.Status == FallbackMemberStatus.NotLoggedIn))
            return false;
        throw new TranslationException(Primary.Detail ?? $"{Primary.Translator.Name}를 쓸 수 없습니다.");
    }

    private static async Task CheckAsync(Member member, CancellationToken ct)
    {
        member.Status = FallbackMemberStatus.Checking;
        member.ClearCoolDown();
        try
        {
            var ready = await member.Translator.IsLoggedInAsync(ct);
            member.Detail = null;
            member.Status = ready ? FallbackMemberStatus.Ready : FallbackMemberStatus.NotLoggedIn;
        }
        catch (TranslationException e)
        {
            member.Detail = e.Message;
            member.Status = FallbackMemberStatus.Unavailable;
        }
    }

    /// <summary>
    /// 준비된(또는 아직 확인 중인) 도구를 우선순위대로 시도한다. 최근 실패해 건너뛰는 도구는 다른 도구가 모두 실패했을 때만 쓴다.
    /// 성공하면 결과의 <see cref="TranslationResult.Engine"/> 에 번역한 도구의 ShortName 을 적는다
    /// </summary>
    /// <exception cref="TranslationException">
    /// 모든 도구가 실패했거나 쓸 도구가 없음. 실패가 모두 로그인 문제였으면(또는 로그인만 하면 쓸 도구가 있으면) IsAuthError
    /// </exception>
    public async Task<TranslationResult> TranslateAsync(string text, TranslationDirection direction, CancellationToken ct = default)
    {
        // 아직 확인 중인 도구도 쓸 수 있을지 모르니 시도한다 (확인하기 전에 번역을 부른 경우 등)
        var usable = _members.Where(m => m.Status is FallbackMemberStatus.Ready or FallbackMemberStatus.Checking).ToList();
        var order = usable.Where(m => !m.CoolingDown).Concat(usable.Where(m => m.CoolingDown)).ToList();
        if (order.Count == 0)
        {
            throw new TranslationException("쓸 수 있는 번역 도구가 없습니다.")
            {
                IsAuthError = _members.Any(m => m.Status == FallbackMemberStatus.NotLoggedIn),
            };
        }

        var failures = new List<(Member Member, TranslationException Error)>();
        foreach (var member in order)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await member.Translator.TranslateAsync(text, direction, ct);
                member.ClearCoolDown();
                return result with { Engine = member.Translator.ShortName };
            }
            catch (Exception raw) when (raw is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // 예상하지 못한 오류(도구 안에서 새어 나온 시간 초과 등)도 다음 도구로 넘어간다. 취소는 그대로 올린다
                var e = raw as TranslationException
                        ?? new TranslationException($"알 수 없는 오류 ({raw.GetType().Name})", raw);
                failures.Add((member, e));
                member.Detail = e.Message;
                if (e.IsAuthError)
                    member.Status = FallbackMemberStatus.NotLoggedIn;
                else
                    member.CoolDown();
            }
        }

        if (failures.Count == 1)
            throw failures[0].Error; // 도구가 하나면 그 도구의 오류를 그대로 (로그인 판정도 그대로) 넘긴다
        throw new TranslationException(
            string.Join(" / ", failures.Select(f => $"{f.Member.Translator.Name}: {f.Error.Message}")), failures[^1].Error)
        {
            IsAuthError = failures.All(f => f.Error.IsAuthError),
        };
    }

    /// <summary>이 키의 도구 (없으면 null)</summary>
    public Member? Find(string key) => _members.FirstOrDefault(m => m.Key == key);
}
