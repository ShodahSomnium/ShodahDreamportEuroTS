using TMPro;
using UnityEditor;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.EditorTools
{
    /// <summary>
    /// Builds the scene objects the Euro Truck components expect, so getting from an empty scene
    /// to a running game is one menu item rather than a wiring exercise.
    ///
    /// Both entries make the same thing: an object holding the component, and a status label in
    /// front of it. The label is not decoration - every failure these components can hit is
    /// reported through it, and a scene without one fails silently as far as anyone standing in
    /// the world is concerned.
    /// </summary>
    internal static class EtsSceneSetup
    {
        /// <summary>
        /// The one to run first. Launches the game once, measures everything the embedded game
        /// depends on, and prints the answers on the label and in the console.
        /// </summary>
        [MenuItem("Somnium Euro Truck/Create capability probe", false, 0)]
        private static void CreateProbe()
        {
            GameObject host = CreateHost("Ets Capability Probe", out TMP_Text label);
            var spike = host.AddComponent<EtsLaunchSpike>();

            var serialized = new SerializedObject(spike);
            serialized.FindProperty("_output").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            label.text = "Somnium Euro Truck - capability probe\nPress Play to run.";
            Select(host);
        }

        /// <summary>
        /// The game's own truck, decoded out of the archives and standing in the scene. The first
        /// thing here that puts the game inside Somnium rather than beside it.
        /// </summary>
        [MenuItem("Somnium Euro Truck/Create model viewer", false, 20)]
        private static void CreateModelViewer()
        {
            GameObject host = CreateHost("Ets Model Viewer", out TMP_Text label);
            var viewer = host.AddComponent<EtsModelViewer>();

            var serialized = new SerializedObject(viewer);
            serialized.FindProperty("_status").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            label.text = "Somnium Euro Truck - model viewer\nPress Play.";
            Select(host);
        }

        /// <summary>
        /// A piece of the game's map - roads, junctions, buildings - built from the sector files
        /// around a chosen point.
        /// </summary>
        [MenuItem("Somnium Euro Truck/Create map viewer", false, 21)]
        private static void CreateMapViewer()
        {
            GameObject host = CreateHost("Ets Map Viewer", out TMP_Text label);
            var viewer = host.AddComponent<EtsMapViewer>();

            var serialized = new SerializedObject(viewer);
            serialized.FindProperty("_status").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            label.text = "Somnium Euro Truck - map viewer\nPress Play.";
            Select(host);
        }

        [MenuItem("Somnium Euro Truck/Create embedded game (window capture)", false, 40)]
        private static void CreateEmbeddedGame()
        {
            GameObject host = CreateHost("Ets Embedded Game", out TMP_Text label);
            var game = host.AddComponent<EtsEmbeddedGame>();

            var serialized = new SerializedObject(game);
            serialized.FindProperty("_status").objectReferenceValue = label;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            label.text = "Somnium Euro Truck\nPress Play to launch Euro Truck Simulator 2.";
            Select(host);
        }

        /// <summary>
        /// Creates the component host and a screen-space label in front of it.
        ///
        /// The label canvas sorts at 100 while the game screen sorts at -100, so status text
        /// stays readable over the game rather than being painted over by it the moment the
        /// first frame arrives.
        /// </summary>
        private static GameObject CreateHost(string name, out TMP_Text label)
        {
            var host = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(host, "Create " + name);

            var canvasObject = new GameObject("Status Canvas");
            canvasObject.transform.SetParent(host.transform, false);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var textObject = new GameObject("Status");
            textObject.transform.SetParent(canvasObject.transform, false);

            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = 20f;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.raycastTarget = false;

            RectTransform rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(32f, 32f);
            rect.offsetMax = new Vector2(-32f, -32f);

            label = text;
            return host;
        }

        private static void Select(GameObject host)
        {
            Selection.activeGameObject = host;
            EditorGUIUtility.PingObject(host);
        }
    }
}
