using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix.Games
{
    /// <summary>
    /// One game's patches: everything the bridge does only for that game. GameProfiles
    /// picks the profile whose executable is running; the generic bridge calls the hooks
    /// below through GameProfiles, so a game without a profile pays nothing.
    /// </summary>
    public abstract class GameProfile
    {
        public abstract string Name { get; }

        // Process names (without .exe), compared ignoring case.
        public abstract string[] ExecutableNames { get; }

        protected ManualLogSource Logger { get; private set; }

        internal void Initialize(ManualLogSource logger) => Logger = logger;

        // Harmony patches, applied once in the plugin's Awake.
        public virtual void Apply(Harmony harmony) { }

        // Whether the static scene scan leaves a renderer to the per-frame capture
        // (the game moves, spawns or swaps it).
        public virtual bool IsDynamicObject(MeshFilter filter, Renderer renderer) => false;

        // A static renderer the game shows or hides by deactivating it: the scan draws it only
        // while active in the hierarchy, even when inactive geometry is otherwise kept.
        public virtual bool FollowsActiveState(MeshFilter filter, Renderer renderer) => false;

        // A material the game never shows (helper shapes, trigger volumes): surfaces with it
        // are not drawn.
        public virtual bool IsHiddenMaterial(Material material) => false;

        // Adds the game's own per-frame meshes (geometry Unity's renderers don't show).
        public virtual void CaptureFrame(RemixMaterialManager materials, RemixFrameCapture.FrameState state, int frameCount) { }

        // A single (non-additive) scene load: drop per-scene state.
        public virtual void OnSceneLoaded() { }

        // Corrects what the generic material capture resolved for a material (applied
        // however the material is first captured).
        public virtual void OverrideMaterial(Material material, ref RemixMaterialManager.AlphaMode alphaMode, ref float alphaCutoff, ref Color albedoColor) { }

        // The camera the game draws its view models (weapons, arms) with, when it isn't the
        // world camera; null: the world camera's projection.
        public virtual Camera FindViewModelCamera(Camera worldCamera) => null;

        // The Remix menu opened or closed (Single Window). On close, returns whether the
        // cursor locks again; null restores the lock it had when the menu opened.
        public virtual bool? OnRemixMenu(bool open) => null;
    }
}
