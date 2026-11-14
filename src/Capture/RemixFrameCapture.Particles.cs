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
    // Particle systems and trails: billboards, mesh particles (instanced or baked), trail ribbons.
    public partial class RemixFrameCapture
    {
        /// <summary>
        /// Capture active particle systems (both pooled and dynamic) using zero-allocation billboard extraction.
        /// </summary>
        public void CaptureParticleSystems(FrameState state, int frameCount)
        {
            if (configCaptureParticles != null && !configCaptureParticles.Value)
            {
                if (frameCount % 30 == 0 || _latestParticleStats == null || _latestParticleStats.TotalTrackedSystems == 0)
                {
                    int totalPs = trackedParticleSystems.Count;
                    int activePs = 0;
                    for (int s = 0; s < trackedParticleSystems.Count; s++)
                    {
                        var trk = trackedParticleSystems[s];
                        if (trk.renderer != null && trk.renderer.enabled && trk.renderer.gameObject.activeInHierarchy)
                            activePs++;
                    }
                    _latestParticleStats = new ParticleStatsSnapshot
                    {
                        TotalTrackedSystems = totalPs,
                        ActiveSystems = activePs,
                        InactiveSystems = totalPs - activePs,
                        TotalTrackedTrails = trackedTrailRenderers.Count
                    };
                }
                return;
            }

            Camera mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
            if (mainCam == null || !mainCam.gameObject.activeInHierarchy || !mainCam.enabled || Time.frameCount < 10)
                return;

            Vector3 camRight = mainCam.transform.right;
            Vector3 camUp = mainCam.transform.up;
            Vector3 camForward = mainCam.transform.forward;
            Vector3 camPos = mainCam.transform.position;
            float maxParticleDist = (configParticleMaxDistance != null && configParticleMaxDistance.Value > 0f)
                ? configParticleMaxDistance.Value
                : 60f;
            float maxParticleDistSqr = maxParticleDist * maxParticleDist;
            long discoverStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // Recycle batch buckets from previous frame
            for (int b = 0; b < _activeParticleBatches.Count; b++)
            {
                _batchBucketPool.Add(_activeParticleBatches[b]);
            }
            _activeParticleBatches.Clear();
            _particleBatchesByMat.Clear();

            // Check active particle renderers periodically to capture newly spawned systems without sweeping full scene every frame
            if (frameCount % 4 == 0)
            {
                var activeRenderers = UnityCompat.FindActiveSceneComponents<ParticleSystemRenderer>();
                for (int a = 0; a < activeRenderers.Length; a++)
                {
                    var ar = activeRenderers[a];
                    if (ar != null && trackedParticleSystemIds.Add(ar.GetInstanceID()))
                    {
                        var aps = ar.GetComponent<ParticleSystem>();
                        if (aps != null)
                        {
                            UnityCompat.KeepSimulating(aps);

                            trackedParticleSystems.Add(new TrackedParticleSystem
                            {
                                renderer = ar,
                                system = aps,
                                id = ar.GetInstanceID()
                            });
                        }
                    }
                }
            }

            // Always check camera hierarchy every frame for instantaneous first-person / viewmodel particle discovery
            if (mainCam != null)
            {
                var camRenderers = mainCam.GetComponentsInChildren<ParticleSystemRenderer>(false);
                for (int a = 0; a < camRenderers.Length; a++)
                {
                    var ar = camRenderers[a];
                    if (ar != null && trackedParticleSystemIds.Add(ar.GetInstanceID()))
                    {
                        var aps = ar.GetComponent<ParticleSystem>();
                        if (aps != null)
                        {
                            UnityCompat.KeepSimulating(aps);

                            trackedParticleSystems.Add(new TrackedParticleSystem
                            {
                                renderer = ar,
                                system = aps,
                                id = ar.GetInstanceID()
                            });
                        }
                    }
                }
            }

            // Periodically prune destroyed particle systems
            if (frameCount % 180 == 0)
            {
                for (int d = trackedParticleSystems.Count - 1; d >= 0; d--)
                {
                    var t = trackedParticleSystems[d];
                    if (t.renderer == null || t.system == null)
                    {
                        trackedParticleSystemIds.Remove(t.id);
                        trackedParticleSystems.RemoveAt(d);
                    }
                }
            }

            int totalTracked = trackedParticleSystems.Count;
            int activeCount = 0;
            int inactiveCount = 0;
            int distanceCulled = 0;
            int emittingCount = 0;
            int renderedParticles = 0;
            int culledParticles = 0;
            int bakedMeshes = 0;
            float furthestDist = 0f;
            string furthestName = null;

            bool collectEmitters = (frameCount % 30 == 0);
            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleDiscover, RemixProfiler.ElapsedMs(discoverStart));
            long loopStart = System.Diagnostics.Stopwatch.GetTimestamp();
            double readMs = 0;
            particleAppendMs = 0;
            particleCurveCount = 0;
            List<ParticleEmitterInfo> emitterDetails = collectEmitters ? new List<ParticleEmitterInfo>(Math.Min(totalTracked, 256)) : null;

            for (int i = 0; i < trackedParticleSystems.Count; i++)
            {
                var tracked = trackedParticleSystems[i];
                var pr = tracked.renderer;
                var ps = tracked.system;

                if (pr == null || ps == null) continue;
                if (!pr.enabled || !pr.gameObject.activeInHierarchy)
                {
                    inactiveCount++;
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = 0f,
                            ParticleCount = 0,
                            Status = "Inactive"
                        });
                    }
                    continue;
                }

                activeCount++;
                Bounds prBounds = pr.bounds;
                float distSqr = prBounds.extents.sqrMagnitude > 0.01f
                    ? prBounds.SqrDistance(camPos)
                    : (pr.transform.position - camPos).sqrMagnitude;
                float dist = Mathf.Sqrt(distSqr);
                if (dist > furthestDist)
                {
                    furthestDist = dist;
                    furthestName = pr.name;
                }

                int numAlive = ps.particleCount;

                // Distance culling: skip particle systems whose bounds/transform are beyond max render distance
                if (configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value && distSqr > maxParticleDistSqr)
                {
                    distanceCulled++;
                    culledParticles += numAlive;
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = numAlive,
                            Status = "Culled"
                        });
                    }
                    continue;
                }

                if (numAlive <= 0)
                {
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = 0,
                            Status = "Idle (0)"
                        });
                    }
                    continue;
                }

                // Expand buffer if needed
                if (_particleBuffer.Length < numAlive)
                {
                    _particleBuffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(numAlive)];
                }

                long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                int actualAlive = ps.GetParticles(_particleBuffer);
                readMs += RemixProfiler.ElapsedMs(readStart);
                if (actualAlive <= 0)
                {
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = 0,
                            Status = "Idle (0)"
                        });
                    }
                    continue;
                }

                emittingCount++;

                Material mat = pr.sharedMaterial;
                if (mat == null)
                {
                    var mats = pr.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                        mat = mats[0];
                }
                // Materials that draw nothing of their own (hidden, or a screen distortion).
                if (mat == null || SceneMeshScanner.IsMaterialOrShaderInvisible(mat)) continue;

                // Sample color from active particles (fast struct read) or fallback
                Color pCol = Color.white;
                bool foundAliveColor = false;
                for (int pIdx = 0; pIdx < Math.Min(actualAlive, 8); pIdx++)
                {
                    Color32 c = _particleBuffer[pIdx].startColor;
                    if (c.a > 0 && (c.r > 2 || c.g > 2 || c.b > 2))
                    {
                        pCol = (Color)c;
                        foundAliveColor = true;
                        break;
                    }
                }
                if (!foundAliveColor && actualAlive > 0)
                {
                    Color32 c = _particleBuffer[0].GetCurrentColor(ps);
                    if (c.a > 0 && (c.r > 2 || c.g > 2 || c.b > 2))
                    {
                        pCol = (Color)c;
                        foundAliveColor = true;
                    }
                }
                if (!foundAliveColor)
                {
                    var main = ps.main;
                    var startColor = main.startColor;
                    if (startColor.mode == ParticleSystemGradientMode.Color)
                        pCol = startColor.color;
                    else if (startColor.mode == ParticleSystemGradientMode.TwoColors)
                        pCol = (startColor.colorMin + startColor.colorMax) * 0.5f;
                    else if (startColor.gradient != null)
                        pCol = startColor.gradient.Evaluate(0.5f);
                }

                // The material's blend and tint (the particles' own colours are their vertex colours).
                int matInstId = mat.GetInstanceID();
                ParticleLook look = GetParticleLook(mat, materialManager.UsesParticleSnapshot(mat));
                float matAlpha = Mathf.Clamp01(look.alphaScale);

                long traitsKey = ((long)tracked.id << 32) | (uint)matInstId;
                if (!_particleTraitsCache.TryGetValue(traitsKey, out var traits))
                {
                    traits.emissive = look.IsAdditive || (!look.IsMultiply && IsEmissiveParticleSystem(pr, mat));
                    traits.jumpPad = pr.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0
                        || (pr.transform.parent != null && pr.transform.parent.name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0);
                    _particleTraitsCache[traitsKey] = traits;
                }
                bool isEmissiveParticle = traits.emissive;
                float emIntensity = isEmissiveParticle ? 0.8f : 0f;

                // Emission is not tinted by vertex colours: an emissive system's material emits its
                // particles' colour (quantized, 0..15 per channel, to keep the material count down).
                Color emissionColor = look.EmissionColor(pCol);
                int r4 = Mathf.Clamp((int)(emissionColor.r * 15f + 0.5f), 0, 15);
                int g4 = Mathf.Clamp((int)(emissionColor.g * 15f + 0.5f), 0, 15);
                int b4 = Mathf.Clamp((int)(emissionColor.b * 15f + 0.5f), 0, 15);
                int colorKey = isEmissiveParticle ? (1 << 12) | (r4 << 8) | (g4 << 4) | b4 : 0;
                Color quantizedColor = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);

                int tintedMatId = unchecked(mat.GetInstanceID() * 397 ^ colorKey);

                if (_capturedParticleMatIds.Add(tintedMatId))
                {
                    materialManager.CaptureMaterialTextures(
                        mat, 
                        tintedMatId, 
                        mpbEmissiveColor: isEmissiveParticle ? (Color?)quantizedColor : null, 
                        mpbEmissiveIntensity: emIntensity, 
                        mpbColor: look.MaterialColor,
                        suppressLightGeneration: true,
                        renderer: pr
                    );
                }

                if (configDebugLogInterval.Value > 0)
                {
                    if (loggedParticleSystems.Add(pr.name))
                    {
                        if (VerboseLog.On) logger.LogInfo($"[ParticleSystem] First capture '{pr.name}' (renderMode={pr.renderMode}, alive={actualAlive}, mat='{mat.name}', color=({quantizedColor.r:F2},{quantizedColor.g:F2},{quantizedColor.b:F2}), matAlpha={matAlpha:F2}, em={emIntensity:F1})");
                    }
                }

                if (!describedParticleSystems.Contains(tracked.id) &&
                    materialManager.TryDescribeMaterial(tintedMatId, out string matDescription))
                {
                    describedParticleSystems.Add(tracked.id);
                    var trailsModule = ps.trails;
                    string trailText = trailsModule.enabled
                        ? $"trails {trailsModule.mode} (texture {trailsModule.textureMode}, ribbons {trailsModule.ribbonCount}, mat '{(pr.trailMaterial != null ? pr.trailMaterial.name : "none")}' shader '{(pr.trailMaterial != null && pr.trailMaterial.shader != null ? pr.trailMaterial.shader.name : "-")}')"
                        : "no trails";
                    if (VerboseLog.On) logger.LogInfo($"[ParticleSystem] '{pr.name}': {pr.renderMode}, align {pr.alignment}, space {ps.main.simulationSpace}, {actualAlive} alive, " +
                                   $"mat '{mat.name}' shader '{(mat.shader != null ? mat.shader.name : "-")}' queue {mat.renderQueue}{DescribeBlend(mat)}, " +
                                   $"look {look.kind} tint ({look.tint.r:F2},{look.tint.g:F2},{look.tint.b:F2}) x{look.alphaScale:F2}, sample ({pCol.r:F2},{pCol.g:F2},{pCol.b:F2},{pCol.a:F2}), size {ps.main.startSizeMultiplier:F1}, emission ({quantizedColor.r:F2},{quantizedColor.g:F2},{quantizedColor.b:F2}) emissive {isEmissiveParticle}, {trailText} | Remix {matDescription}");
                }

                if (pr.renderMode == ParticleSystemRenderMode.None)
                    continue;

                bool isFlatShape = ps.shape.enabled && (
                    ps.shape.shapeType == ParticleSystemShapeType.Circle || 
                    ps.shape.shapeType == ParticleSystemShapeType.Donut ||
                    ps.shape.shapeType == ParticleSystemShapeType.Rectangle
                );
                bool isShapeAligned = isFlatShape && ps.shape.alignToDirection;
                bool isJumpPad = traits.jumpPad;
                bool isJumpPadRing = isJumpPad && (isShapeAligned || isFlatShape || ps.main.startSizeMultiplier >= 1.0f);

                bool isSurfaceAligned = isShapeAligned || isJumpPadRing;

                if (!isSurfaceAligned && pr.renderMode == ParticleSystemRenderMode.Mesh)
                {
                    if (!TryInstanceMeshParticles(tracked.id, pr, ps, mainCam, tintedMatId, look, actualAlive, state) &&
                        !BakeMeshParticleSystem(pr, ps, mainCam, tintedMatId, look, state))
                    {
                        int particlesToDraw = Math.Min(actualAlive, 4096);
                        renderedParticles += particlesToDraw;
                        var bucket = GetOrCreateBatchBucket(tintedMatId, particlesToDraw);
                        long appendStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        AppendBillboardParticles(bucket, pr, ps, particlesToDraw, mainCam, camRight, camUp, camForward, look, isSurfaceAligned);
                        particleAppendMs += RemixProfiler.ElapsedMs(appendStart);
                        if (collectEmitters && emitterDetails.Count < 200)
                        {
                            emitterDetails.Add(new ParticleEmitterInfo
                            {
                                Name = pr.name,
                                Distance = dist,
                                ParticleCount = particlesToDraw,
                                Status = "Rendered (Batch)"
                            });
                        }
                    }
                    else
                    {
                        bakedMeshes++;
                        renderedParticles += actualAlive;
                        if (collectEmitters && emitterDetails.Count < 200)
                        {
                            emitterDetails.Add(new ParticleEmitterInfo
                            {
                                Name = pr.name,
                                Distance = dist,
                                ParticleCount = actualAlive,
                                Status = "Rendered (Bake)"
                            });
                        }
                    }
                }
                else
                {
                    int particlesToDraw = Math.Min(actualAlive, 4096);
                    renderedParticles += particlesToDraw;
                    var bucket = GetOrCreateBatchBucket(tintedMatId, particlesToDraw);
                    long appendStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    AppendBillboardParticles(bucket, pr, ps, particlesToDraw, mainCam, camRight, camUp, camForward, look, isSurfaceAligned);
                    particleAppendMs += RemixProfiler.ElapsedMs(appendStart);
                    if (collectEmitters && emitterDetails.Count < 200)
                    {
                        emitterDetails.Add(new ParticleEmitterInfo
                        {
                            Name = pr.name,
                            Distance = dist,
                            ParticleCount = particlesToDraw,
                            Status = "Rendered (Batch)"
                        });
                    }
                }

                // Capture trails if enabled on this particle system
                var trails = ps.trails;
                if (trails.enabled)
                {
                    BakeParticleTrails(pr, ps, mainCam, pCol, emIntensity, state);
                }
            }

            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleRead, readMs);
            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleBuild, RemixProfiler.ElapsedMs(loopStart) - readMs);
            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleAppend, particleAppendMs);
            RemixProfiler.AddParticles(renderedParticles, emittingCount, particleCurveCount);
            long emitStart = System.Diagnostics.Stopwatch.GetTimestamp();

            // Emit batched billboard meshes to state.skinned
            for (int b = 0; b < _activeParticleBatches.Count; b++)
            {
                var bucket = _activeParticleBatches[b];
                if (bucket.ParticleCount <= 0 || bucket.Vertices.Count == 0) continue;

                ulong remixMeshHash = 0x4000000000000000UL | ((ulong)(uint)bucket.SubBucketIndex << 32) | (ulong)(uint)bucket.MaterialId;

                int vertexCount = bucket.Vertices.Count, indexCount = bucket.Triangles.Count;
                var buffers = TakeParticleBuffers(remixMeshHash, frameCount, vertexCount, indexCount);
                if (buffers != null)
                {
                    bucket.Vertices.CopyTo(buffers.vertices);
                    bucket.Normals.CopyTo(buffers.normals);
                    bucket.UVs.CopyTo(buffers.uvs);
                    bucket.Colors.CopyTo(buffers.colors);
                    bucket.Triangles.CopyTo(buffers.triangles);
                }

                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.Billboard,
                    meshId = bucket.MaterialId,
                    remixMeshHash = remixMeshHash,
                    materialId = bucket.MaterialId,
                    vertices = buffers != null ? buffers.vertices : bucket.Vertices.ToArray(),
                    normals = buffers != null ? buffers.normals : bucket.Normals.ToArray(),
                    uvs = buffers != null ? buffers.uvs : bucket.UVs.ToArray(),
                    colors = buffers != null ? buffers.colors : bucket.Colors.ToArray(),
                    triangles = buffers != null ? buffers.triangles : bucket.Triangles.ToArray(),
                    usedVertexCount = buffers != null ? vertexCount : 0,
                    usedIndexCount = buffers != null ? indexCount : 0,
                    localToWorld = Matrix4x4.identity,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                    bakeFrame = frameCount
                });
            }

            RemixProfiler.AddDetail(RemixProfiler.Detail.ParticleEmit, RemixProfiler.ElapsedMs(emitStart));
            if (frameCount % 600 == 0)
                PruneParticleBuffers(frameCount);

            if (emitterDetails != null)
            {
                emitterDetails.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            }

            var prevStats = _latestParticleStats;
            _latestParticleStats = new ParticleStatsSnapshot
            {
                TotalTrackedSystems = totalTracked,
                ActiveSystems = activeCount,
                InactiveSystems = inactiveCount,
                DistanceCulledSystems = distanceCulled,
                EmittingSystems = emittingCount,
                RenderedParticles = renderedParticles,
                CulledParticles = culledParticles,
                BatchedDrawCalls = _activeParticleBatches.Count,
                BakedMeshes = bakedMeshes,
                FurthestSystemDistance = furthestDist,
                FurthestSystemName = furthestName,
                TotalTrackedTrails = prevStats != null ? prevStats.TotalTrackedTrails : 0,
                ActiveTrails = prevStats != null ? prevStats.ActiveTrails : 0,
                DistanceCulledTrails = prevStats != null ? prevStats.DistanceCulledTrails : 0,
                Emitters = emitterDetails != null ? emitterDetails.ToArray() : (prevStats != null ? prevStats.Emitters : Array.Empty<ParticleEmitterInfo>())
            };
        }

        private ParticleBatchBucket GetOrCreateBatchBucket(int matId, int neededParticles)
        {
            if (_particleBatchesByMat.TryGetValue(matId, out var existing))
            {
                if (existing.Vertices.Count + neededParticles * 4 <= 65535)
                    return existing;
            }

            int subIndex = existing != null ? existing.SubBucketIndex + 1 : 0;

            ParticleBatchBucket bucket;
            if (_batchBucketPool.Count > 0)
            {
                bucket = _batchBucketPool[_batchBucketPool.Count - 1];
                _batchBucketPool.RemoveAt(_batchBucketPool.Count - 1);
                bucket.Reset(matId, subIndex);
            }
            else
            {
                bucket = new ParticleBatchBucket();
                bucket.Reset(matId, subIndex);
            }

            _particleBatchesByMat[matId] = bucket;
            _activeParticleBatches.Add(bucket);
            return bucket;
        }

        // A particle material's blend and tint (ParticleLook), worked out once per material.
        private ParticleLook GetParticleLook(Material mat, bool snapshot)
        {
            int id = mat.GetInstanceID();
            if (!_particleLookCache.TryGetValue(id, out var look))
            {
                look = ParticleLook.Of(mat, snapshot);
                _particleLookCache[id] = look;
            }
            return look;
        }

        private static bool IsEmissiveParticleSystem(ParticleSystemRenderer pr, Material mat)
        {
            if (mat != null)
            {
                if (mat.HasProperty("EMISSIVE") && mat.GetFloat("EMISSIVE") > 0.5f)
                    return true;
                if (mat.IsKeywordEnabled("_EMISSION") && mat.HasProperty("_EmissionColor"))
                {
                    Color ec = mat.GetColor("_EmissionColor");
                    if (ec.r > 0.05f || ec.g > 0.05f || ec.b > 0.05f)
                        return true;
                }
            }

            string name = (pr != null ? pr.name : "") + " " + (mat != null ? mat.name : "");
            if (name.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("mist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dust", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("blood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("gore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("gib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("splash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("drip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("rubble", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("debris", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dirt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (name.IndexOf("spark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("flame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("laser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("flash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("explo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("electr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("lightn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("plasma", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("orb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("energy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("ember", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("portal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("holy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("hell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("radian", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("additive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private double particleAppendMs;
        private int particleCurveCount;
        // Particle systems and trails described in the log once (what they are, and how they reach Remix).
        private readonly HashSet<int> describedParticleSystems = new HashSet<int>();
        private readonly HashSet<int> describedParticleTrails = new HashSet<int>();

        private static string DescribeBlend(Material mat)
        {
            string text = "";
            if (mat.HasProperty("_SrcBlend")) text += $" src {mat.GetFloat("_SrcBlend")}";
            if (mat.HasProperty("_DstBlend")) text += $" dst {mat.GetFloat("_DstBlend")}";
            if (mat.HasProperty("_Surface")) text += $" surface {mat.GetFloat("_Surface")}";
            if (mat.HasProperty("_Blend")) text += $" blend {mat.GetFloat("_Blend")}";
            if (mat.HasProperty("_ZWrite")) text += $" zwrite {mat.GetFloat("_ZWrite")}";
            var keywords = mat.shaderKeywords;
            if (keywords != null && keywords.Length > 0) text += $" keywords [{string.Join(" ", keywords)}]";
            return text.Length > 0 ? " (" + text.Trim() + ")" : "";
        }

        // Particle geometry goes to the render thread in reused buffers, not new arrays every
        // frame (megabytes per frame, and a garbage collection every few frames). Each mesh
        // has three sets; a set is refilled only when neither the frame the render thread is
        // drawing nor the last published one uses it.
        internal static volatile int RenderingFrame = -1;
        internal static volatile int PublishedFrame = -1;

        private sealed class ParticleBuffers
        {
            public Vector3[] vertices = Array.Empty<Vector3>();
            public Vector3[] normals = Array.Empty<Vector3>();
            public Vector2[] uvs = Array.Empty<Vector2>();
            public Color32[] colors = Array.Empty<Color32>();
            public int[] triangles = Array.Empty<int>();
            public int frame = int.MinValue;

            public void Ensure(int vertexCount, int indexCount)
            {
                if (vertices.Length < vertexCount)
                {
                    int capacity = Mathf.NextPowerOfTwo(vertexCount);
                    vertices = new Vector3[capacity];
                    normals = new Vector3[capacity];
                    uvs = new Vector2[capacity];
                    colors = new Color32[capacity];
                }
                if (triangles.Length < indexCount)
                    triangles = new int[Mathf.NextPowerOfTwo(indexCount)];
            }
        }

        private sealed class ParticleBufferRing
        {
            public readonly ParticleBuffers[] sets = { new ParticleBuffers(), new ParticleBuffers(), new ParticleBuffers() };
            public int lastUsedFrame;
        }

        private readonly Dictionary<ulong, ParticleBufferRing> particleBufferRings = new Dictionary<ulong, ParticleBufferRing>();
        private readonly List<ulong> staleParticleBufferKeys = new List<ulong>();

        private ParticleBuffers TakeParticleBuffers(ulong meshHash, int frameCount, int vertexCount, int indexCount)
        {
            if (!particleBufferRings.TryGetValue(meshHash, out var ring))
            {
                ring = new ParticleBufferRing();
                particleBufferRings[meshHash] = ring;
            }
            ring.lastUsedFrame = frameCount;
            int rendering = RenderingFrame, published = PublishedFrame;
            ParticleBuffers buffers = null;
            foreach (var set in ring.sets)
            {
                if (set.frame != frameCount && set.frame != rendering && set.frame != published)
                {
                    buffers = set;
                    break;
                }
            }
            if (buffers == null)
                return null; // cannot happen with three sets; the caller allocates instead
            buffers.frame = frameCount;
            buffers.Ensure(vertexCount, indexCount);
            return buffers;
        }

        private void PruneParticleBuffers(int frameCount)
        {
            staleParticleBufferKeys.Clear();
            foreach (var kvp in particleBufferRings)
            {
                if (frameCount - kvp.Value.lastUsedFrame > 600)
                    staleParticleBufferKeys.Add(kvp.Key);
            }
            foreach (var key in staleParticleBufferKeys)
                particleBufferRings.Remove(key);
        }

        private readonly List<Vector3> bakeVertices = new List<Vector3>();
        private readonly List<Vector3> bakeNormals = new List<Vector3>();
        private readonly List<Vector2> bakeUvs = new List<Vector2>();
        private readonly List<Color32> bakeColors = new List<Color32>();
        private readonly List<int> bakeTriangles = new List<int>();

        private void AppendBillboardParticles(
            ParticleBatchBucket bucket,
            ParticleSystemRenderer pr,
            ParticleSystem ps,
            int numParticlesAlive,
            Camera cam,
            Vector3 camRight,
            Vector3 camUp,
            Vector3 camForward,
            ParticleLook look,
            bool isSurfaceAligned = false)
        {
            var mainModule = ps.main;
            bool isWorldSpace = mainModule.simulationSpace == ParticleSystemSimulationSpace.World;
            bool isCustomSpace = mainModule.simulationSpace == ParticleSystemSimulationSpace.Custom && mainModule.customSimulationSpace != null;
            Matrix4x4 sysTransform = isWorldSpace ? Matrix4x4.identity 
                : (isCustomSpace ? mainModule.customSimulationSpace.localToWorldMatrix : ps.transform.localToWorldMatrix);

            bool isStartSize3D = mainModule.startSize3D;
            bool hasSizeOverLifetime = ps.sizeOverLifetime.enabled;
            bool hasColorOverLifetime = ps.colorOverLifetime.enabled;
            if (hasSizeOverLifetime || hasColorOverLifetime)
                particleCurveCount += numParticlesAlive;

            var texModule = ps.textureSheetAnimation;
            bool useTexSheet = texModule.enabled;
            int tilesX = useTexSheet ? Math.Max(1, texModule.numTilesX) : 1;
            int tilesY = useTexSheet ? Math.Max(1, texModule.numTilesY) : 1;
            float uvWidth = 1f / tilesX;
            float uvHeight = 1f / tilesY;

            var renderMode = pr.renderMode;
            var alignment = pr.alignment;
            bool isHorizontal = renderMode == ParticleSystemRenderMode.HorizontalBillboard;
            bool isVertical = renderMode == ParticleSystemRenderMode.VerticalBillboard;
            bool isStretch = renderMode == ParticleSystemRenderMode.Stretch;
            bool isStandardView = !isSurfaceAligned && !isHorizontal && !isVertical && !isStretch && alignment == ParticleSystemRenderSpace.View;

            Vector3 upDir = pr.transform.up;
            if (upDir.sqrMagnitude < 0.001f) upDir = Vector3.up;

            Vector3 defaultNormal = -camForward;
            if (isSurfaceAligned) defaultNormal = upDir;
            else if (isHorizontal || alignment == ParticleSystemRenderSpace.World) defaultNormal = Vector3.up;
            else if (alignment == ParticleSystemRenderSpace.Local) defaultNormal = pr.transform.forward;

            int baseVert = bucket.Vertices.Count;

            for (int i = 0; i < numParticlesAlive; i++)
            {
                var p = _particleBuffer[i];
                Vector3 pos = isWorldSpace ? p.position : sysTransform.MultiplyPoint3x4(p.position);
                
                Vector3 size;
                if (hasSizeOverLifetime)
                    size = isStartSize3D ? p.GetCurrentSize3D(ps) : (Vector3.one * p.GetCurrentSize(ps));
                else
                    size = isStartSize3D ? p.startSize3D : (Vector3.one * p.startSize);

                Color32 col = look.Vertex(hasColorOverLifetime ? p.GetCurrentColor(ps) : p.startColor);

                float rot = p.rotation;
                float cosR = 1f;
                float sinR = 0f;
                if (rot != 0f)
                {
                    float rotRad = rot * Mathf.Deg2Rad;
                    cosR = Mathf.Cos(rotRad);
                    sinR = Mathf.Sin(rotRad);
                }

                Vector3 hR, hU;
                Vector3 rAxis = camRight, uAxis = camUp;
                if (isStandardView)
                {
                    if (rot == 0f)
                    {
                        hR = camRight * (size.x * 0.5f);
                        hU = camUp * (size.y * 0.5f);
                    }
                    else
                    {
                        rAxis = camRight * cosR + camUp * sinR;
                        uAxis = -camRight * sinR + camUp * cosR;
                        hR = rAxis * (size.x * 0.5f);
                        hU = uAxis * (size.y * 0.5f);
                    }
                }
                else if (isSurfaceAligned)
                {
                    Vector3 rightDir = pr.transform.right;
                    Vector3 fwdDir = pr.transform.forward;
                    if (rightDir.sqrMagnitude < 0.001f) rightDir = Vector3.right;
                    if (fwdDir.sqrMagnitude < 0.001f) fwdDir = Vector3.forward;

                    rAxis = rightDir * cosR + fwdDir * sinR;
                    uAxis = -rightDir * sinR + fwdDir * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                    pos += upDir * 0.02f;
                }
                else if (isHorizontal)
                {
                    rAxis = Vector3.right * cosR + Vector3.forward * sinR;
                    uAxis = -Vector3.right * sinR + Vector3.forward * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (isVertical)
                {
                    Vector3 facing = Vector3.ProjectOnPlane(camForward, Vector3.up).normalized;
                    if (facing.sqrMagnitude < 0.001f) facing = Vector3.forward;
                    Vector3 side = Vector3.Cross(Vector3.up, facing).normalized;
                    rAxis = side * cosR + Vector3.up * sinR;
                    uAxis = -side * sinR + Vector3.up * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (isStretch)
                {
                    Vector3 rawVel = p.totalVelocity.sqrMagnitude > 0.0001f ? p.totalVelocity : p.velocity;
                    Vector3 worldVel = isWorldSpace ? rawVel : sysTransform.MultiplyVector(rawVel);
                    float speed = worldVel.magnitude;
                    if (speed > 0.001f)
                    {
                        Vector3 velDir = worldVel / speed;
                        Vector3 cross = Vector3.Cross(velDir, camForward).normalized;
                        if (cross.sqrMagnitude < 0.001f) cross = camRight;
                        float stretchLen = Mathf.Clamp(size.y * Mathf.Abs(pr.lengthScale) + speed * pr.velocityScale, size.y, 20f);
                        hR = cross * (size.x * 0.5f);
                        hU = velDir * (stretchLen * 0.5f);
                        rAxis = cross;
                        uAxis = velDir;
                    }
                    else
                    {
                        rAxis = camRight * cosR + camUp * sinR;
                        uAxis = -camRight * sinR + camUp * cosR;
                        hR = rAxis * (size.x * 0.5f);
                        hU = uAxis * (size.y * 0.5f);
                    }
                }
                else if (alignment == ParticleSystemRenderSpace.Local)
                {
                    // Aligned with the ParticleSystem transform (e.g. jump pads, surface rings)
                    rAxis = pr.transform.right * cosR + pr.transform.up * sinR;
                    uAxis = -pr.transform.right * sinR + pr.transform.up * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (alignment == ParticleSystemRenderSpace.World)
                {
                    // Aligned with world axes (horizontal plane)
                    rAxis = Vector3.right * cosR + Vector3.forward * sinR;
                    uAxis = -Vector3.right * sinR + Vector3.forward * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (alignment == ParticleSystemRenderSpace.Facing)
                {
                    // Faces camera position directly
                    Vector3 toCam = (cam.transform.position - pos).normalized;
                    if (toCam.sqrMagnitude < 0.001f) toCam = -camForward;
                    Vector3 right = Vector3.Cross(camUp, toCam).normalized;
                    if (right.sqrMagnitude < 0.001f) right = camRight;
                    Vector3 up = Vector3.Cross(toCam, right).normalized;
                    rAxis = right * cosR + up * sinR;
                    uAxis = -right * sinR + up * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else if (alignment == ParticleSystemRenderSpace.Velocity)
                {
                    Vector3 rawVel = p.totalVelocity.sqrMagnitude > 0.0001f ? p.totalVelocity : p.velocity;
                    Vector3 worldVel = isWorldSpace ? rawVel : sysTransform.MultiplyVector(rawVel);
                    Vector3 velDir = worldVel.normalized;
                    if (velDir.sqrMagnitude < 0.001f) velDir = pr.transform.forward;
                    Vector3 side = Vector3.Cross(velDir, camForward).normalized;
                    if (side.sqrMagnitude < 0.001f) side = Vector3.Cross(velDir, Vector3.up).normalized;
                    if (side.sqrMagnitude < 0.001f) side = camRight;
                    rAxis = side * cosR + velDir * sinR;
                    uAxis = -side * sinR + velDir * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }
                else
                {
                    rAxis = camRight * cosR + camUp * sinR;
                    uAxis = -camRight * sinR + camUp * cosR;
                    hR = rAxis * (size.x * 0.5f);
                    hU = uAxis * (size.y * 0.5f);
                }

                Vector3 norm = defaultNormal;
                if (!isStandardView)
                {
                    norm = isSurfaceAligned ? upDir : Vector3.Cross(uAxis, rAxis).normalized;
                    if (norm.sqrMagnitude < 0.001f) norm = defaultNormal;
                }

                int vi = baseVert + i * 4;
                bucket.Vertices.Add(pos - hR - hU);
                bucket.Vertices.Add(pos + hR - hU);
                bucket.Vertices.Add(pos + hR + hU);
                bucket.Vertices.Add(pos - hR + hU);

                bucket.Normals.Add(norm);
                bucket.Normals.Add(norm);
                bucket.Normals.Add(norm);
                bucket.Normals.Add(norm);

                float uvX0 = 0f, uvX1 = 1f, uvY0 = 0f, uvY1 = 1f;
                if (useTexSheet)
                {
                    float progress = p.startLifetime > 0.0001f ? (1f - (p.remainingLifetime / p.startLifetime)) : 0f;
                    int totalFrames = tilesX * tilesY;
                    int frameIdx = Mathf.Clamp(Mathf.FloorToInt(progress * totalFrames), 0, totalFrames - 1);
                    int tx = frameIdx % tilesX;
                    int ty = tilesY - 1 - (frameIdx / tilesX);
                    uvX0 = tx * uvWidth;
                    uvX1 = (tx + 1) * uvWidth;
                    uvY0 = ty * uvHeight;
                    uvY1 = (ty + 1) * uvHeight;
                }

                bucket.UVs.Add(new Vector2(uvX0, uvY0));
                bucket.UVs.Add(new Vector2(uvX1, uvY0));
                bucket.UVs.Add(new Vector2(uvX1, uvY1));
                bucket.UVs.Add(new Vector2(uvX0, uvY1));

                bucket.Colors.Add(col);
                bucket.Colors.Add(col);
                bucket.Colors.Add(col);
                bucket.Colors.Add(col);

                bucket.Triangles.Add(vi + 0);
                bucket.Triangles.Add(vi + 2);
                bucket.Triangles.Add(vi + 1);
                bucket.Triangles.Add(vi + 0);
                bucket.Triangles.Add(vi + 3);
                bucket.Triangles.Add(vi + 2);
            }

            bucket.ParticleCount += numParticlesAlive;
        }

        // Mesh-mode particle systems drawn as instances of their mesh, uploaded once, instead of
        // BakeMesh'd and re-uploaded every frame (a swarm of a hundred small meshes is tens of
        // thousands of vertices a frame). Unity documents no exact per-particle transform, so
        // each system's is picked from a few models by matching BakeMesh, and checked again
        // now and then; a system none matches keeps baking.
        private sealed class MeshParticleInstancing
        {
            public Mesh mesh;
            public bool rejected;
            public int model = -1;
            public int checkFrame;
            public Vector3[] vertices, normals;
            public Vector2[] uvs;
            public Color32[] meshColors; // null: white
            public int[] triangles;
            // (material, quantized particle color) -> mesh with that color baked into its vertices
            public readonly Dictionary<long, ulong> variantHashes = new Dictionary<long, ulong>();
            public readonly Dictionary<ulong, Color32[]> variantColors = new Dictionary<ulong, Color32[]>();
        }

        private struct MeshParticleFrame
        {
            public Quaternion systemRotation, cameraRotation;
            public Vector3 lossyScale, pivot;
        }

        private readonly Dictionary<int, MeshParticleInstancing> meshParticleInstancing = new Dictionary<int, MeshParticleInstancing>();
        private readonly List<long> meshParticleVariantKeys = new List<long>();
        private readonly List<Vector3> meshParticleSizes = new List<Vector3>();
        private const int MaxInstancedMeshParticles = 1024;
        private const int MeshParticleCheckInterval = 300;
        private const int MeshParticleCheckParticles = 16;
        private const int MeshParticleVariantLimit = 64;
        // Rotation frames x Euler orders x scales.
        private const int MeshParticleBases = 5, MeshParticleOrders = 3, MeshParticleScales = 2;
        private const int MeshParticleModels = MeshParticleBases * MeshParticleOrders * MeshParticleScales;

        private static Matrix4x4 MeshParticleMatrix(int model, ref ParticleSystem.Particle particle, Vector3 size, in MeshParticleFrame frame)
        {
            int scaleMode = model % MeshParticleScales;
            int order = (model / MeshParticleScales) % MeshParticleOrders;
            int frameMode = model / (MeshParticleScales * MeshParticleOrders);

            Vector3 e = particle.rotation3D;
            Quaternion rx = Quaternion.AngleAxis(e.x, Vector3.right);
            Quaternion ry = Quaternion.AngleAxis(e.y, Vector3.up);
            Quaternion rz = Quaternion.AngleAxis(e.z, Vector3.forward);
            Quaternion rotation = order == 0 ? ry * rx * rz      // Quaternion.Euler: Z, then X, then Y
                                : order == 1 ? rz * ry * rx      // X, then Y, then Z
                                : rx * ry * rz;                  // Z, then Y, then X
            switch (frameMode)
            {
                case 1: rotation = frame.systemRotation * rotation; break;
                case 2: rotation = Quaternion.Inverse(frame.systemRotation) * rotation; break;
                case 3: rotation = frame.cameraRotation * rotation; break;
                case 4: rotation = frame.cameraRotation * Quaternion.AngleAxis(180f, Vector3.up) * rotation; break;
            }
            if (scaleMode == 1)
                size = Vector3.Scale(size, frame.lossyScale);
            return Matrix4x4.TRS(particle.position, rotation, size) * Matrix4x4.Translate(frame.pivot);
        }

        private bool PrepareMeshParticleInstancing(MeshParticleInstancing inst, ParticleSystemRenderer pr, Mesh mesh, out string reason)
        {
            reason = null;
            inst.mesh = mesh;
            inst.model = -1;
            inst.variantHashes.Clear();
            inst.variantColors.Clear();
            if (mesh.isReadable)
            {
                inst.vertices = mesh.vertices;
                var normals = mesh.normals;
                inst.normals = normals != null && normals.Length == inst.vertices.Length ? normals : null;
                var uvs = mesh.uv;
                inst.uvs = uvs != null && uvs.Length == inst.vertices.Length ? uvs : null;
                var colors = mesh.colors32;
                inst.meshColors = colors != null && colors.Length == inst.vertices.Length ? colors : null;
                inst.triangles = mesh.triangles;
            }
            else
            {
                if (!NativeMeshReader.ReadMeshFromGPU(mesh, out var positions, out var normals, out var uvs, out var subMeshIndices))
                {
                    reason = "GPU read of its non-readable mesh failed";
                    return false;
                }
                inst.vertices = positions;
                inst.normals = normals != null && normals.Length == positions.Length ? normals : null;
                inst.uvs = uvs != null && uvs.Length == positions.Length ? uvs : null;
                inst.meshColors = null;
                var all = new List<int>();
                foreach (var sub in subMeshIndices)
                    if (sub != null) all.AddRange(sub);
                inst.triangles = all.ToArray();
            }
            if (inst.vertices == null || inst.vertices.Length == 0 || inst.triangles == null || inst.triangles.Length < 3)
            {
                reason = "empty mesh";
                return false;
            }
            return true;
        }

        // Compares the models with BakeMesh on the first particles: the current one first, then
        // all of them when it no longer matches.
        private bool CheckMeshParticleModel(MeshParticleInstancing inst, ParticleSystemRenderer pr, ParticleSystem ps, Camera cam,
                                            int alive, in MeshParticleFrame frame, out string reason)
        {
            reason = null;
            if (_reusableParticleMesh == null)
                _reusableParticleMesh = new Mesh { name = "ReusableParticleBakeMesh" };
            _reusableParticleMesh.Clear();
#pragma warning disable CS0618
            if (cam != null) pr.BakeMesh(_reusableParticleMesh, cam, false);
            else pr.BakeMesh(_reusableParticleMesh, false);
#pragma warning restore CS0618
            _reusableParticleMesh.GetVertices(bakeVertices);
            int meshVertices = inst.vertices.Length;
            if (bakeVertices.Count != alive * meshVertices)
            {
                reason = $"BakeMesh made {bakeVertices.Count} vertices, not {alive} x {meshVertices}";
                return false;
            }
            int checkCount = Math.Min(alive, MeshParticleCheckParticles);
            meshParticleSizes.Clear();
            for (int i = 0; i < checkCount; i++)
                meshParticleSizes.Add(_particleBuffer[i].GetCurrentSize3D(ps));
            float tolerance = Mathf.Max(1e-3f * _reusableParticleMesh.bounds.size.magnitude, 1e-4f);
            float toleranceSqr = tolerance * tolerance;

            int best = -1;
            float bestError = float.PositiveInfinity;
            for (int pass = 0; pass < 2 && best < 0; pass++)
            {
                for (int model = 0; model < MeshParticleModels; model++)
                {
                    if (pass == 0 ? model != inst.model : model == inst.model) continue;
                    float worst = 0f;
                    for (int i = 0; i < checkCount && worst <= toleranceSqr; i++)
                    {
                        var m = MeshParticleMatrix(model, ref _particleBuffer[i], meshParticleSizes[i], frame);
                        int baseVertex = i * meshVertices;
                        for (int v = 0; v < meshVertices; v++)
                        {
                            float d = (m.MultiplyPoint3x4(inst.vertices[v]) - bakeVertices[baseVertex + v]).sqrMagnitude;
                            if (d > worst) { worst = d; if (worst > toleranceSqr) break; }
                        }
                    }
                    if (worst < bestError) bestError = worst;
                    if (worst <= toleranceSqr) { best = model; break; }
                }
            }
            if (best < 0)
            {
                reason = $"no transform model matches BakeMesh (closest {Mathf.Sqrt(bestError):G3} > {tolerance:G3})";
                return false;
            }
            if (best != inst.model && inst.model >= 0)
                logger.LogInfo($"[MeshParticles] '{pr.name}' transform model {inst.model} -> {best}");
            inst.model = best;
            return true;
        }

        private bool TryInstanceMeshParticles(int systemId, ParticleSystemRenderer pr, ParticleSystem ps, Camera cam,
                                              int matId, ParticleLook look, int alive, FrameState state)
        {
            if (alive <= 0 || alive > MaxInstancedMeshParticles)
                return false;
            if (!meshParticleInstancing.TryGetValue(systemId, out var inst))
            {
                inst = new MeshParticleInstancing();
                meshParticleInstancing[systemId] = inst;
            }
            if (inst.rejected)
                return false;

            var main = ps.main;
            var space = main.simulationSpace;
            Mesh mesh = pr.mesh;
            string reason = null;
            if (space == ParticleSystemSimulationSpace.Custom) reason = "custom simulation space";
            else if (mesh == null || pr.meshCount != 1) reason = "not exactly one mesh";
            else if (UnityCompat.GetParticleFlip(pr) != Vector3.zero) reason = "flipped particles";
            else if (!ReferenceEquals(inst.mesh, mesh) && !PrepareMeshParticleInstancing(inst, pr, mesh, out reason)) { }
            if (reason != null)
            {
                inst.rejected = true;
                logger.LogInfo($"[MeshParticles] '{pr.name}' keeps BakeMesh: {reason}");
                return false;
            }

            var tr = pr.transform;
            Quaternion systemRotation = tr.rotation;
            Quaternion cameraRotation = cam != null ? cam.transform.rotation : Quaternion.identity;
            if (space == ParticleSystemSimulationSpace.Local)
                cameraRotation = Quaternion.Inverse(systemRotation) * cameraRotation;
            var frame = new MeshParticleFrame
            {
                systemRotation = systemRotation,
                cameraRotation = cameraRotation,
                lossyScale = tr.lossyScale,
                pivot = pr.pivot
            };

            int frameCount = state.frameCount;
            if (inst.model < 0 || frameCount >= inst.checkFrame)
            {
                bool first = inst.model < 0;
                if (!CheckMeshParticleModel(inst, pr, ps, cam, alive, frame, out reason))
                {
                    if (first || !reason.StartsWith("BakeMesh made"))
                    {
                        inst.rejected = true;
                        logger.LogInfo($"[MeshParticles] '{pr.name}' keeps BakeMesh: {reason}");
                    }
                    return false;
                }
                inst.checkFrame = frameCount + MeshParticleCheckInterval;
                if (first)
                    logger.LogInfo($"[MeshParticles] '{pr.name}' instanced: {alive} particles of '{inst.mesh.name}' ({inst.vertices.Length} verts), transform model {inst.model}");
            }

            // Particle colors, quantized like the billboard batches: one mesh per color in use.
            meshParticleVariantKeys.Clear();
            for (int i = 0; i < alive; i++)
            {
                Color32 c = look.Vertex(_particleBuffer[i].GetCurrentColor(ps));
                int key = ((c.r * 15 + 127) / 255 << 12) | ((c.g * 15 + 127) / 255 << 8) | ((c.b * 15 + 127) / 255 << 4) | ((c.a * 15 + 127) / 255);
                long variantKey = ((long)matId << 32) | (uint)key;
                if (!inst.variantHashes.ContainsKey(variantKey))
                {
                    if (inst.variantHashes.Count >= MeshParticleVariantLimit)
                    {
                        inst.rejected = true;
                        logger.LogInfo($"[MeshParticles] '{pr.name}' keeps BakeMesh: more than {MeshParticleVariantLimit} particle colors");
                        return false;
                    }
                    ulong hash = 14695981039346656037UL;
                    hash = (hash ^ (uint)inst.mesh.GetInstanceID()) * 1099511628211UL;
                    hash = (hash ^ (uint)systemId) * 1099511628211UL;
                    hash = (hash ^ (ulong)variantKey) * 1099511628211UL;
                    hash = (hash & 0x1FFFFFFFFFFFFFFFUL) | 0x6000000000000000UL;
                    var tint = new Color32((byte)((key >> 12 & 15) * 17), (byte)((key >> 8 & 15) * 17), (byte)((key >> 4 & 15) * 17), (byte)((key & 15) * 17));
                    var colors = new Color32[inst.vertices.Length];
                    for (int v = 0; v < colors.Length; v++)
                    {
                        Color32 m = inst.meshColors != null ? inst.meshColors[v] : new Color32(255, 255, 255, 255);
                        colors[v] = new Color32((byte)(m.r * tint.r / 255), (byte)(m.g * tint.g / 255), (byte)(m.b * tint.b / 255), (byte)(m.a * tint.a / 255));
                    }
                    inst.variantHashes[variantKey] = hash;
                    inst.variantColors[hash] = colors;
                }
                meshParticleVariantKeys.Add(variantKey);
            }

            Matrix4x4 simToWorld = space == ParticleSystemSimulationSpace.World ? Matrix4x4.identity : tr.localToWorldMatrix;
            for (int i = 0; i < alive; i++)
            {
                ref var particle = ref _particleBuffer[i];
                var m = MeshParticleMatrix(inst.model, ref particle, particle.GetCurrentSize3D(ps), frame);
                ulong hash = inst.variantHashes[meshParticleVariantKeys[i]];
                state.skinned.Add(new SkinnedMeshData
                {
                    source = DynamicSource.MeshInstanced,
                    meshId = HashCombine(systemId, (int)particle.randomSeed),
                    remixMeshHash = hash,
                    materialId = matId,
                    vertices = inst.vertices,
                    normals = inst.normals,
                    uvs = inst.uvs,
                    colors = inst.variantColors[hash],
                    triangles = inst.triangles,
                    localToWorld = simToWorld * m,
                    boneTransforms = null,
                    skinningData = null,
                    categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                    bakeFrame = 1 // the geometry never changes: uploaded once
                });
            }
            return true;
        }

        private bool BakeMeshParticleSystem(ParticleSystemRenderer pr, ParticleSystem ps, Camera cam, int matId, ParticleLook look, FrameState state)
        {
            if (_reusableParticleMesh == null)
            {
                _reusableParticleMesh = new Mesh();
                _reusableParticleMesh.name = "ReusableParticleBakeMesh";
            }

            try
            {
                _reusableParticleMesh.Clear();
                // Pass useTransform=false so local-space particles stay in the emitter's local space
                // and receive the full TRS via localToWorld (useTransform=true only applies R+S without translation).
#pragma warning disable CS0618
                if (cam != null)
                {
                    pr.BakeMesh(_reusableParticleMesh, cam, false);
                }
                else
                {
                    pr.BakeMesh(_reusableParticleMesh, false);
                }
#pragma warning restore CS0618

                var mesh = _reusableParticleMesh;
                int vertexCount = mesh.vertexCount;
                if (vertexCount > 0 && mesh.subMeshCount > 0)
                {
                    ulong remixMeshHash = (ulong)(uint)pr.GetInstanceID() | 0x4000000000000000UL;
                    bool isWorldSpace = ps != null && ps.main.simulationSpace == ParticleSystemSimulationSpace.World;
                    Matrix4x4 particleLocalToWorld = isWorldSpace ? Matrix4x4.identity : pr.transform.localToWorldMatrix;

                    mesh.GetVertices(bakeVertices);
                    mesh.GetNormals(bakeNormals);
                    mesh.GetUVs(0, bakeUvs);
                    mesh.GetColors(bakeColors);
                    if (mesh.subMeshCount == 1)
                        mesh.GetTriangles(bakeTriangles, 0);
                    else
                    {
                        bakeTriangles.Clear();
                        bakeTriangles.AddRange(mesh.triangles);
                    }
                    int indexCount = bakeTriangles.Count;
                    if (indexCount == 0)
                        return false;
                    bool hasNormals = bakeNormals.Count == vertexCount;
                    bool hasUvs = bakeUvs.Count == vertexCount;
                    bool hasColors = bakeColors.Count == vertexCount;

                    var buffers = TakeParticleBuffers(remixMeshHash, state.frameCount, vertexCount, indexCount);
                    Vector3[] vertices, normals = null;
                    Vector2[] uvs = null;
                    Color32[] colors = null;
                    int[] triangles;
                    if (buffers != null)
                    {
                        bakeVertices.CopyTo(buffers.vertices);
                        bakeTriangles.CopyTo(buffers.triangles);
                        vertices = buffers.vertices;
                        triangles = buffers.triangles;
                        if (hasNormals) { bakeNormals.CopyTo(buffers.normals); normals = buffers.normals; }
                        if (hasUvs) { bakeUvs.CopyTo(buffers.uvs); uvs = buffers.uvs; }
                        if (hasColors) { bakeColors.CopyTo(buffers.colors); colors = buffers.colors; }
                    }
                    else
                    {
                        vertices = bakeVertices.ToArray();
                        triangles = bakeTriangles.ToArray();
                        if (hasNormals) normals = bakeNormals.ToArray();
                        if (hasUvs) uvs = bakeUvs.ToArray();
                        if (hasColors) colors = bakeColors.ToArray();
                    }

                    look.ApplyTo(colors, vertexCount);

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.MeshBaked,
                        meshId = pr.GetInstanceID(),
                        remixMeshHash = remixMeshHash,
                        materialId = matId,
                        vertices = vertices,
                        normals = normals,
                        uvs = uvs,
                        colors = colors,
                        triangles = triangles,
                        usedVertexCount = buffers != null ? vertexCount : 0,
                        usedIndexCount = buffers != null ? indexCount : 0,
                        localToWorld = particleLocalToWorld,
                        boneTransforms = null,
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = state.frameCount
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"BakeMeshParticleSystem failed on '{pr.name}': {ex.Message}");
            }
            return false;
        }

        private void BakeParticleTrails(
            ParticleSystemRenderer pr, 
            ParticleSystem ps, 
            Camera cam, 
            Color trailColor, 
            float emIntensity, 
            FrameState state)
        {
            if (_reusableParticleTrailMesh == null)
            {
                _reusableParticleTrailMesh = new Mesh { name = "ParticleTrailBakeMesh" };
            }

            try
            {
                _reusableParticleTrailMesh.Clear();
                // useTransform=false keeps vertices in the ParticleSystem's local space so we
                // can apply the full TRS via localToWorld below. This correctly handles both
                // world-space and local-space simulated particles (e.g. blood gibs attached to
                // a moving transform). With useTransform=true Unity only applies R+S without
                // the translation, which misplaces local-space particles.
#pragma warning disable CS0618
                pr.BakeTrailsMesh(_reusableParticleTrailMesh, cam, false);
#pragma warning restore CS0618

                var trailMesh = _reusableParticleTrailMesh;
                int vertexCount = trailMesh.vertexCount;
                if (vertexCount >= 3 && trailMesh.subMeshCount > 0)
                {
                    // Only use a dedicated trail material. Falling back to the particle's own
                    // billboard material (pr.sharedMaterial) causes opaque sprite quads to be
                    // rendered over the ribbon geometry, appearing as flat "squares".
                    Material trailMat = pr.trailMaterial;
                    if (trailMat == null)
                    {
                        var mats = pr.sharedMaterials;
                        if (mats != null && mats.Length > 1 && mats[1] != null)
                            trailMat = mats[1];
                    }
                    // No dedicated trail material → skip rather than using the wrong one.
                    if (trailMat == null || SceneMeshScanner.IsMaterialOrShaderInvisible(trailMat)) return;

                    // The trail material's tint and blend; its vertex colours are the particles'.
                    ParticleLook trailLook = GetParticleLook(trailMat, false);
                    bool trailEmissive = emIntensity > 0f || trailLook.IsAdditive;
                    Color trailEmission = trailLook.EmissionColor(trailColor);
                    int r4 = Mathf.Clamp((int)(trailEmission.r * 15f + 0.5f), 0, 15);
                    int g4 = Mathf.Clamp((int)(trailEmission.g * 15f + 0.5f), 0, 15);
                    int b4 = Mathf.Clamp((int)(trailEmission.b * 15f + 0.5f), 0, 15);
                    int colorKey = trailEmissive ? (1 << 12) | (r4 << 8) | (g4 << 4) | b4 : 0;
                    int trailMatId = unchecked(trailMat.GetInstanceID() * 397 ^ colorKey);

                    if (_capturedParticleMatIds.Add(trailMatId))
                    {
                        materialManager.CaptureMaterialTextures(
                            trailMat,
                            trailMatId,
                            mpbEmissiveColor: trailEmissive ? (Color?)new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f) : null,
                            mpbEmissiveIntensity: trailEmissive ? Mathf.Max(emIntensity, 0.8f) : 0f,
                            mpbColor: trailLook.MaterialColor,
                            suppressLightGeneration: true,
                            renderer: pr
                        );
                    }

                    ulong trailHash = (ulong)(uint)pr.GetInstanceID() | 0x4800000000000000UL;
                    if (!describedParticleTrails.Contains(pr.GetInstanceID()) &&
                        materialManager.TryDescribeMaterial(trailMatId, out string trailDescription))
                    {
                        describedParticleTrails.Add(pr.GetInstanceID());
                        if (VerboseLog.On) logger.LogInfo($"[ParticleTrail] '{pr.name}': {trailMesh.vertexCount} verts, {trailMesh.subMeshCount} submeshes, mat '{trailMat.name}' shader '{(trailMat.shader != null ? trailMat.shader.name : "-")}' queue {trailMat.renderQueue}{DescribeBlend(trailMat)}, " +
                                       $"normals {UnityCompat.MeshHasAttribute(trailMesh, UnityCompat.AttributeNormal)}, colors {UnityCompat.MeshHasAttribute(trailMesh, UnityCompat.AttributeColor)} | Remix {trailDescription}");
                    }

                    // Use the full local-to-world matrix so position is correctly applied.
                    // For world-space simulated particles the transform is usually identity/origin
                    // and this is still correct. For local-space (e.g. attached-gib) particles
                    // this is essential so the trails follow the gib in world space.
                    Matrix4x4 trailLocalToWorld = pr.transform.localToWorldMatrix;

                    // Read into reused buffers (a trail mesh is often tens of thousands of
                    // vertices a frame). Trails rarely carry normals: face the camera, as the
                    // billboards do, instead of computing face normals for every vertex.
                    trailMesh.GetVertices(bakeVertices);
                    trailMesh.GetNormals(bakeNormals);
                    trailMesh.GetUVs(0, bakeUvs);
                    trailMesh.GetColors(bakeColors);
                    if (trailMesh.subMeshCount == 1)
                        trailMesh.GetTriangles(bakeTriangles, 0);
                    else
                    {
                        bakeTriangles.Clear();
                        bakeTriangles.AddRange(trailMesh.triangles);
                    }
                    int indexCount = bakeTriangles.Count;
                    if (indexCount < 3)
                        return;
                    var buffers = TakeParticleBuffers(trailHash, state.frameCount, vertexCount, indexCount);
                    Vector3[] vertices, normals;
                    Vector2[] uvs = null;
                    Color32[] colors = null;
                    int[] triangles;
                    if (buffers != null)
                    {
                        bakeVertices.CopyTo(buffers.vertices);
                        bakeTriangles.CopyTo(buffers.triangles);
                        vertices = buffers.vertices;
                        triangles = buffers.triangles;
                        normals = buffers.normals;
                        if (bakeUvs.Count == vertexCount) { bakeUvs.CopyTo(buffers.uvs); uvs = buffers.uvs; }
                        if (bakeColors.Count == vertexCount) { bakeColors.CopyTo(buffers.colors); colors = buffers.colors; }
                    }
                    else
                    {
                        vertices = bakeVertices.ToArray();
                        triangles = bakeTriangles.ToArray();
                        normals = new Vector3[vertexCount];
                        if (bakeUvs.Count == vertexCount) uvs = bakeUvs.ToArray();
                        if (bakeColors.Count == vertexCount) colors = bakeColors.ToArray();
                    }
                    if (bakeNormals.Count == vertexCount)
                    {
                        bakeNormals.CopyTo(normals);
                    }
                    else
                    {
                        Vector3 facing = cam != null ? pr.transform.InverseTransformDirection(-cam.transform.forward) : Vector3.up;
                        facing = facing.sqrMagnitude > 1e-12f ? facing.normalized : Vector3.up;
                        for (int v = 0; v < vertexCount; v++)
                            normals[v] = facing;
                    }

                    trailLook.ApplyTo(colors, vertexCount);

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.ParticleTrail,
                        meshId = pr.GetInstanceID() ^ 0x0F0F0F,
                        remixMeshHash = trailHash,
                        materialId = trailMatId,
                        vertices = vertices,
                        normals = normals,
                        uvs = uvs,
                        colors = colors,
                        triangles = triangles,
                        usedVertexCount = buffers != null ? vertexCount : 0,
                        usedIndexCount = buffers != null ? indexCount : 0,
                        localToWorld = trailLocalToWorld,
                        boneTransforms = null,
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = state.frameCount
                    });
                }
            }
            catch (Exception ex)
            {
                if (configDebugLogInterval.Value > 0)
                    logger.LogWarning($"BakeParticleTrails failed on '{pr.name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Capture active TrailRenderer ribbons (bullets, gibs, blood trails, projectiles) using BakeMesh.
        /// </summary>
        public void CaptureTrailRenderers(FrameState state, int frameCount)
        {
            if (configCaptureParticles != null && !configCaptureParticles.Value)
                return;

            Camera mainCam = cameraHandler?.GetPreferredCamera() ?? Camera.main;
            if (mainCam == null || !mainCam.gameObject.activeInHierarchy || !mainCam.enabled || Time.frameCount < 10)
                return;

            // Throttle discovery of newly spawned TrailRenderers to avoid full-scene sweeps every frame
            if (frameCount % 4 == 0)
            {
                var activeTrails = UnityCompat.FindActiveSceneComponents<TrailRenderer>();
                for (int a = 0; a < activeTrails.Length; a++)
                {
                    var ar = activeTrails[a];
                    if (ar != null && trackedTrailRendererIds.Add(ar.GetInstanceID()))
                    {
                        trackedTrailRenderers.Add(new TrackedTrailRenderer
                        {
                            renderer = ar,
                            id = ar.GetInstanceID()
                        });
                    }
                }
            }

            // Always check camera hierarchy every frame for instantaneous first-person / viewmodel trails
            if (mainCam != null)
            {
                var camTrails = mainCam.GetComponentsInChildren<TrailRenderer>(false);
                for (int a = 0; a < camTrails.Length; a++)
                {
                    var ar = camTrails[a];
                    if (ar != null && trackedTrailRendererIds.Add(ar.GetInstanceID()))
                    {
                        trackedTrailRenderers.Add(new TrackedTrailRenderer
                        {
                            renderer = ar,
                            id = ar.GetInstanceID()
                        });
                    }
                }
            }

            // Periodically prune destroyed trail renderers
            if (frameCount % 180 == 0)
            {
                for (int d = trackedTrailRenderers.Count - 1; d >= 0; d--)
                {
                    var t = trackedTrailRenderers[d];
                    if (t.renderer == null)
                    {
                        trackedTrailRendererIds.Remove(t.id);
                        trackedTrailRenderers.RemoveAt(d);
                    }
                }
            }

            if (trackedTrailRenderers.Count == 0)
                return;

            if (_reusableTrailRendererMesh == null)
            {
                _reusableTrailRendererMesh = new Mesh { name = "TrailRendererBakeMesh" };
            }

            Vector3 camPos = mainCam.transform.position;
            float maxParticleDist = (configParticleMaxDistance != null && configParticleMaxDistance.Value > 0f)
                ? configParticleMaxDistance.Value
                : 60f;
            float maxParticleDistSqr = maxParticleDist * maxParticleDist;

            int trackedTrails = trackedTrailRenderers.Count;
            int renderingTrails = 0;
            int culledTrails = 0;

            for (int i = 0; i < trackedTrailRenderers.Count; i++)
            {
                var t = trackedTrailRenderers[i];
                var tr = t.renderer;
                if (tr == null || !tr.enabled || !tr.gameObject.activeInHierarchy)
                    continue;

                if (tr.positionCount < 2)
                    continue;

                if (configEnableParticleDistanceCulling != null && configEnableParticleDistanceCulling.Value && (tr.transform.position - camPos).sqrMagnitude > maxParticleDistSqr)
                {
                    culledTrails++;
                    continue;
                }

                renderingTrails++;

                Material mat = tr.sharedMaterial;
                if (mat == null)
                {
                    var mats = tr.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                        mat = mats[0];
                }
                if (mat == null || SceneMeshScanner.IsMaterialOrShaderInvisible(mat))
                    continue;

                try
                {
                    _reusableTrailRendererMesh.Clear();
                    tr.BakeMesh(_reusableTrailRendererMesh, mainCam, true);

                    var verts = _reusableTrailRendererMesh.vertices;
                    var tris = _reusableTrailRendererMesh.triangles;
                    if (verts == null || verts.Length < 3 || tris == null || tris.Length < 3)
                        continue;

                    // Resolve trail color
                    Color trCol = (tr.startColor + tr.endColor) * 0.5f;
                    if (trCol.a <= 0.01f)
                        trCol = tr.startColor;
                    if (trCol.a <= 0.01f && tr.colorGradient != null)
                        trCol = tr.colorGradient.Evaluate(0.5f);

                    // The material's tint and blend; the ribbon's colour gradient is its vertex colours.
                    ParticleLook look = GetParticleLook(mat, false);
                    Color trailEmission = look.EmissionColor(trCol);
                    int r4 = Mathf.Clamp((int)(trailEmission.r * 15f + 0.5f), 0, 15);
                    int g4 = Mathf.Clamp((int)(trailEmission.g * 15f + 0.5f), 0, 15);
                    int b4 = Mathf.Clamp((int)(trailEmission.b * 15f + 0.5f), 0, 15);
                    int colorKey = (r4 << 8) | (g4 << 4) | b4;
                    Color quantizedColor = new Color(r4 / 15f, g4 / 15f, b4 / 15f, 1f);

                    int trailMatId = unchecked(mat.GetInstanceID() * 397 ^ colorKey);
                    float emIntensity = look.IsMultiply ? 0f : look.IsAdditive || r4 != g4 || g4 != b4 ? 0.8f : 0.2f;

                    if (_capturedParticleMatIds.Add(trailMatId))
                    {
                        materialManager.CaptureMaterialTextures(
                            mat,
                            trailMatId,
                            mpbEmissiveColor: emIntensity > 0f ? (Color?)quantizedColor : null,
                            mpbEmissiveIntensity: emIntensity,
                            mpbColor: look.MaterialColor,
                            suppressLightGeneration: true,
                            renderer: tr
                        );
                    }
                    Color32[] trailColors = _reusableTrailRendererMesh.colors32;
                    look.ApplyTo(trailColors, trailColors != null ? trailColors.Length : 0);

                    ulong remixMeshHash = (ulong)(uint)tr.GetInstanceID() | 0x4C00000000000000UL;

                    state.skinned.Add(new SkinnedMeshData
                    {
                        source = DynamicSource.Trail,
                        meshId = tr.GetInstanceID(),
                        remixMeshHash = remixMeshHash,
                        materialId = trailMatId,
                        vertices = verts,
                        normals = _reusableTrailRendererMesh.normals,
                        uvs = _reusableTrailRendererMesh.uv,
                        colors = trailColors,
                        triangles = tris,
                        localToWorld = Matrix4x4.identity,
                        boneTransforms = null,
                        skinningData = null,
                        categoryFlags = (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE,
                        bakeFrame = frameCount
                    });
                }
                catch (Exception ex)
                {
                    if (configDebugLogInterval.Value > 0)
                        logger.LogWarning($"CaptureTrailRenderers failed on '{tr.name}': {ex.Message}");
                }
            }

            var snap = _latestParticleStats;
            if (snap != null)
            {
                snap.TotalTrackedTrails = trackedTrails;
                snap.ActiveTrails = renderingTrails;
                snap.DistanceCulledTrails = culledTrails;
            }
        }
        
        private const int MAX_REMIX_BONES = 256;
        private const int BONES_PER_VERTEX = 4; // D3D9 standard, Remix expectation
        
        private static readonly bool _hasBoneWeight1 = Type.GetType("UnityEngine.BoneWeight1, UnityEngine.CoreModule") != null;

        private static class ModernBoneWeightHelper
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static bool TryExtractBoneWeights(Mesh mesh, int vertexCount, float[] blendWeights, uint[] blendIndices)
            {
                var bonesPerVertex = mesh.GetBonesPerVertex();
                var allBoneWeights = mesh.GetAllBoneWeights();

                if (bonesPerVertex.Length == 0 || allBoneWeights.Length == 0)
                    return false;

                int weightIdx = 0;
                for (int v = 0; v < vertexCount; v++)
                {
                    int count = bonesPerVertex[v];
                    int baseIdx = v * BONES_PER_VERTEX;
                    for (int w = 0; w < count && w < BONES_PER_VERTEX; w++)
                    {
                        var bw = allBoneWeights[weightIdx + w];
                        blendWeights[baseIdx + w] = bw.weight;
                        blendIndices[baseIdx + w] = (uint)bw.boneIndex;
                    }
                    weightIdx += count;
                }
                return true;
            }
        }
    }
}
