using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class XarcArchiveReader : IArchiveReader
    {
        public string Tag => "XARC/Xuse";
        public string Description => "Xuse resource archive (XARC)";
        public string[] Extensions => new[] { ".arc" };

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

            if (stream.Length < 8) return null;

            uint signature = reader.ReadUInt32(0);
            if (signature != 0x43524158) return null;

            int count = reader.ReadInt32(4);
            if (count <= 0 || count > 100000) return null;

            uint indexOffset = 8;
            uint firstOffset = reader.ReadUInt32(indexOffset);
            if ((uint)count * 4 + 10 != firstOffset) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);

            var offsets = new uint[count];
            for (int i = 0; i < count; i++)
            {
                offsets[i] = reader.ReadUInt32(indexOffset + (uint)i * 4);
            }

            var entries = new List<ArchiveEntry>(count);
            for (int i = 0; i < count; i++)
            {
                uint entryOffset = offsets[i];
                if (!reader.AsciiEqual(entryOffset, "DATA"))
                    return null;

                ushort nameLength = reader.ReadUInt16(entryOffset + 0x18);
                uint size = reader.ReadUInt32(entryOffset + 0x1C);
                var nameBytes = reader.ReadBytes(entryOffset + 0x20, nameLength);
                string name = DecryptName(nameBytes);

                uint dataOffset = entryOffset + 0x22 + nameLength;

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

        private static string DecryptName(byte[] name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                name[i] = RotByteL(name[i], 4);
            }
            return Encoding.GetEncoding(932).GetString(name);
        }

        private static byte RotByteL(byte val, int count)
        {
            return (byte)((val << count) | (val >> (8 - count)));
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
            long originalFileLength = originalStream.Length;

            int count = reader.ReadInt32(4);

            var originalOffsets = new uint[count];
            for (int i = 0; i < count; i++)
                originalOffsets[i] = reader.ReadUInt32(8 + (uint)i * 4);

            int targetIndex = -1;
            for (int i = 0; i < count; i++)
            {
                uint blockOffset = originalOffsets[i];
                ushort nameLen = reader.ReadUInt16(blockOffset + 0x18);
                uint dataOffset = blockOffset + 0x22u + nameLen;
                if (dataOffset == (uint)entry.Offset)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                throw new InvalidOperationException("Could not find the target entry in the XARC archive.");

            var nameLens = new ushort[count];
            var origDataSizes = new uint[count];
            for (int i = 0; i < count; i++)
            {
                uint blockOffset = originalOffsets[i];
                nameLens[i] = reader.ReadUInt16(blockOffset + 0x18);
                origDataSizes[i] = reader.ReadUInt32(blockOffset + 0x1C);
            }

            var newOffsets = new uint[count];
            uint currentPosition = originalOffsets[0];

            for (int i = 0; i < count; i++)
            {
                newOffsets[i] = currentPosition;

                uint headerSize = 0x22u + nameLens[i];
                uint dataSize = (i == targetIndex) ? (uint)newData.Length : origDataSizes[i];

                currentPosition += headerSize + dataSize + 2;
            }

            byte[] header = reader.ReadBytes(0, 8);
            outputStream.Write(header, 0, header.Length);

            for (int i = 0; i < count; i++)
            {
                byte[] offsetBytes = BitConverter.GetBytes(newOffsets[i]);
                outputStream.Write(offsetBytes, 0, 4);
            }

            int offsetTableEnd = 8 + count * 4;
            int gapToFirstBlock = (int)(originalOffsets[0] - offsetTableEnd);
            if (gapToFirstBlock > 0)
            {
                using var headerMs = new MemoryStream();
                headerMs.Write(header, 0, header.Length);
                for (int i = 0; i < count; i++)
                {
                    byte[] ob = BitConverter.GetBytes(newOffsets[i]);
                    headerMs.Write(ob, 0, 4);
                }
                byte[] headerRegion = headerMs.ToArray();
                byte[] gapCrc = XuseCrc16.ComputeStoredCrcBytes(headerRegion);
                outputStream.Write(gapCrc, 0, gapCrc.Length);

                if (gapToFirstBlock > 2)
                {
                    byte[] extraGap = reader.ReadBytes(offsetTableEnd + 2, gapToFirstBlock - 2);
                    outputStream.Write(extraGap, 0, extraGap.Length);
                }
            }

            for (int i = 0; i < count; i++)
            {
                uint blockOffset = originalOffsets[i];
                uint headerSize = 0x22u + nameLens[i];

                if (i == targetIndex)
                {
                    byte[] blockHeader = reader.ReadBytes(blockOffset, (int)headerSize);
                    byte[] sizeBytes = BitConverter.GetBytes((uint)newData.Length);
                    blockHeader[0x1C] = sizeBytes[0];
                    blockHeader[0x1D] = sizeBytes[1];
                    blockHeader[0x1E] = sizeBytes[2];
                    blockHeader[0x1F] = sizeBytes[3];

                    byte[] headerCrc = XuseCrc16.ComputeStoredCrcBytes(blockHeader, 0, (int)headerSize - 2);
                    blockHeader[headerSize - 2] = headerCrc[0];
                    blockHeader[headerSize - 1] = headerCrc[1];

                    outputStream.Write(blockHeader, 0, blockHeader.Length);
                    outputStream.Write(newData, 0, newData.Length);

                    byte[] crcBytes = XuseCrc16.ComputeStoredCrcBytes(newData);
                    outputStream.Write(crcBytes, 0, 2);
                }
                else
                {
                    uint totalBlockSize = headerSize + origDataSizes[i];
                    byte[] block = reader.ReadBytes(blockOffset, (int)totalBlockSize);
                    outputStream.Write(block, 0, block.Length);

                    long crcOffset = blockOffset + headerSize + origDataSizes[i];
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
