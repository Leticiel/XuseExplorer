using System;

namespace XuseExplorer.Models
{
    public class ScriptEditRecord
    {
        public int EntryIndex { get; set; }

        public int SeqId { get; set; }

        public string OldText { get; set; } = "";

        public string NewText { get; set; } = "";

        public int OldVoice { get; set; }

        public int NewVoice { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;

        public string DisplayText
        {
            get
            {
                string oldShort = Truncate(OldText.Split('\n')[0], 30);
                string newShort = Truncate(NewText.Split('\n')[0], 30);
                string time = Timestamp.ToString("HH:mm:ss");
                return $"[{time}]  #{EntryIndex}  \"{oldShort}\"  →  \"{newShort}\"";
            }
        }

        public string OldTextPreview => OldText.Length > 200 ? OldText[..200] + "..." : OldText;

        public string NewTextPreview => NewText.Length > 200 ? NewText[..200] + "..." : NewText;

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..max] + "…";
    }
}
