using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AnimeJaNaiConfEditor.Services
{
    // A GPU DirectML can upscale on. Integrated is null when Direct3D 12 couldn't tell.
    public sealed record DxgiGpu(string Name, uint VendorId, bool? Integrated);

    // The GPUs for the DirectML GPU choice (Windows only): DXGI's hardware adapters in the order
    // Windows ranks them for high performance, which is the order the player's
    // d3d11-adapter-preference=high-performance (DirectML's Auto) picks from. Each name is listed
    // once, because mpv's --d3d11-adapter selects by name. Integrated graphics are told apart the
    // way libaji does it, by Direct3D 12's UMA flag.
    //
    // COM calls go through the vtables directly (indices from the DXGI and D3D12 headers), so
    // nothing here needs the full interface declarations.
    public static class GpuAdapters
    {
        public static List<DxgiGpu> Enumerate()
        {
            var gpus = new List<DxgiGpu>();
            if (!OperatingSystem.IsWindows())
            {
                return gpus;
            }
            try
            {
                var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref iidFactory1, out var factory) < 0 || factory == IntPtr.Zero)
                {
                    return gpus;
                }
                try
                {
                    // IDXGIFactory6 (Windows 10 1803+) enumerates by preference; older Windows
                    // falls back to plain adapter order
                    var iidFactory6 = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
                    Slot<QueryInterfaceFn>(factory, 0)(factory, ref iidFactory6, out var factory6);
                    try
                    {
                        var iidAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (uint i = 0; ; i++)
                        {
                            IntPtr adapter;
                            int hr = factory6 != IntPtr.Zero
                                ? Slot<EnumAdapterByGpuPreferenceFn>(factory6, 29)(
                                    factory6, i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, ref iidAdapter1, out adapter)
                                : Slot<EnumAdapters1Fn>(factory, 12)(factory, i, out adapter);
                            if (hr < 0 || adapter == IntPtr.Zero)
                            {
                                break; // DXGI_ERROR_NOT_FOUND: no more adapters
                            }
                            try
                            {
                                if (Describe(adapter) is (string name, uint vendor, bool software) &&
                                    !software && name.Length > 0 && seen.Add(name))
                                {
                                    gpus.Add(new DxgiGpu(name, vendor, IsIntegrated(adapter)));
                                }
                            }
                            finally
                            {
                                Release(adapter);
                            }
                        }
                    }
                    finally
                    {
                        if (factory6 != IntPtr.Zero)
                        {
                            Release(factory6);
                        }
                    }
                }
                finally
                {
                    Release(factory);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
            {
            }
            return gpus;
        }

        // DXGI_ADAPTER_DESC1: Description (WCHAR[128]) at 0, VendorId at 256, Flags at 304.
        private static (string, uint, bool)? Describe(IntPtr adapter)
        {
            IntPtr desc = Marshal.AllocHGlobal(320);
            try
            {
                if (Slot<GetDesc1Fn>(adapter, 10)(adapter, desc) < 0)
                {
                    return null;
                }
                string name = (Marshal.PtrToStringUni(desc, 128) ?? "").Split('\0')[0].Trim();
                uint vendor = (uint)Marshal.ReadInt32(desc, 256);
                uint flags = (uint)Marshal.ReadInt32(desc, 304);
                return (name, vendor, (flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0);
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
            }
        }

        // D3D12_FEATURE_DATA_ARCHITECTURE.UMA: memory shared with the CPU, i.e. integrated graphics.
        private static bool? IsIntegrated(IntPtr adapter)
        {
            try
            {
                var iidDevice = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
                if (D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, ref iidDevice, out var device) < 0 ||
                    device == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    IntPtr data = Marshal.AllocHGlobal(16);
                    try
                    {
                        for (int k = 0; k < 16; k += 4)
                        {
                            Marshal.WriteInt32(data, k, 0);
                        }
                        if (Slot<CheckFeatureSupportFn>(device, 13)(device, D3D12_FEATURE_ARCHITECTURE, data, 16) < 0)
                        {
                            return null;
                        }
                        return Marshal.ReadInt32(data, 8) != 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(data);
                    }
                }
                finally
                {
                    Release(device);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        private const int DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE = 2;
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
        private const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
        private const int D3D12_FEATURE_ARCHITECTURE = 1;

        private static T Slot<T>(IntPtr obj, int index) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

        private static void Release(IntPtr obj) => Slot<ReleaseFn>(obj, 2)(obj);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceFn(IntPtr self, ref Guid riid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapterByGpuPreferenceFn(IntPtr self, uint index, int preference, ref Guid riid, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CheckFeatureSupportFn(IntPtr self, int feature, IntPtr data, uint size);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("d3d12.dll")]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, out IntPtr device);
    }
}
