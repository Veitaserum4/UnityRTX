using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityRemix
{
    /// <summary>
    /// Real-time profiling integration using Tracy Profiler.
    /// Connects to Tracy Profiler GUI (v0.8.x) on localhost:8086.
    /// Uses persistent native source location structures matching C++ Tracy.
    /// </summary>
    public static class RemixTracy
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ZoneContext
        {
            public uint id;
            public int active;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceLocationData
        {
            public IntPtr name;
            public IntPtr function;
            public IntPtr file;
            public uint line;
            public uint color;
        }

        public readonly struct ZoneScope : IDisposable
        {
            private readonly ZoneContext _ctx;
            private readonly bool _active;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ZoneScope(ZoneContext ctx)
            {
                _ctx = ctx;
                _active = ctx.active != 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose()
            {
                if (_active && _isAvailable)
                {
                    try
                    {
                        ___tracy_emit_zone_end(_ctx);
                    }
                    catch { }
                }
            }
        }

        #region Native Imports

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("TracyClient.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ___tracy_connected();

        [DllImport("TracyClient.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ___tracy_emit_frame_mark(IntPtr name);

        [DllImport("TracyClient.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern ZoneContext ___tracy_emit_zone_begin(IntPtr srcloc, int active);

        [DllImport("TracyClient.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ___tracy_emit_zone_end(ZoneContext ctx);

        [DllImport("TracyClient.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ___tracy_set_thread_name(byte[] name);

        [DllImport("TracyClient.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ___tracy_emit_plot(byte[] name, double val);

        #endregion

        private static bool _isAvailable = false;
        private static bool _initialized = false;
        private static readonly ConcurrentDictionary<string, IntPtr> _srclocCache = new ConcurrentDictionary<string, IntPtr>();

        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _isAvailable;
            }
        }

        public static bool IsConnected
        {
            get
            {
                if (!IsAvailable) return false;
                try { return ___tracy_connected() != 0; }
                catch { return false; }
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                IntPtr handle = LoadLibrary("TracyClient.dll");
                if (handle != IntPtr.Zero)
                {
                    _isAvailable = true;
                }
            }
            catch
            {
                _isAvailable = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetThreadName(string name)
        {
            if (!IsAvailable) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
                ___tracy_set_thread_name(bytes);
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FrameMark()
        {
            if (!IsAvailable) return;
            try
            {
                ___tracy_emit_frame_mark(IntPtr.Zero);
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Plot(string name, double value)
        {
            if (!IsAvailable) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
                ___tracy_emit_plot(bytes, value);
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ZoneScope Zone(
            string name,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            if (!IsAvailable) return default;

            try
            {
                string cacheKey = name;
                if (!_srclocCache.TryGetValue(cacheKey, out IntPtr locPtr))
                {
                    var loc = new SourceLocationData
                    {
                        name = Marshal.StringToHGlobalAnsi(name ?? ""),
                        function = Marshal.StringToHGlobalAnsi(member ?? ""),
                        file = Marshal.StringToHGlobalAnsi(file ?? ""),
                        line = (uint)line,
                        color = 0
                    };

                    locPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SourceLocationData>());
                    Marshal.StructureToPtr(loc, locPtr, false);
                    _srclocCache[cacheKey] = locPtr;
                }

                ZoneContext ctx = ___tracy_emit_zone_begin(locPtr, 1);
                return new ZoneScope(ctx);
            }
            catch
            {
                return default;
            }
        }
    }
}
