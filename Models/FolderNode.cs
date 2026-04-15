using System.Collections.ObjectModel;

namespace XuseExplorer.Models
{
    public class FolderNode
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Icon { get; set; } = "📁";
        public ObservableCollection<FolderNode> Children { get; set; } = new();
        public ObservableCollection<ArchiveEntry> Entries { get; set; } = new();
        public bool IsExpanded { get; set; }
    }
}
