// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);

            readonly GaussianSplatTemporalAccumulator m_Temporal = new();

            internal void Reset(Camera camera) => m_Temporal.Reset(camera);
            internal void Dispose() => m_Temporal.Dispose();

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle GaussianSplatDepthRT;
                internal RenderTexture SplatDepthOverride;  // M13:SortedBlended 深度导出的持久 RT(reprojected 路径不用)
                internal bool SplatDepthExport;             // M13:SortedBlended 深度导出开关
                internal GSRenderPass Pass;
                internal StochasticTemporalFilter Filter;
                internal Matrix4x4 Projection, View;
                internal int Eye, TargetSlice;
                internal int Width, Height;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                // scratch color/depth are 2D for the current eye. note that camera targets
                // can be arrays even in multi-pass
                rtDesc.dimension = TextureDimension.Tex2D;
                rtDesc.volumeDepth = 1;
                rtDesc.vrUsage = VRTextureUsage.None;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);

                var filter = GaussianSplatRenderingOptions.instance.temporalFilter;
                var options = GaussianSplatRenderingOptions.instance;
                passData.Pass = this;
                passData.Filter = filter;
                passData.Projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                passData.View = cameraData.GetViewMatrix();
                passData.Eye = cameraData.xr.enabled ? cameraData.xr.multipassId + 1 : 0;
                passData.TargetSlice = cameraData.xr.enabled ? cameraData.xr.GetTextureArraySlice() : -1;
                passData.Width = rtDesc.width;
                passData.Height = rtDesc.height;
                passData.SplatDepthExport = GaussianSplatRenderSystem.SplatDepthExportEnabled(options);
                passData.SplatDepthOverride = null;
                if (filter == StochasticTemporalFilter.Reprojected)
                {
                    rtDesc.graphicsFormat = GraphicsFormat.R32_SFloat;
                    passData.GaussianSplatDepthRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, "_GaussianSplatDepthRT", true);
                    builder.UseTexture(passData.GaussianSplatDepthRT, AccessFlags.ReadWrite);
                }
                else if (passData.SplatDepthExport)
                {
                    // M13 T13.2:SortedBlended 深度导出绑定持久 MRT 目标(reprojected 路径保持原 RG 纹理不变)
                    var system = GaussianSplatRenderSystem.instance;
                    passData.SplatDepthOverride = system.EnsureSplatDepthRT(rtDesc.width, rtDesc.height);
                }
                if (filter != StochasticTemporalFilter.None)
                {
                    var history = m_Temporal.Prepare(cameraData.camera, rtDesc.width, rtDesc.height, passData.Eye);
                    builder.UseTexture(renderGraph.ImportTexture(history.previousHandle), AccessFlags.ReadWrite);
                    builder.UseTexture(renderGraph.ImportTexture(history.nextHandle), AccessFlags.ReadWrite);
                }
                else
                    m_Temporal.Reset(cameraData.camera);

                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = textureHandle;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                builder.UseTexture(textureHandle, AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    var system = GaussianSplatRenderSystem.instance;
                    var camera = data.CameraData.camera;
                    // S2P M6:GPU 分块剔除(Hi-Z + 视锥)在绑定 cameraDepth 为 DSV 渲染目标前派发,
                    // 避免 D3D12 同一深度纹理同时作为 DSV 与 Compute SRV 读取产生状态冲突
                    commandBuffer.SetGlobalTexture("_S2PHiZSrcDepth", data.SourceDepth);
                    Vector3 camPos = data.View.inverse.GetColumn(3);
                    system.DispatchGpuChunkCulling(commandBuffer, data.Projection, data.View, data.Width, data.Height, camPos);

                    commandBuffer.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);
                    commandBuffer.SetGlobalVector(GaussianSplatRenderer.Props.CameraTargetTextureTexelSize, Vector4.zero);
                    RenderTargetIdentifier cameraDepth = data.SourceDepth;
                    cameraDepth = new RenderTargetIdentifier(cameraDepth, 0, CubemapFace.Unknown, data.TargetSlice);
                    if (data.Filter == StochasticTemporalFilter.Reprojected)
                    {
                        commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatDepthRT, data.GaussianSplatDepthRT);
                        CoreUtils.SetRenderTarget(commandBuffer,
                            new RenderTargetIdentifier[] { data.GaussianSplatRT, data.GaussianSplatDepthRT },
                            cameraDepth, ClearFlag.Color, Color.clear);
                    }
                    else if (data.SplatDepthExport && data.SplatDepthOverride != null)
                    {
                        // M13 T13.2:SortedBlended 深度导出 —— 同样绑定 MRT 深度目标,
                        // Front-to-Back Under 混合在该目标上累积预乘 α 深度 D = Σ w_i·z_i
                        commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatDepthRT, data.SplatDepthOverride);
                        CoreUtils.SetRenderTarget(commandBuffer,
                            new RenderTargetIdentifier[] { data.GaussianSplatRT, data.SplatDepthOverride },
                            cameraDepth, ClearFlag.Color, Color.clear);
                    }
                    else
                        CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, cameraDepth, ClearFlag.Color, Color.clear);

                    commandBuffer.SetViewport(new Rect(0, 0, data.Width, data.Height));
                    Material matComposite = system.SortAndRenderSplats(camera, commandBuffer, data.Projection, data.Width, data.Height, data.View);
                    commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    if (data.Filter != StochasticTemporalFilter.None)
                    {
                        var filtered = data.Pass.m_Temporal.Accumulate(commandBuffer, camera, matComposite, system.GetTemporalSceneHash(),
                            data.Filter, data.Width, data.Height, data.Projection * data.View, data.Eye, data.View);
                        commandBuffer.SetGlobalTexture(s_gaussianSplatRT, filtered);
                    }
                    RenderTargetIdentifier cameraColor = data.SourceTexture;
                    cameraColor = new RenderTargetIdentifier(cameraColor, 0, CubemapFace.Unknown, data.TargetSlice);

                    // —— S2P M13 T13.3:合成像素有效表面深度写回相机深度 ——
                    // 场景深度先复制为 R32 副本(此刻相机深度未绑定为 DSV,无资源冲突),
                    // 合成时以副本为 SRV 回退采样,同时把相机深度绑定为 DSV 接收 SV_Depth 写入。
                    bool compositeDepthWrite = data.SplatDepthExport && data.Filter == StochasticTemporalFilter.None;
                    if (compositeDepthWrite)
                    {
                        var scratch = system.EnsureSceneDepthScratch(data.Width, data.Height);
                        commandBuffer.Blit(cameraDepth, scratch);
                        commandBuffer.SetGlobalTexture("_S2PSceneDepthTexture", scratch);
                        commandBuffer.SetGlobalInt(GaussianSplatRenderer.Props.S2PHasSceneDepth, 1);
                    }
                    else
                    {
                        commandBuffer.SetGlobalTexture("_S2PSceneDepthTexture", data.SourceDepth);
                        commandBuffer.SetGlobalInt(GaussianSplatRenderer.Props.S2PHasSceneDepth, 1);
                    }
                    commandBuffer.SetGlobalInt(GaussianSplatRenderer.Props.S2PHasSplatDepth,
                        data.Filter == StochasticTemporalFilter.Reprojected || data.SplatDepthExport ? 1 : 0);
                    commandBuffer.SetGlobalInt(GaussianSplatRenderer.Props.S2PSplatDepthPremultiplied,
                        data.SplatDepthExport ? 1 : 0);
                    commandBuffer.SetGlobalInt(GaussianSplatRenderer.Props.S2PWriteCompositeDepth, compositeDepthWrite ? 1 : 0);
                    commandBuffer.SetGlobalFloat(GaussianSplatRenderer.Props.S2PDepthAlphaThreshold,
                        GaussianSplatRenderingOptions.instance.m_SplatDepthAlphaThreshold);
                    if (compositeDepthWrite)
                    {
                        // 写回模式:相机深度绑定为 DSV 接收 SV_Depth;否则保持原行为(仅颜色目标)
                        matComposite.SetFloat("_ZWrite", 1f);
                        CoreUtils.SetRenderTarget(commandBuffer, cameraColor, cameraDepth);
                    }
                    else
                    {
                        matComposite.SetFloat("_ZWrite", 0f);
                        CoreUtils.SetRenderTarget(commandBuffer, cameraColor);
                    }
                    commandBuffer.SetViewport(new Rect(0, 0, data.Width, data.Height));
                    commandBuffer.SetGlobalVector("_SplatRenderSize", new Vector4(data.Width, data.Height, 0, 0));
                    commandBuffer.SetGlobalMatrix("_SplatInvViewProjection", (data.Projection * data.View).inverse);
                    commandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3);
                    commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                });
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass?.Dispose();
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents,
                requiresIntermediateTexture = true
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
            {
                m_Pass.Reset(cameraData.camera);
                return;
            }

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
