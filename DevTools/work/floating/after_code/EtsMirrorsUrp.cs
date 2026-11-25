using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Makes a mirror camera cheap. Every camera gets what the client's URP asset asks for
    /// (URP_RenderPipelineAsset: m_RequireDepthTexture 1, m_RequireOpaqueTexture 1, shadows in 4
    /// cascades out to 134 m), which a 512 px mirror picture does not need. Now that the mirrors
    /// render every frame instead of taking turns, that saving matters in a headset.
    /// </summary>
    public static class EtsMirrorsUrp
    {
        public static void MakeCheap(Camera camera, bool shadows)
        {
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();

            if (data == null)
            {
                return;
            }

            data.renderShadows = shadows;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
        }
    }
}
