using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AetheryteChat.Translation;

/// <summary>
/// 그래픽카드 하나. MemoryBytes 는 그래픽카드 전용 메모리 (내장 그래픽은 BIOS 가 떼어 준 만큼, 못 읽으면 null).
/// OllamaCanUse: Ollama 가 계산에 쓸 수 있는 카드 — NVIDIA 이거나 AMD 외장(RX·PRO). 내장 그래픽·Intel 은 CPU 로 돈다
/// </summary>
public sealed record GpuAdapter(string Name, long? MemoryBytes, bool IsNvidia, bool IsAmdDiscrete)
{
    public bool OllamaCanUse => IsNvidia || IsAmdDiscrete;

    /// <summary>이름 (12GB) 꼴</summary>
    public override string ToString() => MemoryBytes is { } m ? $"{Name} ({m / (double)(1L << 30):0.#}GB)" : Name;
}

/// <summary>이 PC 의 그래픽카드를 레지스트리(디스플레이 어댑터 장치 클래스)에서 읽는다. 관리자 권한은 필요 없다</summary>
public static class GpuInfo
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<IReadOnlyList<GpuAdapter>> Cached = new(Read);

    /// <summary>이 PC 의 그래픽카드들 (원격 데스크톱·기본 디스플레이 같은 가상 장치는 뺀다). 한 번 읽고 기억한다</summary>
    public static IReadOnlyList<GpuAdapter> Adapters => Cached.Value;

    /// <summary>Ollama 가 쓸 수 있는 카드 중 메모리가 가장 큰 것. 없으면 null</summary>
    public static GpuAdapter? BestForOllama =>
        Adapters.Where(a => a.OllamaCanUse).OrderByDescending(a => a.MemoryBytes ?? 0).FirstOrDefault();

    private static IReadOnlyList<GpuAdapter> Read()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var result = new List<GpuAdapter>();
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (display is null)
                return result;
            foreach (var name in display.GetSubKeyNames())
            {
                RegistryKey? adapter;
                try
                {
                    adapter = display.OpenSubKey(name);
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    continue; // Properties 처럼 읽을 수 없는 하위 키
                }
                using (adapter)
                {
                    if (adapter?.GetValue("DriverDesc") is not string desc || IsVirtual(desc))
                        continue;
                    result.Add(new GpuAdapter(desc, MemorySize(adapter), IsNvidia(desc), IsAmdDiscrete(desc)));
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 못 읽으면 찾은 데까지만
        }
        return result;
    }

    private static bool IsVirtual(string desc) =>
        desc.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) // 원격 디스플레이·기본 디스플레이 어댑터
        || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || desc.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    private static bool IsNvidia(string desc) =>
        desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAmdDiscrete(string desc)
    {
        var isAmd = desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        // 내장 그래픽은 "AMD Radeon 760M Graphics" 처럼 RX·PRO 가 없다
        var isDiscrete = desc.Contains(" RX ", StringComparison.OrdinalIgnoreCase) || desc.Contains(" PRO ", StringComparison.OrdinalIgnoreCase)
                         || desc.EndsWith(" PRO", StringComparison.OrdinalIgnoreCase);
        return isAmd && isDiscrete;
    }

    // qwMemorySize(QWORD)가 정확하다. 옛 드라이버는 MemorySize(DWORD 또는 4바이트 BINARY, 4GB 까지)만 둔다
    [SupportedOSPlatform("windows")]
    private static long? MemorySize(RegistryKey adapter)
    {
        static long? Parse(object? value) => value switch
        {
            long l when l > 0 => l,
            int i when i != 0 => (uint)i,
            byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
            _ => null,
        };
        return Parse(adapter.GetValue("HardwareInformation.qwMemorySize")) ?? Parse(adapter.GetValue("HardwareInformation.MemorySize"));
    }
}
