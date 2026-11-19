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
    /// ollama.exe serve 를 창 없이 켠다. 이 프로세스(게임)가 어떻게 끝나든 — 비정상 종료라도 — 같이 꺼지도록 묶어 둔다.
    /// 다 뜰 때까지 기다리려면 <see cref="OllamaServerProcess.WaitUntilReadyAsync"/>
    /// </summary>
    /// <exception cref="TranslationException">실행하지 못함</exception>
    public static OllamaServerProcess Start(string executable, string endpoint)
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
        return new OllamaServerProcess(process, executable);
    }
}

/// <summary>플러그인이 켠 Ollama 서버. Dispose 하면 서버와 그 아래 모델 실행기까지 끈다</summary>
public sealed class OllamaServerProcess : IDisposable
{
    private readonly Process _process;

    // 서버 로그는 버리되(채팅 내용이 남지 않게 파일로 쓰지 않는다) 켜다 실패했을 때 이유를 보여 주려고 끝 몇 줄만 둔다
    private readonly Queue<string> _lastLines = new();

    internal OllamaServerProcess(Process process, string executable)
    {
        _process = process;
        Executable = executable;
        // 읽지 않으면 출력 버퍼가 차서 서버가 멈춘다
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string Executable { get; }
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
