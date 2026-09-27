using System;
using System.Threading;
using BepInEx.Logging;

namespace UnityRemix
{
    public static class RemixWatchdog
    {
        private static Thread watchdogThread;
        private static volatile bool running = false;
        private static ManualLogSource log;

        public static volatile string MainThreadPhase = "None";
        private static long mainThreadHeartbeat = 0;

        public static volatile string RenderThreadPhase = "None";
        private static long renderThreadHeartbeat = 0;

        public static void Start(ManualLogSource logger)
        {
            if (running) return;
            log = logger;
            running = true;
            watchdogThread = new Thread(WatchdogLoop)
            {
                Name = "RemixWatchdog",
                IsBackground = true
            };
            watchdogThread.Start();
            log?.LogInfo("[RemixWatchdog] Thread hang watchdog started (timeout: 2.0s).");
        }

        public static void Stop()
        {
            running = false;
        }

        private static void WatchdogLoop()
        {
            long lastReportMain = 0;
            long lastReportRender = 0;

            while (running)
            {
                try
                {
                    Thread.Sleep(500);
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double freq = System.Diagnostics.Stopwatch.Frequency;

                    long lastMain = Interlocked.Read(ref mainThreadHeartbeat);
                    if (lastMain > 0)
                    {
                        double elapsedMain = (now - lastMain) / freq;
                        if (elapsedMain > 2.0 && (now - lastReportMain) / freq > 1.0)
                        {
                            lastReportMain = now;
                            log?.LogError($"[WATCHDOG] MAIN THREAD HUNG for {elapsedMain:F1}s! Last Phase: '{MainThreadPhase}'");
                        }
                    }

                    long lastRender = Interlocked.Read(ref renderThreadHeartbeat);
                    if (lastRender > 0)
                    {
                        double elapsedRender = (now - lastRender) / freq;
                        if (elapsedRender > 2.0 && (now - lastReportRender) / freq > 1.0)
                        {
                            lastReportRender = now;
                            log?.LogError($"[WATCHDOG] RENDER THREAD HUNG for {elapsedRender:F1}s! Last Phase: '{RenderThreadPhase}'");
                        }
                    }
                }
                catch
                {
                    // Watchdog must not terminate on unexpected exceptions
                }
            }
        }

        public static void BeatMain(string phase)
        {
            MainThreadPhase = phase;
            Interlocked.Exchange(ref mainThreadHeartbeat, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        public static void BeatRender(string phase)
        {
            RenderThreadPhase = phase;
            Interlocked.Exchange(ref renderThreadHeartbeat, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }
}
