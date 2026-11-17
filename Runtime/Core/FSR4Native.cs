using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4
{
    /// <summary>Mirrors FfxApiCreateContextUpscaleFlags (ffx_upscale.h).</summary>
    [Flags]
    internal enum FSR4CreateFlags : uint
    {
        None                       = 0,
        HighDynamicRange           = 1 << 0,
        DisplayResolutionMotionVec = 1 << 1,
        MotionVectorJitterCancel   = 1 << 2,
        DepthInverted              = 1 << 3,
        DepthInfinite              = 1 << 4,
        AutoExposure               = 1 << 5,
        DynamicResolution          = 1 << 6,
        DebugChecking              = 1 << 7,
        NonLinearColorSpace        = 1 << 8,
        DebugVisualization         = 1 << 9,
    }

    /// <summary>
    /// Must match FSR4DispatchParams in Native~/src/fsr4_plugin.cpp byte for byte (168 bytes, 8-byte packing).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FSR4DispatchParams
    {
        public int contextId;
        public uint createFlags;
        public ulong versionId;
        public uint maxRenderW, maxRenderH;
        public uint displayW, displayH;
        public uint renderW, renderH;
        public uint dispatchFlags;
        public int enableSharpening;
        public int reset;
        public int pad0;
        public float jitterX, jitterY;
        public float mvScaleX, mvScaleY;
        public float sharpness;
        public float frameTimeMs;
        public float preExposure;
        public float cameraNear, cameraFar, cameraFovY;
        public float viewSpaceToMeters;
        public float pad1;
        /// <summary>Bit i = apply tuning i: 0 velocityFactor, 1 reactivenessScale, 2 shadingChangeScale, 3 accumulationAddedPerFrame, 4 minDisocclusionAccumulation.</summary>
        public int tuningMask;
        public float tuning0, tuning1, tuning2, tuning3, tuning4;
        public IntPtr color, depth, motionVectors, exposure, output;
    }

    internal readonly struct FSR4Version
    {
        public readonly ulong id;
        public readonly string name;
        public FSR4Version(ulong id, string name) { this.id = id; this.name = name; }
    }

    /// <summary>P/Invoke surface of FSR4UnityPlugin.dll plus the plugin-event plumbing.</summary>
    internal static unsafe class FSR4Native
    {
        const string Dll = "FSR4UnityPlugin";

        public const int EventDispatch = 1;
        public const int EventDestroy = 2;

        // Parameter blocks are read by the render thread some time after we issue the event, so they live in a ring of
        // unmanaged slots instead of on the stack / in a managed array. 32 is far more than any frame can have in flight.
        const int RingSize = 32;
        static IntPtr s_Ring;
        static int s_RingIndex;
        static IntPtr s_RenderEventFunc;

        [DllImport(Dll)] static extern IntPtr FSR4_GetRenderEventFunc();
        [DllImport(Dll)] static extern int FSR4_AllocContextId();
        [DllImport(Dll)] static extern int FSR4_GetStatus();
        [DllImport(Dll)] static extern int FSR4_GetStructSize();
        [DllImport(Dll)] static extern int FSR4_GetLastMessage(byte* buffer, int bufferLength);
        [DllImport(Dll)] static extern int FSR4_GetVersions(ulong* ids, byte* names, int maxCount, int nameLength);
        [DllImport(Dll)] static extern int FSR4_Probe(uint createFlags, ulong versionId);
        [DllImport(Dll)] static extern int FSR4_GetContextState(int contextId);
        [DllImport(Dll)] static extern void FSR4_GetDispatchCounts(ulong* attempts, ulong* executed);
        [DllImport(Dll)] static extern int FSR4_GetContextVersion(int contextId, byte* buffer, int bufferLength);
        [DllImport(Dll)] static extern int FSR4_GetContextMemory(int contextId, ulong* totalBytes, ulong* aliasableBytes);
        [DllImport(Dll)] static extern int FSR4_GetBackend();

        /// <summary>True when the plugin loaded, we are on D3D12 or Vulkan and the AMD DLLs could be found.</summary>
        public static bool TryInitialize(out string error)
        {
            error = null;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            {
                error = $"FSR4 needs the Direct3D12 or Vulkan graphics API (current: {SystemInfo.graphicsDeviceType}).";
                return false;
            }

            try
            {
                if (FSR4_GetStructSize() != sizeof(FSR4DispatchParams))
                {
                    error = $"FSR4UnityPlugin.dll is out of date (struct size {FSR4_GetStructSize()} vs {sizeof(FSR4DispatchParams)}). Rebuild it.";
                    return false;
                }

                int status = FSR4_GetStatus();
                if (status == 1)
                {
                    error = "FSR4UnityPlugin loaded, but it has not seen the graphics device. Is the plugin set to 'Load on startup' and is the Editor/Player running with D3D12 or Vulkan?";
                    return false;
                }
                if (status == 2)
                {
                    error = "Could not load the AMD FidelityFX DLLs. " + LastMessage();
                    return false;
                }

                s_RenderEventFunc = FSR4_GetRenderEventFunc();
                return true;
            }
            catch (DllNotFoundException e)
            {
                error = "FSR4UnityPlugin.dll was not found - build it (see Documentation~/Building-the-native-plugins.md) and copy it next to the AMD DLLs. " + e.Message;
                return false;
            }
            catch (EntryPointNotFoundException e)
            {
                error = "FSR4UnityPlugin.dll is missing an export - rebuild it. " + e.Message;
                return false;
            }
        }

        public static int AllocContextId() => FSR4_AllocContextId();

        /// <summary>Synchronously tries to create (and destroy) a tiny context. Returns 0 on success.</summary>
        public static int Probe(FSR4CreateFlags flags, ulong versionId) => FSR4_Probe((uint)flags, versionId);

        public static string LastMessage()
        {
            byte* buf = stackalloc byte[512];
            int len = FSR4_GetLastMessage(buf, 512);
            return len <= 0 ? string.Empty : Encoding.UTF8.GetString(buf, Math.Min(len, 511));
        }

        public static List<FSR4Version> GetVersions()
        {
            const int Max = 16, NameLen = 96;
            var result = new List<FSR4Version>();
            ulong* ids = stackalloc ulong[Max];
            byte* names = stackalloc byte[Max * NameLen];
            int count = FSR4_GetVersions(ids, names, Max, NameLen);
            for (int i = 0; i < count && i < Max; i++)
            {
                byte* n = names + i * NameLen;
                int len = 0;
                while (len < NameLen && n[len] != 0) len++;
                result.Add(new FSR4Version(ids[i], Encoding.UTF8.GetString(n, len)));
            }
            return result;
        }

        public static int GetContextState(int contextId) => FSR4_GetContextState(contextId);

        /// <summary>"Direct3D12", "Vulkan" or "none" - which backend the native plugin attached to.</summary>
        public static string BackendName()
        {
            try { return FSR4_GetBackend() switch { 1 => "Direct3D12", 2 => "Vulkan", _ => "none" }; }
            catch (Exception) { return "unknown"; }
        }

        /// <summary>GPU memory of the most recently dispatched context. False if unknown / old plugin.</summary>
        public static bool TryGetContextMemory(out ulong totalBytes, out ulong aliasableBytes)
        {
            totalBytes = aliasableBytes = 0;
            if (s_LastContextId == 0) return false;
            try
            {
                ulong t, a;
                if (FSR4_GetContextMemory(s_LastContextId, &t, &a) == 0) return false;
                totalBytes = t; aliasableBytes = a;
                return true;
            }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }
        }

        static int s_LastContextId;

        /// <summary>Provider name of the most recently dispatched context (debug HUD).</summary>
        public static string TryGetAnyContextVersion()
        {
            try { return s_LastContextId != 0 ? GetContextVersion(s_LastContextId) : string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>Native-side counters. False when the loaded plugin predates them (rebuild it).</summary>
        public static bool TryGetDispatchCounts(out ulong attempts, out ulong executed)
        {
            attempts = executed = 0;
            try
            {
                ulong a, e;
                FSR4_GetDispatchCounts(&a, &e);
                attempts = a; executed = e;
                return true;
            }
            catch (EntryPointNotFoundException) { return false; }
            catch (DllNotFoundException) { return false; }
        }

        public static string GetContextVersion(int contextId)
        {
            byte* buf = stackalloc byte[96];
            int len = FSR4_GetContextVersion(contextId, buf, 96);
            return len <= 0 ? string.Empty : Encoding.UTF8.GetString(buf, Math.Min(len, 95));
        }

        public static void Dispatch(CommandBuffer cmd, in FSR4DispatchParams p)
        {
            if (s_Ring == IntPtr.Zero)
                s_Ring = Marshal.AllocHGlobal(sizeof(FSR4DispatchParams) * RingSize);

            IntPtr slot = s_Ring + (s_RingIndex % RingSize) * sizeof(FSR4DispatchParams);
            s_RingIndex++;
            *(FSR4DispatchParams*)slot = p;
            s_LastContextId = p.contextId;
            cmd.IssuePluginEventAndData(s_RenderEventFunc, EventDispatch, slot);
        }

        public static void DestroyContext(CommandBuffer cmd, int contextId)
        {
            if (s_RenderEventFunc == IntPtr.Zero)
                return;
            cmd.IssuePluginEventAndData(s_RenderEventFunc, EventDestroy, (IntPtr)contextId);
        }
    }
}
