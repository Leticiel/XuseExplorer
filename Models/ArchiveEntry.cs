using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XuseExplorer.Models
{
    public class ArchiveEntry : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = "unknown";
        public long Offset { get; set; }
        public uint Size { get; set; }
        public uint UnpackedSize { get; set; }
        public bool IsPacked { get; set; }
        public string ArchivePath { get; set; } = string.Empty;
        public string FormatTag { get; set; } = string.Empty;

        private bool _isModified;
        public bool IsModified
        {
            get => _isModified;
            set
            {
                if (_isModified != value)
                {
                    _isModified = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ModifiedDisplay));
                }
            }
        }

        public string ModifiedDisplay => IsModified ? "*" : "";

        public string SizeDisplay => FormatSize(Size);

        public string TypeDisplay => Type switch
        {
            "image" => "🖼 Image",
            "audio" => "🔊 Audio",
            "script" => "📜 Script",
            "video" => "🎬 Video",
            "archive" => "📦 Archive",
            _ => "📄 File"
        };

        public string Extension
        {
            get
            {
                var ext = System.IO.Path.GetExtension(Name);
                return string.IsNullOrEmpty(ext) ? "(none)" : ext;
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
