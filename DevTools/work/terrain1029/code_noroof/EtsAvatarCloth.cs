using System;
using System.Collections.Generic;
using System.Reflection;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Avatar cloth and spring bones kept still on a moving vehicle (2026-10-16, the user: "make Magica cloth/dynamic bone
    /// not jitter when they are ontop of vehicles or inside as a passenger").
    ///
    /// A truck at 90 km/h carries its riders 25 m a second; the avatar's MagicaCloth 2 and Dynamic Bone components read
    /// that as the wearer flying through the air, and hair, skirts and tails trail and snap back every frame. The client
    /// handles this for its own elevators (MB AvatarSystem/Scripts/RootHandlers/Common/PhysicComponentsHandler.cs,
    /// SetInElevator, called from MobilePlatformPlayerHandler) - out of a world's reach, so it is rebuilt here, the same
    /// way for every player EtsRiders knows is on a vehicle (the own player seated or standing on one, every remote rider):
    ///  - MagicaCloth (MagicaCloth2.MagicaCloth): its inertia ANCHOR set to the vehicle, anchorInertia 0 - MagicaCloth's own
    ///    feature for "a character rides a vehicle" (InertiaConstraint.SerializeData.anchor: "Anchor translation and
    ///    rotation are excluded from simulation"; read every frame by its TeamManager), so the vehicle's travel and turns are
    ///    cancelled while the wearer's own movement still sways the cloth; then SetParameterChange, as its API asks;
    ///  - Dynamic Bone: m_Inert 1 and UpdateParameters (the client's elevator value: the root's movement ignored).
    /// Both are put back to the avatar's own values when the player gets off, changes avatar or leaves.
    ///
    /// The components are the client's types, not compiled into the world, so they are found by type name under the
    /// avatar's holder (every copy: main, shadow, mirror) and set through reflection, each step in try/catch: a client
    /// that renames them only loses this, with one log line. A scan per rider at most every 2 s (on boarding, then while
    /// the avatar has not been found); nothing per frame.
    /// </summary>
    public sealed class EtsAvatarCloth : MonoBehaviour
    {
        private const float CheckEvery = 0.5f;
        private const float RescanEvery = 2f;
        private const float FindRadius = 0.6f;

        private sealed class Cloth
        {
            public Component Target;
            public bool Magica;
            public Transform OldAnchor;
            public float OldValue;
            public bool Applied;
        }

        private sealed class Rider
        {
            public ISomniumPlayer Player;
            public Transform Frame;
            public Transform Holder;
            public readonly List<Cloth> Parts = new List<Cloth>();
            public float NextScan;
            public int Seen;
        }

        private readonly List<Rider> _riders = new List<Rider>(8);
        private readonly List<ISomniumPlayer> _players = new List<ISomniumPlayer>(8);
        private readonly List<Transform> _frames = new List<Transform>(8);
        private readonly List<MonoBehaviour> _found = new List<MonoBehaviour>(64);
        private float _next;
        private int _pass;
        private bool _saidOn, _saidFail;

        // Reflection, looked up once per client type.
        private static Type s_magicaType, s_boneType;
        private static PropertyInfo s_serializeData;
        private static FieldInfo s_inertia, s_anchor, s_anchorInertia, s_inert;
        private static MethodInfo s_setChange, s_updateParams;
        private static PropertyInfo s_inertProp;

        public static EtsAvatarCloth Ensure(GameObject host)
        {
            if (host == null) return null;
            EtsAvatarCloth cloth = host.GetComponent<EtsAvatarCloth>();
            return cloth != null ? cloth : host.AddComponent<EtsAvatarCloth>();
        }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + CheckEvery;
            _pass++;

            EtsRiders riders = EtsRiders.Instance;

            if (riders != null)
            {
                ISomniumPlayer local = null;

                try { local = SomniumBridge.PlayersContainer?.LocalPlayer; }
                catch (Exception) { local = null; }

                Transform own = riders.LocalPlatformFrame;
                if (local != null && own != null) Visit(local, own);

                riders.RidingRemotes(_players, _frames);
                for (int i = 0; i < _players.Count; i++) Visit(_players[i], _frames[i]);
            }

            for (int i = _riders.Count - 1; i >= 0; i--)
            {
                if (_riders[i].Seen != _pass)
                {
                    Restore(_riders[i]);
                    _riders.RemoveAt(i);
                }
            }
        }

        private void OnDisable()
        {
            for (int i = 0; i < _riders.Count; i++) Restore(_riders[i]);
            _riders.Clear();
        }

        private void Visit(ISomniumPlayer player, Transform frame)
        {
            Rider rider = null;

            for (int i = 0; i < _riders.Count; i++)
            {
                if (_riders[i].Player == player) { rider = _riders[i]; break; }
            }

            if (rider == null)
            {
                rider = new Rider { Player = player };
                _riders.Add(rider);
            }

            rider.Seen = _pass;

            // A new avatar (the holder went away) or a part destroyed: give back what is left, scan again.
            bool lost = rider.Holder == null && rider.Parts.Count > 0;
            for (int i = 0; i < rider.Parts.Count && !lost; i++) lost = rider.Parts[i].Target == null;

            if (lost)
            {
                Restore(rider);
                rider.Holder = null;
                rider.NextScan = 0f;
            }

            if (rider.Holder == null && Time.unscaledTime >= rider.NextScan)
            {
                rider.NextScan = Time.unscaledTime + RescanEvery;
                Scan(rider);
            }

            if (rider.Frame != frame)
            {
                rider.Frame = frame;
                for (int i = 0; i < rider.Parts.Count; i++) Apply(rider.Parts[i], frame);
            }
        }

        /// <summary>The player's avatar holder (the parent of its main copy's root) and every cloth and spring bone under it.</summary>
        private void Scan(Rider rider)
        {
            Transform head = null, bodyRoot = null;

            try
            {
                head = rider.Player?.References?.Body?.Head;
                bodyRoot = rider.Player?.References?.Body?.Root;
            }
            catch (Exception) { head = null; }

            if (head == null && bodyRoot == null) return;

            Vector3 at = head != null ? head.position : bodyRoot.position + Vector3.up * 1.2f;
            Animator anim = EtsAvatarSync.FindMain(at, out Transform mainRoot);
            if (anim == null || mainRoot == null) return;

            Transform holder = mainRoot.parent != null ? mainRoot.parent : mainRoot;
            rider.Holder = holder;
            rider.Frame = null; // applied by Visit once the parts are listed
            rider.Parts.Clear();

            _found.Clear();
            holder.GetComponentsInChildren(true, _found);
            int magica = 0, bones = 0;

            for (int i = 0; i < _found.Count; i++)
            {
                MonoBehaviour b = _found[i];
                if (b == null) continue;
                Type t = b.GetType();

                if (t.Name == "MagicaCloth" && Learn(t, true))
                {
                    rider.Parts.Add(new Cloth { Target = b, Magica = true });
                    magica++;
                }
                else if (t.Name == "DynamicBone" && Learn(t, false))
                {
                    rider.Parts.Add(new Cloth { Target = b, Magica = false });
                    bones++;
                }
            }

            _found.Clear();

            if (!_saidOn && rider.Parts.Count > 0)
            {
                _saidOn = true;
                Debug.Log("[SomniumEuroTruck] Avatar cloth: a rider's avatar '" + anim.name + "' has " + magica + " MagicaCloth and " + bones +
                          " Dynamic Bone components; anchored to the vehicle while riding (the client's elevator fix, rebuilt).");
            }
        }

        /// <summary>The reflection members of a client type, once; false (logged once) when they are not there.</summary>
        private bool Learn(Type t, bool magica)
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                if (magica)
                {
                    if (s_magicaType == t) return true;
                    PropertyInfo serialize = t.GetProperty("SerializeData", all);
                    MethodInfo change = t.GetMethod("SetParameterChange", all, null, Type.EmptyTypes, null);
                    FieldInfo inertia = serialize != null ? serialize.PropertyType.GetField("inertiaConstraint", all) : null;
                    FieldInfo anchor = inertia != null ? inertia.FieldType.GetField("anchor", all) : null;
                    FieldInfo anchorInertia = inertia != null ? inertia.FieldType.GetField("anchorInertia", all) : null;

                    if (serialize == null || change == null || inertia == null || anchor == null || anchorInertia == null)
                    {
                        Fail("MagicaCloth has no SerializeData.inertiaConstraint.anchor / SetParameterChange");
                        return false;
                    }

                    s_serializeData = serialize; s_setChange = change; s_inertia = inertia; s_anchor = anchor; s_anchorInertia = anchorInertia;
                    s_magicaType = t;
                    return true;
                }

                if (s_boneType == t) return true;
                FieldInfo inert = t.GetField("m_Inert", all);
                PropertyInfo inertProp = null;
                MethodInfo update = t.GetMethod("UpdateParameters", all, null, Type.EmptyTypes, null) ?? t.GetMethod("ForceUpdateParameters", all, null, Type.EmptyTypes, null);

                // The client's own path (PhysicComponentsHandler.SetToElevatorStates): its IDynamicBone interface,
                // m_InertiaWeight + ForceUpdateParameters. The 2026-10-29 client log said the class itself had neither
                // m_Inert nor UpdateParameters by those names.
                foreach (Type face in t.GetInterfaces())
                {
                    if (face.Name != "IDynamicBone") continue;
                    if (inert == null) inertProp = face.GetProperty("m_InertiaWeight");
                    if (update == null) update = face.GetMethod("ForceUpdateParameters", Type.EmptyTypes);
                }

                if (inert == null && (inertProp == null || !inertProp.CanWrite))
                {
                    var names = new System.Text.StringBuilder();
                    foreach (FieldInfo f in t.GetFields(all))
                    {
                        if (f.FieldType == typeof(float) && names.Length < 400) names.Append(f.Name).Append(' ');
                    }

                    Fail("DynamicBone (" + t.FullName + ") has no m_Inert field nor IDynamicBone.m_InertiaWeight; its float fields: " + names);
                    return false;
                }

                s_inert = inert; s_inertProp = inertProp; s_updateParams = update;
                s_boneType = t;
                return true;
            }
            catch (Exception e)
            {
                Fail(e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Anchors one part to the vehicle (frame) or, with null, gives it back its own values.</summary>
        private void Apply(Cloth part, Transform frame)
        {
            if (part.Target == null) return;

            try
            {
                if (part.Magica)
                {
                    object inertia = s_inertia.GetValue(s_serializeData.GetValue(part.Target));
                    if (inertia == null) return;

                    if (frame != null)
                    {
                        if (!part.Applied)
                        {
                            part.OldAnchor = s_anchor.GetValue(inertia) as Transform;
                            part.OldValue = (float)s_anchorInertia.GetValue(inertia);
                            part.Applied = true;
                        }

                        s_anchor.SetValue(inertia, frame);
                        s_anchorInertia.SetValue(inertia, 0f);
                    }
                    else if (part.Applied)
                    {
                        s_anchor.SetValue(inertia, part.OldAnchor);
                        s_anchorInertia.SetValue(inertia, part.OldValue);
                        part.Applied = false;
                    }

                    s_setChange.Invoke(part.Target, null);
                }
                else
                {
                    if (frame != null)
                    {
                        if (!part.Applied)
                        {
                            part.OldValue = s_inert != null ? (float)s_inert.GetValue(part.Target) : (float)s_inertProp.GetValue(part.Target);
                            part.Applied = true;
                        }

                        if (s_inert != null) s_inert.SetValue(part.Target, 1f); else s_inertProp.SetValue(part.Target, 1f);
                    }
                    else if (part.Applied)
                    {
                        if (s_inert != null) s_inert.SetValue(part.Target, part.OldValue); else s_inertProp.SetValue(part.Target, part.OldValue);
                        part.Applied = false;
                    }

                    if (s_updateParams != null) s_updateParams.Invoke(part.Target, null);
                }
            }
            catch (Exception e)
            {
                Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        private void Restore(Rider rider)
        {
            for (int i = 0; i < rider.Parts.Count; i++) Apply(rider.Parts[i], null);
            rider.Parts.Clear();
            rider.Frame = null;
        }

        private void Fail(string why)
        {
            if (_saidFail) return;
            _saidFail = true;
            Debug.LogWarning("[SomniumEuroTruck] Avatar cloth: cannot anchor avatar cloth/bones to vehicles (" + why + "); they keep the client's own settings.");
        }
    }
}
