using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityRemix
{
    /// <summary>
    /// Interacts with ULTRAKILL's GameStateManager to cleanly unlock/lock mouse cursor,
    /// player movement/shooting, and camera rotation when the RTX Remix menu (ImGui) opens and closes.
    /// Uses reflection and Harmony so the bridge compiles and runs cleanly even if GameStateManager is absent.
    /// </summary>
    public static class RemixGameStateHelper
    {
        private static bool typesInitialized = false;
        private static Type gsmType;
        private static MethodInfo gsmInstanceProp;
        private static MethodInfo registerStateMethod;
        private static MethodInfo popStateMethod;
        private static MethodInfo setCursorLockedMethod;
        private static MethodInfo setCameraLockedMethod;
        private static MethodInfo setPlayerInputLockedMethod;
        private static FieldInfo stateOrderField;
        private static FieldInfo activeStatesField;

        private static Type gameStateType;
        private static ConstructorInfo gameStateCtor;
        private static FieldInfo cursorLockField;
        private static FieldInfo cameraInputLockField;
        private static FieldInfo playerInputLockField;
        private static FieldInfo priorityField;

        private static bool isStateRegistered = false;
        private static bool wasCursorLocked = true;
        private static CursorLockMode previousLockMode = CursorLockMode.Locked;
        private static bool previousCursorVisible = false;

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private static void InitializeTypes()
        {
            if (typesInitialized) return;
            typesInitialized = true;

            try
            {
                gsmType = Type.GetType("GameStateManager, Assembly-CSharp");
                if (gsmType != null)
                {
                    gsmInstanceProp = gsmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                    registerStateMethod = gsmType.GetMethod("RegisterState", BindingFlags.Public | BindingFlags.Instance);
                    popStateMethod = gsmType.GetMethod("PopState", BindingFlags.Public | BindingFlags.Instance);
                    setCursorLockedMethod = gsmType.GetMethod("set_CursorLocked", BindingFlags.NonPublic | BindingFlags.Instance);
                    setCameraLockedMethod = gsmType.GetMethod("set_CameraLocked", BindingFlags.NonPublic | BindingFlags.Instance);
                    setPlayerInputLockedMethod = gsmType.GetMethod("set_PlayerInputLocked", BindingFlags.NonPublic | BindingFlags.Instance);
                    stateOrderField = gsmType.GetField("stateOrder", BindingFlags.NonPublic | BindingFlags.Instance);
                    activeStatesField = gsmType.GetField("activeStates", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                gameStateType = Type.GetType("GameState, Assembly-CSharp");
                if (gameStateType != null)
                {
                    gameStateCtor = gameStateType.GetConstructor(new Type[] { typeof(string) });
                    cursorLockField = gameStateType.GetField("cursorLock");
                    cameraInputLockField = gameStateType.GetField("cameraInputLock");
                    playerInputLockField = gameStateType.GetField("playerInputLock");
                    priorityField = gameStateType.GetField("priority");
                }
            }
            catch { }
        }

        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            InitializeTypes();
            if (gsmType != null && harmony != null)
            {
                try
                {
                    var evalMethod = gsmType.GetMethod("EvaluateState", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (evalMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(RemixGameStateHelper), nameof(EvaluateStatePrefix));
                        harmony.Patch(evalMethod, prefix: prefix);
                        logger?.LogInfo("[RemixGameStateHelper] Successfully patched GameStateManager.EvaluateState with safe defaults prefix.");
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"[RemixGameStateHelper] Failed to patch GameStateManager.EvaluateState: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// ULTRAKILL's EvaluateState loop only modifies CursorLocked/CameraLocked/PlayerInputLocked
        /// if a state in stateOrder explicitly specifies them. If stateOrder is empty (or states don't specify them),
        /// GameStateManager retains stale locked values! This prefix sets safe baseline defaults prior to evaluation.
        /// </summary>
        private static void EvaluateStatePrefix(object __instance)
        {
            try
            {
                bool inGameplay = IsInGameplay();
                setCameraLockedMethod?.Invoke(__instance, new object[] { false });
                setPlayerInputLockedMethod?.Invoke(__instance, new object[] { false });
                setCursorLockedMethod?.Invoke(__instance, new object[] { inGameplay });
            }
            catch { }
        }

        public static bool IsInGameplay()
        {
            try
            {
                string sceneName = SceneManager.GetActiveScene().name;
                if (string.IsNullOrEmpty(sceneName) || sceneName == "Main Menu" || sceneName == "Intro" || sceneName == "Bootstrap")
                    return false;

                Type ccType = Type.GetType("CameraController, Assembly-CSharp");
                if (ccType != null)
                {
                    var instProp = ccType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instProp != null && instProp.GetValue(null, null) != null)
                        return true;
                }
            }
            catch { }
            return false;
        }

        public static void SetRemixMenuState(bool open, ManualLogSource logger = null)
        {
            InitializeTypes();

            if (open)
            {
                wasCursorLocked = (Cursor.lockState == CursorLockMode.Locked);
                previousLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;

                ClipCursor(IntPtr.Zero);
                ReleaseCapture();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                if (!isStateRegistered && gsmType != null && gameStateType != null && gameStateCtor != null)
                {
                    try
                    {
                        object gsmInstance = gsmInstanceProp?.Invoke(null, null);
                        if (gsmInstance != null)
                        {
                            object state = gameStateCtor.Invoke(new object[] { "RemixMenu" });
                            // ULTRAKILL LockMode enum: 0 = None, 1 = Lock, 2 = Unlock
                            cursorLockField?.SetValue(state, Enum.ToObject(cursorLockField.FieldType, 2 /* Unlock */));
                            cameraInputLockField?.SetValue(state, Enum.ToObject(cameraInputLockField.FieldType, 1 /* Lock */));
                            playerInputLockField?.SetValue(state, Enum.ToObject(playerInputLockField.FieldType, 1 /* Lock */));
                            priorityField?.SetValue(state, 1000);

                            registerStateMethod?.Invoke(gsmInstance, new object[] { state });
                            isStateRegistered = true;
                            logger?.LogInfo("[RemixGameStateHelper] Registered 'RemixMenu' GameState: cursor unlocked, camera & player input locked.");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[RemixGameStateHelper] Failed to register GameState: {ex.Message}");
                    }
                }
            }
            else
            {
                if (isStateRegistered && gsmType != null && popStateMethod != null)
                {
                    try
                    {
                        object gsmInstance = gsmInstanceProp?.Invoke(null, null);
                        if (gsmInstance != null)
                        {
                            popStateMethod.Invoke(gsmInstance, new object[] { "RemixMenu" });
                            isStateRegistered = false;
                            logger?.LogInfo("[RemixGameStateHelper] Popped 'RemixMenu' GameState: game controls and cursor restored.");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[RemixGameStateHelper] Failed to pop GameState: {ex.Message}");
                    }
                }

                bool inGameplay = IsInGameplay();
                bool shouldLockCursor = inGameplay;
                bool shouldLockCamera = false;
                bool shouldLockPlayerInput = false;

                // Inspect any remaining states in GameStateManager
                if (gsmType != null)
                {
                    try
                    {
                        object gsmInstance = gsmInstanceProp?.Invoke(null, null);
                        if (gsmInstance != null)
                        {
                            var stateOrder = stateOrderField?.GetValue(gsmInstance) as System.Collections.IList;
                            var activeStates = activeStatesField?.GetValue(gsmInstance) as System.Collections.IDictionary;

                            if (stateOrder != null && activeStates != null && stateOrder.Count > 0)
                            {
                                for (int i = stateOrder.Count - 1; i >= 0; i--)
                                {
                                    object key = stateOrder[i];
                                    if (key != null && activeStates.Contains(key))
                                    {
                                        object stateObj = activeStates[key];
                                        if (stateObj != null)
                                        {
                                            int cLock = Convert.ToInt32(cursorLockField?.GetValue(stateObj) ?? 0);
                                            if (cLock != 0) shouldLockCursor = (cLock == 1);

                                            int pLock = Convert.ToInt32(playerInputLockField?.GetValue(stateObj) ?? 0);
                                            if (pLock != 0) shouldLockPlayerInput = (pLock == 1);

                                            int camLock = Convert.ToInt32(cameraInputLockField?.GetValue(stateObj) ?? 0);
                                            if (camLock != 0) shouldLockCamera = (camLock == 1);
                                        }
                                    }
                                }
                            }

                            // Write clean evaluated states directly into GameStateManager
                            setCursorLockedMethod?.Invoke(gsmInstance, new object[] { shouldLockCursor });
                            setPlayerInputLockedMethod?.Invoke(gsmInstance, new object[] { shouldLockPlayerInput });
                            setCameraLockedMethod?.Invoke(gsmInstance, new object[] { shouldLockCamera });
                        }
                    }
                    catch { }
                }

                logger?.LogInfo($"[RemixGameStateHelper] Pop evaluated: inGameplay={inGameplay}, lockCursor={shouldLockCursor}, lockCam={shouldLockCamera}, lockPlayer={shouldLockPlayerInput}");

                Cursor.lockState = shouldLockCursor ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !shouldLockCursor;
                if (!shouldLockCursor)
                {
                    ClipCursor(IntPtr.Zero);
                }
                ReleaseCapture();
            }
        }
    }
}
