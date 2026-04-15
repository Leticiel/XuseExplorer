using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class BgArchiveReader : IArchiveReader
    {
        public string Tag => "BG/Xuse";
        public string Description => "Xuse bitmap archive (BG)";
        public string[] Extensions => new[] { "" };

        public ArchiveFile? TryOpen(string filePath)
        {
            string arcName = Path.GetFileName(filePath);
            if (!arcName.StartsWith("bg00", StringComparison.OrdinalIgnoreCase) &&
                !arcName.StartsWith("sbg", StringComparison.OrdinalIgnoreCase))
                return null;

            var fi = new FileInfo(filePath);
            return TryOpenInternal(fi.Length, filePath, arcName);
        }

        public ArchiveFile? TryOpenFromStream(Stream stream, string virtualName)
        {
            string arcName = Path.GetFileName(virtualName);
            if (!arcName.StartsWith("bg00", StringComparison.OrdinalIgnoreCase) &&
                !arcName.StartsWith("sbg", StringComparison.OrdinalIgnoreCase))
                return null;

            return TryOpenInternal(stream.Length, virtualName, arcName);
        }

        private ArchiveFile? TryOpenInternal(long fileLength, string filePath, string arcName)
        {
            uint entrySize = arcName[0] == 's' || arcName[0] == 'S' ? 0x96400u : 0x4B400u;
            long rem = fileLength % entrySize;
            int count = (int)(fileLength / entrySize);

            if (rem != 0 || count <= 0 || count > 100000)
                return null;

            var entries = new List<ArchiveEntry>(count);
            uint offset = 0;
            for (int i = 0; i < count; i++)
            {
                entries.Add(new ArchiveEntry
                {
                    Name = $"{arcName}#{i:D4}.bmp",
                    Path = $"{arcName}#{i:D4}.bmp",
                    Type = "image",
                    Offset = offset,
                    Size = entrySize,
                    ArchivePath = filePath,
                    FormatTag = Tag
                });
                offset += entrySize;
            }

            return new ArchiveFile
            {
                FilePath = filePath,
                FormatTag = Tag,
                FormatDescription = Description,
                FileSize = fileLength,
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

                uint height = entry.Size == 0x4B400u ? 480u : 960u;
                uint width = 640;
                return ConvertToBmp(raw, (int)width, (int)height);
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }

        private static byte[] ConvertToBmp(byte[] raw, int width, int height)
        {
            int pixelDataSize = width * height;
            int paletteStart = pixelDataSize;

            int bmpHeaderSize = 14 + 40 + 1024;
            int totalSize = bmpHeaderSize + pixelDataSize;
            var bmp = new byte[totalSize];

            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            WriteInt32(bmp, 2, totalSize);
            WriteInt32(bmp, 10, bmpHeaderSize);

            WriteInt32(bmp, 14, 40);
            WriteInt32(bmp, 18, width);
            WriteInt32(bmp, 22, height);
            WriteInt16(bmp, 26, 1);
            WriteInt16(bmp, 28, 8);
            WriteInt32(bmp, 34, pixelDataSize);

            if (paletteStart + 1024 <= raw.Length)
            {
                Array.Copy(raw, paletteStart, bmp, 54, 1024);
            }

            int srcOffset = 0;
            int dstBase = bmpHeaderSize;
            for (int y = height - 1; y >= 0; y--)
            {
                Array.Copy(raw, srcOffset, bmp, dstBase + y * width, width);
                srcOffset += width;
            }

            return bmp;
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt16(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
        }
    }
}
