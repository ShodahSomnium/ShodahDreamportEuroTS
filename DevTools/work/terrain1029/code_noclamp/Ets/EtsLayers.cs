namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The Somnium project's physics layers, by the indices its own TagManager gives them, and how
    /// this world uses them.
    ///
    /// The layer collision matrix is the same string in the client
    /// (E:\Somnium Space\Main Build\ProjectSettings\DynamicsManager.asset) and in this SDK project,
    /// compared byte for byte. Decoded little-endian:
    ///
    ///   Ignore Raycast (2), the avatar's CharacterController (XR.Body / NonXR.Body, with a
    ///       kinematic Rigidbody): collides with 0 Default, 14 Terrain, 16, 23 Environment, 28 Vehicle.
    ///   Vehicle (28): collides with 0, 2, 3, 7, 11, 14, 15, 16, 23, 24, 26, 28-31.
    ///
    /// The VR player's ground check (_groundLayers on Player.XR.Features.prefab) is layers
    /// 0, 2, 7, 14, 16, 23, 28. So the avatar stands on everything this world builds - the drive
    /// surface on Default, buildings and roadside models on Environment - in both modes, the same
    /// way the Quake world's map (built under a Default-layer loader) is walked on.
    ///
    ///   - The drive surface (roads, junctions, terrain strips) stays on <see cref="Default"/>.
    ///     The wheels feel it by ray. The truck's and trailer's body boxes ignore it per collider
    ///     (<see cref="BodyExcludes"/>), so on a crest or a kerb the box never meets the road and
    ///     fights the suspension.
    ///   - Buildings and roadside models go on <see cref="Environment"/>, which the truck and
    ///     trailer bodies collide with, so you can no longer drive through a house.
    ///   - The truck and trailer go on <see cref="Vehicle"/>. Their coupling joint turns off
    ///     collision between the two, and the wheel rays skip the Vehicle layer so a wheel never
    ///     catches on its own chassis.
    ///
    /// The seated player is the one thing that must not touch the truck. The Bridge's
    /// SetCollisionDisableState only adds layer 2 to the avatar controller's own excludeLayers
    /// (the client's MotionFeature: it is meant to stop players bumping into each other), so the
    /// avatar's capsule, a kinematic body, stays solid against the truck. Written into the cab
    /// every frame, it pushed the truck out of itself at up to the 10 m/s depenetration speed
    /// until the wheels went under the road (the first client run, 2026-09-21: 6, then 11, then
    /// 297 km/h). The editor never showed it because the editor has no avatar. So while a player
    /// sits in it the truck's box also ignores the avatar's layers (<see cref="SeatedBodyExcludes"/>);
    /// parked, it is kinematic and solid, and nobody can walk through it or shove it.
    /// </summary>
    public static class EtsLayers
    {
        public const int Default = 0;
        public const int IgnoreRaycast = 2;
        public const int Ui = 5;
        public const int PhysicalObject = 7;
        public const int Terrain = 14;
        public const int Environment = 23;
        public const int Player = 24;
        public const int Vehicle = 28;

        /// <summary>The layers a wheel or a spawn ray should find: the drive surface, never the truck.</summary>
        public const int GroundMask = (1 << Default) | (1 << Terrain);

        /// <summary>What a truck or trailer body box must pass through: the drive surface its wheels ride on.</summary>
        public const int BodyExcludes = GroundMask;

        /// <summary>
        /// What the body boxes pass through while somebody is driving: the drive surface and the
        /// avatar - its controller capsule (2), the VR hands' physical boxes (7, dynamic 1 kg bodies
        /// on joints) and the Player layer (24).
        /// </summary>
        public const int SeatedBodyExcludes = GroundMask | (1 << IgnoreRaycast) | (1 << PhysicalObject) | (1 << Player);

        /// <summary>
        /// What a seated passenger's own capsule (the CharacterController on Body.Root) passes through while
        /// seated: every vehicle. The passenger sits in another player's puppet, which stays kinematic and solid
        /// so walkers can stand on it and cannot walk through it (BodyExcludes), and the Bridge's collision switch
        /// only adds layer 2 to the capsule's excludeLayers (MotionFeature.cs:180-191) - the capsule would fight
        /// the cab it is written into every frame. The client only ORs and clears bit 2 of that mask, so this bit
        /// survives until EtsTruckSeat clears it again ([elev §7.2], PLAN §5.5 "Passenger" step 3).
        /// </summary>
        public const int SeatedCapsuleExcludes = 1 << Vehicle;

        /// <summary>
        /// What a walker can stand on and be carried by: trucks, trailers and AI cars are all one box on the
        /// Vehicle layer, box and Rigidbody on the same root (EtsTruckController.Configure, EtsTrailer.cs:156-169,
        /// EtsTrafficModels.cs:365-374). The rider pass casts down against this only, so the road under a truck
        /// never counts as riding it, and the hit's Rigidbody is the frame the rider is carried by.
        /// </summary>
        public const int PlatformMask = 1 << Vehicle;

        /// <summary>Puts a whole object tree on one layer.</summary>
        public static void SetRecursively(UnityEngine.Transform root, int layer)
        {
            root.gameObject.layer = layer;

            for (int i = 0; i < root.childCount; i++)
            {
                SetRecursively(root.GetChild(i), layer);
            }
        }
    }
}
