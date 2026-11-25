// Vendored from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License, Copyright (c) 2020 Andrew Ward;
// notice in LICENSE.md beside this file. Changed here: the namespace, moved under the world's own; Span<float> read
// buffers replaced by float[]; Enum.HasFlag (it boxes) replaced by a mask test. The Ogg container, VorbisReader and the
// obsolete interfaces are left out - EtsFmodBank feeds the packets of the game's FSB5 samples straight in.

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis.Contracts
{
    interface ICodebook
    {
        void Init(IPacket packet, IHuffman huffman);

        int Dimensions { get; }
        int Entries { get; }
        int MapType { get; }

        float this[int entry, int dim] { get; }

        int DecodeScalar(IPacket packet);
    }
}
