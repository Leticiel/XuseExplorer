using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class WagArchiveReader : IArchiveReader
    {
        public string Tag => "WAG";
        public string Description => "Xuse/Eternal resource archive (WAG)";
        public string[] Extensions => new[] { ".wag", ".4ag", ".004" };

        public bool CanImport => true;

        public ArchiveFile? TryOpen(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return TryOpenInternal(stream, filePath, Path.GetFileName(filePath));
        }

        public ArchiveFile? TryOpenFromStream(Stream stream, string virtualName)
        {
            return TryOpenInternal(stream, virtualName, virtualName);
        }

        private ArchiveFile? TryOpenInternal(Stream stream, string filePath, string arcFilename)
        {
            var reader = new BinaryReaderEx(stream);

            if (stream.Length < 0x4A) return null;

            uint signature = reader.ReadUInt32(0);
            if (signature != 0x40474157 && signature != 0x34464147) return null;

            int version = reader.ReadUInt16(4);
            if (version != 0x300 && version != 0x200) return null;

            int count = reader.ReadInt32(0x46);
            if (count <= 0 || count > 100000) return null;

            byte[] title = reader.ReadBytes(6, 0x40);
            int titleLength = Array.IndexOf<byte>(title, 0);
            if (titleLength == -1) titleLength = title.Length;

            if (version != 0x200) arcFilename = arcFilename.ToLowerInvariant();
            string baseFilename = Path.GetFileNameWithoutExtension(arcFilename);

            byte[] nameKey = GenerateKey(Encoding.GetEncoding(932).GetBytes(arcFilename));

            uint indexOffset = 0x200 + (uint)nameKey.Select(x => (int)x).Sum();
            for (int i = 0; i < nameKey.Length; i++)
            {
                indexOffset ^= nameKey[i];
                indexOffset = RotR(indexOffset, 1);
            }
            for (int i = 0; i < nameKey.Length; i++)
            {
                indexOffset ^= nameKey[i];
                indexOffset = RotR(indexOffset, 1);
            }
            indexOffset %= 0x401;
            indexOffset += 0x4A;

            byte[] index = reader.ReadBytes(indexOffset, 4 * count);
            if (index.Length != 4 * count) return null;

            byte[] indexKey = new byte[index.Length];
            for (int i = 0; i < indexKey.Length; i++)
            {
                int v = nameKey[(i + 1) % nameKey.Length] ^ (nameKey[i % nameKey.Length] + i);
                indexKey[i] = (byte)(count + v);
            }
            Decrypt(indexOffset, indexKey, index);

            byte[] dataKey = GenerateKey(title, titleLength);

            var entries = new List<ArchiveEntry>(count);
            int currentOffset = 0;
            uint nextOffset = BitConverter.ToUInt32(index, currentOffset);

            byte[] chunkBuf = new byte[0x40];

            for (int i = 0; i < count; i++)
            {
                currentOffset += 4;
                uint entryOffset = nextOffset;
                if (entryOffset >= stream.Length) return null;

                uint entryNextOffset;
                if (i + 1 == count)
                    entryNextOffset = (uint)stream.Length;
                else
                    entryNextOffset = BitConverter.ToUInt32(index, currentOffset);

                uint entrySize = entryNextOffset - entryOffset;

                string entryName = "";
                string entryType = "";
                long dataOffset = entryOffset;
                uint dataSize = entrySize;

                try
                {
                    int chunkSize = 8;
                    if (chunkSize > chunkBuf.Length) chunkBuf = new byte[chunkSize];
                    reader.Read(entryOffset, chunkBuf, 0, chunkSize);
                    DecryptChunk(entryOffset, dataKey, chunkBuf, chunkSize);

                    if (version == 0x200)
                    {
                        uint dSize = BitConverter.ToUInt32(chunkBuf, 0);
                        int nameLength = BitConverter.ToInt32(chunkBuf, 4);
                        dataOffset = entryOffset + 0x10;
                        dataSize = dSize;

                        if (nameLength > 0 && dSize < entrySize)
                        {
                            var nameBuf = new byte[nameLength];
                            reader.Read(dataOffset + dSize, nameBuf, 0, nameLength);
                            DecryptChunk((uint)(dataOffset + dSize), dataKey, nameBuf, nameLength);
                            if (nameBuf[nameLength - 1] == '|')
                                nameLength--;
                            entryName = Encoding.GetEncoding(932).GetString(nameBuf, 0, nameLength);
                            if (!string.IsNullOrEmpty(entryName))
                                entryType = DetectTypeFromName(entryName);
                        }
                    }
                    else
                    {
                        if (chunkBuf[0] == 'D' && chunkBuf[1] == 'S' && chunkBuf[2] == 'E' && chunkBuf[3] == 'T')
                        {
                            int chunkCount = BitConverter.ToInt32(chunkBuf, 4);
                            uint chunkOffset = entryOffset + 10;

                            for (int chunk = 0; chunk < chunkCount; chunk++)
                            {
                                var subBuf = new byte[8];
                                reader.Read(chunkOffset, subBuf, 0, 8);
                                DecryptChunk(chunkOffset, dataKey, subBuf, 8);
                                int subSize = BitConverter.ToInt32(subBuf, 4);
                                if (subSize <= 0) break;

                                bool isPICT = subBuf[0] == 'P' && subBuf[1] == 'I' && subBuf[2] == 'C' && subBuf[3] == 'T';
                                bool isFTAG = subBuf[0] == 'F' && subBuf[1] == 'T' && subBuf[2] == 'A' && subBuf[3] == 'G';

                                if (isPICT && string.IsNullOrEmpty(entryType))
                                {
                                    entryType = "image";
                                    dataOffset = chunkOffset + 0x10;
                                    dataSize = (uint)(subSize - 6);
                                }
                                else if (isFTAG && string.IsNullOrEmpty(entryName))
                                {
                                    int ftagSize = subSize - 2;
                                    if (ftagSize > 0)
                                    {
                                        var ftagBuf = new byte[ftagSize];
                                        reader.Read(chunkOffset + 10, ftagBuf, 0, ftagSize);
                                        DecryptChunk(chunkOffset + 10, dataKey, ftagBuf, ftagSize);
                                        entryName = Encoding.GetEncoding(932).GetString(ftagBuf, 0, ftagSize);
                                        int bsIdx = entryName.LastIndexOf('\\');
                                        if (bsIdx >= 0) entryName = entryName.Substring(bsIdx + 1);
                                    }
                                }

                                chunkOffset += 10 + (uint)subSize;
                            }
                        }
                    }
                }
                catch
                {
                }

                if (string.IsNullOrEmpty(entryName))
                    entryName = $"{baseFilename}#{i:D4}";
                if (string.IsNullOrEmpty(entryType))
                    entryType = "unknown";

                entries.Add(new ArchiveEntry
                {
                    Name = entryName,
                    Path = entryName,
                    Type = entryType,
                    Offset = dataOffset,
                    Size = dataSize,
                    ArchivePath = filePath,
                    FormatTag = Tag
                });

                nextOffset = entryNextOffset;
            }

            return new ArchiveFile
            {
                FilePath = filePath,
                FormatTag = Tag,
                FormatDescription = Description,
                FileSize = stream.Length,
                Entries = entries,
                DecryptionKey = dataKey
            };
        }

        public byte[] ExtractEntry(ArchiveFile archive, ArchiveEntry entry)
        {
            Stream stream;
            bool ownsStream;
            if (archive.DataStream != null)
            {
                archive.DataStream.Position = 0;
                stream = archive.DataStream;
                ownsStream = false;
            }
            else
            {
                stream = File.OpenRead(archive.FilePath);
                ownsStream = true;
            }
            try
            {
                var reader = new BinaryReaderEx(stream);
                var data = reader.ReadBytes(entry.Offset, (int)entry.Size);

                if (archive.DecryptionKey != null)
                    Decrypt((uint)entry.Offset, archive.DecryptionKey, data);

                return data;
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }

        public void ImportEntry(ArchiveFile archive, ArchiveEntry entry, byte[] newData, Stream originalStream, Stream outputStream)
        {
            var reader = new BinaryReaderEx(originalStream);
            long originalFileLength = originalStream.Length;

            uint signature = reader.ReadUInt32(0);
            int version = reader.ReadUInt16(4);
            if (version != 0x300)
                throw new NotSupportedException($"WAG import is only supported for v3 (0x300) archives, got v{version:X}.");

            byte[] title = reader.ReadBytes(6, 0x40);
            int titleLength = Array.IndexOf<byte>(title, 0);
            if (titleLength == -1) titleLength = title.Length;

            int count = reader.ReadInt32(0x46);

            string arcFilename = Path.GetFileName(archive.FilePath);
            if (version != 0x200) arcFilename = arcFilename.ToLowerInvariant();

            byte[] nameKey = GenerateKey(Encoding.GetEncoding(932).GetBytes(arcFilename));

            uint indexOffset = 0x200 + (uint)nameKey.Select(x => (int)x).Sum();
            for (int i = 0; i < nameKey.Length; i++)
            {
                indexOffset ^= nameKey[i];
                indexOffset = RotR(indexOffset, 1);
            }
            for (int i = 0; i < nameKey.Length; i++)
            {
                indexOffset ^= nameKey[i];
                indexOffset = RotR(indexOffset, 1);
            }
            indexOffset %= 0x401;
            uint len0 = indexOffset;
            indexOffset += 0x4A;

            uint len1 = CalcLen1(nameKey);

            byte[] index = reader.ReadBytes(indexOffset, 4 * count);
            byte[] indexKey = new byte[index.Length];
            for (int i = 0; i < indexKey.Length; i++)
            {
                int v = nameKey[(i + 1) % nameKey.Length] ^ (nameKey[i % nameKey.Length] + i);
                indexKey[i] = (byte)(count + v);
            }
            Decrypt(indexOffset, indexKey, index);

            uint[] offsets = new uint[count];
            for (int i = 0; i < count; i++)
                offsets[i] = BitConverter.ToUInt32(index, i * 4);

            var sortedOffsets = offsets.OrderBy(x => x).ToList();
            sortedOffsets.Add((uint)originalFileLength);

            byte[] dataKey = GenerateKey(title, titleLength);

            var newEntryDataList = new byte[count][];
            uint firstDataOffset = offsets[0];
            uint currentOffset = firstDataOffset;

            int targetIndex = -1;
            for (int i = 0; i < count; i++)
            {
                uint start = offsets[i];
                int sortIdx = sortedOffsets.IndexOf(start);
                uint end = sortedOffsets[sortIdx + 1];
                if (entry.Offset >= start && entry.Offset < end)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                throw new InvalidOperationException("Could not find the target entry in the WAG archive.");

            for (int i = 0; i < count; i++)
            {
                uint start = offsets[i];
                int sortIdx = sortedOffsets.IndexOf(start);
                uint end = sortedOffsets[sortIdx + 1];
                uint entryLen = end - start;

                byte[] encData = reader.ReadBytes(start, (int)entryLen);
                byte[] decData = (byte[])encData.Clone();
                Decrypt(start, dataKey, decData);

                if (i == targetIndex && version == 0x300 && decData.Length >= 10)
                {
                    decData = RebuildDsetWithReplacement(decData, newData);
                }

                byte[] newEncData = (byte[])decData.Clone();
                Decrypt(currentOffset, dataKey, newEncData);
                if (version == 0x300)
                {
                    bool is4ag = archive.FilePath.EndsWith(".4ag", StringComparison.OrdinalIgnoreCase);
                    if (is4ag)
                        FixDsetCrcs4ag(newEncData, decData);
                    else
                        FixDsetCrcs004(newEncData, decData);
                }
                newEntryDataList[i] = newEncData;

                currentOffset += (uint)decData.Length;
            }

            uint[] newOffsets = new uint[count];
            currentOffset = firstDataOffset;
            for (int i = 0; i < count; i++)
            {
                newOffsets[i] = currentOffset;
                currentOffset += (uint)newEntryDataList[i].Length;
            }

            byte[] rawNewIndex = new byte[count * 4];
            for (int i = 0; i < count; i++)
                Array.Copy(BitConverter.GetBytes(newOffsets[i]), 0, rawNewIndex, i * 4, 4);

            byte[] encNewIndex = (byte[])rawNewIndex.Clone();
            Decrypt(indexOffset, indexKey, encNewIndex);

            byte[] headerStart = reader.ReadBytes(0, 6);
            byte[] headerDesc = reader.ReadBytes(6, 0x40);
            byte[] headerCount = reader.ReadBytes(0x46, 4);

            uint pad0Start = 0x4A;
            byte[] headerPad0 = reader.ReadBytes(pad0Start, (int)len0);

            uint pad1Start = indexOffset + (uint)(count * 4);
            byte[] headerPad1 = reader.ReadBytes(pad1Start, (int)len1);

            using var headerMs = new MemoryStream();
            headerMs.Write(headerStart, 0, headerStart.Length);
            headerMs.Write(headerDesc, 0, headerDesc.Length);
            headerMs.Write(headerCount, 0, headerCount.Length);
            headerMs.Write(headerPad0, 0, headerPad0.Length);
            headerMs.Write(encNewIndex, 0, encNewIndex.Length);
            headerMs.Write(headerPad1, 0, headerPad1.Length);
            byte[] headerCrcRegion = headerMs.ToArray();

            byte[] headerCrc = XuseCrc16.ComputeCcittStoredCrcBytes(headerCrcRegion, 0, headerCrcRegion.Length);

            outputStream.Write(headerCrcRegion, 0, headerCrcRegion.Length);
            outputStream.Write(headerCrc, 0, 2);

            for (int i = 0; i < count; i++)
                outputStream.Write(newEntryDataList[i], 0, newEntryDataList[i].Length);
        }

        private static byte[] RebuildDsetWithReplacement(byte[] decData, byte[] newData)
        {
            if (decData.Length < 10) return decData;

            int chunkCount = BitConverter.ToInt32(decData, 4);

            using var ms = new MemoryStream();
            ms.Write(decData, 0, 10);

            int offset = 10;
            for (int c = 0; c < chunkCount; c++)
            {
                if (offset + 10 > decData.Length) break;

                byte[] magic = new byte[4];
                Array.Copy(decData, offset, magic, 0, 4);
                int size = BitConverter.ToInt32(decData, offset + 4);

                bool isPICT = magic[0] == 'P' && magic[1] == 'I' && magic[2] == 'C' && magic[3] == 'T';

                if (isPICT && size >= 6)
                {
                    byte[] pictInner = new byte[6];
                    if (offset + 10 + 6 <= decData.Length)
                        Array.Copy(decData, offset + 10, pictInner, 0, 6);

                    int newSize = 6 + newData.Length;

                    byte[] chunkHeader = new byte[8];
                    Array.Copy(magic, 0, chunkHeader, 0, 4);
                    Array.Copy(BitConverter.GetBytes(newSize), 0, chunkHeader, 4, 4);
                    byte[] chunkCrc = XuseCrc16.ComputeCcittStoredCrcBytes(chunkHeader, 0, 8);

                    ms.Write(chunkHeader, 0, 8);
                    ms.Write(chunkCrc, 0, 2);

                    ms.Write(pictInner, 0, 6);
                    ms.Write(newData, 0, newData.Length);
                }
                else
                {
                    int chunkTotal = 10 + size;
                    if (offset + chunkTotal <= decData.Length)
                    {
                        ms.Write(decData, offset, chunkTotal);
                    }
                    else
                    {
                        ms.Write(decData, offset, decData.Length - offset);
                    }
                }

                offset += 10 + size;
            }

            return ms.ToArray();
        }

        private static void FixDsetCrcs4ag(byte[] encData, byte[] decData)
        {
            if (decData.Length < 10) return;

            int chunkCount = BitConverter.ToInt32(decData, 4);

            byte[] dsetCrc = XuseCrc16.ComputeCcittStoredCrcBytes(encData, 0, 8);
            encData[8] = dsetCrc[0];
            encData[9] = dsetCrc[1];

            int offset = 10;
            for (int c = 0; c < chunkCount; c++)
            {
                if (offset + 10 > decData.Length) break;

                byte[] magic = new byte[4];
                Array.Copy(decData, offset, magic, 0, 4);
                int size = BitConverter.ToInt32(decData, offset + 4);

                byte[] chunkCrc = XuseCrc16.ComputeCcittStoredCrcBytes(encData, offset, 8);
                encData[offset + 8] = chunkCrc[0];
                encData[offset + 9] = chunkCrc[1];

                bool isPICT = magic[0] == 'P' && magic[1] == 'I' && magic[2] == 'C' && magic[3] == 'T';
                if (isPICT && size >= 6 && offset + 16 <= encData.Length)
                {
                    byte[] pictCrc = XuseCrc16.ComputeCcittStoredCrcBytes(encData, offset + 10, 4);
                    encData[offset + 14] = pictCrc[0];
                    encData[offset + 15] = pictCrc[1];
                }

                offset += 10 + size;
            }
        }

        private static void FixDsetCrcs004(byte[] encData, byte[] decData)
        {
            if (decData.Length < 10) return;

            int chunkCount = BitConverter.ToInt32(decData, 4);

            byte[] dsetCrc = XuseCrc16.ComputeCcittStoredCrcBytes(encData, 0, 8);
            encData[8] = dsetCrc[0];
            encData[9] = dsetCrc[1];

            int offset = 10;
            for (int c = 0; c < chunkCount; c++)
            {
                if (offset + 10 > decData.Length) break;

                int size = BitConverter.ToInt32(decData, offset + 4);

                byte[] chunkCrc = XuseCrc16.ComputeCcittStoredCrcBytes(encData, offset, 8);
                encData[offset + 8] = chunkCrc[0];
                encData[offset + 9] = chunkCrc[1];

                offset += 10 + size;
            }
        }

        private static uint CalcLen1(byte[] nameKey)
        {
            uint len1 = 512;
            for (int i = 0; i < nameKey.Length; i++)
                len1 = (uint)((int)len1 - nameKey[i]);
            for (int i = 0; i < nameKey.Length; i++)
            {
                int shift = nameKey[i] % 32;
                len1 = RotR(len1, shift);
            }
            for (int i = 0; i < nameKey.Length; i++)
            {
                len1 ^= (~(uint)nameKey[i]) & 0xFFFFFFFF;
                len1 = RotR(len1, 1);
            }
            len1 %= 0x401;
            return len1;
        }

        private static byte[] GenerateKey(byte[] keyword) => GenerateKey(keyword, keyword.Length);

        private static byte[] GenerateKey(byte[] keyword, int length)
        {
            int hash = 0;
            for (int i = 0; i < length; i++)
                hash = (((sbyte)keyword[i] + i) ^ hash) + length;

            int keyLength = (hash & 0xFF) + 0x40;

            for (int i = 0; i < length; i++)
                hash += (sbyte)keyword[i];

            byte[] key = new byte[keyLength--];
            key[1] = (byte)(hash >> 8);
            hash &= 0xF;
            key[0] = (byte)hash;
            key[2] = 0x46;
            key[3] = 0x88;

            for (int i = 4; i < keyLength; i++)
            {
                hash += (((sbyte)keyword[i % length] ^ hash) + i) & 0xFF;
                key[i] = (byte)hash;
            }
            return key;
        }

        private static void Decrypt(uint offset, byte[] key, byte[] data)
        {
            Decrypt(offset, key, data, 0, data.Length);
        }

        private static void Decrypt(uint offset, byte[] key, byte[] data, int pos, int length)
        {
            uint keyLast = (uint)key.Length - 1;
            if (keyLast == 0) return;
            for (uint i = 0; i < length; i++)
                data[pos + i] ^= key[(offset + i) % keyLast];
        }

        private static void DecryptChunk(uint offset, byte[] key, byte[] data, int length)
        {
            Decrypt(offset, key, data, 0, length);
        }

        private static uint RotR(uint val, int count)
        {
            return (val >> count) | (val << (32 - count));
        }

        private static string DetectTypeFromName(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".bmp" => "image"
            };
        }
    }
}
