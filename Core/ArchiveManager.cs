using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XuseExplorer.Core.Formats;
using XuseExplorer.Models;

namespace XuseExplorer.Core
{
    public class ArchiveManager
    {
        private readonly List<IArchiveReader> _readers;
        private readonly Dictionary<string, IArchiveReader> _readerByTag = new();

        public IReadOnlyList<IArchiveReader> Readers => _readers;

        public ArchiveManager()
        {
            _readers = new List<IArchiveReader>
            {
                new XarcArchiveReader(),
                new MikoArchiveReader(),
                new WagArchiveReader(),
                new KotoriArchiveReader(),
                new BinArchiveReader(),
                new WvbArchiveReader(),
                new GdArchiveReader(),
                new BgArchiveReader(),
                new HArchiveReader(),
                new AifArchiveReader(),
                new DatArchiveReader(),
                new CdFilesArchiveReader(),
                new XuseBmpArchiveReader(),
            };

            foreach (var r in _readers)
                _readerByTag[r.Tag] = r;
        }

        public string FileFilter
        {
            get
            {
                var allExts = _readers.SelectMany(r => r.Extensions)
                    .Where(e => !string.IsNullOrEmpty(e))
                    .Select(e => $"*{e}")
                    .Distinct()
                    .ToList();

                allExts.Add("*.cd");
                allExts.Add("*.cd3");

                string all = string.Join(";", allExts);
                return $"All Supported Files ({all})|{all}" +
                       $"|WAG Archives (*.wag;*.4ag;*.004)|*.wag;*.4ag;*.004" +
                       $"|ARC Archives (*.arc;*.xarc)|*.arc;*.xarc" +
                       $"|BIN Archives (*.bin)|*.bin" +
                       $"|WVB Archives (*.wvb)|*.wvb" +
                       $"|GD Archives (*.gd)|*.gd" +
                       $"|AIF Image Archives (*.aif)|*.aif" +
                       $"|DAT Audio Archives (*.dat)|*.dat" +
                       $"|CDFiles Archives (CDFiles*)|CDFiles*" +
                       $"|Image Archives (*.002;*.003)|*.002;*.003" +
                       $"|Script Files (*.cd;*.cd3)|*.cd;*.cd3" +
                       $"|All Files (*.*)|*.*";
            }
        }

        public ArchiveFile? OpenArchive(string filePath)
        {
            var errors = new List<string>();
            foreach (var reader in _readers)
            {
                try
                {
                    var archive = reader.TryOpen(filePath);
                    if (archive != null)
                        return archive;
                }
                catch (Exception ex)
                {
                    errors.Add($"{reader.Tag}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            LastOpenErrors = errors;
            return null;
        }

        public List<string> LastOpenErrors { get; private set; } = new();

        public ArchiveFile? OpenArchiveFromData(byte[] data, string virtualName)
        {
            var errors = new List<string>();
            var ms = new MemoryStream(data);

            foreach (var reader in _readers)
            {
                try
                {
                    ms.Position = 0;
                    var archive = reader.TryOpenFromStream(ms, virtualName);
                    if (archive != null)
                    {
                        archive.DataStream = ms;
                        return archive;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{reader.Tag}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            ms.Dispose();
            LastOpenErrors = errors;
            return null;
        }

        public bool IsEntryArchive(ArchiveFile archive, ArchiveEntry entry)
        {
            try
            {
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (ext is ".arc" or ".xarc" or ".wag" or ".4ag" or ".004" or ".bin" or ".wvb" or ".gd")
                    return true;

                var header = ExtractEntryPartial(archive, entry, 16);
                return header != null && SignatureDetector.IsLikelyArchive(header, entry.Name);
            }
            catch
            {
                return false;
            }
        }

        private byte[]? ExtractEntryPartial(ArchiveFile archive, ArchiveEntry entry, int maxBytes)
        {
            try
            {
                if (_readerByTag.TryGetValue(archive.FormatTag, out var reader))
                {
                    var data = reader.ExtractEntry(archive, entry);
                    if (data.Length > maxBytes)
                    {
                        var partial = new byte[maxBytes];
                        Array.Copy(data, partial, maxBytes);
                        return partial;
                    }
                    return data;
                }
            }
            catch { }
            return null;
        }

        public byte[] ExtractEntry(ArchiveFile archive, ArchiveEntry entry)
        {
            if (_readerByTag.TryGetValue(archive.FormatTag, out var reader))
                return reader.ExtractEntry(archive, entry);

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
                var buf = new byte[entry.Size];
                stream.Seek(entry.Offset, SeekOrigin.Begin);
                int totalRead = 0;
                while (totalRead < buf.Length)
                {
                    int read = stream.Read(buf, totalRead, buf.Length - totalRead);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return buf;
            }
            finally
            {
                if (ownsStream) stream.Dispose();
            }
        }

        public void ExtractEntryToFile(ArchiveFile archive, ArchiveEntry entry, string outputPath)
        {
            var data = ExtractEntry(archive, entry);
            data = FixObfuscatedData(data, ref outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, data);
        }

        private static byte[] FixObfuscatedData(byte[] data, ref string outputPath)
        {
            if (data.Length > 4 && data[0] == 0x4E && data[1] == 0x47 && data[2] == 0x0D && data[3] == 0x0A)
            {
                var pngData = new byte[data.Length + 2];
                pngData[0] = 0x89;
                pngData[1] = 0x50;
                Array.Copy(data, 0, pngData, 2, data.Length);

                string ext = Path.GetExtension(outputPath).ToLowerInvariant();
                if (ext != ".png")
                    outputPath = Path.ChangeExtension(outputPath, ".png");

                return pngData;
            }
            return data;
        }

        public void ExtractAll(ArchiveFile archive, string outputDir, IProgress<(int current, int total, string name)>? progress = null)
        {
            int total = archive.Entries.Count;
            for (int i = 0; i < total; i++)
            {
                var entry = archive.Entries[i];
                progress?.Report((i + 1, total, entry.Name));

                string safeName = SanitizeFileName(entry.Name);
                string outputPath = Path.Combine(outputDir, safeName);
                ExtractEntryToFile(archive, entry, outputPath);
            }
        }

        public void ImportFile(ArchiveFile archive, ArchiveEntry entry, string importFilePath, string outputArchivePath)
        {
            byte[] newData = File.ReadAllBytes(importFilePath);
            ImportFileData(archive, entry, newData, outputArchivePath);
        }

        public void ImportFileData(ArchiveFile archive, ArchiveEntry entry, byte[] newData, string outputArchivePath)
        {
            if (_readerByTag.TryGetValue(archive.FormatTag, out var reader) && reader.CanImport)
            {
                using var originalStream = File.OpenRead(archive.FilePath);
                using var outputStream = File.Create(outputArchivePath);
                reader.ImportEntry(archive, entry, newData, originalStream, outputStream);
            }
            else
            {
                throw new NotSupportedException(
                    $"Saving into '{archive.FormatTag}' archives is not supported. " +
                    $"Extract the script, edit it externally, then replace the archive manually.");
            }
        }

        private static void CopyBytes(Stream source, Stream dest, long count)
        {
            byte[] buffer = new byte[81920];
            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = source.Read(buffer, 0, toRead);
                if (read <= 0) break;
                dest.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static void CopyStream(Stream source, Stream dest)
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                dest.Write(buffer, 0, read);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
