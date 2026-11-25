// Vendored from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License, Copyright (c) 2020 Andrew Ward;
// notice in LICENSE.md beside this file. Changed here: the namespace, moved under the world's own; Span<float> read
// buffers replaced by float[]; Enum.HasFlag (it boxes) replaced by a mask test. The Ogg container, VorbisReader and the
// obsolete interfaces are left out - EtsFmodBank feeds the packets of the game's FSB5 samples straight in.

using SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis.Contracts;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis
{
    class Factory : IFactory
    {
        public IHuffman CreateHuffman()
        {
            return new Huffman();
        }

        public IMdct CreateMdct()
        {
            return new Mdct();
        }

        public ICodebook CreateCodebook()
        {
            return new Codebook();
        }

        public IFloor CreateFloor(IPacket packet)
        {
            var type = (int)packet.ReadBits(16);
            switch (type)
            {
                case 0: return new Floor0();
                case 1: return new Floor1();
                default: throw new System.IO.InvalidDataException("Invalid floor type!");
            }
        }

        public IMapping CreateMapping(IPacket packet)
        {
            if (packet.ReadBits(16) != 0)
            {
                throw new System.IO.InvalidDataException("Invalid mapping type!");
            }

            return new Mapping();
        }

        public IMode CreateMode()
        {
            return new Mode();
        }

        public IResidue CreateResidue(IPacket packet)
        {
            var type = (int)packet.ReadBits(16);
            switch (type)
            {
                case 0: return new Residue0();
                case 1: return new Residue1();
                case 2: return new Residue2();
                default: throw new System.IO.InvalidDataException("Invalid residue type!");
            }
        }
    }
}
