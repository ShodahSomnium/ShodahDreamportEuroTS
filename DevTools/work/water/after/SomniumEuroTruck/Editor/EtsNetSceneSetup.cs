using Fusion;
using SomniumSpace.Network.Bridge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.EditorTools
{
    /// <summary>
    /// Puts the world's network objects into the open scene, the way the AoM scene holds its own
    /// ("Aom Net" with "Scene Networking", "Aom Net Lobby", "Aom Net Events", "Aom Net Slots n"):
    ///
    ///   Ets Net                  EtsNet (the bootstrap; EtsTraffic and the new flow's parts are added at run time)
    ///     Scene Networking       SceneNetworking (this world's copy)
    ///     Ets Net Truck 0        NetworkObject + NetworkBridgeData64   the old single truck (EtsNet), until Phase 2
    ///     Ets Net Session        the same object renamed: the session (EtsSession, EtsSessionWire)
    ///     Ets Net Events         NetworkObject + NetworkBridgeEvents   (news, EtsNet.Announce)
    ///     Ets Net Traffic 0..3   (EtsTrafficSceneSetup)
    ///     Ets Net Player 0..49   NetworkObject + NetworkBridgeData64   one per player (EtsPlayerWire) [net §2.3]
    ///
    /// Flags are SceneNetworking.NETWORK_OBJECT_DEFAULT_FLAGS (V1 | AllowStateAuthorityOverride = 524289, as in
    /// the AoM and Quake scenes) and Object Interest Global. Fusion's editor bakes SortKey and
    /// NetworkedBehaviours when the scene is saved (NetworkObjectPostprocessor.OnSceneSaving), so a changed
    /// scene is saved at the end. Both menu items are idempotent in the way EtsTrafficSceneSetup is: a complete
    /// object is left exactly as it is, an incomplete one only gets what it lacks, and the scene is saved only
    /// when something changed.
    ///
    /// The hand-inserted alternative is S/design/session-net/scene_player_slots.yaml (fileIDs 2900000200-693,
    /// SortKeys computed; probe 1820 checked it against the scene of 2026-09-22: 763 checks). Objects made here
    /// get Unity's own fileIDs instead; Fusion keys them on save either way.
    /// </summary>
    public static class EtsNetSceneSetup
    {
        /// <summary>
        /// Scene Networking, the events object and, while the old single-truck flow is live (EtsFeatures.NewFlow
        /// off, and no "Ets Net Session" yet), "Ets Net Truck 0". It never makes "Ets Net Truck 0" beside a
        /// session: that object is the session, renamed ([net risk 10]: an old tool re-creating it after the
        /// rename would put a second object under the old name).
        /// </summary>
        [MenuItem("Somnium Euro Truck/Add network objects (truck sync)", false, 60)]
        public static void AddNetworkObjects()
        {
            bool changed = false;
            GameObject parent = GameObject.Find("Ets Net");

            if (parent == null)
            {
                parent = new GameObject("Ets Net");
                changed = true;
            }

            // EtsNet lives in the scene, not on the truck: a player without the game installed never gets a
            // truck (EtsModelViewer stops at the mount), and still needs to draw the stand-in box where the
            // others' truck is. EtsModelViewer attaches the truck to it.
            if (parent.GetComponent<EtsNet>() == null)
            {
                parent.AddComponent<EtsNet>();
                changed = true;
            }

            changed |= Child(parent, "Scene Networking", go =>
            {
                if (go.GetComponent<SceneNetworking>() != null)
                {
                    return false;
                }

                go.AddComponent<SceneNetworking>();
                return true;
            });

            bool session = parent.transform.Find(EtsSessionWire.ObjectName) != null;

            if (!session && !EtsFeatures.NewFlow)
            {
                changed |= Child(parent, EtsNet.StateObjectName, go => Networked<NetworkBridgeData64>(go));
            }
            else if (parent.transform.Find(EtsNet.StateObjectName) == null)
            {
                Debug.Log("[SomniumEuroTruck] '" + EtsNet.StateObjectName + "' not made: " + (session ? "the scene has '" + EtsSessionWire.ObjectName + "', which is that object renamed" : "the new flow is on") + ".");
            }

            changed |= Child(parent, EtsNet.EventsObjectName, go => Networked<NetworkBridgeEvents>(go));
            Finish(parent, changed, "Network objects (truck sync)");
        }

        /// <summary>
        /// The integrated plan's objects [net §2.3]: "Ets Net Session" (today's "Ets Net Truck 0" renamed - same
        /// fileIDs, so the same SortKey 3877532583 - or a new one in a scene that has neither) and the fifty
        /// "Ets Net Player k". The rename switches the old single-truck networking off until the Phase 3 EtsNet
        /// (it finds its truck by the old name), so while EtsFeatures.NewFlow is off it asks first.
        /// </summary>
        [MenuItem("Somnium Euro Truck/Add network objects (session + players)", false, 62)]
        public static void AddSessionAndPlayers()
        {
            GameObject parent = GameObject.Find("Ets Net");

            if (parent == null)
            {
                Debug.LogError("[SomniumEuroTruck] No 'Ets Net' in the scene: run 'Add network objects (truck sync)' first.");
                return;
            }

            bool changed = false;
            Transform session = parent.transform.Find(EtsSessionWire.ObjectName);
            Transform old = parent.transform.Find(EtsNet.StateObjectName);

            if (session == null && old != null)
            {
                bool rename = EtsFeatures.NewFlow || (!Application.isBatchMode && EditorUtility.DisplayDialog("Somnium Euro Truck",
                    "Rename '" + EtsNet.StateObjectName + "' to '" + EtsSessionWire.ObjectName + "'?\n\n" +
                    "The object keeps its fileIDs and SortKey. Today's single-truck networking finds its truck by the old name, " +
                    "so trucks stop being networked until the per-player EtsNet (Phase 3). EtsFeatures.NewFlow is off.",
                    "Rename", "Leave it"));

                if (rename)
                {
                    Undo.RecordObject(old.gameObject, "Rename to " + EtsSessionWire.ObjectName);
                    old.gameObject.name = EtsSessionWire.ObjectName;
                    changed |= Networked<NetworkBridgeData64>(old.gameObject);
                    EditorUtility.SetDirty(old.gameObject);
                    changed = true;
                    Debug.Log("[SomniumEuroTruck] '" + EtsNet.StateObjectName + "' renamed to '" + EtsSessionWire.ObjectName + "' (same object, same SortKey).");
                }
                else
                {
                    Debug.Log("[SomniumEuroTruck] '" + EtsNet.StateObjectName + "' left as it is; no session object yet.");
                }
            }
            else if (session == null)
            {
                changed |= Child(parent, EtsSessionWire.ObjectName, go => Networked<NetworkBridgeData64>(go));
            }
            else
            {
                changed |= Networked<NetworkBridgeData64>(session.gameObject);

                if (old != null)
                {
                    // [net risk 10]: an old tool re-created it after the rename. The tool deletes nothing; it says so,
                    // and the count below then reads one NetworkObject over the 56 expected.
                    Debug.LogWarning("[SomniumEuroTruck] Both '" + EtsSessionWire.ObjectName + "' and '" + EtsNet.StateObjectName + "' are in the scene; the second is a stale copy of the first ([net risk 10]). Delete it by hand.");
                }
            }

            int complete = 0;

            for (int k = 0; k < EtsNet.MaxSlots; k++)
            {
                bool slotChanged = Child(parent, EtsPlayerWire.ObjectPrefix + k, go => Networked<NetworkBridgeData64>(go));
                changed |= slotChanged;

                if (!slotChanged)
                {
                    complete++;
                }
            }

            int slotType = 0;

            foreach (Transform child in parent.transform)
            {
                if ((child.name == EtsSessionWire.ObjectName || child.name.StartsWith(EtsPlayerWire.ObjectPrefix, System.StringComparison.Ordinal)) && child.GetComponent<NetworkBridgeData64>() != null)
                {
                    slotType++;
                }
            }

            int networkObjects = Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            Debug.Log("[SomniumEuroTruck] Session and player objects: " + slotType + " slot-type objects (session + players; 51 expected), " + networkObjects +
                      " NetworkObjects in the scene (56 expected with the four traffic blocks and the events object); " + complete + " of " + EtsNet.MaxSlots + " player slots were already complete.");
            Finish(parent, changed, "Session and player objects");
        }

        /// <summary>
        /// A NetworkObject (flags 524289, interest Global) and the data component T, whichever are missing; an
        /// existing NetworkObject whose flags or interest differ is corrected. True if anything changed.
        /// </summary>
        private static bool Networked<T>(GameObject go) where T : Component
        {
            bool changed = false;

            // Unity's null test, not ??: a missing component is a fake null in the editor that ?? keeps.
            NetworkObject networkObject = go.GetComponent<NetworkObject>();

            if (networkObject == null)
            {
                networkObject = go.AddComponent<NetworkObject>();
                changed = true;
            }

            if (networkObject.Flags != SceneNetworking.NETWORK_OBJECT_DEFAULT_FLAGS)
            {
                networkObject.Flags = SceneNetworking.NETWORK_OBJECT_DEFAULT_FLAGS;
                changed = true;
            }

            changed |= Interest(networkObject, go.name);

            if (go.GetComponent<T>() == null)
            {
                go.AddComponent<T>();
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(go);
            }

            return changed;
        }

        /// <summary>
        /// Object interest Global (1), as every NetworkObject in the AoM and Quake scenes has it ("ObjectInterest:
        /// 1"): every client gets the object wherever it is. The field is internal to Fusion
        /// (NetworkObject+ObjectInterestModes: AreaOfInterest 0, Global 1, Explicit 2), so it is set through its
        /// serialized property. True if it changed.
        /// </summary>
        private static bool Interest(NetworkObject networkObject, string name)
        {
            var serialized = new SerializedObject(networkObject);
            SerializedProperty interest = serialized.FindProperty("ObjectInterest");

            if (interest == null)
            {
                Debug.LogWarning("[SomniumEuroTruck] NetworkObject has no ObjectInterest property; set Object Interest to Global by hand on " + name + ".");
                return false;
            }

            if (interest.intValue == 1)
            {
                return false;
            }

            interest.intValue = 1;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        /// <summary>The named child of parent, made if missing, then set up. True if it was made or setUp changed it.</summary>
        private static bool Child(GameObject parent, string name, System.Func<GameObject, bool> setUp)
        {
            Transform found = parent.transform.Find(name);
            bool made = found == null;
            GameObject go = made ? new GameObject(name) : found.gameObject;

            if (made)
            {
                go.transform.SetParent(parent.transform, false);
            }

            bool changed = setUp(go) | made;

            if (changed)
            {
                EditorUtility.SetDirty(go);
            }

            return changed;
        }

        private static void Finish(GameObject parent, bool changed, string what)
        {
            if (!changed)
            {
                Debug.Log("[SomniumEuroTruck] " + what + ": all in the scene already; nothing changed.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(parent.scene);
            EditorSceneManager.SaveScene(parent.scene);
            Debug.Log("[SomniumEuroTruck] " + what + " are in the scene and it has been saved (Fusion baked them on save).");
        }
    }
}
