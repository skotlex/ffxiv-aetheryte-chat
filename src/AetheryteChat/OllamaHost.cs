using AetheryteChat.Translation;

namespace AetheryteChat;

/// <summary>
/// 이 PC 의 Ollama 를 플러그인이 챙긴다.
/// - 번역할 때 Ollama 가 꺼져 있으면 창 없이 켜고(ollama.exe serve), 플러그인·게임을 끄면 끈다. 게임이 비정상 종료돼도 Windows 가 같이 끈다
/// - 사용자가 직접 켠 Ollama(공식 앱 등)는 쓰기만 하고 건드리지 않는다
/// - 설치 프로그램 없이 공식 GitHub 릴리스의 zip 으로 설치·업데이트한다 (플러그인 설정 폴더의 ollama\)
/// </summary>
internal sealed class OllamaHost(string pluginConfigDirectory) : IDisposable
{
    /// <summary>플러그인이 설치한 Ollama 폴더</summary>
    public string InstallRoot { get; } = Path.Combine(pluginConfigDirectory, "ollama");

    /// <summary>시작프로그램에서 뺀 Ollama 앱 바로가기를 두는 곳</summary>
    public string StartupBackupDir { get; } = Path.Combine(pluginConfigDirectory, "ollama-startup");

    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile OllamaServerProcess? _server;

    /// <summary>플러그인이 켠 서버가 돌고 있다 (아니면 꺼져 있거나 다른 프로그램이 켠 것)</summary>
    public bool OwnsRunningServer => _server is { HasExited: false };

    public string ManagedExecutable => OllamaInstaller.ExecutablePath(InstallRoot);
    public bool IsManagedInstalled => OllamaInstaller.IsInstalled(InstallRoot);
    public string? ManagedVersion => OllamaInstaller.InstalledVersion(InstallRoot);

    /// <summary>쓸 수 있는 ollama.exe 가 있는지 (플러그인 설치·공식 설치·PATH)</summary>
    public bool IsAnyInstalled => OllamaServer.FindExecutable(ManagedExecutable) is not null;

    /// <summary>
    /// 번역기가 상태를 확인하기 전에 부른다. 이 PC 의 주소인데 Ollama 가 꺼져 있으면 창 없이 켜고 뜰 때까지 기다린다
    /// </summary>
    /// <exception cref="TranslationException">설치되어 있지 않거나 켜지 못함</exception>
    public async Task EnsureServerAsync(string endpoint, CancellationToken ct)
    {
        if (!OllamaModels.IsLocal(endpoint) || await OllamaServer.GetVersionAsync(endpoint, ct) is not null)
            return; // 다른 PC 의 Ollama 이거나, 이미 누군가 켜 두었다

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _serverLock.WaitAsync(linked.Token);
        try
        {
            if (await OllamaServer.GetVersionAsync(endpoint, linked.Token) is not null)
                return; // 기다리는 사이 켜졌다

            StopServerLocked(); // 꺼진 채 남은 것을 치운다
            var executable = OllamaServer.FindExecutable(ManagedExecutable)
                ?? throw new TranslationException("Ollama가 설치되어 있지 않습니다 — '번역 도구' 탭에서 설치해 주세요.");
            var server = OllamaServer.Start(executable, endpoint, OllamaServer.ParallelForThisPc);
            _server = server;
            await server.WaitUntilReadyAsync(endpoint, TimeSpan.FromSeconds(30), linked.Token);
            Plugin.Log.Information($"Ollama 서버를 켰습니다 (pid {server.ProcessId})");
        }
        finally
        {
            _serverLock.Release();
        }
    }

    /// <summary>
    /// 플러그인이 켠 서버를 끈다 (다른 도구로 바꿨거나 다른 PC 의 Ollama 를 쓰게 됐을 때). 남이 켠 것은 두고 간다.
    /// 서버를 켜는 중이면 그것이 끝나기를 기다려야 해서 UI 스레드를 막지 않게 뒤에서 한다
    /// </summary>
    public void StopServerInBackground()
    {
        if (_server is null)
            return;
        _ = Task.Run(async () =>
        {
            await _serverLock.WaitAsync();
            try
            {
                StopServerLocked();
            }
            finally
            {
                _serverLock.Release();
            }
        });
    }

    private void StopServerLocked()
    {
        if (_server is not { } server)
            return;
        _server = null;
        server.Dispose();
        Plugin.Log.Information("Ollama 서버를 껐습니다");
    }

    // ── 설치·업데이트 ──────────────────────────────────────────

    private Task? _install;
    private CancellationTokenSource? _installCts;

    public bool IsInstalling => _install is { IsCompleted: false };
    public volatile OllamaInstallProgress? InstallProgress;

    /// <summary>마지막 설치가 실패했으면 그 이유</summary>
    public string? InstallError { get; private set; }

    /// <summary>
    /// 받기·검증·풀기를 하고, 플러그인이 켠 서버를 잠깐 끈 뒤 바꿔 끼운다. 끝나면 onDone (프레임워크 스레드)
    /// </summary>
    public void StartInstall(OllamaRelease release, bool includeRocm, Action onDone)
    {
        if (IsInstalling)
            return;
        InstallError = null;
        InstallProgress = null;
        _installCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _installCts.Token;
        _install = Task.Run(async () =>
        {
            try
            {
                var newDir = await OllamaInstaller.PrepareAsync(InstallRoot, release, includeRocm, p => InstallProgress = p, ct);
                await _serverLock.WaitAsync(ct);
                try
                {
                    StopServerLocked(); // 실행 중인 ollama.exe 는 지울 수 없다
                    OllamaInstaller.Activate(InstallRoot, newDir, release.Tag);
                }
                finally
                {
                    _serverLock.Release();
                }
                Plugin.Log.Information($"Ollama {release.Tag} 를 설치했습니다");
            }
            catch (OperationCanceledException)
            {
                return; // 받은 부분은 남아 다음에 이어 받는다
            }
            catch (TranslationException e)
            {
                InstallError = e.Message;
                return;
            }
            await Plugin.Framework.RunOnFrameworkThread(onDone);
        }, ct);
    }

    public void CancelInstall() => _installCts?.Cancel();

    /// <summary>플러그인이 설치한 Ollama 를 지운다. 받아 둔 모델은 Ollama 모델 폴더에 남는다. 뒤에서 부른다</summary>
    /// <exception cref="TranslationException">지우지 못함</exception>
    public async Task UninstallAsync()
    {
        await _serverLock.WaitAsync();
        try
        {
            // 플러그인 폴더의 ollama.exe 로 켠 서버면 먼저 끈다
            if (_server is { } server && server.Executable.StartsWith(InstallRoot, StringComparison.OrdinalIgnoreCase))
                StopServerLocked();
            OllamaInstaller.Uninstall(InstallRoot);
        }
        finally
        {
            _serverLock.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // 받다 만 것은 남겨 두고(다음에 이어 받기), 플러그인이 켠 서버는 끈다
        if (_server is { } server)
        {
            _server = null;
            server.Dispose();
        }
    }
}
