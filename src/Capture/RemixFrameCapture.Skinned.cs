using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityRemix
{
    // Skinned meshes: GPU skinning, BakeMesh fallback, blend shapes, their materials.
    public partial class RemixFrameCapture
    {
        /// <summary>
        /// Capture skinned meshes — Remix GPU skinning primary path with BakeMesh fallback.
        /// GPU skinning: mesh created once (bind-pose + bone weights), bone transforms sent per-frame.
        /// BakeMesh fallback: when bone extraction fails (>256 bones, missing weights, etc.).
        /// </summary>
        public void CaptureSkinnedMeshes(FrameState state, int frameCount)
        {
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            
            if (!configCaptureSkinnedMeshes.Value)
                return;
            
            skinnedCaptureCount++;
            bool doLog = configDebugLogInterval.Value > 0 && (skinnedCaptureCount % configDebugLogInterval.Value == 1);
            
            int total = cachedSkinnedRenderers.Count;
            if (total == 0) return;
            
            int gpuSkinned = 0;
            int baked = 0;
            int skipNull = 0, skipLayer = 0, skipVis = 0, skipDist = 0, skipNoMesh = 0;
            reusableValidSkinnedIds.Clear();
            var validSkinnedIds = reusableValidSkinnedIds;
            
            Camera mainCam = cameraHandler.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;
            Transform mainCamTransform = mainCam != null ? mainCam.transform : null;
            viewModelSkinnedIds.Clear();
            playerBodySkinnedIds.Clear();

            if (mainCam != null)
            {
                EnsureCameraRenderersTracked(mainCam, frameCount);
            }

            // BakeMesh fallback budget
            var bakeSw = System.Diagnostics.Stopwatch.StartNew();
            const float bakeMaxMs = 100.0f;
            if (skinnedRoundRobinIndex >= total) skinnedRoundRobinIndex = 0;
            reusableBakeFallbackQueue.Clear();
            var bakeFallbackQueue = reusableBakeFallbackQueue;

            bool useDistCulling = configUseDistanceCulling.Value;
            float maxDistSqr = configMaxRenderDistance.Value * configMaxRenderDistance.Value;
            bool useHwSkinning = configHardwareSkinning.Value;
            bool useEvenBuffer = (frameCount & 1) == 0;
            // Baked renderers outside the camera's view (seen only in reflections and shadows)
            // are re-baked every OffscreenBakeInterval frames, staggered; they still follow their
            // transform every frame.
            offscreenBakeSkips = 0;
            bool haveFrustum = mainCam != null;
            if (haveFrustum)
                GeometryUtility.CalculateFrustumPlanes(mainCam, skinnedFrustumPlanes);
            
            for (int i = 0; i < total; i++)
            {
                var skinned = cachedSkinnedRenderers[i];
                if (skinned == null)
                {
                    skipNull++;
                    continue;
                }

                var info = GetOrCreateSkinnedInfo(skinned);
                int skinnedId = info.hierarchyHash;
                var go = info.gameObject;

                if (!skinned.enabled || go == null || !go.activeInHierarchy || SceneMeshScanner.IsRenderingOff(skinned))
                {
                    persistentSkinnedData.Remove(skinnedId);
                    skipNull++;
                    continue;
                }

                var tr = info.transform;
                var ls = tr.lossyScale;
                if (ls.sqrMagnitude < 0.0001f)
                {
                    persistentSkinnedData.Remove(skinnedId);
                    skipNull++;
                    continue;
                }

                if (!skinned.updateWhenOffscreen)
                    skinned.updateWhenOffscreen = true;

                if (!info.animatorResolved)
                {
                    info.animator = skinned.GetComponentInParent<Animator>();
                    info.animatorResolved = true;
                }
                // Single Window draws nothing with the game's cameras: a culling animator
                // would stop posing (enemies slide instead of walking).
                var anim = info.animator;
                if (anim != null && anim.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                int rendererId = skinned.GetInstanceID();
                bool playerBody = IsPlayerBodyRenderer(skinned);
                if ((!playerBody && IsLayerDisabled(go.layer)) || IsRendererDisabled(skinnedId) || previewRendererIds.Contains(rendererId))
                {
                    skipLayer++;
                    continue;
                }
                
                if (useDistCulling)
                {
                    float sqrDistance = (skinned.bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDistSqr)
                    {
                        skipDist++;
                        continue;
                    }
                }
                
                var sharedMesh = skinned.sharedMesh;
                if (sharedMesh == null)
                {
                    if (configDebugLogInterval.Value > 0 && persistentSkinnedData.ContainsKey(skinnedId))
                        logger.LogWarning($"[SkinSkip] '{go.name}' id={skinnedId}: sharedMesh became NULL");
                    skipNoMesh++;
                    continue;
                }
                
                validSkinnedIds.Add(skinnedId);
                // Held by the camera (arms, weapons) or on a view-model layer: drawn as view models.
                bool viewModel = IsViewModel(tr, go.layer, mainCamTransform, "skinned mesh");
                if (viewModel)
                    viewModelSkinnedIds.Add(skinnedId);
                if (playerBody)
                    playerBodySkinnedIds.Add(skinnedId);
                // Also register per-submesh sub-keys so they survive the stale-pruning pass and
                // carry the view-model and player-body flags. Entries exist per submesh, which
                // can outnumber the materials (V1's body weapons: one material, four submeshes).
                // Formula: sub0 = skinnedId, sub>0 = skinnedId ^ (sub * KNUTH_PRIME)
                var smMaterials = info.sharedMaterials;
                int registeredSubs = Math.Max(smMaterials != null ? smMaterials.Length : 1, sharedMesh != null ? sharedMesh.subMeshCount : 1);
                {
                    for (int sub = 1; sub < registeredSubs; sub++)
                    {
                        int subKey = (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                        validSkinnedIds.Add(subKey);
                        if (viewModel)
                            viewModelSkinnedIds.Add(subKey);
                        if (playerBody)
                            playerBodySkinnedIds.Add(subKey);
                    }
                }
                
                // Compute unscaled transform (sign-only scale preserves winding)
                Matrix4x4 unscaledMatrix = Matrix4x4.TRS(
                    tr.position,
                    tr.rotation,
                    new Vector3(Mathf.Sign(ls.x), Mathf.Sign(ls.y), Mathf.Sign(ls.z))
                );
                
                Material primaryMat = info.primarySharedMaterial;
                int matId = ResolveMaterial(skinned, primaryMat, doLog);
                int sharedMeshId = sharedMesh.GetInstanceID();

                // Compute BlendShape hash from active weights to support custom character creator proportions (belly, torso, etc.)
                uint bsHash = 0;
                int bsCount = sharedMesh.blendShapeCount;
                if (bsCount > 0)
                {
                    bsHash = 2166136261u;
                    for (int bsi = 0; bsi < bsCount; bsi++)
                    {
                        int wInt = Mathf.RoundToInt(skinned.GetBlendShapeWeight(bsi) * 10f);
                        bsHash = (bsHash ^ (uint)wInt) * 16777619u;
                    }
                }
                long skinCacheKey = ((long)sharedMeshId << 32) | (uint)bsHash;
                
                // Try GPU skinning path: extract bone weights once, compute bone transforms each frame
                var skinData = (CachedSkinningData)null;
                if (useHwSkinning)
                {
                    if (!cachedSkinning.TryGetValue(skinCacheKey, out skinData))
                    {
                        CacheSkinningData(skinned, skinCacheKey);
                        cachedSkinning.TryGetValue(skinCacheKey, out skinData);
                    }
                    if (skinData != null)
                        skinData.lastUsedFrame = frameCount;
                }

                var bones = info.bones;
                if (skinData != null && bones != null && bones.Length > 0)
                {
                    // Compute bone transforms for Remix GPU skinning using double-buffered array
                    int boneCount = skinData.boneCount;
                    Matrix4x4[] boneMatrices;
                    if (useEvenBuffer)
                    {
                        if (info.boneMatricesA == null || info.boneMatricesA.Length != boneCount)
                            info.boneMatricesA = new Matrix4x4[boneCount];
                        boneMatrices = info.boneMatricesA;
                    }
                    else
                    {
                        if (info.boneMatricesB == null || info.boneMatricesB.Length != boneCount)
                            info.boneMatricesB = new Matrix4x4[boneCount];
                        boneMatrices = info.boneMatricesB;
                    }
                    
                    Matrix4x4 invInstance = unscaledMatrix.inverse;
                    var skeleton = GetSkeletonPose(info, frameCount);
                    var world = skeleton.world;
                    var present = skeleton.present;
                    var bindPoses = skinData.bindPoses;
                    for (int b = 0; b < boneCount; b++)
                    {
                        if (b < present.Length && present[b])
                        {
                            MulAffine(ref invInstance, ref world[b], out Matrix4x4 boneToInstance);
                            MulAffine(ref boneToInstance, ref bindPoses[b], out boneMatrices[b]);
                        }
                        else
                        {
                            boneMatrices[b] = Matrix4x4.identity;
                        }
                    }
                    
                    if (!skinnedMeshHashes.TryGetValue(sharedMeshId, out ulong combinedMeshHash))
                    {
                        combinedMeshHash = RemixMeshConverter.GenerateMeshHash(sharedMesh);
                        skinnedMeshHashes[sharedMeshId] = combinedMeshHash;
                    }
                    var boneHashes = info.boneNameHashes;
                    if (boneHashes != null)
                    {
                        for (int b = 0; b < boneHashes.Length; b++)
                        {
                            if (boneHashes[b] != 0UL)
                                combinedMeshHash ^= boneHashes[b];
                            combinedMeshHash *= 1099511628211UL;
                        }
                    }
                    if (bsHash != 0)
                    {
                        combinedMeshHash ^= (ulong)bsHash;
                        combinedMeshHash *= 1099511628211UL;
                    }
                    
                    // Emit one SkinnedMeshData per submesh so each submesh gets its correct material
                    int subCount = (skinData.submeshTriangles != null) ? skinData.submeshTriangles.Length : 1;
                    for (int sub = 0; sub < subCount; sub++)
                    {
                        int[] subTris = (skinData.submeshTriangles != null && sub < skinData.submeshTriangles.Length)
                            ? skinData.submeshTriangles[sub]
                            : skinData.triangles;
                        if (subTris == null || subTris.Length == 0) continue;
                        
                        // Resolve per-submesh material
                        int subMatId = matId; // default to renderer-best material
                        if (smMaterials != null && sub < smMaterials.Length && smMaterials[sub] != null)
                        {
                            var subMat = smMaterials[sub];
                            subMatId = (sub == 0) ? matId : ResolveMaterial(skinned, subMat, doLog);
                            if (subMatId == 0) subMatId = matId;
                        }
                        
                        // Unique key per submesh: xor with Knuth multiplied submesh index
                        int subSkinnedId = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                        ulong subMeshHash = (sub == 0) ? combinedMeshHash : combinedMeshHash ^ ((ulong)(uint)sub * 2654435761UL);
                        
                        persistentSkinnedData[subSkinnedId] = new SkinnedMeshData
                        {
                            meshId = subSkinnedId,
                            remixMeshHash = subMeshHash,
                            materialId = subMatId,
                            vertices = skinData.bindVertices,
                            normals = skinData.bindNormals,
                            uvs = skinData.uvs,
                            colors = skinData.colors,
                            triangles = subTris,
                            localToWorld = unscaledMatrix,
                            boneTransforms = boneMatrices,
                            skinningData = skinData
                        };
                        validSkinnedIds.Add(subSkinnedId);
                    }
                    gpuSkinned++;
                    continue;
                }
                
                // Update transform on existing persistent data (for all submesh entries of this renderer)
                int subCountForUpdate = (smMaterials != null) ? smMaterials.Length : 1;
                for (int sub = 0; sub < subCountForUpdate; sub++)
                {
                    int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                    if (persistentSkinnedData.TryGetValue(subKey, out var existing))
                    {
                        existing.localToWorld = unscaledMatrix;
                        persistentSkinnedData[subKey] = existing;
                    }
                }
                
                if (haveFrustum && ((frameCount + skinnedId) % OffscreenBakeInterval) != 0 &&
                    persistentSkinnedData.ContainsKey(skinnedId) &&
                    !GeometryUtility.TestPlanesAABB(skinnedFrustumPlanes, skinned.bounds))
                {
                    offscreenBakeSkips++;
                    continue;
                }

                // Queue for BakeMesh fallback
                bakeFallbackQueue.Add((i, skinned, skinnedId, unscaledMatrix, matId));
            }
            
            // Process BakeMesh fallback queue with round-robin + time budget
            if (bakeFallbackQueue.Count > 0)
            {
                int fbStart = 0;
                for (int f = 0; f < bakeFallbackQueue.Count; f++)
                {
                    if (bakeFallbackQueue[f].idx >= skinnedRoundRobinIndex) { fbStart = f; break; }
                }
                
                for (int f = 0; f < bakeFallbackQueue.Count; f++)
                {
                    int fi = (fbStart + f) % bakeFallbackQueue.Count;
                    var (idx, skinned, skinnedId, matrix, matId) = bakeFallbackQueue[fi];
                    
                    if (BakeSingleMesh(skinned, skinnedId, matId, matrix, doLog))
                    {
                        baked++;
                        // Register all per-submesh keys as valid so they aren't pruned
                        var smMatsForBake = GetOrCreateSkinnedInfo(skinned).sharedMaterials;
                        int subCntBake = Math.Max((smMatsForBake != null) ? smMatsForBake.Length : 1,
                                                  skinned.sharedMesh != null ? skinned.sharedMesh.subMeshCount : 1);
                        for (int sub = 0; sub < subCntBake; sub++)
                        {
                            int subKey = (sub == 0) ? skinnedId : (int)((uint)skinnedId ^ ((uint)sub * 0x9E3779B9u));
                            validSkinnedIds.Add(subKey);
                        }
                    }
                    
                    if (bakeSw.Elapsed.TotalMilliseconds > bakeMaxMs)
                    {
                        skinnedRoundRobinIndex = bakeFallbackQueue[(fi + 1) % bakeFallbackQueue.Count].idx;
                        break;
                    }
                }
            }
            
            // Prune any persistent entries that are no longer valid (e.g. unequipped weapons, deactivated objects)
            if (persistentSkinnedData.Count > validSkinnedIds.Count)
            {
                reusableStaleSkinnedKeys.Clear();
                foreach (var id in persistentSkinnedData.Keys)
                {
                    if (!validSkinnedIds.Contains(id))
                    {
                        reusableStaleSkinnedKeys.Add(id);
                    }
                }
                for (int s = 0; s < reusableStaleSkinnedKeys.Count; s++)
                {
                    persistentSkinnedData.Remove(reusableStaleSkinnedKeys[s]);
                }
            }
            
            // Step 5: Submit only currently valid entries to frame state
            foreach (var entry in persistentSkinnedData.Values)
            {
                if (validSkinnedIds.Contains(entry.meshId))
                {
                    var submitted = entry;
                    if (viewModelSkinnedIds.Contains(entry.meshId))
                        submitted.categoryFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_VIEW_MODEL;
                    if (playerBodySkinnedIds.Contains(entry.meshId))
                        submitted.categoryFlags |= (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_THIRD_PERSON_PLAYER_BODY;
                    state.skinned.Add(submitted);
                }
            }
            
            PruneSkinningCache(frameCount);

            // World-space UI screens: on the weapons (Nailgun heat, Shotgun slider, Rocket Launcher timer) and in the level (shop terminals)
            CaptureCanvasScreens(state, frameCount);
            
            if (frameCount % 60 == 0) // Force log every 60 frames
            {
                if (VerboseLog.On) logger.LogInfo($"CaptureSkinnedMeshes: gpuSkinned={gpuSkinned}, baked={baked}, offscreenSkipped={offscreenBakeSkips}, " +
                    $"skip(null={skipNull},layer={skipLayer},vis={skipVis},dist={skipDist},mesh={skipNoMesh}), " +
                    $"cached={persistentSkinnedData.Count}, total={total}");
            }
        }

        private static readonly MethodInfo _doMeshGenerationMethod = typeof(Graphic).GetMethod("DoMeshGeneration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly PropertyInfo _workerMeshProperty = typeof(Graphic).GetProperty("workerMesh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo _canvasRendererGetMesh = typeof(CanvasRenderer).GetMethod("GetMesh", Type.EmptyTypes);
        private static Type _tmpTextType;
        private static PropertyInfo _tmpMeshProperty;
        private static MethodInfo _tmpForceMeshUpdateMethod;
        private static bool _tmpReflectionChecked;
    }
}
