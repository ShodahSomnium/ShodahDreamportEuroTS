namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Switches for the integrated plan's new flow (owner L, the lead; PLAN.md §8 Phase 0, task 0.2).
    ///
    /// NewFlow gates the bootstrap list in EtsNet.Awake (PLAN §3.2: EtsSession, EtsProfileStore, EtsMapIndex,
    /// EtsJobs, EtsRiders, the seat/camera/wheel kit, EtsTruckAudio, EtsMenu, EtsTabletApp, EtsRouteAdvisor,
    /// EtsFlow). It was false through Phase 1, so those builds played exactly as the world did before Phase 0: the
    /// old front end, the one scene truck, the old single-truck networking.
    ///
    /// Phase 2 (2026-09-23) turns it on (PLAN §8 Phase 2, first line): the game's own start - the headquarters map
    /// in a black void, the loading screen, the chosen city's yard - and the ETS 2 app in the Somnium tablet, with a
    /// truck per player from Company > Truck Preference. Setting it back to false still compiles and plays the old
    /// flow: every owner's Phase 2 change is behind this flag, and C and F build the scene truck and the map at start
    /// whenever it is false (the scene has _loadOnStart 0 on both viewers). On the Phase 2 scene ("Ets Net Session",
    /// no "Ets Net Truck 0") the old one-truck sync finds nothing to network, so the scene truck then plays alone.
    ///
    /// A static field, not a scene value: the scene overrides code defaults for serialized fields (memory
    /// "ets2-161-port", Trap 1), and a flag that must read the same in the editor and in the client should not
    /// depend on what the last scene save wrote. Nothing starts the Phase 0 client probes (EtsOrderProbe,
    /// EtsClientProbe) any more; their files still compile.
    /// </summary>
    public static class EtsFeatures
    {
        public static bool NewFlow = true;
    }
}
