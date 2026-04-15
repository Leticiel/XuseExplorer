using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class DatArchiveReader : IArchiveReader
    {
        public string Tag => "DAT/Xuse";
        public string Description => "Xuse audio archive (DAT)";
        public string[] Extensions => new[] { ".dat" };

        private const int ENTRY_SIZE = 16;
        private const int ALIGNMENT = 2048;

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
            if (stream.Length < ENTRY_SIZE) return null;

            var reader = new BinaryReaderEx(stream);

            uint firstDword = reader.ReadUInt32(0);
            if (firstDword == 0x40474157 || firstDword == 0x34464147 || firstDword == 0x43524158 ||
                firstDword == 0x4F4B494D || firstDword == 0x4F544F4B)
                return null;

            var rawEntries = new List<(uint unk1, uint size, uint offset, uint unk2)>();
            int maxEntries = (int)Math.Min(stream.Length / ENTRY_SIZE, 100000);

            for (int i = 0; i < maxEntries; i++)
            {
                int pos = i * ENTRY_SIZE;
                if (pos + ENTRY_SIZE > stream.Length) break;

                uint unk1 = reader.ReadUInt32(pos);
                uint size = reader.ReadUInt32(pos + 4);
                uint offset = reader.ReadUInt32(pos + 8);
                uint unk2 = reader.ReadUInt32(pos + 12);

                if (size == 0)
                    break;

                rawEntries.Add((unk1, size, offset, unk2));
            }

            if (rawEntries.Count == 0) return null;

            int indexEnd = rawEntries.Count * ENTRY_SIZE;
            int dataBlockStart = ((indexEnd + ALIGNMENT - 1) / ALIGNMENT) * ALIGNMENT;

            uint expectedFirstOffset = (uint)(dataBlockStart + 4);
            if (rawEntries[0].offset != expectedFirstOffset)
            {
                int align4096 = ((indexEnd + 4095) / 4096) * 4096;
                if (rawEntries[0].offset == (uint)(align4096 + 4))
                {
                    dataBlockStart = align4096;
                    expectedFirstOffset = (uint)(dataBlockStart + 4);
                }
                else
                {
                    return null;
                }
            }

            uint firstOffset = rawEntries[0].offset;
            uint firstSize = rawEntries[0].size;
            if (firstOffset + firstSize > (uint)stream.Length) return null;

            if (firstOffset + 4 <= (uint)stream.Length)
            {
                byte[] sigBytes = reader.ReadBytes(firstOffset, 4);
                uint sig = (uint)(sigBytes[0] | (sigBytes[1] << 8) | (sigBytes[2] << 16) | (sigBytes[3] << 24));
                if (sig == 0x40474157 || sig == 0x34464147 || sig == 0x43524158 ||
                    sig == 0x4F4B494D || sig == 0x4F544F4B)
                    return null;
            }

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var entries = new List<ArchiveEntry>(rawEntries.Count);

            for (int i = 0; i < rawEntries.Count; i++)
            {
                var (unk1, size, offset, unk2) = rawEntries[i];

                if (offset + size > (uint)stream.Length) continue;

                string ext = "ogg";
                string type = "audio";
                if (offset + 4 <= (uint)stream.Length)
                {
                    byte[] sigBytes = reader.ReadBytes(offset, Math.Min(4, (int)(stream.Length - offset)));
                    var detected = SignatureDetector.Detect(sigBytes);
                    if (detected.type != "unknown")
                    {
                        ext = detected.extension;
                        type = detected.type;
                    }
                }

                entries.Add(new ArchiveEntry
                {
                    Name = $"{baseName}#{i:D4}.{ext}",
                    Path = $"{baseName}#{i:D4}.{ext}",
                    Type = type,
                    Offset = offset,
                    Size = size,
                    ArchivePath = filePath,
                    FormatTag = Tag
                });
            }

            if (entries.Count == 0) return null;

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
    }
}
