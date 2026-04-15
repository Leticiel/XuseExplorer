using System;
using System.Collections.Generic;
using System.IO;
using XuseExplorer.Models;

namespace XuseExplorer.Core
{
    public interface IArchiveReader
    {
        string Tag { get; }
        string Description { get; }
        string[] Extensions { get; }

        ArchiveFile? TryOpen(string filePath);

        ArchiveFile? TryOpenFromStream(Stream stream, string virtualName);

        byte[] ExtractEntry(ArchiveFile archive, ArchiveEntry entry);

        void ImportEntry(ArchiveFile archive, ArchiveEntry entry, byte[] newData, Stream originalStream, Stream outputStream)
        {
            throw new NotSupportedException($"Format '{Tag}' does not support entry replacement.");
        }

        bool CanImport => false;
    }

    public static class SignatureDetector
    {
        private static readonly Dictionary<uint, (string ext, string type)> KnownSignatures = new()
        {
            { 0x474E5089, ("png", "image") },
            { 0xE0FFD8FF, ("jpg", "image") },
            { 0x46464952, ("wav", "audio") },
            { 0x5367674F, ("ogg", "audio") },
            { 0x6468544D, ("mid", "audio") },
            { 0x00010000, ("ttf", "font") },
            { 0x504B0304, ("zip", "archive") },
            { 0x04034B50, ("zip", "archive") },
            { 0x38464947, ("gif", "image") },
            { 0x002A4949, ("tif", "image") },
            { 0x2A004D4D, ("tif", "image") },
            { 0x0A0D474E, ("png", "image") },
            { 0xBA010000, ("mpg", "video") },
        };

        private static readonly HashSet<uint> ArchiveSignatures = new()
        {
            0x40474157,
            0x34464147,
            0x43524158,
            0x4F4B494D,
            0x4F544F4B,
        };

        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".arc", ".xarc", ".wag", ".4ag", ".004", ".bin", ".wvb", ".gd", ".aif", ".dat"
        };

        private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cd", ".cd3"
        };

        private static readonly HashSet<uint> ScriptSignatures = new()
        {
            0x204B4D53,
            0x49524F4E,
        };

        public static (string extension, string type) Detect(byte[] data)
        {
            if (data.Length < 4) return ("bin", "unknown");

            uint sig = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

            if (KnownSignatures.TryGetValue(sig, out var result))
                return result;

            if (data[0] == 'B' && data[1] == 'M')
                return ("bmp", "image");

            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
                return ("ogg", "audio");

            if (ArchiveSignatures.Contains(sig))
                return ("arc", "archive");

            if (ScriptSignatures.Contains(sig))
                return ("cd3", "script");

            if (sig == 1 && data.Length > 0x14)
                return ("bin", "archive");

            return ("bin", "unknown");
        }

        public static (string extension, string type) DetectFromSignature(uint signature)
        {
            if (KnownSignatures.TryGetValue(signature, out var result))
                return result;
            return ("bin", "unknown");
        }

        public static (string extension, string type) Detect(byte[] data, string fileName)
        {
            string fileExt = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            if (fileExt == ".debug")
                return ("debug", "file");

            var result = Detect(data);
            if (result.type != "unknown") return result;

            string ext = fileExt;
            if (ScriptExtensions.Contains(ext))
                return (ext.TrimStart('.'), "script");

            return result;
        }

        public static bool IsLikelyArchive(byte[] data, string name)
        {
            if (data.Length >= 4)
            {
                uint sig = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                if (ArchiveSignatures.Contains(sig))
                    return true;

                if (sig == 1 && data.Length > 0x14)
                    return true;
            }

            string ext = System.IO.Path.GetExtension(name);
            if (ArchiveExtensions.Contains(ext))
                return true;

            return false;
        }

        public static bool IsLikelyScript(byte[] data, string name)
        {
            if (data.Length >= 4)
            {
                uint sig = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                if (ScriptSignatures.Contains(sig))
                    return true;
            }

            string ext = System.IO.Path.GetExtension(name);
            if (ScriptExtensions.Contains(ext))
                return true;

            return false;
        }
    }
}
