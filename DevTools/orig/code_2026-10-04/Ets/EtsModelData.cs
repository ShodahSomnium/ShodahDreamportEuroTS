using System;
using System.Collections.Generic;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>One drawable chunk of a model: one material, one vertex buffer, one index list.</summary>
    public sealed class EtsModelPiece
    {
        public int MaterialIndex;
        public int VertexCount;
        public int UvChannels;

        /// <summary>x, y, z per vertex, in the engine's own right-handed frame.</summary>
        public float[] Positions;

        public float[] Normals;

        /// <summary>u, v per vertex for the first channel. Null when the piece has none.</summary>
        public float[] Uv0;

        /// <summary>Second channel, when present.</summary>
        public float[] Uv1;

        /// <summary>Every uv channel, u, v per vertex each. Uv0 and Uv1 are the first two.</summary>
        public float[][] Uvs = new float[0][];

        /// <summary>r, g, b, a bytes per vertex. Null when absent.</summary>
        public byte[] Colors;

        /// <summary>x, y, z, w per vertex. Null when absent.</summary>
        public float[] Tangents;

        /// <summary>Triangle list.</summary>
        public int[] Indices;

        public float[] BoundsMin = new float[3];
        public float[] BoundsMax = new float[3];
    }

    /// <summary>A named group of pieces that a variant can switch on and off together.</summary>
    public sealed class EtsModelPart
    {
        public string Name;
        public int PieceStart;
        public int PieceCount;
        public int LocatorStart;
        public int LocatorCount;

        /// <summary>Whether this part is shown, per variant index. Null means always.</summary>
        public bool[] VisibleInVariant;

        public bool IsVisible(int variant)
        {
            if (VisibleInVariant == null || variant < 0 || variant >= VisibleInVariant.Length)
            {
                return true;
            }

            return VisibleInVariant[variant];
        }
    }

    /// <summary>A named point on the model where something else attaches: a wheel, a mirror, a light.</summary>
    public sealed class EtsModelLocator
    {
        public string Name;
        public float[] Position = new float[3];

        /// <summary>x, y, z, w. The file stores w first; this is already reordered.</summary>
        public float[] Rotation = new float[4];

        public float Scale = 1f;
        public int Hookup;
    }

    /// <summary>
    /// A bone of a skinned model's skeleton: its name, parent and bind pose relative to the
    /// parent, plus the bind transform in model space as the file stores it.
    /// </summary>
    public sealed class EtsModelBone
    {
        public string Name;
        public int Parent = -1;

        /// <summary>x, y, z, w relative to the parent. The file stores w first; reordered here.</summary>
        public float[] Rotation = { 0f, 0f, 0f, 1f };

        public float[] Translation = new float[3];
        public float[] Scale = { 1f, 1f, 1f };

        /// <summary>Bind pose, bone to model space, 4x4 row-major with the translation in the last column.</summary>
        public float[] World = new float[16];
    }

    /// <summary>
    /// A model as the game stores it: a descriptor (.pmd) that names its materials, looks, variants
    /// and parts, and a geometry file (.pmg) that holds the vertices. Both are read here into one
    /// object that knows nothing about Unity, so the harness can check it against the archives.
    ///
    /// Everything about the layout was read off real files - a 392-byte road decal and a
    /// 1.2-megabyte bridge - and confirmed against the DAF truck:
    ///
    /// pmd, version 4: sixteen u32 of counts and offsets, then look names and variant names as
    /// tokens, then per part a range into an attribute table, the attribute table (name token,
    /// type, offset), the attribute values (one block per variant), then for every look a row of
    /// u32 offsets to material path strings.
    ///
    /// pmg, version 0x15 ("gmP"): a 112-byte header with counts, bounds and pool offsets; parts
    /// of 24 bytes (name token, piece range, locator range); locators of 44 bytes; pieces of
    /// 100 bytes; then the string, vertex and index pools. Vertices are interleaved per piece
    /// with a stride at piece offset 60 and absolute offsets of position, normal, uv, colour and
    /// tangent for vertex zero at 64, 68, 72, 76 and 84 - minus one when the attribute is absent.
    /// Extra uv channels follow the first at eight-byte steps. Indices are 16-bit.
    /// </summary>
    public sealed class EtsModelData
    {
        public string Path;
        public string[] Looks = new string[0];
        public string[] Variants = new string[0];

        /// <summary>Material path per look, then per material index.</summary>
        public string[][] LookMaterials = new string[0][];

        public EtsModelPart[] Parts = new EtsModelPart[0];
        public EtsModelPiece[] Pieces = new EtsModelPiece[0];
        public EtsModelLocator[] Locators = new EtsModelLocator[0];
        public EtsModelBone[] Bones = new EtsModelBone[0];

        /// <summary>Hash of the skeleton; an animation file names the skeleton it was made for with the same value.</summary>
        public ulong SkeletonHash;

        /// <summary>Index of the bone with this name, or -1.</summary>
        public int FindBone(string name)
        {
            for (int i = 0; i < Bones.Length; i++)
            {
                if (Bones[i].Name == name) return i;
            }

            return -1;
        }

        public float[] BoundsMin = new float[3];
        public float[] BoundsMax = new float[3];

        public int LookIndex(string name)
        {
            return Array.IndexOf(Looks, name);
        }

        public int VariantIndex(string name)
        {
            return Array.IndexOf(Variants, name);
        }

        public EtsModelLocator FindLocator(string name)
        {
            foreach (EtsModelLocator locator in Locators)
            {
                if (locator.Name == name)
                {
                    return locator;
                }
            }

            return null;
        }

        /// <param name="pmd">The descriptor bytes.</param>
        /// <param name="pmg">The geometry bytes.</param>
        /// <param name="path">The descriptor's archive path, for messages.</param>
        public static EtsModelData Parse(byte[] pmd, byte[] pmg, string path)
        {
            var model = new EtsModelData { Path = path };
            ParsePmd(model, pmd);
            ParsePmg(model, pmg);
            return model;
        }

        // ------------------------------------------------------------------ pmd

        private static void ParsePmd(EtsModelData model, byte[] b)
        {
            if (b == null || b.Length < 64)
            {
                throw new ArgumentException(model.Path + ": pmd too short.");
            }

            uint version = U32(b, 0);

            if (version != 4)
            {
                throw new ArgumentException(model.Path + ": pmd version " + version + " is not understood (expected 4).");
            }

            int materialCount = (int)U32(b, 4);
            int lookCount = (int)U32(b, 8);
            int variantCount = (int)U32(b, 16);
            int partCount = (int)U32(b, 20);
            int attribCount = (int)U32(b, 24);
            int attribValuesSize = (int)U32(b, 28);
            int lookOffset = (int)U32(b, 36);
            int variantOffset = (int)U32(b, 40);
            int partAttribsOffset = (int)U32(b, 44);
            int attribValueOffset = (int)U32(b, 48);
            int attribHeaderOffset = (int)U32(b, 52);
            int materialOffset = (int)U32(b, 56);

            model.Looks = new string[lookCount];

            for (int i = 0; i < lookCount; i++)
            {
                model.Looks[i] = EtsToken.Decode(U64(b, lookOffset + i * 8));
            }

            model.Variants = new string[variantCount];

            for (int i = 0; i < variantCount; i++)
            {
                model.Variants[i] = EtsToken.Decode(U64(b, variantOffset + i * 8));
            }

            // Attribute table: which attribute a part owns, and where each variant's value sits.
            var attribNames = new string[attribCount];
            var attribTypes = new int[attribCount];
            var attribOffsets = new int[attribCount];

            for (int i = 0; i < attribCount; i++)
            {
                int o = attribHeaderOffset + i * 16;
                attribNames[i] = EtsToken.Decode(U64(b, o));
                attribTypes[i] = (int)U32(b, o + 8);
                attribOffsets[i] = (int)U32(b, o + 12);
            }

            model.Parts = new EtsModelPart[partCount];

            for (int p = 0; p < partCount; p++)
            {
                int from = (int)U32(b, partAttribsOffset + p * 8);
                int to = (int)U32(b, partAttribsOffset + p * 8 + 4);
                var part = new EtsModelPart();

                for (int a = from; a < to && a < attribCount; a++)
                {
                    if (attribNames[a] != "visible")
                    {
                        continue;
                    }

                    part.VisibleInVariant = new bool[variantCount];

                    for (int v = 0; v < variantCount; v++)
                    {
                        int o = attribValueOffset + v * attribValuesSize + attribOffsets[a];
                        part.VisibleInVariant[v] = o + 4 <= b.Length && U32(b, o) != 0;
                    }
                }

                model.Parts[p] = part;
            }

            model.LookMaterials = new string[lookCount][];

            for (int l = 0; l < lookCount; l++)
            {
                model.LookMaterials[l] = new string[materialCount];

                for (int m = 0; m < materialCount; m++)
                {
                    int o = materialOffset + (l * materialCount + m) * 4;
                    int stringOffset = (int)U32(b, o);
                    model.LookMaterials[l][m] = ReadCString(b, stringOffset);
                }
            }
        }

        // ------------------------------------------------------------------ pmg

        private static void ParsePmg(EtsModelData model, byte[] b)
        {
            if (b == null || b.Length < 112)
            {
                throw new ArgumentException(model.Path + ": pmg too short.");
            }

            int version = b[0];
            string signature = Encoding.ASCII.GetString(b, 1, 3);

            if (signature != "gmP")
            {
                throw new ArgumentException(model.Path + ": not a pmg (signature \"" + signature + "\").");
            }

            if (version != 0x15)
            {
                throw new ArgumentException(model.Path + ": pmg version 0x" + version.ToString("X") + " is not understood (expected 0x15).");
            }

            int pieceCount = (int)U32(b, 4);
            int partCount = (int)U32(b, 8);
            int locatorCount = (int)U32(b, 20);
            int boneCount = (int)U32(b, 12);
            int skeletonOffset = (int)U32(b, 72);
            model.SkeletonHash = U64(b, 24);

            for (int i = 0; i < 3; i++)
            {
                model.BoundsMin[i] = F32(b, 48 + i * 4);
                model.BoundsMax[i] = F32(b, 60 + i * 4);
            }

            int partsOffset = (int)U32(b, 76);
            int locatorsOffset = (int)U32(b, 80);
            int piecesOffset = (int)U32(b, 84);
            int vertexPoolOffset = (int)U32(b, 96);
            int vertexPoolSize = (int)U32(b, 100);
            int indexPoolOffset = (int)U32(b, 104);
            int indexPoolSize = (int)U32(b, 108);

            // Parts. The descriptor already made them with their visibility; the geometry adds
            // the names and the ranges. When the two disagree on count, the geometry wins for
            // ranges and the descriptor's visibility is kept where it lines up.
            if (model.Parts.Length != partCount)
            {
                var resized = new EtsModelPart[partCount];

                for (int p = 0; p < partCount; p++)
                {
                    resized[p] = p < model.Parts.Length ? model.Parts[p] : new EtsModelPart();
                }

                model.Parts = resized;
            }

            for (int p = 0; p < partCount; p++)
            {
                int o = partsOffset + p * 24;
                EtsModelPart part = model.Parts[p];
                part.Name = EtsToken.Decode(U64(b, o));
                part.PieceCount = (int)U32(b, o + 8);
                part.PieceStart = (int)U32(b, o + 12);
                part.LocatorCount = (int)U32(b, o + 16);
                part.LocatorStart = (int)U32(b, o + 20);
            }

            // Bones: 200 bytes each - name, bind matrix and its inverse, stretch, rotation (w first),
            // translation, scale, sign, parent index. Only skinned models such as the interior's
            // animated dashboard have any.
            model.Bones = new EtsModelBone[0];

            if (boneCount > 0 && boneCount < 4096 && skeletonOffset > 0 && skeletonOffset + boneCount * 200 <= b.Length)
            {
                model.Bones = new EtsModelBone[boneCount];

                for (int i = 0; i < boneCount; i++)
                {
                    int o = skeletonOffset + i * 200;
                    var bone = new EtsModelBone { Name = EtsToken.Decode(U64(b, o)) };

                    for (int m = 0; m < 16; m++)
                    {
                        bone.World[m] = F32(b, o + 8 + m * 4);
                    }

                    bone.Rotation[3] = F32(b, o + 152);
                    bone.Rotation[0] = F32(b, o + 156);
                    bone.Rotation[1] = F32(b, o + 160);
                    bone.Rotation[2] = F32(b, o + 164);
                    bone.Translation[0] = F32(b, o + 168);
                    bone.Translation[1] = F32(b, o + 172);
                    bone.Translation[2] = F32(b, o + 176);
                    bone.Scale[0] = F32(b, o + 180);
                    bone.Scale[1] = F32(b, o + 184);
                    bone.Scale[2] = F32(b, o + 188);
                    uint parent = U32(b, o + 196);
                    bone.Parent = parent < (uint)boneCount && (int)parent != i ? (int)parent : -1;
                    model.Bones[i] = bone;
                }
            }

            model.Locators = new EtsModelLocator[locatorCount];

            for (int l = 0; l < locatorCount; l++)
            {
                int o = locatorsOffset + l * 44;
                var locator = new EtsModelLocator
                {
                    Name = EtsToken.Decode(U64(b, o)),
                    Scale = F32(b, o + 20),
                    Hookup = (int)U32(b, o + 40)
                };

                locator.Position[0] = F32(b, o + 8);
                locator.Position[1] = F32(b, o + 12);
                locator.Position[2] = F32(b, o + 16);

                // Stored w, x, y, z. Kept x, y, z, w here.
                locator.Rotation[3] = F32(b, o + 24);
                locator.Rotation[0] = F32(b, o + 28);
                locator.Rotation[1] = F32(b, o + 32);
                locator.Rotation[2] = F32(b, o + 36);

                model.Locators[l] = locator;
            }

            model.Pieces = new EtsModelPiece[pieceCount];

            for (int p = 0; p < pieceCount; p++)
            {
                int o = piecesOffset + p * 100;
                var piece = new EtsModelPiece
                {
                    MaterialIndex = (int)U32(b, o + 16),
                    UvChannels = (int)U32(b, o + 12)
                };

                int indexCount = (int)U32(b, o);
                int vertexCount = (int)U32(b, o + 4);

                for (int i = 0; i < 3; i++)
                {
                    piece.BoundsMin[i] = F32(b, o + 36 + i * 4);
                    piece.BoundsMax[i] = F32(b, o + 48 + i * 4);
                }

                int stride = (int)U32(b, o + 60);
                int positionOffset = I32(b, o + 64);
                int normalOffset = I32(b, o + 68);
                int uvOffset = I32(b, o + 72);
                int colorOffset = I32(b, o + 76);
                int tangentOffset = I32(b, o + 84);
                int indexOffset = I32(b, o + 96);

                piece.VertexCount = vertexCount;

                if (stride <= 0 || positionOffset < 0)
                {
                    throw new ArgumentException(model.Path + ": piece " + p + " has no vertex layout (stride " + stride + ").");
                }

                if (positionOffset + (long)(vertexCount - 1) * stride + 12 > b.Length)
                {
                    throw new ArgumentException(model.Path + ": piece " + p + " vertices run past the end of the file.");
                }

                piece.Positions = new float[vertexCount * 3];

                for (int v = 0; v < vertexCount; v++)
                {
                    int vo = positionOffset + v * stride;
                    piece.Positions[v * 3] = F32(b, vo);
                    piece.Positions[v * 3 + 1] = F32(b, vo + 4);
                    piece.Positions[v * 3 + 2] = F32(b, vo + 8);
                }

                if (normalOffset >= 0)
                {
                    piece.Normals = new float[vertexCount * 3];

                    for (int v = 0; v < vertexCount; v++)
                    {
                        int vo = normalOffset + v * stride;
                        piece.Normals[v * 3] = F32(b, vo);
                        piece.Normals[v * 3 + 1] = F32(b, vo + 4);
                        piece.Normals[v * 3 + 2] = F32(b, vo + 8);
                    }
                }

                if (uvOffset >= 0 && piece.UvChannels > 0)
                {
                    // Channels sit side by side inside each vertex, eight bytes apart, and the
                    // stride confirms it: a piece with three channels is exactly sixteen bytes
                    // wider than the same piece would be with one.
                    piece.Uvs = new float[piece.UvChannels][];

                    for (int c = 0; c < piece.UvChannels; c++)
                    {
                        var channel = new float[vertexCount * 2];

                        for (int v = 0; v < vertexCount; v++)
                        {
                            int vo = uvOffset + c * 8 + v * stride;
                            channel[v * 2] = F32(b, vo);
                            channel[v * 2 + 1] = F32(b, vo + 4);
                        }

                        piece.Uvs[c] = channel;
                    }

                    piece.Uv0 = piece.Uvs[0];
                    piece.Uv1 = piece.UvChannels > 1 ? piece.Uvs[1] : null;
                }

                if (colorOffset >= 0)
                {
                    piece.Colors = new byte[vertexCount * 4];

                    for (int v = 0; v < vertexCount; v++)
                    {
                        Buffer.BlockCopy(b, colorOffset + v * stride, piece.Colors, v * 4, 4);
                    }
                }

                if (tangentOffset >= 0)
                {
                    piece.Tangents = new float[vertexCount * 4];

                    for (int v = 0; v < vertexCount; v++)
                    {
                        int vo = tangentOffset + v * stride;
                        piece.Tangents[v * 4] = F32(b, vo);
                        piece.Tangents[v * 4 + 1] = F32(b, vo + 4);
                        piece.Tangents[v * 4 + 2] = F32(b, vo + 8);
                        piece.Tangents[v * 4 + 3] = F32(b, vo + 12);
                    }
                }

                if (indexOffset < 0 || indexOffset + indexCount * 2 > b.Length)
                {
                    throw new ArgumentException(model.Path + ": piece " + p + " indices run past the end of the file.");
                }

                piece.Indices = new int[indexCount];

                for (int i = 0; i < indexCount; i++)
                {
                    piece.Indices[i] = BitConverter.ToUInt16(b, indexOffset + i * 2);
                }

                model.Pieces[p] = piece;
            }
        }

        // ------------------------------------------------------------------ helpers

        private static uint U32(byte[] b, int o)
        {
            return BitConverter.ToUInt32(b, o);
        }

        private static int I32(byte[] b, int o)
        {
            return BitConverter.ToInt32(b, o);
        }

        private static ulong U64(byte[] b, int o)
        {
            return BitConverter.ToUInt64(b, o);
        }

        private static float F32(byte[] b, int o)
        {
            return BitConverter.ToSingle(b, o);
        }

        private static string ReadCString(byte[] b, int offset)
        {
            if (offset < 0 || offset >= b.Length)
            {
                return string.Empty;
            }

            int end = offset;

            while (end < b.Length && b[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(b, offset, end - offset);
        }
    }
}
