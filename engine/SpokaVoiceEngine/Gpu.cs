// SpokaVoiceEngine - real-time voice conversion for SpokaSoundBoard.
// Copyright (C) 2026 Spokator.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace SpokaVoiceEngine;

/// <summary>A graphics adapter as DXGI lists it (DirectML's device id is the index in that list).</summary>
public sealed record GpuAdapter(int Index, string Name, long DedicatedMemory, uint VendorId, bool IsSoftware)
{
    /// <summary>
    /// A real graphics card: not a software renderer. In a virtual machine or a remote session, Windows lists
    /// « Microsoft Basic Render Driver » (vendor 0x1414, no memory of its own) without the software flag.
    /// </summary>
    public bool IsUsable => !IsSoftware && VendorId != 0x1414 && DedicatedMemory > 64L << 20;
}

/// <summary>Picks the graphics card for DirectML: the one with the most dedicated memory (the gaming GPU on laptops).</summary>
public static unsafe class Gpu
{
    private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    public static List<GpuAdapter> List()
    {
        var result = new List<GpuAdapter>();
        if (CreateDXGIFactory1(DxgiFactory1, out var factory) < 0 || factory == IntPtr.Zero) return result;
        try
        {
            var vtable = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtable[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break; // DXGI_ERROR_NOT_FOUND ends the list
                try
                {
                    var avt = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)avt[10];
                    var size = Marshal.SizeOf<AdapterDesc1>();
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (getDesc1(adapter, buffer) >= 0)
                        {
                            var d = Marshal.PtrToStructure<AdapterDesc1>(buffer);
                            result.Add(new GpuAdapter((int)i, d.Description.Trim(), (long)d.DedicatedVideoMemory, d.VendorId, (d.Flags & 2) != 0));
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        return result;
    }

    public static GpuAdapter? Best() =>
        List().Where(a => a.IsUsable).OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();
}
