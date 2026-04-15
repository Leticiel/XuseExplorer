using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class BinArchiveReader : IArchiveReader
    {
        public string Tag => "BIN/Xuse";
        public string Description => "Xuse audio archive";
        public string[] Extensions => new[] { ".bin", "" };

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

            if (stream.Length < 0x14) return null;

            uint signature = reader.ReadUInt32(0);
            if (signature != 1) return null;

            uint firstOffset = reader.ReadUInt32(8);
            if (firstOffset <= 0x14 || firstOffset >= stream.Length || firstOffset > int.MaxValue)
                return null;

            int indexSize = (int)(firstOffset - 4);
            int count = indexSize / 0x10;
            if (count * 0x10 != indexSize) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            uint indexOffset = 4;
            uint lastOffset = 0;
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                uint offset = reader.ReadUInt32(indexOffset + 4);
                if (offset == 0) break;
                if (offset <= lastOffset) return null;

                uint size = reader.ReadUInt32(indexOffset);

                byte[] sigBytes = reader.ReadBytes(offset, Math.Min(4, (int)(stream.Length - offset)));
                var (ext, type) = SignatureDetector.Detect(sigBytes);

                string name = $"{baseName}#{i:D4}.{ext}";

                var entry = new ArchiveEntry
                {
                    Name = name,
                    Path = name,
                    Type = type,
                    Offset = offset,
                    Size = size,
                    ArchivePath = filePath,
                    FormatTag = Tag
                };

                if (entry.Offset + entry.Size > stream.Length)
                    return null;

                entries.Add(entry);
                lastOffset = offset;
                indexOffset += 0x10;
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
