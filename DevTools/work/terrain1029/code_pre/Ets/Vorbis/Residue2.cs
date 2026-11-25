// Vendored from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License, Copyright (c) 2020 Andrew Ward;
// notice in LICENSE.md beside this file. Changed here: the namespace, moved under the world's own; Span<float> read
// buffers replaced by float[]; Enum.HasFlag (it boxes) replaced by a mask test. The Ogg container, VorbisReader and the
// obsolete interfaces are left out - EtsFmodBank feeds the packets of the game's FSB5 samples straight in.

using SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis.Contracts;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.NVorbis
{
    // all channels in one pass, interleaved
    class Residue2 : Residue0
    {
        int _channels;

        public override void Init(IPacket packet, int channels, ICodebook[] codebooks)
        {
            _channels = channels;
            base.Init(packet, 1, codebooks);
        }

        public override void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer)
        {
            // since we're doing all channels in a single pass, the block size has to be multiplied.
            // otherwise this is just a pass-through call
            base.Decode(packet, doNotDecodeChannel, blockSize * _channels, buffer);
        }

        protected override bool WriteVectors(ICodebook codebook, IPacket packet, float[][] residue, int channel, int offset, int partitionSize)
        {
            var chPtr = 0;

            offset /= _channels;
            for (int c = 0; c < partitionSize;)
            {
                var entry = codebook.DecodeScalar(packet);
                if (entry == -1)
                {
                    return true;
                }
                for (var d = 0; d < codebook.Dimensions; d++, c++)
                {
                    residue[chPtr][offset] += codebook[entry, d];
                    if (++chPtr == _channels)
                    {
                        chPtr = 0;
                        offset++;
                    }
                }
            }

            return false;
        }
    }
}
