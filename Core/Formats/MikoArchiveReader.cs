using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class MikoArchiveReader : IArchiveReader
    {
        public string Tag => "XArc v2";
        public string Description => "Xuse/Eternal resource archive (MIKO/XARC v2)";
        public string[] Extensions => new[] { ".arc", ".xarc" };

        public bool CanImport => true;

        public ArchiveFile? TryOpen(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return TryOpenInternal(stream, filePath);
        }

        public ArchiveFile? TryOpenFromStream(Stream stream, string virtualName)
        {
            return TryOpenInternal(stream, virtualName);
        }

        private ArchiveFile? TryOpenInternal(Stream stream, string filePath)
        {
            var reader = new BinaryReaderEx(stream);

            if (stream.Length < 0x30) return null;

            uint signature = reader.ReadUInt32(0);
            if (signature != 0x4F4B494D && signature != 0x43524158) return null;

            short versionTag = reader.ReadInt16(0xA);
            if (versionTag != 0x1001) return null;

            int count = reader.ReadInt32(0x10);
            if (count <= 0 || count > 100000) return null;

            int mode = reader.ReadInt32(0xC);
            long cadrOffset;
            if ((mode & 0xF) == 0)
            {
                if (!reader.AsciiEqual(0x16, "DFNM"))
                    return null;
                cadrOffset = reader.ReadInt64(0x1A);
            }
            else
            {
                return null;
            }

            int ndixOffset = 0x24;
            if (!reader.AsciiEqual(ndixOffset, "NDIX"))
                return null;

            int indexLength = 8 * count;
            int filenamesOffset = ndixOffset + 8 + 2 * indexLength;
            if (!reader.AsciiEqual(filenamesOffset, "CTIF"))
                return null;

            if (!reader.AsciiEqual(cadrOffset, "CADR"))
                return null;

            var entries = new List<ArchiveEntry>(count);
            var nameBuf = new byte[0x40];
            int currentNdixOffset = ndixOffset + 6;
            long currentCadrOffset = cadrOffset + 6;

            for (int i = 0; i < count; i++)
            {
                uint entryOffset = reader.ReadUInt32(currentNdixOffset);
                short entryTag = reader.ReadInt16(entryOffset);
                if (entryTag != 0x1001) return null;

                ushort nameLength = reader.ReadUInt16(entryOffset + 6);
                if (nameLength > nameBuf.Length)
                    nameBuf = new byte[nameLength];

                reader.Read(entryOffset + 0xA, nameBuf, 0, nameLength);
                for (int n = 0; n < nameLength; n++)
                    nameBuf[n] ^= 0x56;

                string name = Encoding.GetEncoding(932).GetString(nameBuf, 0, nameLength);

                long dataOffset = reader.ReadInt64(currentCadrOffset);
                if (dataOffset >= stream.Length) return null;

                if (!reader.AsciiEqual(dataOffset, "DATA"))
                    return null;
                uint size = reader.ReadUInt32(dataOffset + 0x18);
                dataOffset += 0x1E;

                byte[] sigBytes = reader.ReadBytes(dataOffset, Math.Min(4, (int)(stream.Length - dataOffset)));
                var (ext, type) = SignatureDetector.Detect(sigBytes);
                if (string.IsNullOrEmpty(Path.GetExtension(name)))
                    name = $"{name}.{ext}";

                var entry = new ArchiveEntry
                {
                    Name = name,
                    Path = name,
                    Type = type,
                    Offset = dataOffset,
                    Size = size,
                    ArchivePath = filePath,
                    FormatTag = Tag
                };
                entries.Add(entry);

                currentNdixOffset += 8;
                currentCadrOffset += 12;
            }

            return new ArchiveFile
            {
                FilePath = filePath,
                FormatTag = Tag,
                FormatDescription = Description,
                FileSize = stream.Length,
                Entries = entries
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
                return reader.ReadBytes(entry.Offset, (int)entry.Size);
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }

        public void ImportEntry(ArchiveFile archive, ArchiveEntry entry, byte[] newData, Stream originalStream, Stream outputStream)
        {
            var reader = new BinaryReaderEx(originalStream);

            int count = reader.ReadInt32(0x10);
            long cadrOffset = reader.ReadInt64(0x1A);
            long cadrDataStart = cadrOffset + 6;

            var dataBlockOffsets = new long[count];
            for (int i = 0; i < count; i++)
            {
                long recordOffset = cadrDataStart + (long)i * 12;
                dataBlockOffsets[i] = reader.ReadInt64(recordOffset);
            }

            long targetDataBlockOffset = entry.Offset - 0x1E;
            int targetIndex = -1;
            for (int i = 0; i < count; i++)
            {
                if (dataBlockOffsets[i] == targetDataBlockOffset)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                throw new InvalidOperationException("Could not find the target entry in the MIKO archive.");

            uint originalEntrySize = reader.ReadUInt32(targetDataBlockOffset + 0x18);
            long sizeDiff = newData.Length - (long)originalEntrySize;

            long firstDataBlock = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (dataBlockOffsets[i] < firstDataBlock)
                    firstDataBlock = dataBlockOffsets[i];
            }

            byte[] headerBlob = reader.ReadBytes(0, (int)firstDataBlock);

            ushort cadrCrcTarget = 0;
            {
                int refIdx = (targetIndex == 0) ? 1 : 0;
                long refPos = cadrDataStart + (long)refIdx * 12;
                if (refPos + 10 <= headerBlob.Length)
                {
                    cadrCrcTarget = XuseCrc16.Compute(headerBlob, (int)refPos, 10);
                }
            }

            for (int i = 0; i < count; i++)
            {
                long patchPos = cadrDataStart + (long)i * 12;

                if (i == targetIndex)
                {
                }
                else if (i > targetIndex)
                {
                    if (patchPos + 8 <= headerBlob.Length)
                    {
                        long newOffset = dataBlockOffsets[i] + sizeDiff;
                        byte[] offsetBytes = BitConverter.GetBytes(newOffset);
                        Array.Copy(offsetBytes, 0, headerBlob, (int)patchPos, 8);

                        if (patchPos + 10 <= headerBlob.Length)
                        {
                            byte[] crcBytes = XuseCrc16.ComputeStoredCrcBytesForTarget(
                                headerBlob, (int)patchPos, 8, cadrCrcTarget);
                            headerBlob[(int)patchPos + 8] = crcBytes[0];
                            headerBlob[(int)patchPos + 9] = crcBytes[1];
                        }
                    }
                }
            }

            outputStream.Write(headerBlob, 0, headerBlob.Length);

            var sortedIndices = new int[count];
            for (int i = 0; i < count; i++) sortedIndices[i] = i;
            Array.Sort(sortedIndices, (a, b) => dataBlockOffsets[a].CompareTo(dataBlockOffsets[b]));

            long originalFileLength = originalStream.Length;

            for (int si = 0; si < count; si++)
            {
                int i = sortedIndices[si];
                long blockOffset = dataBlockOffsets[i];

                byte[] dataHeader = reader.ReadBytes(blockOffset, 0x1E);
                uint origSize = (uint)(dataHeader[0x18] | (dataHeader[0x19] << 8) |
                                       (dataHeader[0x1A] << 16) | (dataHeader[0x1B] << 24));

                if (i == targetIndex)
                {
                    byte[] sizeBytes = BitConverter.GetBytes((uint)newData.Length);
                    dataHeader[0x18] = sizeBytes[0];
                    dataHeader[0x19] = sizeBytes[1];
                    dataHeader[0x1A] = sizeBytes[2];
                    dataHeader[0x1B] = sizeBytes[3];

                    byte[] headerCrc = XuseCrc16.ComputeStoredCrcBytes(dataHeader, 0, 0x1C);
                    dataHeader[0x1C] = headerCrc[0];
                    dataHeader[0x1D] = headerCrc[1];

                    outputStream.Write(dataHeader, 0, dataHeader.Length);
                    outputStream.Write(newData, 0, newData.Length);

                    byte[] crcBytes = XuseCrc16.ComputeStoredCrcBytes(newData);
                    outputStream.Write(crcBytes, 0, 2);
                }
                else
                {
                    outputStream.Write(dataHeader, 0, dataHeader.Length);
                    byte[] origData = reader.ReadBytes(blockOffset + 0x1E, (int)origSize);
                    outputStream.Write(origData, 0, origData.Length);

                    long crcOffset = blockOffset + 0x1E + origSize;
                    if (crcOffset + 2 <= originalFileLength)
                    {
                        byte[] crcBytes = reader.ReadBytes(crcOffset, 2);
                        outputStream.Write(crcBytes, 0, 2);
                    }
                }
            }
        }
    }
}
