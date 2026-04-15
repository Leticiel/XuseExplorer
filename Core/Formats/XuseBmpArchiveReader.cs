using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class XuseBmpArchiveReader : IArchiveReader
    {
        public string Tag => "Xuse BMP";
        public string Description => "Xuse image archive (.002/.003)";
        public string[] Extensions => new[] { ".002", ".003" };

        public bool CanImport => true;

        private const int BG_WIDTH = 800;
        private const int BG_HEIGHT = 600;

        private const int BMP_HEADER_SIZE = 14;
        private const int DIB_HEADER_SIZE = 40;
        private const int TOTAL_BMP_HEADER = BMP_HEADER_SIZE + DIB_HEADER_SIZE;

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
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".002")
                return TryOpen002(stream, filePath);
            else if (ext == ".003")
                return TryOpen003(stream, filePath);
            else
                return null;
        }

        private ArchiveFile? TryOpen002(Stream stream, string filePath)
        {
            int stride = RowStride(BG_WIDTH);
            int imageSize = stride * BG_HEIGHT;

            if (stream.Length < imageSize) return null;

            int count = (int)(stream.Length / imageSize);
            if (count <= 0 || count > 10000) return null;

            long remainder = stream.Length - (long)count * imageSize;
            if (remainder > 0) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                long offset = (long)i * imageSize;
                entries.Add(new ArchiveEntry
                {
                    Name = $"{i:D3}.bmp",
                    Path = $"{i:D3}.bmp",
                    Type = "image",
                    Offset = offset,
                    Size = (uint)imageSize,
                    ArchivePath = filePath,
                    FormatTag = Tag
                });
            }

            return new ArchiveFile
            {
                FilePath = filePath,
                FormatTag = Tag,
                FormatDescription = $"{Description} [.002 {BG_WIDTH}×{BG_HEIGHT}]",
                FileSize = stream.Length,
                Entries = entries
            };
        }

        private ArchiveFile? TryOpen003(Stream stream, string filePath)
        {
            if (stream.Length < 16) return null;

            var reader = new BinaryReaderEx(stream);

            uint firstSize = reader.ReadUInt32(0);
            uint firstW = reader.ReadUInt32(4);
            uint firstH = reader.ReadUInt32(8);
            uint firstOff = reader.ReadUInt32(12);

            if (firstOff == 0 || firstOff % 16 != 0) return null;

            int count = (int)(firstOff / 16);
            if (count <= 0 || count > 10000) return null;
            if (firstOff > stream.Length) return null;

            if (firstW == 0 || firstW > 8192 || firstH == 0 || firstH > 8192) return null;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                long pos = (long)i * 16;
                if (pos + 16 > stream.Length) break;

                uint dataSize = reader.ReadUInt32(pos);
                uint w = reader.ReadUInt32(pos + 4);
                uint h = reader.ReadUInt32(pos + 8);
                uint dataOffset = reader.ReadUInt32(pos + 12);

                if (w == 0 || w > 8192 || h == 0 || h > 8192) break;
                if (dataOffset + dataSize > stream.Length) break;

                entries.Add(new ArchiveEntry
                {
                    Name = $"{i:D3}.bmp",
                    Path = $"{i:D3}.bmp",
                    Type = "image",
                    Offset = dataOffset,
                    Size = dataSize,
                    ArchivePath = filePath,
                    FormatTag = Tag
                });
            }

            if (entries.Count == 0) return null;

            return new ArchiveFile
            {
                FilePath = filePath,
                FormatTag = Tag,
                FormatDescription = $"{Description} [.003]",
                FileSize = stream.Length,
                Entries = entries
            };
        }

        public byte[] ExtractEntry(ArchiveFile archive, ArchiveEntry entry)
        {
            string ext = Path.GetExtension(archive.FilePath).ToLowerInvariant();

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
                byte[] bgrData = reader.ReadBytes(entry.Offset, (int)entry.Size);

                int width, height;
                if (ext == ".002")
                {
                    width = BG_WIDTH;
                    height = BG_HEIGHT;
                }
                else
                {
                    (width, height) = Read003EntryDimensions(reader, archive, entry);
                }

                return BuildBmp(width, height, bgrData);
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }

        private static (int width, int height) Read003EntryDimensions(BinaryReaderEx reader, ArchiveFile archive, ArchiveEntry entry)
        {
            uint firstOff = reader.ReadUInt32(12);
            int count = (int)(firstOff / 16);

            for (int i = 0; i < count; i++)
            {
                long pos = (long)i * 16;
                uint dataOffset = reader.ReadUInt32(pos + 12);
                if (dataOffset == (uint)entry.Offset)
                {
                    uint w = reader.ReadUInt32(pos + 4);
                    uint h = reader.ReadUInt32(pos + 8);
                    return ((int)w, (int)h);
                }
            }

            throw new InvalidOperationException(
                $"Could not find dimensions for entry at offset 0x{entry.Offset:X}");
        }

        private static byte[] BuildBmp(int width, int height, byte[] bgrData)
        {
            int stride = RowStride(width);
            int pixelSize = stride * height;
            int fileSize = TOTAL_BMP_HEADER + pixelSize;

            var bmp = new byte[fileSize];
            int pos = 0;

            bmp[pos++] = (byte)'B';
            bmp[pos++] = (byte)'M';
            WriteLE32(bmp, pos, (uint)fileSize); pos += 4;
            WriteLE16(bmp, pos, 0); pos += 2;
            WriteLE16(bmp, pos, 0); pos += 2;
            WriteLE32(bmp, pos, TOTAL_BMP_HEADER); pos += 4;

            WriteLE32(bmp, pos, DIB_HEADER_SIZE); pos += 4;
            WriteLE32(bmp, pos, (uint)width); pos += 4;
            WriteLE32(bmp, pos, (uint)height); pos += 4;
            WriteLE16(bmp, pos, 1); pos += 2;
            WriteLE16(bmp, pos, 24); pos += 2;
            WriteLE32(bmp, pos, 0); pos += 4;
            WriteLE32(bmp, pos, (uint)pixelSize); pos += 4;
            WriteLE32(bmp, pos, 0); pos += 4;
            WriteLE32(bmp, pos, 0); pos += 4;
            WriteLE32(bmp, pos, 0); pos += 4;
            WriteLE32(bmp, pos, 0); pos += 4;

            int copyLen = Math.Min(bgrData.Length, pixelSize);
            Array.Copy(bgrData, 0, bmp, TOTAL_BMP_HEADER, copyLen);

            return bmp;
        }

        public void ImportEntry(ArchiveFile archive, ArchiveEntry entry, byte[] newData,
                                Stream originalStream, Stream outputStream)
        {
            string ext = Path.GetExtension(archive.FilePath).ToLowerInvariant();

            if (ext == ".002")
                Import002(archive, entry, newData, originalStream, outputStream);
            else if (ext == ".003")
                Import003(archive, entry, newData, originalStream, outputStream);
            else
                throw new NotSupportedException($"Unsupported extension for Xuse BMP import: {ext}");
        }

        private void Import002(ArchiveFile archive, ArchiveEntry entry, byte[] newData,
                               Stream originalStream, Stream outputStream)
        {
            var (width, height, bgrData) = ParseBmp(newData);
            int stride = RowStride(BG_WIDTH);
            int imageSize = stride * BG_HEIGHT;

            if (width != BG_WIDTH || height != BG_HEIGHT)
                throw new InvalidOperationException(
                    $"BMP must be {BG_WIDTH}×{BG_HEIGHT} for .002 archives, got {width}×{height}");

            if (bgrData.Length != imageSize)
                throw new InvalidOperationException(
                    $"Pixel data size mismatch: expected {imageSize}, got {bgrData.Length}");

            var reader = new BinaryReaderEx(originalStream);
            long fileLen = originalStream.Length;
            int count = (int)(fileLen / imageSize);

            int targetIndex = (int)(entry.Offset / imageSize);
            if (targetIndex < 0 || targetIndex >= count)
                throw new InvalidOperationException("Could not find the target entry in the .002 archive.");

            for (int i = 0; i < count; i++)
            {
                if (i == targetIndex)
                {
                    outputStream.Write(bgrData, 0, bgrData.Length);
                }
                else
                {
                    byte[] origData = reader.ReadBytes((long)i * imageSize, imageSize);
                    outputStream.Write(origData, 0, origData.Length);
                }
            }
        }

        private void Import003(ArchiveFile archive, ArchiveEntry entry, byte[] newData,
                               Stream originalStream, Stream outputStream)
        {
            var (newWidth, newHeight, newBgrData) = ParseBmp(newData);
            int newStride = RowStride(newWidth);
            int newPixelSize = newStride * newHeight;

            if (newBgrData.Length != newPixelSize)
                throw new InvalidOperationException(
                    $"Pixel data size mismatch: expected {newPixelSize}, got {newBgrData.Length}");

            var reader = new BinaryReaderEx(originalStream);

            uint firstOff = reader.ReadUInt32(12);
            int count = (int)(firstOff / 16);

            var origEntries = new (uint dataSize, uint w, uint h, uint dataOffset)[count];
            for (int i = 0; i < count; i++)
            {
                long pos = (long)i * 16;
                origEntries[i] = (
                    reader.ReadUInt32(pos),
                    reader.ReadUInt32(pos + 4),
                    reader.ReadUInt32(pos + 8),
                    reader.ReadUInt32(pos + 12)
                );
            }

            int targetIndex = -1;
            for (int i = 0; i < count; i++)
            {
                if (origEntries[i].dataOffset == (uint)entry.Offset)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex < 0)
                throw new InvalidOperationException("Could not find the target entry in the .003 archive.");

            int headerSize = count * 16;
            uint currentOffset = (uint)headerSize;

            var newIndex = new (uint dataSize, uint w, uint h, uint dataOffset)[count];
            var dataBlobs = new byte[count][];

            for (int i = 0; i < count; i++)
            {
                if (i == targetIndex)
                {
                    dataBlobs[i] = newBgrData;
                    newIndex[i] = ((uint)newPixelSize, (uint)newWidth, (uint)newHeight, currentOffset);
                }
                else
                {
                    var orig = origEntries[i];
                    dataBlobs[i] = reader.ReadBytes(orig.dataOffset, (int)orig.dataSize);
                    newIndex[i] = (orig.dataSize, orig.w, orig.h, currentOffset);
                }
                currentOffset += newIndex[i].dataSize;
            }

            for (int i = 0; i < count; i++)
            {
                var e = newIndex[i];
                outputStream.Write(BitConverter.GetBytes(e.dataSize), 0, 4);
                outputStream.Write(BitConverter.GetBytes(e.w), 0, 4);
                outputStream.Write(BitConverter.GetBytes(e.h), 0, 4);
                outputStream.Write(BitConverter.GetBytes(e.dataOffset), 0, 4);
            }

            for (int i = 0; i < count; i++)
            {
                outputStream.Write(dataBlobs[i], 0, dataBlobs[i].Length);
            }
        }

        private static (int width, int height, byte[] bgrData) ParseBmp(byte[] data)
        {
            if (data.Length < TOTAL_BMP_HEADER || data[0] != 'B' || data[1] != 'M')
                throw new InvalidOperationException("Import data is not a valid BMP file.");

            uint offBits = BitConverter.ToUInt32(data, 10);
            int width = BitConverter.ToInt32(data, 18);
            int height = BitConverter.ToInt32(data, 22);
            ushort bits = BitConverter.ToUInt16(data, 28);
            uint compression = BitConverter.ToUInt32(data, 30);

            if (bits != 24)
                throw new InvalidOperationException($"Expected 24-bit BMP, got {bits}-bit.");
            if (compression != 0)
                throw new InvalidOperationException($"Expected uncompressed BMP (BI_RGB), got compression={compression}.");

            bool topDown = height < 0;
            int absHeight = Math.Abs(height);
            int stride = RowStride(width);
            int pixelSize = stride * absHeight;

            if (offBits + pixelSize > data.Length)
                throw new InvalidOperationException(
                    $"BMP pixel data truncated: expected {pixelSize} bytes at offset {offBits}, file is {data.Length} bytes.");

            byte[] bgrData = new byte[pixelSize];
            Array.Copy(data, offBits, bgrData, 0, pixelSize);

            if (topDown)
            {
                byte[] flipped = new byte[pixelSize];
                for (int y = 0; y < absHeight; y++)
                {
                    int srcRow = y * stride;
                    int dstRow = (absHeight - 1 - y) * stride;
                    Array.Copy(bgrData, srcRow, flipped, dstRow, stride);
                }
                bgrData = flipped;
            }

            return (width, absHeight, bgrData);
        }

        private static int RowStride(int width)
        {
            return (width * 3 + 3) & ~3;
        }

        private static void WriteLE16(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteLE32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }
    }
}
