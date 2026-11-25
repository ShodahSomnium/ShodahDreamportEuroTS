using Fusion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.EditorTools
{
    /// <summary>
    /// Re-bakes the world scene's network objects for Photon Fusion 2.1 (2026-10-21 migration from 2.0.12, the same step as
    /// the Rec Room Dreamport's RrFusionRebake). Fusion 2.1 bakes NetworkObject flags with version V2; the 56 objects baked
    /// by 2.0 carry V1 (Flags 524289). This opens "Euro Truck Scene", runs Fusion's own "Bake Scene Objects", moves any
    /// object still on V1 to V2 (keeping its other flags, as the migration guide §6 asks), saves the scene, rebuilds the
    /// prefab table (guide §7) and reports the counts. Batch mode:
    /// Unity.exe -batchmode -projectPath ... -executeMethod SomniumSpace.Worlds.SomniumEuroTruck.EditorTools.EtsFusionRebake.Run -quit
    /// </summary>
    public static class EtsFusionRebake
    {
        public const string ScenePath = "Assets/Euro Truck Scene.unity";

        [MenuItem("Somnium Euro Truck/Fusion 2.1: re-bake network objects", false, 40)]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            bool baked = EditorApplication.ExecuteMenuItem("Tools/Fusion/Scene/Bake Scene Objects");

            int total = 0, moved = 0, v2 = 0, overrideFlag = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var no in root.GetComponentsInChildren<NetworkObject>(true))
                {
                    total++;
                    if ((no.Flags & NetworkObjectFlags.V2) == 0)
                    {
                        no.Flags = (no.Flags & ~NetworkObjectFlags.V1) | NetworkObjectFlags.V2;
                        EditorUtility.SetDirty(no);
                        moved++;
                    }

                    if ((no.Flags & NetworkObjectFlags.V2) != 0) v2++;
                    if ((no.Flags & NetworkObjectFlags.AllowStateAuthorityOverride) != 0) overrideFlag++;
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            bool table = EditorApplication.ExecuteMenuItem("Tools/Fusion/Rebuild Prefab Table");
            AssetDatabase.SaveAssets();

            Debug.Log("[SomniumEuroTruck] Fusion 2.1 rebake: bake menu " + baked + ", saved " + saved + ", prefab table " + table
                + "; NetworkObjects " + total + ", moved to V2 by hand " + moved + ", V2 " + v2 + ", AllowStateAuthorityOverride " + overrideFlag);
        }
    }
}
