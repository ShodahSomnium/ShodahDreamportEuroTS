using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SomniumSpace.Worlds.SomniumEuroTruck.EditorTools
{
    /// <summary>
    /// Keeps the map viewer's shader fields filled, so the world bundle always ships the
    /// shaders. Shader.Find only finds a shader something in the build references, and in an
    /// uploaded world that something is these fields. The fourth, _leavesShader
    /// (Shaders/EtsLeaves.shader, "SomniumEuroTruck/Leaves", 2026-09-22 Phase 1), is filled the same
    /// way the first time the scene is opened or saved after the field exists.
    ///
    /// Why it is needed: on 2026-09-22 the sky shader's reference, written into the scene with the
    /// shader itself, came back from an upload as {fileID: 0}. Unity had compiled the project while
    /// the code was half merged (its log: "EtsNet could not be found"), loaded the scene against the
    /// last good assembly - which had no _skyShader field - and saved it with the field empty. The
    /// client then logged "Sky: procedural kept (SomniumEuroTruck/Sky not in the build)". So an empty
    /// field is filled again, by the shader's own GUID, whenever a scene is opened, after every
    /// script reload, and just before any scene is saved (which the SDK's build does).
    /// </summary>
    [InitializeOnLoad]
    internal static class EtsShaderReferences
    {
        private static readonly string[,] Fields =
        {
            { "_decalShader", "7d2c4e5a9b1f4c6e8a3d5f7b9c1e2a4d" },
            { "_multiShader", "3f8a1c6d5b2e4a7f9c0d1e2b3a4c5d6e" },
            { "_skyShader", "5e7a2c91d4b84f06a3c8e1f2b9d6a470" },
            { "_leavesShader", "8e7051a093b44efcaa6567b3266be909" },
        };

        static EtsShaderReferences()
        {
            EditorApplication.delayCall += HealOpenScenes;
            EditorSceneManager.sceneOpened += (scene, mode) => Heal(scene);
            EditorSceneManager.sceneSaving += (scene, path) => Heal(scene);
        }

        private static void HealOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Heal(SceneManager.GetSceneAt(i));
            }
        }

        private static void Heal(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (EtsMapViewer viewer in root.GetComponentsInChildren<EtsMapViewer>(true))
                {
                    var serialized = new SerializedObject(viewer);
                    string filled = "";

                    for (int f = 0; f < Fields.GetLength(0); f++)
                    {
                        SerializedProperty property = serialized.FindProperty(Fields[f, 0]);

                        if (property == null || property.objectReferenceValue != null)
                        {
                            continue;
                        }

                        string path = AssetDatabase.GUIDToAssetPath(Fields[f, 1]);
                        Shader shader = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Shader>(path);

                        if (shader != null)
                        {
                            property.objectReferenceValue = shader;
                            filled += (filled.Length > 0 ? ", " : "") + Fields[f, 0] + " = " + shader.name;
                        }
                    }

                    if (filled.Length > 0)
                    {
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        EditorSceneManager.MarkSceneDirty(scene);
                        Debug.Log("[SomniumEuroTruck] Shader references on '" + viewer.name + "' were empty and are filled again: " + filled + ". Save the scene before uploading.");
                    }
                }
            }
        }
    }
}
