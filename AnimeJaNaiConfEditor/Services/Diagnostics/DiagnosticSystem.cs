using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services.Diagnostics;

public static class DiagnosticSystem
{
    public static async Task<object> CollectAsync()
    {
        var result = new Dictionary<string, object?> {
            ["utc"] = DateTime.UtcNow, ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(), ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["manager_version"] = typeof(DiagnosticSystem).Assembly.GetName().Version?.ToString(),
            ["logical_processors"] = Environment.ProcessorCount
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var (key, query) in new[] {
                ("gpus", "SELECT Name, DriverVersion, DriverDate, Status FROM Win32_VideoController"),
                ("cpu", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"),
                ("windows", "SELECT Caption, Version, BuildNumber, TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem") })
            {
                try { result[key] = await Task.Run(() => Query(query)).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { result[key] = new { unavailable = ex.Message }; }
            }
            try { result["displays"] = Displays(); }
            catch (Exception ex) { result["displays"] = new { unavailable = ex.Message }; }
            try { result["vulkan_layers"] = VulkanLayers(); }
            catch (Exception ex) { result["vulkan_layers"] = new { unavailable = ex.Message }; }
        }
        result["nvidia_smi"] = await NvidiaInfoAsync();
        result["diagnostic_environment"] = new[] { "VK_LOADER_LAYERS_DISABLE", "VK_LOADER_LAYERS_ALLOW", "VK_ICD_FILENAMES", "VK_DRIVER_FILES", "MPV_HOME", "CUDA_VISIBLE_DEVICES" }
            .ToDictionary(k => k, Environment.GetEnvironmentVariable);
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static object[] Query(string query)
    {
        using var search = new ManagementObjectSearcher(new ManagementScope(), new ObjectQuery(query),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(4) });
        using var values = search.Get();
        return values.Cast<ManagementBaseObject>().Select(m =>
        {
            using (m) return (object)m.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => p.Value);
        }).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static object[] VulkanLayers()
    {
        var result = new List<object>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                foreach (string kind in new[] { "ImplicitLayers", "ExplicitLayers" })
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\" + kind);
                    if (key == null) continue;
                    foreach (string name in key.GetValueNames())
                        result.Add(new { hive = hive.ToString(), view = view.ToString(), kind, manifest = name, value = key.GetValue(name) });
                }
        return result.ToArray();
    }

    private static async Task<object> NvidiaInfoAsync()
    {
        using var p = new Process { StartInfo = new ProcessStartInfo {
            FileName = "nvidia-smi", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = "--query-gpu=name,driver_version,memory.total,memory.used,utilization.gpu,power.limit --format=csv" } };
        try
        {
            p.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return new { exit_code = p.ExitCode, output = await output, error = await error };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception stopError) when (stopError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new { unavailable = ex.Message };
        }
    }

    // QueryDisplayConfig preserves fractional refresh (e.g. 24000/1001), unlike WMI.
    // Read-only: diagnostics never calls DisplayConfigSetDeviceInfo/SetDisplayConfig.
    [SupportedOSPlatform("windows")]
    private static object[] Displays()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out uint count, out uint modeCount));
            var paths = new DisplayPath[count];
            IntPtr modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                int rc = QueryDisplayConfig(2, ref count, paths, ref modeCount, modes, IntPtr.Zero);
                if (rc == 122) continue; // Display changed while querying it.
                Check(rc);
                return paths.Take((int)count).Select(path =>
                {
                    var color = new ColorInfo { header = new Header { type = 15, size = 36, adapter = path.target.adapter, id = path.target.id } };
                    int colorResult = DisplayConfigGetDeviceInfo(ref color);
                    uint? width = null, height = null;
                    if (path.source.mode < modeCount)
                    {
                        // DISPLAYCONFIG_MODE_INFO: type/id/LUID (16 bytes), then source width/height.
                        IntPtr mode = IntPtr.Add(modes, checked((int)path.source.mode * 64));
                        if (Marshal.ReadInt32(mode) == 1) { width = (uint)Marshal.ReadInt32(mode, 16); height = (uint)Marshal.ReadInt32(mode, 20); }
                    }
                    return (object)new {
                        adapter_luid = $"{path.target.adapter.high:X8}:{path.target.adapter.low:X8}", target_id = path.target.id,
                        width, height, refresh_numerator = path.target.refresh.numerator, refresh_denominator = path.target.refresh.denominator,
                        hz = path.target.refresh.denominator == 0 ? (double?)null : (double)path.target.refresh.numerator / path.target.refresh.denominator,
                        hdr_query_error = colorResult, hdr_supported = colorResult == 0 ? (bool?)((color.flags & 16) != 0) : null,
                        hdr_user_enabled = colorResult == 0 ? (bool?)((color.flags & 32) != 0) : null,
                        active_color_mode = colorResult == 0 ? (uint?)color.activeMode : null,
                        bits_per_channel = colorResult == 0 ? (uint?)color.bits : null
                    };
                }).ToArray();
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("Display topology changed repeatedly while collecting the report.");
    }

    private static void Check(int result) { if (result != 0) throw new System.ComponentModel.Win32Exception(result); }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint low; public int high; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint numerator, denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct Source { public Luid adapter; public uint id, mode, flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Target { public Luid adapter; public uint id, mode, technology, rotation, scaling; public Rational refresh; public uint scanline, available, flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayPath { public Source source; public Target target; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint type, size; public Luid adapter; public uint id; }
    [StructLayout(LayoutKind.Sequential)] private struct ColorInfo { public Header header; public uint flags, encoding, bits, activeMode; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths, ref uint modeCount, IntPtr modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref ColorInfo info);
}
