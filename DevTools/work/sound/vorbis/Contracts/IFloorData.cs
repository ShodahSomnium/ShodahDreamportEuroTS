namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis.Contracts
{
    interface IFloorData
    {
        bool ExecuteChannel { get; }
        bool ForceEnergy { get; set; }
        bool ForceNoEnergy { get; set; }
    }
}
