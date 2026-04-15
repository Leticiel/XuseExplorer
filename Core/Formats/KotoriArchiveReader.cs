using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class KotoriArchiveReader : IArchiveReader
    {
        public string Tag => "KOTORI/Xuse";
        public string Description => "Xuse/Eternal resource archive (Kotori)";
        public string[] Extensions => new[] { ".bin" };

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

            if (stream.Length < 0x20) return null;

            if (!reader.AsciiEqual(0, "KOTORI")) return null;
            if (reader.ReadInt32(6) != 0x1A1A00) return null;
            if (reader.ReadInt32(0x10) != 0x0100A618) return null;

            int count = reader.ReadUInt16(0x14);
            if (count <= 0 || count > 100000) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            uint currentOffset = 0x18;
            long nextOffset = reader.ReadUInt32(currentOffset);

            var entries = new List<ArchiveEntry>(count);
            for (int i = 0; i < count; i++)
            {
                long entryOffset = nextOffset;

                if (i + 1 != count)
                {
                    currentOffset += 6;
                    nextOffset = reader.ReadUInt32(currentOffset);
                }
                else
                {
                    nextOffset = stream.Length;
                }

                uint size = (uint)(nextOffset - entryOffset);
                bool isPacked = size >= 0x32;
                uint unpackedSize = isPacked ? size - 0x32 : size;

                var entry = new ArchiveEntry
                {
                    Name = $"{baseName}#{i:D4}.ogg",
                    Path = $"{baseName}#{i:D4}.ogg",
                    Type = "audio",
                    Offset = entryOffset,
                    Size = size,
                    IsPacked = isPacked,
                    UnpackedSize = unpackedSize,
                    ArchivePath = filePath,
                    FormatTag = Tag
                };

                if (entry.Offset + entry.Size > stream.Length) return null;
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
                var raw = reader.ReadBytes(entry.Offset, (int)entry.Size);

                if (raw.Length >= 0x32 &&
                    raw[0] == 'K' && raw[1] == 'O' && raw[2] == 'T' && raw[3] == 'O' && raw[4] == 'R' && raw[5] == 'i' &&
                    (raw[6] | (raw[7] << 8) | (raw[8] << 16) | (raw[9] << 24)) == 0x001A1A00 &&
                    (raw[0x10] | (raw[0x11] << 8) | (raw[0x12] << 16) | (raw[0x13] << 24)) == 0x0100A618)
                {
                    var key = new byte[0x10];
                    Array.Copy(raw, 0x20, key, 0, 0x10);

                    uint length = (uint)(raw.Length - 0x32);
                    var data = new byte[length];
                    Array.Copy(raw, 0x32, data, 0, length);

                    for (uint i = 0; i < length; i++)
                        data[i] ^= key[i & 0xF];

                    return data;
                }

                return raw;
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }
    }
}
