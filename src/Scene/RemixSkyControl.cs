using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// The game's time of day and weather, handed to OpenRemix's NUMOS sky
    /// (remix_sky_ext.h, looked up by name) when the game's profile reports them
    /// (GameProfile.GetSkyTime / GetSkyWeather). NUMOS runs the clock itself from the day's
    /// length; the time is sent again only when the game's drifts from it or jumps. The sky
    /// shows only with OpenRemix's sky on (rtx.native.sky.enabled); its sun is then the scene's.
    /// </summary>
    public sealed class RemixSkyControl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SkyTime
        {
            public uint structSize;
            public uint version;
            public float timeOfDayHours;
            public int dayOfYear;
            public float dayLengthMinutes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SkyLights
        {
            public uint structSize;
            public uint version;
            public uint flags; // 1 sun, 2 moon
            public float sunX, sunY, sunZ;
            public float moonX, moonY, moonZ;
            public float moonPhase;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode SetSkyTimeFn(IntPtr time);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode SetSkyLightsFn(IntPtr lights);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode SetSkyWeatherPresetFn([MarshalAs(UnmanagedType.LPStr)] string preset,
                                                                          float transitionSeconds, float precipitation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode ClearSkyWeatherFn(float transitionSeconds);

        private const float WeatherTransitionSeconds = 8f;
        // How far the game's clock may drift from NUMOS's before it is sent again (hours).
        private const float TimeTolerance = 0.05f;
        // How far the game's sun or moon may turn before it is sent again (cosine): about 0.05
        // degrees, a tenth of the sun's disc.
        private const float LightTolerance = 0.9999996f;

        private readonly ManualLogSource logger;
        private readonly object apiLock;
        private SetSkyTimeFn setTime;
        private SetSkyWeatherPresetFn setWeather;
        private ClearSkyWeatherFn clearWeather;
        // Optional: an OpenRemix without it keeps the clock's sun and Earth's Moon.
        private SetSkyLightsFn setLights;
        private bool resolved, available;

        private bool lightsSent;
        private Vector3 sentSun, sentMoon;

        private bool timeSent;
        private float sentHours, sentDayLength, sentAt;
        private bool weatherSent;
        private string sentPreset;
        private float sentPrecipitation;

        public RemixSkyControl(ManualLogSource logger, object apiLock)
        {
            this.logger = logger;
            this.apiLock = apiLock;
        }

        private bool Resolve()
        {
            if (resolved)
                return available;
            resolved = true;
            IntPtr t = RemixAPI.GetRemixProcAddress("remixapi_SetSkyTimeEXT");
            IntPtr w = RemixAPI.GetRemixProcAddress("remixapi_SetSkyWeatherPresetEXT");
            IntPtr c = RemixAPI.GetRemixProcAddress("remixapi_ClearSkyWeatherEXT");
            available = t != IntPtr.Zero && w != IntPtr.Zero && c != IntPtr.Zero;
            if (!available)
            {
                logger.LogInfo("[Sky] OpenRemix has no sky control (remixapi_SetSkyTimeEXT): the game's time and weather stay its own");
                return false;
            }
            setTime = Marshal.GetDelegateForFunctionPointer<SetSkyTimeFn>(t);
            setWeather = Marshal.GetDelegateForFunctionPointer<SetSkyWeatherPresetFn>(w);
            clearWeather = Marshal.GetDelegateForFunctionPointer<ClearSkyWeatherFn>(c);
            IntPtr l = RemixAPI.GetRemixProcAddress("remixapi_SetSkyLightsEXT");
            if (l != IntPtr.Zero)
                setLights = Marshal.GetDelegateForFunctionPointer<SetSkyLightsFn>(l);
            else
                logger.LogInfo("[Sky] OpenRemix has no remixapi_SetSkyLightsEXT: the sun and moon follow its clock");
            return true;
        }

        // Main thread, each frame.
        public void Update()
        {
            bool hasTime = Games.GameProfiles.GetSkyTime(out float hours, out float dayLength);
            bool hasWeather = Games.GameProfiles.GetSkyWeather(out string preset, out float precipitation);
            bool hasLights = Games.GameProfiles.GetSkyLights(out Vector3 toSun, out Vector3 toMoon);
            if (!hasTime && !hasWeather && !hasLights && !timeSent && !weatherSent && !lightsSent)
                return;
            if (!Resolve())
                return;
            float now = Time.unscaledTime;

            if (hasTime)
            {
                // Where NUMOS's clock is now, from what was sent.
                float expected = sentDayLength > 0f ? sentHours + (now - sentAt) / (sentDayLength * 60f) * 24f : sentHours;
                float drift = Mathf.Abs(Mathf.DeltaAngle(expected * 15f, hours * 15f)) / 15f;
                if (!timeSent || drift > TimeTolerance || Mathf.Abs(dayLength - sentDayLength) > 0.01f)
                    SendTime(hours, dayLength, now);
            }
            else if (timeSent)
            {
                lock (apiLock) setTime(IntPtr.Zero);
                timeSent = false;
            }

            if (setLights != null)
            {
                if (hasLights)
                {
                    toSun = toSun.normalized;
                    toMoon = toMoon.normalized;
                    if (!lightsSent || Turned(toSun, sentSun) || Turned(toMoon, sentMoon))
                        SendLights(toSun, toMoon);
                }
                else if (lightsSent)
                {
                    lock (apiLock) setLights(IntPtr.Zero);
                    lightsSent = false;
                }
            }

            if (hasWeather)
            {
                float q = Mathf.Round(Mathf.Clamp01(precipitation) * 10f) / 10f;
                if (!weatherSent || preset != sentPreset || q != sentPrecipitation)
                {
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock) result = setWeather(preset, WeatherTransitionSeconds, q);
                    weatherSent = true;
                    sentPreset = preset;
                    sentPrecipitation = q;
                    if (VerboseLog.On)
                        logger.LogInfo($"[Sky] weather '{preset}', precipitation {q:0.0}: {result}");
                }
            }
            else if (weatherSent)
            {
                lock (apiLock) clearWeather(WeatherTransitionSeconds);
                weatherSent = false;
                sentPreset = null;
            }
        }

        private static bool Turned(Vector3 now, Vector3 sent) =>
            (now == Vector3.zero) != (sent == Vector3.zero) || Vector3.Dot(now, sent) < LightTolerance;

        private void SendLights(Vector3 toSun, Vector3 toMoon)
        {
            // Remix's world is Z up: (x, y, z) -> (x, z, y), as the lights.
            var lights = new SkyLights
            {
                structSize = (uint)Marshal.SizeOf<SkyLights>(),
                version = 1,
                flags = (toSun != Vector3.zero ? 1u : 0u) | (toMoon != Vector3.zero ? 2u : 0u),
                sunX = toSun.x, sunY = toSun.z, sunZ = toSun.y,
                moonX = toMoon.x, moonY = toMoon.z, moonZ = toMoon.y,
                moonPhase = -1f
            };
            var handle = GCHandle.Alloc(lights, GCHandleType.Pinned);
            RemixAPI.remixapi_ErrorCode result;
            try
            {
                lock (apiLock) result = setLights(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            if (!lightsSent || (VerboseLog.On && Time.frameCount % 600 == 0))
                logger.LogInfo($"[Sky] sun elevation {Mathf.Asin(Mathf.Clamp(toSun.y, -1f, 1f)) * Mathf.Rad2Deg:0.0}, " +
                               $"moon elevation {Mathf.Asin(Mathf.Clamp(toMoon.y, -1f, 1f)) * Mathf.Rad2Deg:0.0}: {result}");
            lightsSent = true;
            sentSun = toSun;
            sentMoon = toMoon;
        }

        private void SendTime(float hours, float dayLength, float now)
        {
            var time = new SkyTime
            {
                structSize = (uint)Marshal.SizeOf<SkyTime>(),
                version = 1,
                timeOfDayHours = Mathf.Repeat(hours, 24f),
                dayOfYear = 0,
                dayLengthMinutes = Mathf.Max(0f, dayLength)
            };
            var handle = GCHandle.Alloc(time, GCHandleType.Pinned);
            RemixAPI.remixapi_ErrorCode result;
            try
            {
                lock (apiLock) result = setTime(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            if (VerboseLog.On || !timeSent)
                logger.LogInfo($"[Sky] time {time.timeOfDayHours:0.00} h, day {time.dayLengthMinutes:0.#} min: {result}");
            timeSent = true;
            sentHours = time.timeOfDayHours;
            sentDayLength = time.dayLengthMinutes;
            sentAt = now;
        }
    }
}
