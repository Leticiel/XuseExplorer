using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class WvbArchiveReader : IArchiveReader
    {
        public string Tag => "WVB";
        public string Description => "Xuse audio resource archive";
        public string[] Extensions => new[] { ".wvb" };

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

            if (stream.Length < 16) return null;

            int firstOffset = reader.ReadInt32(4) - 1;
            if (firstOffset < 8 || firstOffset >= stream.Length) return null;

            int count = firstOffset / 8;
            if ((firstOffset & 7) != 0 || count <= 0 || count > 100000) return null;

            uint fmtSize = reader.ReadUInt32(firstOffset);
            long dataTagPos = firstOffset + fmtSize + 4;
            if (dataTagPos + 4 > stream.Length) return null;
            if (!reader.AsciiEqual(dataTagPos, "data")) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            uint indexPos = 0;
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                uint offset = reader.ReadUInt32(indexPos + 4);
                if (offset == 0) break;

                uint rawSize = reader.ReadUInt32(indexPos);

                var entry = new ArchiveEntry
                {
                    Name = $"{baseName}#{i:D2}.wav",
                    Path = $"{baseName}#{i:D2}.wav",
                    Type = "audio",
                    Offset = offset - 1,
                    Size = rawSize - 8 + 16,
                    ArchivePath = filePath,
                    FormatTag = Tag
                };

                entries.Add(entry);
                indexPos += 8;
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

                uint rawSize = entry.Size - 16;
                var rawData = reader.ReadBytes(entry.Offset, (int)rawSize);

                var header = new byte[16];
                header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
                uint totalSize = entry.Size - 8;
                header[4] = (byte)(totalSize);
                header[5] = (byte)(totalSize >> 8);
                header[6] = (byte)(totalSize >> 16);
                header[7] = (byte)(totalSize >> 24);
                header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
                header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';

                var result = new byte[16 + rawData.Length];
                Array.Copy(header, 0, result, 0, 16);
                Array.Copy(rawData, 0, result, 16, rawData.Length);
                return result;
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }
    }
}
