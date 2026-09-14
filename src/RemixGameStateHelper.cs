using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Interacts with ULTRAKILL's GameStateManager to cleanly unlock/lock mouse cursor,
    /// player movement/shooting, and camera rotation when the RTX Remix menu (ImGui) opens and closes.
    /// Uses reflection so the bridge compiles and runs cleanly even if GameStateManager is absent.
    /// </summary>
    public static class RemixGameStateHelper
    {
        private static bool typesInitialized = false;
        private static Type gsmType;
        private static MethodInfo gsmInstanceProp;
        private static MethodInfo registerStateMethod;
        private static MethodInfo popStateMethod;
        private static Type gameStateType;
        private static ConstructorInfo gameStateCtor;
        private static FieldInfo cursorLockField;
        private static FieldInfo cameraInputLockField;
        private static FieldInfo playerInputLockField;
        private static FieldInfo priorityField;

        private static bool isStateRegistered = false;
        private static CursorLockMode previousLockMode = CursorLockMode.Locked;
        private static bool previousCursorVisible = false;

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

        public static void SetRemixMenuState(bool open, ManualLogSource logger = null)
        {
            InitializeTypes();

            if (open)
            {
                previousLockMode = Cursor.lockState;
                previousCursorVisible = Cursor.visible;

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
                else
                {
                    // Fallback for scenes/games without GameStateManager
                    Cursor.lockState = previousLockMode;
                    Cursor.visible = previousCursorVisible;
                }
            }
        }
    }
}
