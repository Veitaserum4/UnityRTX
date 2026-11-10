using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Forwards OpenRemix's SDL_Log output (Vulkan failures, device loss, pipeline and bake
    /// messages) to the BepInEx log as [OpenRemix] lines. remix.dll logs through the SDL3.dll
    /// beside it, whose default output is not captured in a game process. Messages arrive on
    /// any native thread.
    /// </summary>
    internal static class NativeLogForwarder
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SDL_LogOutputFunction(IntPtr userdata, int category, int priority, IntPtr message);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SDL_SetLogOutputFunctionFn(SDL_LogOutputFunction callback, IntPtr userdata);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private const int MaxMessages = 5000;
        private static SDL_LogOutputFunction callback; // kept alive while native code holds it
        private static ManualLogSource log;
        private static int count;

        public static void Install()
        {
            if (callback != null)
                return;
            try
            {
                IntPtr sdl = GetModuleHandleW("SDL3.dll");
                if (sdl == IntPtr.Zero)
                    return;
                IntPtr set = GetProcAddress(sdl, "SDL_SetLogOutputFunction");
                if (set == IntPtr.Zero)
                    return;
                log = Logger.CreateLogSource("OpenRemix");
                callback = Forward;
                Marshal.GetDelegateForFunctionPointer<SDL_SetLogOutputFunctionFn>(set)(callback, IntPtr.Zero);
                log.LogInfo("SDL log output forwarded to BepInEx");
            }
            catch (Exception ex)
            {
                BepInEx.Logging.Logger.CreateLogSource("OpenRemix").LogWarning($"SDL log forwarding unavailable: {ex.Message}");
            }
        }

        private static void Forward(IntPtr userdata, int category, int priority, IntPtr message)
        {
            try
            {
                int n = System.Threading.Interlocked.Increment(ref count);
                if (n > MaxMessages && priority < 6) // past the cap only errors (SDL3: WARN = 5, ERROR = 6)
                    return;
                string text = message != IntPtr.Zero ? Marshal.PtrToStringAnsi(message) : "";
                if (priority >= 6) log.LogError(text);
                else if (priority == 5) log.LogWarning(text);
                else log.LogInfo(text);
            }
            catch
            {
                // Never throw into native code.
            }
        }
    }
}
