using System.Collections.Generic;
using XuseExplorer.Models;

namespace XuseExplorer.Core.Scripts
{
    public class ScriptFile
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(FilePath);
        public string FormatTag { get; set; } = string.Empty;
        public string FormatDescription { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public List<ScriptEntry> Entries { get; set; } = new();

        public byte[] RawData { get; set; } = System.Array.Empty<byte>();

        public ArchiveFile? ParentArchive { get; set; }

        public ArchiveEntry? ParentEntry { get; set; }

        public bool IsInsideArchive => ParentArchive != null && ParentEntry != null;
    }

    public interface IScriptReader
    {
        string Tag { get; }
        string Description { get; }
        string[] Extensions { get; }

        ScriptFile? TryOpen(string filePath);

        ScriptFile? TryOpenFromData(byte[] data, string virtualName);

        byte[] Rebuild(byte[] originalData, List<ScriptEntry> modifiedEntries);
    }
}
