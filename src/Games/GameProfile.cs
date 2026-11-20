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
    /// A 3D skybox: a miniature of the distant land, drawn behind the world by a camera of the
    /// game's own that moves with the player's at 1/scale of its movement (as Source's
    /// sky_camera). What only that camera draws is placed in the world at full size, at
    /// (point - origin) x scale, where the player's camera sees it as the game shows it, and
    /// path-traced with the rest (lit by the sun, in reflections and fog).
    /// </summary>
    public struct SkyboxView
    {
        public Camera camera;
        public Vector3 origin;
        public float scale;

        public Matrix4x4 ToWorld => Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(-origin);
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

        // The folder under Games/ whose Defaults/ hold this game's default settings (GameDefaults);
        // null: none.
        public virtual string DefaultsFolder => null;

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

        // A renderer not path-traced, whatever its materials (one the profile passes through,
        // whose material other renderers share). Main thread; called when renderers are
        // cached or scanned, not every frame.
        public virtual bool IsHiddenRenderer(Renderer renderer) => false;

        // A mesh renderer that fakes a light shaft (an additive cone or box under a window or a
        // lamp): drawn as glowing air instead (LightShafts), when its mesh is readable and its
        // material additive. Asked once per renderer, when it is first met.
        public virtual bool IsLightShaft(Renderer renderer) => false;

        // An additive particle system that gets particle lights (Lighting.ParticleLights): false for
        // faint haze the game draws additively but that is no light source (dust kicked up,
        // ripples). Asked once per renderer.
        public virtual bool CastsParticleLight(ParticleSystemRenderer renderer) => true;

        // How hard it rains where the player is, 0 to 1 (0: dry, or indoors): surfaces get wet
        // with it (Materials.RainWetness). Main thread, each frame: keep it cheap.
        public virtual float RainIntensity() => 0f;

        // A skinned renderer whose shader hides part of it by a vertex colour channel (0 r, 1 g,
        // 2 b, 3 a; PEAK's first-person body, dithered away where red is low): its triangles whose
        // vertices' channel averages below one half are left out. -1: none. Asked once per
        // renderer, again when the renderer cache refreshes.
        public virtual int GhostVertexChannel(SkinnedMeshRenderer renderer) => -1;

        // Adds the game's own per-frame meshes (geometry Unity's renderers don't show).
        public virtual void CaptureFrame(RemixMaterialManager materials, RemixFrameCapture.FrameState state, int frameCount) { }

        // A single (non-additive) scene load: drop per-scene state.
        public virtual void OnSceneLoaded() { }

        // Corrects what the generic material capture resolved for a material (applied
        // however the material is first captured).
        public virtual void OverrideMaterial(Material material, ref RemixMaterialManager.AlphaMode alphaMode, ref float alphaCutoff, ref Color albedoColor) { }

        // The albedo texture's scale and offset (x, y scale; z, w offset) as the game's shader
        // samples it, where that is not the material's own (_ST) tiling.
        public virtual void OverrideTextureTransform(Material material, ref Vector4 scaleOffset) { }

        // A material the game recolours with player-chosen colours through a mask texture
        // (block: the renderer's property block, empty when it has none): the mask and its
        // colours, applied to the albedo by the material capture.
        public virtual bool GetColorMask(Material material, MaterialPropertyBlock block, out RemixMaterialManager.ColorMask colorMask)
        {
            colorMask = default;
            return false;
        }

        // A thin film the game puts over a renderer's material (an oil coating), from the
        // material and the renderer's property block (empty when it has none): drawn with
        // OpenRemix's iridescence. Main thread, per renderer per frame: keep it cheap, and
        // quantise values that change smoothly (each distinct film is a material).
        public virtual bool GetIridescence(Material material, MaterialPropertyBlock block, out RemixMaterialManager.Iridescence film)
        {
            film = default;
            return false;
        }

        // A coat the game draws over a renderer's material (sand over an enemy), from the material
        // and its property block: baked into the albedo (RemixMaterialManager.Coat). Main thread,
        // per renderer per frame: keep it cheap.
        public virtual bool GetCoat(Material material, MaterialPropertyBlock block, out RemixMaterialManager.Coat coat)
        {
            coat = default;
            return false;
        }

        // The game's view portal this frame (worldCamera: the player's view), when it shows one.
        public virtual bool GetRayPortalView(Camera worldCamera, out RayPortalView view)
        {
            view = default;
            return false;
        }

        // A material the game draws as its sky around the world (a dome or sphere): only camera
        // rays meet it, as with a panoramic sky (RemixMeshConverter.IsCameraOnly), so it shows
        // and takes the fog without shadowing the world or blocking the sun and sky.
        public virtual bool IsSkyDome(Material material) => false;

        // A material whose shader blends a colour over its texture in overlay mode (per channel:
        // 2 x texel x colour below a colour of one half, 1 - 2 (1 - colour)(1 - texel) above):
        // that colour, baked into the albedo. Not a multiply tint.
        public virtual bool GetOverlayTint(Material material, out Color tint)
        {
            tint = default;
            return false;
        }

        // A material whose shader scrolls its texture by time (no script moves it): its UV
        // transform now (x, y the tiling, z, w the offset), as the shader samples it.
        public virtual bool GetShaderScroll(Material material, out Vector4 uvST)
        {
            uvST = default;
            return false;
        }

        // The game's 3D skybox this frame (worldCamera: the player's view), when it has one.
        public virtual bool GetSkyboxView(Camera worldCamera, out SkyboxView view)
        {
            view = default;
            return false;
        }

        // The player's own body, which the game shows only in mirrors and through portals: drawn
        // as Remix's reflection-only player body (camera rays pass it), whatever its layer.
        public virtual bool IsPlayerBody(Renderer renderer) => false;

        // A layer whose geometry the game never shows (collision brushes, triggers, player-only
        // barriers): the scene scanner skips it for good.
        public virtual bool IsNeverRenderedLayer(int layer) => false;

        // A light of the game's not converted: one whose look depends on the game's own
        // lighting (a fill light on the player that path tracing makes glaring). Main thread.
        public virtual bool IsHiddenLight(Light light) => false;

        // A renderer's surface that is a light (an explosion's fireball), and how bright: above 0
        // its emission, times this, lights the scene. An additive glow casts what it shows (its
        // coverage and fade); an opaque surface emits its own colour. 0: as converted (glows
        // only show their light; the game's lights do the lighting). Asked when the material
        // is captured.
        public virtual float SceneLightScale(Material material, Renderer renderer) => 0f;

        // An opaque surface that shows its own colour as light without lighting the scene (a
        // projectile whose game light sits inside it), and how bright: above 0 it emits its
        // albedo times this instead of being lit. 0: as converted. Asked when the material is
        // captured, once a material: renderer is null on capture paths that have none.
        public virtual float SelfIlluminationScale(Material material, Renderer renderer) => 0f;

        // A material the game draws unlit (its colour whatever lights it, as Unity's Unlit
        // shaders) through a shader that can be lit. Opaque, it shows its colour as its own
        // light (self-illumination 1, unless SelfIlluminationScale gives one); alpha-blended,
        // it is an unlit sheet, as Unlit/Transparent. Asked when the material is captured.
        public virtual bool IsUnlit(Material material) => false;

        // A material that is never a liquid body, though its name reads like one (a water
        // cooler, a water tank): it skips water detection; glass detection still applies.
        public virtual bool IsNeverLiquid(Material material) => false;

        // An emissive material of the level's far backdrop (a skyline's lit windows): it glows
        // as converted but is no light of the scene. Converted to lights, its many triangles
        // take the light sampler's samples from the lights that reach the play space. Asked
        // when the material is captured, also for static meshes (no renderer).
        public virtual bool IsBackdropEmission(Material material) => false;

        // A static renderer the game rewrote at runtime for its own batching (its mesh a slice of
        // a combined mesh, its materials an atlas material the converter cannot read): the mesh
        // and materials it had before, drawn at its own transform. False: as it is. Asked when
        // the scene scan reads the renderer.
        public virtual bool GetOriginalGeometry(Renderer renderer, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;
            return false;
        }

        // The fog density the game's world camera draws with, given RenderSettings.fogDensity:
        // for a game that changes it per camera while rendering. Main thread, each frame.
        public virtual float WorldFogDensity(float density) => density;

        // A shader of the game's that blends as its _SrcBlend / _DstBlend properties say (as
        // Unity's Standard and Particles/Standard do), so additive ones glow.
        public virtual bool ReadsBlendProperties(string shaderName) => false;

        // The camera is inside a fluid the game shows with an overlay of its own (main thread,
        // a few times a second): its tint, alpha 0 when it has none. False: not shown.
        public virtual bool GetUnderwaterTint(out Color tint)
        {
            tint = default;
            return false;
        }

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

        // The shader pass a pass-through material is drawn with: past a GrabPass (which
        // copies what is behind into _GrabTexture; the path-traced frame stands in for it).
        public virtual int PassThroughShaderPass(Material material) => 0;

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
