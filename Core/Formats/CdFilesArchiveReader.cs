using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Formats
{
    public class CdFilesArchiveReader : IArchiveReader
    {
        public string Tag => "00 Archive";
        public string Description => "CDFiles00 archive with .rf index";
        public string[] Extensions => Array.Empty<string>();

        public bool CanImport => true;

        private static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public ArchiveFile? TryOpen(string filePath)
        {
            string? rfPath = FindRfFile(filePath);
            if (rfPath == null) return null;

            using var archiveStream = File.OpenRead(filePath);
            using var rfStream = File.OpenRead(rfPath);

            return TryOpenInternal(archiveStream, rfStream, filePath);
        }

        public ArchiveFile? TryOpenFromStream(Stream stream, string virtualName)
        {
            return null;
        }

        private ArchiveFile? TryOpenInternal(Stream archiveStream, Stream rfStream, string filePath)
        {
            var rfEntries = ParseRfIndex(rfStream);
            if (rfEntries == null || rfEntries.Count == 0)
                return null;

            long archiveLength = archiveStream.Length;
            if (archiveLength < 8) return null;

            var reader = new BinaryReaderEx(archiveStream);

            int maxIndex = 0;
            foreach (var (fileIndex, _) in rfEntries)
            {
                if (fileIndex > maxIndex) maxIndex = fileIndex;
            }

            long indexTableSize = ((long)maxIndex + 1) * 8;
            if (indexTableSize > archiveLength) return null;

            var (firstIdx, _) = rfEntries[0];
            uint firstSize = reader.ReadUInt32((long)firstIdx * 8);
            uint firstOffset = reader.ReadUInt32((long)firstIdx * 8 + 4);

            if (firstOffset < indexTableSize) return null;
            if (firstOffset + firstSize > archiveLength) return null;

            var entries = new List<ArchiveEntry>(rfEntries.Count);

            foreach (var (fileIndex, name) in rfEntries)
            {
                long tablePos = (long)fileIndex * 8;
                if (tablePos + 8 > archiveLength) continue;

                uint size = reader.ReadUInt32(tablePos);
                uint offset = reader.ReadUInt32(tablePos + 4);

                if (offset + size > archiveLength) continue;

                byte[] sigBytes = reader.ReadBytes(offset, (int)Math.Min(4, size));
                var (ext, type) = SignatureDetector.Detect(sigBytes, name);

                string entryName = name;
                if (string.IsNullOrEmpty(Path.GetExtension(entryName)) && ext != "bin")
                    entryName = $"{name}.{ext}";

                entries.Add(new ArchiveEntry
                {
                    Name = entryName,
                    Path = entryName,
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
                FileSize = archiveLength,
                Entries = entries
            };
        }

        public byte[] ExtractEntry(ArchiveFile archive, ArchiveEntry entry)
        {
            Stream stream;
            bool ownsStream;
            if (archive.DataStream != null)
            {
                stream = archive.DataStream;
                stream.Seek(0, SeekOrigin.Begin);
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
            string? rfPath = FindRfFile(archive.FilePath);
            if (rfPath == null)
                throw new InvalidOperationException("Cannot find the .rf index file.");

            List<(int fileIndex, string name)> rfEntries;
            using (var rfStream = File.OpenRead(rfPath))
                rfEntries = ParseRfIndex(rfStream) ?? throw new InvalidOperationException("Failed to parse .rf index.");

            var reader = new BinaryReaderEx(originalStream);

            int maxIndex = 0;
            foreach (var (idx, _) in rfEntries)
                if (idx > maxIndex) maxIndex = idx;

            int indexSlotCount = maxIndex + 1;
            long indexTableSize = (long)indexSlotCount * 8;

            var sizes = new uint[indexSlotCount];
            var offsets = new uint[indexSlotCount];
            for (int i = 0; i < indexSlotCount; i++)
            {
                sizes[i] = reader.ReadUInt32((long)i * 8);
                offsets[i] = reader.ReadUInt32((long)i * 8 + 4);
            }

            int targetSlot = -1;
            for (int i = 0; i < indexSlotCount; i++)
            {
                if (offsets[i] == (uint)entry.Offset && sizes[i] == entry.Size)
                {
                    targetSlot = i;
                    break;
                }
            }

            if (targetSlot < 0)
                throw new InvalidOperationException("Could not find the target entry in the archive index.");

            var usedSlots = new List<(int slot, uint origOffset, uint origSize)>();
            for (int i = 0; i < indexSlotCount; i++)
            {
                if (sizes[i] > 0 || offsets[i] > 0)
                    usedSlots.Add((i, offsets[i], sizes[i]));
            }
            usedSlots.Sort((a, b) => a.origOffset.CompareTo(b.origOffset));

            uint dataStart = Align((uint)indexTableSize, 0x400);

            var newSizes = new uint[indexSlotCount];
            var newOffsets = new uint[indexSlotCount];
            Array.Copy(sizes, newSizes, indexSlotCount);
            Array.Copy(offsets, newOffsets, indexSlotCount);

            newSizes[targetSlot] = (uint)newData.Length;

            uint currentOffset = dataStart;
            foreach (var (slot, _, _) in usedSlots)
            {
                newOffsets[slot] = currentOffset;
                currentOffset += newSizes[slot];
            }

            for (int i = 0; i < indexSlotCount; i++)
            {
                outputStream.Write(BitConverter.GetBytes(newSizes[i]), 0, 4);
                outputStream.Write(BitConverter.GetBytes(newOffsets[i]), 0, 4);
            }

            long paddingNeeded = dataStart - indexTableSize;
            if (paddingNeeded > 0)
            {
                var zeros = new byte[paddingNeeded];
                outputStream.Write(zeros, 0, zeros.Length);
            }

            foreach (var (slot, origOffset, origSize) in usedSlots)
            {
                if (slot == targetSlot)
                {
                    outputStream.Write(newData, 0, newData.Length);
                }
                else
                {
                    byte[] data = reader.ReadBytes(origOffset, (int)origSize);
                    outputStream.Write(data, 0, data.Length);
                }
            }
        }

        private static uint Align(uint val, uint alignment)
        {
            return (val + (alignment - 1)) & ~(alignment - 1);
        }

        private static string? FindRfFile(string archivePath)
        {
            string fileName = Path.GetFileName(archivePath);

            string dir = Path.GetDirectoryName(archivePath) ?? ".";

            string baseName = fileName;
            while (baseName.Length > 0 && char.IsDigit(baseName[^1]))
                baseName = baseName[..^1];

            if (string.IsNullOrEmpty(baseName)) return null;

            string rfPath = Path.Combine(dir, baseName + ".rf");
            if (File.Exists(rfPath)) return rfPath;

            foreach (var file in Directory.GetFiles(dir, "*.rf"))
            {
                if (Path.GetFileName(file).Equals(baseName + ".rf", StringComparison.OrdinalIgnoreCase))
                    return file;
            }

            return null;
        }

        private static List<(int fileIndex, string name)>? ParseRfIndex(Stream rfStream)
        {
            if (rfStream.Length < 4) return null;

            using var br = new BinaryReader(rfStream, Encoding.ASCII, leaveOpen: true);

            uint entryCount = br.ReadUInt32();
            if (entryCount == 0 || entryCount > 100000) return null;

            var entries = new List<(int, string)>((int)entryCount);

            for (int i = 0; i < entryCount; i++)
            {
                if (rfStream.Position + 4 > rfStream.Length) return null;
                uint fileIndex = br.ReadUInt32();

                var nameBytes = new List<byte>();
                while (rfStream.Position < rfStream.Length)
                {
                    byte b = br.ReadByte();
                    if (b == 0) break;
                    nameBytes.Add(b);
                }

                string name = ShiftJis.GetString(nameBytes.ToArray());
                entries.Add(((int)fileIndex, name));
            }

            return entries;
        }
    }
}
