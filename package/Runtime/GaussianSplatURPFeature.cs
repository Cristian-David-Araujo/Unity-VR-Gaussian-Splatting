// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        class GSRenderPass : ScriptableRenderPass
        {
            const string k_GaussianSplatRTName = "_GaussianSplatRT";
            internal CommandBuffer m_Cmb = null;

            public void Dispose()
            {
            }

            // Shared between the two render-graph passes below: SortAndRenderSplats issues the
            // splat draws AND returns the composite material, so pass 1 produces it and pass 2 uses it.
            Material m_MatComposite;

            class RenderPassData
            {
                internal TextureHandle gaussianRT;
                internal Camera camera;
                internal GSRenderPass pass;
                internal Matrix4x4 viewMatrix;
                internal Matrix4x4 projMatrix;
            }

            class ComposePassData
            {
                internal TextureHandle gaussianRT;
                internal TextureHandle activeColorTexture;
                internal bool flipY;
                internal GSRenderPass pass;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Cmb == null)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                if (Time.frameCount % 120 == 0)
                {
                    var d = cameraData.cameraTargetDescriptor;
                    Debug.Log($"[GS-DIAG] RecordRenderGraph cam='{cameraData.camera.name}' targetDim={d.dimension} vol={d.volumeDepth} msaa={d.msaaSamples} xrEnabled={(cameraData.xr != null && cameraData.xr.enabled)} viewCount={(cameraData.xr != null ? cameraData.xr.viewCount : -1)}");
                }

                var rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                // Always use a plain 2D texture for the gaussian RT.
                // The splats are rendered MONO (the RenderGaussianSplats shader has no stereo output),
                // so a single-layer Tex2D is correct.  In VR (multiview) the camera target is a
                // Texture2DArray, but the composite shader declares "Texture2D _GaussianSplatRT" and
                // replicates the same center-eye image into both eye slices via stereo instancing.
                rtDesc.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
                rtDesc.volumeDepth = 1;

                // Render the splats at REDUCED resolution. Splat rendering is heavily fragment/fill
                // bound: hundreds of thousands of large overlapping transparent quads, blended, and in
                // Multi-Pass VR this is done TWICE per frame (once per eye). At full eye resolution the
                // Quest's Adreno GPU can't finish in the frame budget when the dense splat cloud fills
                // the view, so the XR compositor reprojects -> one eye visibly flickers (and ONLY where
                // splats are, since empty views are cheap). Rendering at half the linear resolution
                // cuts the splat fragment/blend cost to ~1/4. The composite then bilinearly upsamples
                // the result back to full eye resolution. The splats' on-screen SIZE is unchanged
                // (it is defined in NDC and is independent of target resolution), only slightly softer.
                // Tune splatRTScale (1.0 = full res, 0.75 = ~56% fragments, 0.5 = 25% fragments).
                // Quest uses 0.5 for maximum performance on the Adreno GPU (composited via bilinear upsampling).
                float splatRTScale = (Application.platform == RuntimePlatform.Android) ? 0.5f : 1.0f;
                int gsRtW = Mathf.Max(1, Mathf.RoundToInt(rtDesc.width * splatRTScale));
                int gsRtH = Mathf.Max(1, Mathf.RoundToInt(rtDesc.height * splatRTScale));

                // Create a TRANSIENT render-graph texture (not a persistent imported RTHandle).  With
                // multi-pass VR each eye runs its own RecordRenderGraph; a single shared imported
                // texture written by both eye passes caused one eye to flicker (cross-eye resource
                // hazard).  A transient texture gives each eye pass its own isolated resource.
                var gaussianTexDesc = new TextureDesc(gsRtW, gsRtH)
                {
                    format = rtDesc.graphicsFormat,
                    dimension = TextureDimension.Tex2D,
                    slices = 1,
                    depthBufferBits = DepthBits.None,
                    msaaSamples = MSAASamples.None,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    // RenderGraph-managed clear (defense in depth).  The manual ClearRenderTarget inside
                    // the unsafe pass below is NOT reliably tracked by RenderGraph on tile GPUs, so on
                    // the FIRST eye of a Multi-Pass frame the transient texture could be sampled by the
                    // composite before it was actually cleared/resolved -> garbage RGB written over the
                    // whole eye buffer (opaque platform flickers, ONLY left eye, ONLY on device, ONLY in
                    // MR where the eye-buffer alpha is the passthrough composition mask). Letting the
                    // graph own the clear guarantees a zeroed, resolved resource on both eyes.
                    clearBuffer = true,
                    clearColor = Color.clear,
                    name = k_GaussianSplatRTName
                };
                TextureHandle gaussianRT = renderGraph.CreateTexture(gaussianTexDesc);

                // PASS 1 — render the splats into the offscreen gaussianRT.
                // This is kept SEPARATE from the composite pass on purpose: on tile-based GPUs (Adreno
                // on Quest) writing a render target and then sampling it within the same pass is a
                // read-after-write hazard — the tile data is not flushed to main memory yet, so the
                // composite reads garbage that changes every frame (whole-screen flicker).  Splitting
                // into two render-graph passes makes RenderGraph insert the barrier / tile resolve so
                // the splat image is in main memory before the composite samples it.
                using (var builder = renderGraph.AddUnsafePass<RenderPassData>("GaussianSplat.Render", out var passData))
                {
                    passData.gaussianRT = gaussianRT;
                    passData.camera = cameraData.camera;
                    passData.pass = this;
                    // Capture the CURRENT eye's view/projection matrices.  In multi-pass VR each eye
                    // is rendered as a separate pass, so view index 0 is the eye being rendered now.
                    passData.viewMatrix = cameraData.GetViewMatrix(0);
                    passData.projMatrix = cameraData.GetProjectionMatrix(0);

                    builder.UseTexture(gaussianRT, AccessFlags.Write);
                    // Prevent RenderGraph from culling this pass.  It writes a transient texture from
                    // an unsafe pass; in multi-pass VR the dependency analysis can drop the pass on one
                    // eye, so that eye loses the splats intermittently (left-eye on/off flicker).
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((RenderPassData data, UnsafeGraphContext ctx) =>
                    {
                        CommandBuffer nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                        // Render splats into gaussianRT without a depth attachment.  The active depth
                        // buffer uses the camera's MSAA sample count (e.g. 4x in VR) but gaussianRT is
                        // 1x MSAA; Vulkan requires all attachments in a pass to share the sample count.
                        // Splats are sorted back-to-front so they composite correctly without depth test.
                        ctx.cmd.SetRenderTarget(data.gaussianRT);
                        ctx.cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1, 0);

                        // When rendering into a custom render target during XR, the global stereo
                        // matrices (UNITY_MATRIX_VP) are NOT automatically bound.  The splat shader's
                        // _OptimizeForQuest path recomputes each splat's clip position from
                        // UNITY_MATRIX_VP, so without this the splats project off-screen (invisible).
                        // Bind the captured per-eye view/projection so UNITY_MATRIX_VP is correct.
                        nativeCmd.SetViewProjectionMatrices(data.viewMatrix, data.projMatrix);

                        data.pass.m_MatComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.camera, nativeCmd);
                    });
                }

                // PASS 2 — composite the resolved gaussianRT onto the camera color target.
                // Use a RASTER pass with SetRenderAttachment so RenderGraph binds the real eye color
                // buffer (manual SetRenderTarget inside an unsafe pass does not reliably bind the
                // camera target in XR — the clear/draw went nowhere and nothing showed up).
                using (var builder = renderGraph.AddRasterRenderPass<ComposePassData>("GaussianSplat.Compose", out var passData))
                {
                    passData.gaussianRT = gaussianRT;
                    passData.activeColorTexture = resourceData.activeColorTexture;
                    passData.flipY = cameraData.xr != null && cameraData.xr.enabled;
                    passData.pass = this;

                    // Read gaussianRT (forces the resolve from pass 1) and bind the scene color as the
                    // render attachment (preserved/loaded, then blended onto via the composite material).
                    builder.UseTexture(gaussianRT, AccessFlags.Read);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                    // The composite shader reads the global _GaussianSplatRT; setting a global from a
                    // raster pass requires explicitly allowing global-state modification.
                    builder.AllowGlobalStateModification(true);
                    // Don't let RenderGraph cull the composite on either eye.
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((ComposePassData data, RasterGraphContext ctx) =>
                    {
                        Material matComposite = data.pass.m_MatComposite;
                        if (matComposite == null)
                            return;

                        ctx.cmd.SetGlobalTexture(k_GaussianSplatRTName, data.gaussianRT);
                        // In VR the off-screen gaussianRT is rendered Y-flipped relative to the eye
                        // color buffer, so the composite samples it upside-down.  Flag the shader to
                        // flip the sample row.  (In the editor the orientation already matches.)
                        ctx.cmd.SetGlobalFloat("_GaussianSplatFlipY", data.flipY ? 1f : 0f);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
                    });
                }
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                // Composite AFTER post-processing so nothing overwrites our result.  When this ran at
                // BeforeRenderingTransparents the camera color we wrote was later replaced by the
                // transparent/post/blit passes, so even a solid red clear never reached the display.
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            CommandBuffer cmb = system.InitialClearCmdBuffer(cameraData.camera);
            m_Pass.m_Cmb = cmb;
            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
