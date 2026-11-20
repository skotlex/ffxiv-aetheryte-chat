using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AetheryteChat.Translation;

/// <summary>
/// 이 PC 의 Ollama 서버를 찾고, 꺼져 있으면 창 없이 직접 켠다. Ollama 앱(작업 표시줄 아이콘·환영 화면)을 거치지 않고
/// ollama.exe serve 만 띄운다
/// </summary>
public static class OllamaServer
{
    /// <summary>공식 설치 프로그램이 까는 위치</summary>
    public static string OfficialExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");

    public static bool IsOfficialInstalled => File.Exists(OfficialExecutable);

    /// <summary>쓸 ollama.exe: 플러그인이 설치한 것 → 공식 설치 → PATH. 없으면 null</summary>
    public static string? FindExecutable(string? managedExecutable)
    {
        if (managedExecutable is not null && File.Exists(managedExecutable))
            return managedExecutable;
        if (IsOfficialInstalled)
            return OfficialExecutable;
        return CliLocation.PathDirs()
            .Select(dir => Path.Combine(dir, "ollama.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>서버가 떠 있으면 그 버전, 아니면 null (/api/version)</summary>
    public static async Task<string?> GetVersionAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"{OllamaTranslator.NormalizeEndpoint(endpoint)}/api/version", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or UriFormatException
                                       || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// 켤 때 주는 동시 처리 수 (OLLAMA_NUM_PARALLEL). 기본값 1 이면 플러그인의 번역 작업자들이 보낸 요청을 하나씩 처리해
    /// 뒤의 요청이 앞의 것을 기다린다. 동시에 돌려도 요청 하나의 속도는 거의 그대로라 공략 매크로의 묶음 여럿이 빨리 끝난다
    /// (2026-11-20 RTX 4090·gemma3:12b, 30줄 = 10줄 묶음 셋: 동시 1 약 18초, 동시 2 약 12초, 동시 3 약 7.7초).
    /// 대신 모델 메모리가 는다 (동시 1 / 2 / 3): gemma3:12b 8.1 / 8.6 / 9.7GB, aya-expanse:8b 5.4 / 5.7 / 6.1GB,
    /// gemma3:4b 2.9 / 3.1 / 3.5GB.
    /// 그래서 카드 메모리가 '그 모델의 동시 3 크기 + 게임 몫(<see cref="GameReserveBytes"/>)' 이상일 때만 3, 그 밖에는 2.
    /// 크기를 모르는 모델은 16GB 이상 카드에서만 3 (카드가 16GB 를 조금 못 미치게 알리는 경우가 있어 15.5GB 로 본다).
    /// 카드 메모리는 총량이라 다른 프로그램이 쓰는 양은 모른다 — 올린 뒤 GPU 에 다 못 올라갔으면 플러그인이 2 로 다시 켠다.
    /// CPU 로 돌 때(카드 없음)도 2. 사용자가 직접 켠 Ollama(공식 앱 등)는 그 설정을 그대로 쓴다
    /// </summary>
    public static int ParallelFor(long? gpuMemoryBytes, string model)
    {
        if (gpuMemoryBytes is not { } total)
            return 2;
        if (KnownSizeAtThree.FirstOrDefault(k => OllamaTranslator.SameModel(k.Model, model)) is { Model: not null } known)
            return total >= known.Bytes + GameReserveBytes ? 3 : 2;
        return total >= 31L << 29 ? 3 : 2; // 15.5GB
    }

    /// <summary>이 PC 의 그래픽카드(<see cref="GpuInfo.BestForOllama"/>)와 모델로 정한 동시 처리 수</summary>
    public static int ParallelForThisPc(string model) => ParallelFor(GpuInfo.BestForOllama?.MemoryBytes, model);

    /// <summary>게임이 쓸 그래픽카드 메모리로 남겨 둘 몫 (모델 권장 기준과 같게 약 4GB)</summary>
    public const long GameReserveBytes = 4L << 30;

    /// <summary>동시 3, 문맥 2048 로 올렸을 때 Ollama 가 알려 준 크기 (2026-11-20 RTX 4090, /api/ps size)</summary>
    private static readonly (string Model, long Bytes)[] KnownSizeAtThree =
    [
        ("gemma3:12b", 9_700_000_000),
        ("aya-expanse:8b", 6_100_000_000),
        ("gemma3:4b", 3_520_000_000),
    ];

    /// <summary>
    /// ollama.exe serve 를 창 없이 켠다. 이 프로세스(게임)가 어떻게 끝나든 — 비정상 종료라도 — 같이 꺼지도록 묶어 둔다.
    /// 다 뜰 때까지 기다리려면 <see cref="OllamaServerProcess.WaitUntilReadyAsync"/>
    /// </summary>
    /// <exception cref="TranslationException">실행하지 못함</exception>
    /// <param name="numParallel">동시 처리 수 (<see cref="ParallelForThisPc"/>). <see cref="OllamaServerProcess.NumParallel"/> 로 다시 읽는다</param>
    public static OllamaServerProcess Start(string executable, string endpoint, int numParallel)
    {
        var psi = new ProcessStartInfo(executable, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        // 주소의 호스트·포트로 띄운다 (기본 127.0.0.1:11434)
        if (Uri.TryCreate(OllamaTranslator.NormalizeEndpoint(endpoint), UriKind.Absolute, out var uri))
            psi.Environment["OLLAMA_HOST"] = $"{uri.Host}:{uri.Port}";
        psi.Environment["OLLAMA_NUM_PARALLEL"] = numParallel.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new TranslationException("Ollama를 켜지 못했습니다.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new TranslationException($"Ollama를 켜지 못했습니다: {e.Message}", e);
        }

        KillOnExitJob.Assign(process);
        return new OllamaServerProcess(process, executable, numParallel);
    }
}

/// <summary>플러그인이 켠 Ollama 서버. Dispose 하면 서버와 그 아래 모델 실행기까지 끈다</summary>
public sealed class OllamaServerProcess : IDisposable
{
    private readonly Process _process;

    // 서버 로그는 버리되(채팅 내용이 남지 않게 파일로 쓰지 않는다) 켜다 실패했을 때 이유를 보여 주려고 끝 몇 줄만 둔다
    private readonly Queue<string> _lastLines = new();

    internal OllamaServerProcess(Process process, string executable, int numParallel)
    {
        _process = process;
        Executable = executable;
        NumParallel = numParallel;
        // 읽지 않으면 출력 버퍼가 차서 서버가 멈춘다
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Executable { get; }

    /// <summary>켤 때 준 동시 처리 수 (OLLAMA_NUM_PARALLEL)</summary>
    public int NumParallel { get; }

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    /// <summary>/api/version 이 답할 때까지 기다린다</summary>
    /// <exception cref="TranslationException">그 전에 서버가 끝났거나 시간 안에 뜨지 않음</exception>
    public async Task WaitUntilReadyAsync(string endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_process.HasExited)
                throw new TranslationException($"Ollama가 켜지자마자 꺼졌습니다: {LastLines()}");
            if (await OllamaServer.GetVersionAsync(endpoint, ct) is not null)
                return;
            await Task.Delay(300, ct);
        }
        throw new TranslationException($"Ollama가 {timeout.TotalSeconds:0}초 안에 켜지지 않았습니다.");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // 이미 끝났다
        }
        _process.Dispose();
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_lastLines)
        {
            _lastLines.Enqueue(line);
            while (_lastLines.Count > 5)
                _lastLines.Dequeue();
        }
    }

    private string LastLines()
    {
        lock (_lastLines)
            return string.Join(" / ", _lastLines);
    }
}

/// <summary>
/// 게임 프로세스가 끝나면(비정상 종료 포함) 묶어 둔 자식 프로세스를 Windows 가 함께 끝내게 하는 작업 개체(Job Object).
/// 핸들은 프로세스가 끝날 때 닫히므로 일부러 닫지 않는다
/// </summary>
internal static class KillOnExitJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Assign(Process process)
    {
        var job = Job.Value;
        // 묶지 못해도 서버는 쓸 수 있다 — 플러그인을 끌 때는 직접 끄므로, 게임이 비정상 종료될 때만 남는다
        if (job != IntPtr.Zero)
            AssignProcessToJobObject(job, process.Handle);
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(info)))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, ref JobObjectExtendedLimitInformationStruct info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
