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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode SetSkyTimeFn(IntPtr time);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode SetSkyWeatherPresetFn([MarshalAs(UnmanagedType.LPStr)] string preset,
                                                                          float transitionSeconds, float precipitation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode ClearSkyWeatherFn(float transitionSeconds);

        private const float WeatherTransitionSeconds = 8f;
        // How far the game's clock may drift from NUMOS's before it is sent again (hours).
        private const float TimeTolerance = 0.05f;

        private readonly ManualLogSource logger;
        private readonly object apiLock;
        private SetSkyTimeFn setTime;
        private SetSkyWeatherPresetFn setWeather;
        private ClearSkyWeatherFn clearWeather;
        private bool resolved, available;

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
            return true;
        }

        // Main thread, each frame.
        public void Update()
        {
            bool hasTime = Games.GameProfiles.GetSkyTime(out float hours, out float dayLength);
            bool hasWeather = Games.GameProfiles.GetSkyWeather(out string preset, out float precipitation);
            if (!hasTime && !hasWeather && !timeSent && !weatherSent)
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
