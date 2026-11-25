using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The camera frame and the wind the leaves shader ("SomniumEuroTruck/Leaves", Shaders/EtsLeaves.shader)
    /// reads, set for every camera just before it renders (owner F; environment DESIGN.md §A.3, PLAN.md §5.4
    /// step 8).
    ///
    /// Why per camera: the game's leaves vertex program pushes each card vertex sideways along two EYE-space
    /// axes (leaves_data[3].xy and .zw, times the vertex's uv set 1 where uv set 2 is 0.5 or more; read from
    /// the demo's /effect/eut2/_shd/ef447134a6749386441b43ba1987cd28.glsl.vso). Here the same offset is made in
    /// world space along the camera's roll-free right and up, which must be the same for everything the camera
    /// draws - its shadow maps, its depth prepass (the client forces depth priming, URP_Renderer.asset
    /// m_DepthPrimingMode 2, so colour is drawn ZTest Equal against it) and its colour pass - or the cards in
    /// one pass would not cover the depth written in another. URP renders all three inside the camera's
    /// RenderSingleCamera call, right after RenderPipelineManager.beginCameraRendering, so they are set there.
    /// In single-pass instanced stereo the Camera's transform is the centre eye, so both eyes get the same
    /// cards. A mirror camera (EtsMirrors) gets its own frame, which is right for what it sees.
    ///
    /// Set twice over: immediately (Shader.SetGlobalVector) and through a command buffer queued on the
    /// camera's context, so the values are in effect for this camera's draws whether the pipeline reads global
    /// state when a command is recorded or when it is submitted. No allocation per frame after the first
    /// (the command buffer comes from URP's pool).
    ///
    /// Static, started by EtsMapViewer when it opens the archives and stopped when it is destroyed:
    /// statics outlive a visit to the world, so the subscription must not.
    /// </summary>
    public static class EtsVegetationLook
    {
        /// <summary>The global property ids, in a class of their own so the pure parts here (CameraAxes, the settings) need no engine.</summary>
        private static class Ids
        {
            public static readonly int CamPos = Shader.PropertyToID("_EtsVegCamPos");
            public static readonly int CamRight = Shader.PropertyToID("_EtsVegCamRight");
            public static readonly int CamUp = Shader.PropertyToID("_EtsVegCamUp");
            public static readonly int Wind = Shader.PropertyToID("_EtsWind");
            public static readonly int Fade = Shader.PropertyToID("_EtsVegFade");
        }

        /// <summary>
        /// How far the wind sways a card at full vertex-colour strength, metres (EtsMapViewer._windAmplitude).
        /// The game's own amplitude is an engine constant (leaves_data[0], not in the files); a headset wants
        /// little movement near the eye, so 0.08-0.15 m.
        /// </summary>
        public static float WindAmplitude = 0.12f;

        /// <summary>
        /// The wind's heading in the ground plane (Unity x, z). The climate names a wind type and speed
        /// (nice.sii: wind_type 1, speed_coef 100) whose mapping onto a direction is not in the files, so it
        /// is a constant from the south-west.
        /// </summary>
        public static Vector2 WindHeading = new Vector2(0.8f, 0.6f);

        /// <summary>The wind's angular speed, radians a second: one sway every 4.8 s.</summary>
        public static float WindSpeed = 1.3f;

        /// <summary>
        /// The fade windows the shader's bands use (_EtsVegFade): x the middle of the near-tree band and y half its
        /// width - the game's /def/game_data.sii leaves_lod_start 240 / leaves_lod_end 300 m, "first lod class for
        /// trees" (tree .mat lod_selector {1, 0, 0}), so 270 +- 30 m (was 140 +- 15, unused until the instanced tree
        /// field, EtsTreeField, drew on bands 1 and 2) - z the detail-card end and w half its band (60 m in a headset,
        /// 105 m on a desktop, the file's /def/world/detail_vegetation.sii max_distance; 8 m). Map-placed plants draw
        /// on band 0 (no fade); the tree field and the detail cards use these.
        /// </summary>
        public static Vector4 Fade = new Vector4(270f, 30f, 60f, 8f);

        /// <summary>Whether the per-camera globals are being set.</summary>
        public static bool Running { get; private set; }

        /// <summary>The position of the last screen camera (a Game camera with no target texture) this frame, for LOD decisions.</summary>
        public static Vector3 ViewPosition { get; private set; }

        /// <summary>Time.frameCount of the last <see cref="ViewPosition"/>; -1 before the first.</summary>
        public static int ViewFrame { get; private set; } = -1;

        /// <summary>Subscribe to beginCameraRendering; harmless when already running.</summary>
        public static void Start()
        {
            if (Running)
            {
                return;
            }

            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            Running = true;
        }

        /// <summary>Unsubscribe; harmless when not running.</summary>
        public static void Stop()
        {
            if (!Running)
            {
                return;
            }

            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            Running = false;
        }

        private static void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || camera.cameraType == CameraType.Preview)
            {
                return;
            }

            Transform t = camera.transform;
            Vector3 position = t.position;
            CameraAxes(t.forward, t.right, out Vector3 right, out Vector3 up);

            float phase = Time.time * WindSpeed;
            float heading = WindHeading.magnitude;
            Vector2 flat = heading > 1e-4f ? WindHeading / heading : new Vector2(1f, 0f);
            var wind = new Vector4(flat.x * WindAmplitude, 0f, flat.y * WindAmplitude, phase);
            Vector4 fade = Fade;

            // A headset draws grass nearer (Phase C); the client sets XR for the whole session.
            if (UnityEngine.XR.XRSettings.enabled && fade.z > 60f)
            {
                fade.z = 60f;
            }

            var pos4 = new Vector4(position.x, position.y, position.z, 0f);
            var right4 = new Vector4(right.x, right.y, right.z, 0f);
            var up4 = new Vector4(up.x, up.y, up.z, 0f);

            Shader.SetGlobalVector(Ids.CamPos, pos4);
            Shader.SetGlobalVector(Ids.CamRight, right4);
            Shader.SetGlobalVector(Ids.CamUp, up4);
            Shader.SetGlobalVector(Ids.Wind, wind);
            Shader.SetGlobalVector(Ids.Fade, fade);

            CommandBuffer command = CommandBufferPool.Get();
            command.SetGlobalVector(Ids.CamPos, pos4);
            command.SetGlobalVector(Ids.CamRight, right4);
            command.SetGlobalVector(Ids.CamUp, up4);
            command.SetGlobalVector(Ids.Wind, wind);
            command.SetGlobalVector(Ids.Fade, fade);
            context.ExecuteCommandBuffer(command);
            CommandBufferPool.Release(command);

            if (camera.cameraType == CameraType.Game && camera.targetTexture == null)
            {
                ViewPosition = position;
                ViewFrame = Time.frameCount;
            }
        }

        /// <summary>
        /// The card axes for a camera: right is the camera's right flattened onto the ground plane, so the
        /// cards do not lean when the head rolls (the game's leaves_data[3].xy is its eye-space image of a
        /// horizontal axis); up is forward x right, square to the view, so a card faces the eye. When the
        /// camera looks straight up or down its right is still horizontal; should it ever be vertical, the
        /// flattened forward turned a quarter is used. Pure, for the harness.
        /// </summary>
        public static void CameraAxes(Vector3 forward, Vector3 cameraRight, out Vector3 right, out Vector3 up)
        {
            right = new Vector3(cameraRight.x, 0f, cameraRight.z);

            if (right.sqrMagnitude < 1e-6f)
            {
                // Turned a quarter clockwise seen from above: Unity's right of a forward (x, z) is (z, -x).
                right = new Vector3(forward.z, 0f, -forward.x);
            }

            if (right.sqrMagnitude < 1e-6f)
            {
                right = Vector3.right;
            }

            right.Normalize();
            up = Vector3.Cross(forward, right);

            if (up.sqrMagnitude < 1e-6f)
            {
                up = Vector3.up;
            }

            up.Normalize();
        }
    }
}
