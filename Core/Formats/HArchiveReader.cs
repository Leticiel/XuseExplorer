using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class HArchiveReader : IArchiveReader
    {
        public string Tag => "H/Xuse";
        public string Description => "Xuse bitmap archive (H)";
        public string[] Extensions => new[] { "" };

        public ArchiveFile? TryOpen(string filePath)
        {
            string arcName = Path.GetFileName(filePath);
            if (!arcName.StartsWith("H", StringComparison.OrdinalIgnoreCase))
                return null;

            var fi = new FileInfo(filePath);
            return TryOpenInternal(fi.Length, filePath, arcName);
        }

        public ArchiveFile? TryOpenFromStream(Stream stream, string virtualName)
        {
            string arcName = Path.GetFileName(virtualName);
            if (!arcName.StartsWith("H", StringComparison.OrdinalIgnoreCase))
                return null;

            return TryOpenInternal(stream.Length, virtualName, arcName);
        }

        private ArchiveFile? TryOpenInternal(long fileLength, string filePath, string arcName)
        {
            uint entrySize = arcName.EndsWith("W", StringComparison.OrdinalIgnoreCase) ? 0x2A700u : 0x25480u;
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

                uint width = entry.Size == 0x25480u ? 280u : 320u;
                uint height = 480;
                return ConvertToBmp32(raw, (int)width, (int)height);
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }

        private static byte[] ConvertToBmp32(byte[] raw, int width, int height)
        {
            int stride8bpp = width;
            int alphaStride = (width / 8 + 1) & ~1;
            int pixelDataSize = stride8bpp * height;
            int alphaDataSize = alphaStride * height;

            int paletteOffset = raw.Length - 0x400;

            var palette = new byte[256 * 4];
            if (paletteOffset + 1024 <= raw.Length)
                Array.Copy(raw, paletteOffset, palette, 0, 1024);

            int stride32bpp = width * 4;
            int bmpPixelSize = stride32bpp * height;
            int bmpHeaderSize = 14 + 40;
            int totalSize = bmpHeaderSize + bmpPixelSize;
            var bmp = new byte[totalSize];

            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            WriteInt32(bmp, 2, totalSize);
            WriteInt32(bmp, 10, bmpHeaderSize);

            WriteInt32(bmp, 14, 40);
            WriteInt32(bmp, 18, width);
            WriteInt32(bmp, 22, height);
            WriteInt16(bmp, 26, 1);
            WriteInt16(bmp, 28, 32);
            WriteInt32(bmp, 34, bmpPixelSize);

            int src = pixelDataSize - stride8bpp;
            int asrc = alphaDataSize - alphaStride;
            int dstRow = bmpHeaderSize;

            for (int y = 0; y < height; y++)
            {
                int dst = dstRow;
                for (int x = 0; x < width; x++)
                {
                    if (src + x >= 0 && src + x < raw.Length)
                    {
                        int palIdx = raw[src + x] * 4;
                        bmp[dst] = palette[palIdx];
                        bmp[dst + 1] = palette[palIdx + 1];
                        bmp[dst + 2] = palette[palIdx + 2];

                        int alphaIdx = asrc + (x >> 3);
                        if (alphaIdx >= 0 && alphaIdx < raw.Length)
                        {
                            int a = (raw[alphaIdx] << (x & 7)) & 0x80;
                            bmp[dst + 3] = (byte)(a == 0 ? 0xFF : 0);
                        }
                        else
                        {
                            bmp[dst + 3] = 0xFF;
                        }
                    }
                    dst += 4;
                }
                src -= stride8bpp;
                asrc -= alphaStride;
                dstRow += stride32bpp;
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
