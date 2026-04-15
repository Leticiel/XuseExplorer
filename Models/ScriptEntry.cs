using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XuseExplorer.Models
{
    public class ScriptEntry : INotifyPropertyChanged
    {
        private int _index;
        private int _seqId;
        private int _voice;
        private string _text = string.Empty;

        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(); }
        }

        public int SeqId
        {
            get => _seqId;
            set { _seqId = value; OnPropertyChanged(); }
        }

        public int Voice
        {
            get => _voice;
            set { _voice = value; OnPropertyChanged(); }
        }

        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }

        public Dictionary<string, object>? Extra { get; set; }

        public ScriptEntry Clone() => new()
        {
            Index = Index,
            SeqId = SeqId,
            Voice = Voice,
            Text = Text,
            Extra = Extra != null ? new Dictionary<string, object>(Extra) : null
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
