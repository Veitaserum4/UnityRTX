using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Publishes the static scene OpenRemix's Forward+ renderer bakes its lightmaps, probes and
    /// reflection captures from (remixapi_SetBakeSceneEXT). Without one, Forward+ waits for map
    /// inputs and shades with live lights only. Render thread only.
    ///
    /// The scene is the retained static instances drawn now (unmoved) plus the static lights
    /// (RemixLightConverter settles level lights) and the sky dome. It is republished, as a new
    /// revision, once a changed set has held still for StableChecks checks, so streaming and
    /// rooms toggling on and off do not restart the bake every frame. OpenRemix caches bakes
    /// by content, so a set seen before comes back from its cache.
    ///
    /// Bake once: after a level's first publish the scene is not republished until the next
    /// scene load (Reset): rooms the game toggles on and off, and lights settling in rooms
    /// visited later, would otherwise restart the bake each time. The published lights are
    /// kept alive as baked (RemixLightConverter.FreezeBakeLights); OpenRemix keeps its own
    /// snapshot of the instances.
    ///
    /// The runtime refuses a whole scene for one ineligible input (an animated or still-loading
    /// material, a dynamic texture, a second sun). A refused scene is bisected to find those
    /// inputs, which are left out and retried later with a growing back-off.
    /// </summary>
    internal sealed class RemixBakeScene
    {
        private const int CheckInterval = 30;
        private const int StableChecks = 6;
        private const int RetryFrames = 600, MaxRetryFrames = 36000;
        private const int ChunkSize = 64, DiagnoseCallBudget = 512;

        private readonly ManualLogSource logger;
        private ulong sceneId = 1;
        private string sceneName = "";
        private volatile string pendingSceneName;
        private ulong revision;
        private bool published;
        private ulong publishedSignature, candidateSignature;
        private int stableChecks;
        private bool wasForwardPlus;

        private struct Refusal { public int retryFrame, backoff; }
        private readonly Dictionary<ulong, Refusal> refused = new Dictionary<ulong, Refusal>();
        private readonly List<ulong> instances = new List<ulong>(4096);
        private readonly List<IntPtr> lights = new List<IntPtr>(128);
        private ulong[] instanceBuffer = new ulong[4096];
        private IntPtr[] lightBuffer = new IntPtr[128];
        private int diagnoseCalls;

        private uint lastState = uint.MaxValue;
        private string lastStatus;
        private int lastProgressLogFrame;

        public RemixBakeScene(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public static bool Available => RemixAPI.SetBakeSceneEXTFunc != null;

        /// <summary>The level's scene is published: its lights are the baked ones (render thread).</summary>
        public bool Published => published;

        /// <summary>The lights of the published scene (valid while Published).</summary>
        public List<IntPtr> PublishedLights => lights;

        /// <summary>Main thread, on a single scene load: names the next bake scene.</summary>
        public void SetSceneName(string name) => pendingSceneName = name ?? "";

        /// <summary>Whether this frame checks the scene; the caller then fills the inputs.</summary>
        public bool ShouldCheck(int frame) => Available && frame % CheckInterval == 0;

        /// <summary>
        /// Called before the retained instances are cleared (scene change): the previous
        /// scene is withdrawn, so Forward+ waits for the new one.
        /// </summary>
        public void Reset()
        {
            if (published)
                Call(RemixAPI.REMIXAPI_BAKE_SCENE_LOADING_EXT, 0, 0);
            published = false;
            publishedSignature = candidateSignature = 0;
            stableChecks = 0;
            refused.Clear();
        }

        /// <summary>
        /// One check: candidate static instances (retained handles drawn now, never moved),
        /// static lights and the dome light (zero if none).
        /// </summary>
        public void Check(int frame, List<ulong> candidateInstances, List<IntPtr> staticLights, IntPtr domeLight)
        {
            var name = pendingSceneName;
            if (name != null)
            {
                pendingSceneName = null;
                sceneName = name;
                sceneId = Fnv(name) | 1UL;
            }

            bool forwardPlus = IsForwardPlus();
            if (forwardPlus != wasForwardPlus)
            {
                wasForwardPlus = forwardPlus;
                logger.LogInfo(forwardPlus
                    ? "[Bake] Forward+ active: publishing the static scene for baking"
                    : "[Bake] Forward+ not active: no bake scene");
                stableChecks = 0;
            }
            if (!forwardPlus)
                return;

            instances.Clear();
            for (int i = 0; i < candidateInstances.Count; i++)
                if (!IsRefused(candidateInstances[i], frame))
                    instances.Add(candidateInstances[i]);
            lights.Clear();
            for (int i = 0; i < staticLights.Count; i++)
                if (!IsRefused(Key(staticLights[i]), frame))
                    lights.Add(staticLights[i]);
            if (domeLight != IntPtr.Zero && !IsRefused(Key(domeLight), frame))
                lights.Add(domeLight);
            instances.Sort();
            lights.Sort((a, b) => Key(a).CompareTo(Key(b)));
            for (int i = lights.Count - 1; i > 0; i--)
                if (lights[i] == lights[i - 1])
                    lights.RemoveAt(i); // a handle listed twice refuses the scene

            LogStatus(frame);

            if (published)
                return; // bake once: kept until the scene changes (Reset)
            if (instances.Count == 0)
                return;
            ulong signature = Signature();
            if (published && signature == publishedSignature)
            {
                stableChecks = 0;
                return;
            }
            if (signature != candidateSignature)
            {
                candidateSignature = signature;
                stableChecks = 1;
                return;
            }
            if (++stableChecks < StableChecks)
                return;
            stableChecks = 0;
            Publish(frame);
        }

        private void Publish(int frame)
        {
            int count = instances.Count, lightCount = lights.Count;
            var result = CallReady(instances, 0, count, lights, 0, lightCount);
            if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                diagnoseCalls = 0;
                var badLights = new List<int>();
                var badInstances = new List<int>();
                if (lightCount > 0 && CallReady(instances, 0, 0, lights, 0, lightCount) != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    for (int i = 0; i < lightCount && diagnoseCalls < DiagnoseCallBudget; i++)
                        if (CallReady(instances, 0, 0, lights, i, 1) != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                            badLights.Add(i);
                for (int first = 0; first < count && diagnoseCalls < DiagnoseCallBudget; first += ChunkSize)
                    Bisect(first, Math.Min(ChunkSize, count - first), badInstances);

                for (int i = badLights.Count - 1; i >= 0; i--)
                {
                    Refuse(Key(lights[badLights[i]]), frame);
                    lights.RemoveAt(badLights[i]);
                }
                for (int i = badInstances.Count - 1; i >= 0; i--)
                {
                    Refuse(instances[badInstances[i]], frame);
                    instances.RemoveAt(badInstances[i]);
                }
                count = instances.Count;
                lightCount = lights.Count;
                result = count > 0 ? CallReady(instances, 0, count, lights, 0, lightCount) : RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS;
                logger.LogWarning($"[Bake] scene refused; left out {badInstances.Count} instances and {badLights.Count} lights " +
                                  $"({diagnoseCalls} test calls, {RemixProfiler.ElapsedMs(start):F0} ms), retry -> {result}");
            }

            if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                published = true;
                publishedSignature = Signature();
                logger.LogInfo($"[Bake] published '{sceneName}' revision {revision}: {count} instances, {lightCount} lights");
            }
            else
            {
                // Test calls may have left a partial scene in place: withdraw it.
                Call(RemixAPI.REMIXAPI_BAKE_SCENE_LOADING_EXT, 0, 0);
                published = false;
                logger.LogWarning($"[Bake] could not publish '{sceneName}': {result}");
            }
        }

        // Finds the instances in [first, first + length) the runtime refuses on their own.
        private void Bisect(int first, int length, List<int> bad)
        {
            if (diagnoseCalls >= DiagnoseCallBudget)
                return;
            if (CallReady(instances, first, length, lights, 0, 0) == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                return;
            if (length == 1)
            {
                bad.Add(first);
                return;
            }
            int half = length / 2;
            Bisect(first, half, bad);
            Bisect(first + half, length - half, bad);
        }

        private bool IsRefused(ulong key, int frame)
        {
            return refused.TryGetValue(key, out var r) && frame < r.retryFrame;
        }

        private void Refuse(ulong key, int frame)
        {
            int backoff = refused.TryGetValue(key, out var r) ? Math.Min(r.backoff * 2, MaxRetryFrames) : RetryFrames;
            refused[key] = new Refusal { retryFrame = frame + backoff, backoff = backoff };
        }

        private RemixAPI.remixapi_ErrorCode CallReady(List<ulong> instanceList, int first, int count, List<IntPtr> lightList, int firstLight, int lightCount)
        {
            diagnoseCalls++;
            if (instanceBuffer.Length < count)
                instanceBuffer = new ulong[Math.Max(count, instanceBuffer.Length * 2)];
            if (lightBuffer.Length < lightCount)
                lightBuffer = new IntPtr[Math.Max(lightCount, lightBuffer.Length * 2)];
            instanceList.CopyTo(first, instanceBuffer, 0, count);
            lightList.CopyTo(firstLight, lightBuffer, 0, lightCount);
            revision++;
            return Call(RemixAPI.REMIXAPI_BAKE_SCENE_READY_EXT, (uint)count, (uint)lightCount);
        }

        private unsafe RemixAPI.remixapi_ErrorCode Call(uint state, uint count, uint lightCount)
        {
            if (state == RemixAPI.REMIXAPI_BAKE_SCENE_LOADING_EXT)
                revision++;
            fixed (ulong* pInstances = instanceBuffer)
            fixed (IntPtr* pLights = lightBuffer)
            {
                var info = new RemixAPI.remixapi_BakeSceneInfoEXT
                {
                    structSize = (uint)sizeof(RemixAPI.remixapi_BakeSceneInfoEXT),
                    version = RemixAPI.REMIXAPI_BAKE_SCENE_VERSION_EXT,
                    sceneId = sceneId,
                    revision = revision,
                    sceneScale = 1f, // Unity units are metres
                    state = state,
                    instanceCount = count,
                    lightCount = lightCount,
                    instances = count > 0 ? (IntPtr)pInstances : IntPtr.Zero,
                    lights = lightCount > 0 ? (IntPtr)pLights : IntPtr.Zero
                };
                try
                {
                    return RemixAPI.SetBakeSceneEXTFunc((IntPtr)(&info));
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Bake] SetBakeSceneEXT threw: {ex.Message}");
                    return RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_GENERAL_FAILURE;
                }
            }
        }

        private static unsafe bool IsForwardPlus()
        {
            if (RemixAPI.GetBackendCapabilitiesEXTFunc == null)
                return false;
            var caps = new RemixAPI.remixapi_BackendCapabilitiesEXT { structSize = (uint)sizeof(RemixAPI.remixapi_BackendCapabilitiesEXT) };
            try
            {
                return RemixAPI.GetBackendCapabilitiesEXTFunc(ref caps) == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS &&
                       caps.activeBackend == RemixAPI.REMIXAPI_BACKEND_FORWARD_PLUS;
            }
            catch
            {
                return false;
            }
        }

        // Bake progress in the log: every state or status change, and progress while tracing.
        private unsafe void LogStatus(int frame)
        {
            if (RemixAPI.GetBakeStatisticsEXTFunc == null)
                return;
            var stats = new RemixAPI.remixapi_BakeStatisticsEXT { structSize = (uint)sizeof(RemixAPI.remixapi_BakeStatisticsEXT) };
            if (RemixAPI.GetBakeStatisticsEXTFunc(ref stats) != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                return;
            int length = 0;
            while (length < 192 && stats.status[length] != 0) length++;
            string status = Encoding.UTF8.GetString(stats.status, length);
            bool progress = stats.state == RemixAPI.REMIXAPI_BAKE_TRACING_EXT && frame - lastProgressLogFrame >= 600;
            if (stats.state == lastState && status == lastStatus && !progress)
                return;
            lastState = stats.state;
            lastStatus = status;
            lastProgressLogFrame = frame;
            logger.LogInfo($"[Bake] {StateName(stats.state)}: {status} (probes {stats.completed}/{stats.probes}, " +
                           $"{stats.memoryBytes / (1024 * 1024)} MB, active {stats.active}, reflections requested {stats.reflectionRequested} " +
                           $"pending {stats.reflectionPending} active {stats.reflectionActive} failed {stats.reflectionFailed})");
        }

        private static string StateName(uint state)
        {
            switch (state)
            {
                case RemixAPI.REMIXAPI_BAKE_WAITING_EXT: return "waiting";
                case RemixAPI.REMIXAPI_BAKE_PREPARING_EXT: return "preparing";
                case RemixAPI.REMIXAPI_BAKE_TRACING_EXT: return "tracing";
                case RemixAPI.REMIXAPI_BAKE_READY_EXT: return "ready";
                case RemixAPI.REMIXAPI_BAKE_FAILED_EXT: return "failed";
                case RemixAPI.REMIXAPI_BAKE_UNSUPPORTED_EXT: return "unsupported";
                case RemixAPI.REMIXAPI_BAKE_DISABLED_EXT: return "disabled";
                default: return $"state {state}";
            }
        }

        private ulong Signature()
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < instances.Count; i++)
                h = (h ^ instances[i]) * 1099511628211UL;
            h = (h ^ 0xB4CE) * 1099511628211UL;
            for (int i = 0; i < lights.Count; i++)
                h = (h ^ Key(lights[i])) * 1099511628211UL;
            return h;
        }

        private static ulong Key(IntPtr handle) => (ulong)handle.ToInt64();

        private static ulong Fnv(string text)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++)
                h = (h ^ text[i]) * 1099511628211UL;
            return h;
        }
    }
}
