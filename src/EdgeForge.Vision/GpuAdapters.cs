using System.Runtime.InteropServices;

namespace EdgeForge.Vision;

/// <summary>
/// Chooses which GPU DirectML should run on.
/// </summary>
/// <remarks>
/// DirectML addresses GPUs by adapter index, and that index carries no promise
/// about speed: it is simply enumeration order. On a laptop with switchable
/// graphics index 0 is normally the integrated GPU — on this machine that meant
/// yolov8m at 193 ms against 28 ms on the discrete card, a 7x penalty from one
/// integer nobody thinks to question.
///
/// Rather than reach into DXGI through COM, the adapter list comes from the
/// display driver registry key, which the graphics stack itself populates and
/// which records dedicated video memory per adapter. That is the value which
/// separates a discrete card from an integrated one: integrated graphics carve
/// their memory out of system RAM and report little or no dedicated VRAM.
///
/// What this deliberately does NOT do is map that to a DirectML index. The two
/// orderings are not the same — measured on this machine the registry lists the
/// GTX first while DirectML answers to it as index 1 — so guessing the index from
/// the registry would confidently select the wrong GPU. Enumeration is used only
/// to describe what is present and to warn when the configured index looks like
/// the integrated adapter; the index itself stays an explicit setting.
/// </remarks>
internal static class GpuAdapters
{
    /// <summary>One graphics adapter.</summary>
    internal readonly record struct Adapter(int Index, string Description, ulong DedicatedVideoMemory);

    private const string VideoKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Adapters in driver-enumeration order, which is the order DirectML indexes
    /// them. Returns an empty list on any failure — this is an optimisation, and a
    /// machine where it does not work must still run on the configured index.
    /// </summary>
    internal static IReadOnlyList<Adapter> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return EnumerateWindows();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<Adapter> EnumerateWindows()
    {
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(VideoKey);
        if (root is null)
        {
            return [];
        }

        var adapters = new List<Adapter>();

        // Subkeys are "0000", "0001", ... in the order the driver stack assigned.
        foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).Order())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("DriverDesc") is not string desc)
            {
                continue;
            }

            // Written by the driver as a QWORD; absent on some virtual adapters.
            ulong vram = key.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long l => (ulong)l,
                int i => (uint)i,
                byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                _ => 0UL,
            };

            adapters.Add(new Adapter(adapters.Count, desc, vram));
        }

        return adapters;
    }

    /// <summary>
    /// True when the machine has more than one adapter and they differ enough in
    /// dedicated video memory to suggest a discrete/integrated pair — the case
    /// where picking the wrong DirectML index is expensive and silent.
    /// </summary>
    internal static bool HasMultipleAdapters(out string summary)
    {
        var adapters = Enumerate();
        summary = string.Join(", ", adapters.Select(
            a => $"{a.Description} ({a.DedicatedVideoMemory / (1024 * 1024)} MB)"));

        return adapters.Count > 1;
    }
}
