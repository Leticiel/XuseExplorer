using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class AifArchiveReader : IArchiveReader
    {
        public string Tag => "AIF/Xuse";
        public string Description => "Xuse image archive (AIF)";
        public string[] Extensions => new[] { ".aif" };

        private const uint ENTRY_OPCODE = 0x10;
        private const int ENTRY_SIZE = 32;

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

            uint firstOpcode = reader.ReadUInt32(0);
            if (firstOpcode != ENTRY_OPCODE) return null;

            uint firstOffset = reader.ReadUInt32(4);
            if (firstOffset < ENTRY_SIZE || firstOffset >= stream.Length) return null;
            if (firstOffset % ENTRY_SIZE != 0) return null;

            int count = (int)(firstOffset / ENTRY_SIZE);
            if (count <= 0 || count > 100000) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int pos = i * ENTRY_SIZE;
                if (pos + ENTRY_SIZE > stream.Length) break;

                uint opcode = reader.ReadUInt32(pos);
                if (opcode != ENTRY_OPCODE) break;

                uint offset = reader.ReadUInt32(pos + 4);
                uint size = reader.ReadUInt32(pos + 8);

                if (offset == 0 || size == 0) continue;
                if (offset + size > stream.Length) continue;

                string ext = "png";
                string type = "image";
                if (offset + 4 <= stream.Length)
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
