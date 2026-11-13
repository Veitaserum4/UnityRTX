using System;
using System.Collections.Generic;
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
    /// <summary>
    /// A view portal: surfaces that show another part of the level as seen from a camera of
    /// the game's own, which follows the player's at a scale (ULTRAKILL's Limbo walls; a
    /// Source-style 3D skybox seen through windows). Camera rays meeting such a surface go on
    /// from exitCamera + scale x (point - camera), in the same direction (RayPortals).
    /// The exit region's geometry is drawn wherever the player is (never distance-culled).
    /// </summary>
    public struct RayPortalView
    {
        public Vector3 exitCamera;
        public float scale;
        public Vector3 exitRegionCenter;
        public float exitRegionRadius;

        public bool InExitRegion(Vector3 point) => (point - exitRegionCenter).sqrMagnitude <= exitRegionRadius * exitRegionRadius;
    }

    /// <summary>
    /// One of the game's portals: its two ends (unscaled frames; the opening lies in the frame's
    /// local XY plane, normal +Z, width along x, height along y). What is seen through the entry
    /// is at the exit mapped by exit diag(-1, 1, -1) entry^-1, and the other way round.
    /// </summary>
    public struct PortalPair
    {
        public int id;
        public Matrix4x4 entry, exit;
        public float width, height;
        // A mirror: the opening reflects (exit is entry).
        public bool mirror;
        // How far in front of its plane the opening must stand to be met before what lies over
        // it (a pool's water, a mirror's glass), which the game draws the portal over.
        public float lift;
    }

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

        // A material the game recolours with player-chosen colours through a mask texture
        // (block: the renderer's property block, empty when it has none): the mask and its
        // colours, applied to the albedo by the material capture.
        public virtual bool GetColorMask(Material material, MaterialPropertyBlock block, out RemixMaterialManager.ColorMask colorMask)
        {
            colorMask = default;
            return false;
        }

        // The game's view portal this frame (worldCamera: the player's view), when it shows one.
        public virtual bool GetRayPortalView(Camera worldCamera, out RayPortalView view)
        {
            view = default;
            return false;
        }

        // The player's own body, which the game shows only in mirrors and through portals: drawn
        // as Remix's reflection-only player body (camera rays pass it), whatever its layer.
        public virtual bool IsPlayerBody(Renderer renderer) => false;

        // The game's portals this frame, nearest the camera first (RayPortals draws as many as
        // Remix has room for).
        public virtual void GetPortals(Camera worldCamera, List<PortalPair> portals) { }

        // A material whose surfaces open the view portal (they keep their look where it doesn't
        // open: at their back, for rays other than the camera's).
        public virtual bool OpensViewPortal(Material material) => false;

        // Renderers whose own shaders pass through to OpenRemix as host passes (ShaderPassThrough):
        // drawn by Unity from the game camera's view each frame and replayed over the path-traced frame.
        public virtual void GetPassThroughRenderers(Camera worldCamera, List<Renderer> renderers) { }

        // Whether GetPassThroughRenderers ever returns any: shader pass-through then needs
        // Unity's graphics jobs off (ShaderPassThrough.RequireImmediateRendering).
        public virtual bool UsesShaderPassThrough => false;

        // The texture a pass-through material samples that the path-traced frame stands in for
        // (its screen-space view of the scene); null: none. Main thread.
        public virtual Texture PassThroughFrameTexture(Material material) => null;

        // A full-screen effect the game applies over its frame (a material Unity blits with,
        // reading the frame as _MainTex), passed through over the composed path-traced frame
        // this frame; null: none. Main thread.
        public virtual Material PassThroughScreenEffect() => null;

        // The profile's own options, drawn in the Remix menu's Plugin tab (RemixSettingsUI) on
        // its render thread with RemixImGui; false: it has none.
        public virtual bool DrawSettings() => false;

        // The resolution the game renders the frame such an effect reads at (its downsampling):
        // the composed frame is scaled to it (nearest) first; zero: the frame's own. Main thread.
        public virtual Vector2Int PassThroughScreenResolution(Material material) => Vector2Int.zero;

        // The chromatic aberration of what such a material shows (its red right and blue left of
        // green), a fraction of the screen's width; 0 none. Main thread.
        public virtual float ViewChromaticAberration(Material material) => 0f;

        // The camera the game draws its view models (weapons, arms) with, when it isn't the
        // world camera; null: the world camera's projection.
        public virtual Camera FindViewModelCamera(Camera worldCamera) => null;

        // The Remix menu opened or closed (Single Window). On close, returns whether the
        // cursor locks again; null restores the lock it had when the menu opened.
        public virtual bool? OnRemixMenu(bool open) => null;
    }
}
