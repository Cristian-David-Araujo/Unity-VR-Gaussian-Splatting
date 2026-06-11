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
            RTHandle m_RenderTarget;
            internal CommandBuffer m_Cmb = null;

            public void Dispose()
            {
                m_RenderTarget?.Release();
                m_RenderTarget = null;
            }

            class PassData
            {
                internal TextureHandle gaussianRT;
                internal TextureHandle activeColorTexture;
                internal RTHandle gaussianRTHandle;
                internal Camera camera;
                internal int stereoViewCount;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Cmb == null)
                    return;

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                var rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                // Always use a plain 2D texture for the gaussian RT.
                // In VR (Single-Pass Instanced / multiview) the camera target is a Texture2DArray,
                // but the composite shader declares "Texture2D _GaussianSplatRT".
                // Binding a Texture2DArray to a Texture2D slot on Vulkan (Quest) causes the
                // compositor to sample garbage, producing flicker / invisible splats.
                // Splats are rendered from the mono/center-eye camera view and composited onto
                // both eye slices by Unity's stereo draw instancing in the composite pass.
                rtDesc.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
                rtDesc.volumeDepth = 1;
                RenderingUtils.ReAllocateHandleIfNeeded(ref m_RenderTarget, rtDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianSplatRT");

                TextureHandle gaussianRT = renderGraph.ImportTexture(m_RenderTarget);

                using (var builder = renderGraph.AddUnsafePass<PassData>("GaussianSplat", out var passData))
                {
                    passData.gaussianRT = gaussianRT;
                    passData.activeColorTexture = resourceData.activeColorTexture;
                    passData.gaussianRTHandle = m_RenderTarget;
                    passData.camera = cameraData.camera;
                    // instanceCount = 1: Quest Pro uses GPU Multiview, the hardware replicates the draw
                    // to both eye slices automatically.  The composite shader already sets up
                    // UNITY_VERTEX_OUTPUT_STEREO / SV_RenderTargetArrayIndex for correct eye selection.
                    // Passing viewCount (2) here would double-composite each eye.
                    passData.stereoViewCount = 1;

                    builder.UseTexture(gaussianRT, AccessFlags.ReadWrite);
                    builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);

                    builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                    {
                        CommandBuffer nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                        // Render splats into gaussianRT without a depth attachment.
                        // The active depth buffer uses the camera's MSAA sample count (e.g. 4x in VR),
                        // but gaussianRT is always 1x MSAA.  Vulkan requires all attachments in a
                        // render pass to share the same sample count, so mixing them causes a
                        // validation error and skipped draw calls.  Splats are sorted back-to-front
                        // so they composite correctly without GPU depth testing.
                        ctx.cmd.SetRenderTarget(data.gaussianRT);
                        ctx.cmd.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1, 0);

                        // add sorting, view calc and drawing commands for each splat object
                        Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.camera, nativeCmd);
                        if (matComposite == null)
                        {
                            Debug.LogWarning("[GS-DIAG] SortAndRenderSplats returned null matComposite — no splats rendered");
                            return;
                        }
                        Debug.Log("[GS-DIAG] SortAndRenderSplats OK, compositing...");

                        // Set the gaussian RT as a global texture AFTER splats have been drawn into it,
                        // then switch to the camera color target.  The render-pass boundary that occurs
                        // when SetRenderTarget changes the attachment is what causes Vulkan to flush the
                        // gaussianRT tile data to main memory so the composite can read it correctly.
                        nativeCmd.SetGlobalTexture(data.gaussianRTHandle.name, data.gaussianRTHandle.nameID);

                        // compose: draw gaussian splats onto the active camera color target
                        nativeCmd.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                        ctx.cmd.SetRenderTarget(data.activeColorTexture);
                        nativeCmd.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, data.stereoViewCount);
                        nativeCmd.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                    });
                }
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            Debug.Log("[GS-DIAG] GaussianSplatURPFeature.Create() called — feature IS in build");
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
            {
                Debug.Log($"[GS-DIAG] GatherSplatsForCamera returned false for camera '{cameraData.camera?.name}'");
                return;
            }

            CommandBuffer cmb = system.InitialClearCmdBuffer(cameraData.camera);
            m_Pass.m_Cmb = cmb;
            m_HasCamera = true;
            Debug.Log($"[GS-DIAG] GatherSplatsForCamera OK for camera '{cameraData.camera?.name}'");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Debug.Log($"[GS-DIAG] AddRenderPasses called, m_HasCamera={m_HasCamera}");
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
