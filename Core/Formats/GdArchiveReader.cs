using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class GdArchiveReader : IArchiveReader
    {
        public string Tag => "GD/Xuse";
        public string Description => "Xuse resource archive (GD+DLL)";
        public string[] Extensions => new[] { ".gd" };

        public ArchiveFile? TryOpen(string filePath)
        {
            if (!filePath.EndsWith(".gd", StringComparison.OrdinalIgnoreCase))
                return null;

            string indexName = Path.ChangeExtension(filePath, ".dll");
            if (!File.Exists(indexName)) return null;

            using var dataStream = File.OpenRead(filePath);
            using var indexStream = File.OpenRead(indexName);
            return TryOpenInternal(dataStream, indexStream, filePath);
        }

        public ArchiveFile? TryOpenFromStream(Stream stream, string virtualName)
        {
            return null;
        }

        private ArchiveFile? TryOpenInternal(Stream dataStream, Stream indexStream, string filePath)
        {
            if (indexStream.Length < 12) return null;

            var dataReader = new BinaryReaderEx(dataStream);
            var idxReader = new BinaryReaderEx(indexStream);

            int count = dataReader.ReadInt32(0);
            if (count <= 0 || count > 100000) return null;

            if ((count & 0xFFFF) == 0x5A4D) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var entries = new List<ArchiveEntry>(count);
            uint indexOffset = 4;
            int i = 0;
            uint lastOffset = 3;

            while (indexOffset + 8 <= indexStream.Length)
            {
                uint offset = idxReader.ReadUInt32(indexOffset);
                if (offset <= lastOffset) return null;

                uint size = idxReader.ReadUInt32(indexOffset + 4);

                byte[] sigBytes = dataReader.ReadBytes(offset, Math.Min(4, (int)(dataStream.Length - offset)));
                var (ext, type) = SignatureDetector.Detect(sigBytes);

                string name = $"{baseName}#{i:D5}.{ext}";

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

                if (entry.Offset + entry.Size > dataStream.Length) return null;

                entries.Add(entry);
                lastOffset = offset;
                indexOffset += 8;
                i++;
            }

            return new ArchiveFile
            {
                FilePath = filePath,
                FormatTag = Tag,
                FormatDescription = Description,
                FileSize = dataStream.Length,
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
