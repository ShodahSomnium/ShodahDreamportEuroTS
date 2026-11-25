using Fusion;
using SomniumSpace.Network.Bridge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.EditorTools
{
    /// <summary>
    /// Puts the traffic's network objects into the open scene, beside the truck's
    /// (EtsNetSceneSetup), in the same shape:
    ///
    ///   Ets Net                      EtsNet (EtsTraffic is added to it by EtsNet.Awake at run time)
    ///     Ets Net Traffic 0..3       NetworkObject + NetworkBridgeData64 (fifteen cars each)
    ///
    /// Flags SceneNetworking.NETWORK_OBJECT_DEFAULT_FLAGS (524289), Object Interest Global, as
    /// every NetworkObject in the AoM, Quake and this scene has them. Idempotent: a block that is
    /// already complete (both components) is left exactly as it is, an incomplete one only gets
    /// what it lacks, and an EtsTraffic is added to 'Ets Net' only when the scene has none at
    /// all - so running it on a scene whose objects were written in by hand changes nothing. The
    /// scene is saved only if something changed, because Fusion bakes SortKey and
    /// NetworkedBehaviours on save.
    /// </summary>
    public static class EtsTrafficSceneSetup
    {
        [MenuItem("Somnium Euro Truck/Add network objects (traffic)", false, 61)]
        public static void AddTrafficObjects()
        {
            GameObject parent = GameObject.Find("Ets Net");

            if (parent == null)
            {
                Debug.LogError("[SomniumEuroTruck] No 'Ets Net' in the scene: run 'Add network objects (truck sync)' first.");
                return;
            }

            bool changed = false;

            // EtsNet adds EtsTraffic itself; one in the scene is only for tuning it in the inspector.
            // Never a second one (FindObjectsByType counts inactive ones too).
            if (Object.FindObjectsByType<EtsTraffic>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0 && parent.GetComponent<EtsNet>() == null)
            {
                parent.AddComponent<EtsTraffic>();
                changed = true;
            }

            int complete = 0;

            for (int i = 0; i < EtsTraffic.MaxBlocks; i++)
            {
                string name = EtsTraffic.BlockPrefix + i;
                Transform found = parent.transform.Find(name);

                if (found != null && found.GetComponent<NetworkObject>() != null && found.GetComponent<NetworkBridgeData64>() != null)
                {
                    complete++;
                    continue;
                }

                GameObject go = found != null ? found.gameObject : new GameObject(name);
                go.transform.SetParent(parent.transform, false);

                // Unity's null test, not ??: a missing component is a fake null in the editor that ?? keeps.
                NetworkObject networkObject = go.GetComponent<NetworkObject>();

                if (networkObject == null)
                {
                    networkObject = go.AddComponent<NetworkObject>();
                    networkObject.Flags = SceneNetworking.NETWORK_OBJECT_DEFAULT_FLAGS;
                    Interest(networkObject, name);
                }

                if (go.GetComponent<NetworkBridgeData64>() == null)
                {
                    go.AddComponent<NetworkBridgeData64>();
                }

                EditorUtility.SetDirty(go);
                changed = true;
            }

            if (!changed)
            {
                Debug.Log("[SomniumEuroTruck] Traffic network objects: all " + complete + " already in the scene; nothing changed.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(parent.scene);
            EditorSceneManager.SaveScene(parent.scene);
            Debug.Log("[SomniumEuroTruck] Traffic network objects are in the scene (" + complete + " were already complete) and it has been saved (Fusion baked them on save).");
        }

        /// <summary>Object interest Global (1), through the serialized property (the field is internal to Fusion), as EtsNetSceneSetup does.</summary>
        private static void Interest(NetworkObject networkObject, string name)
        {
            var serialized = new SerializedObject(networkObject);
            SerializedProperty interest = serialized.FindProperty("ObjectInterest");

            if (interest != null)
            {
                interest.intValue = 1;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[SomniumEuroTruck] NetworkObject has no ObjectInterest property; set Object Interest to Global by hand on " + name + ".");
            }
        }
    }
}
