using System;
using System.Collections.Generic;

namespace XuseExplorer.Models
{
    public class ArchiveFile : IDisposable
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(FilePath);
        public string FormatTag { get; set; } = string.Empty;
        public string FormatDescription { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public List<ArchiveEntry> Entries { get; set; } = new();

        public byte[]? DecryptionKey { get; set; }

        public System.IO.FileStream? Stream { get; set; }

        public System.IO.MemoryStream? DataStream { get; set; }

        public bool IsNested => DataStream != null;

        public string DisplayPath { get; set; } = string.Empty;

        public void Dispose()
        {
            Stream?.Dispose();
            Stream = null;
            DataStream?.Dispose();
            DataStream = null;
        }
    }
}
