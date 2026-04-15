using System.Windows;
using XuseExplorer.Models;

namespace XuseExplorer.Views
{
    public partial class EditScriptEntryDialog : Window
    {
        public ScriptEntry Entry { get; private set; }

        public bool WasModified { get; private set; }

        public EditScriptEntryDialog(ScriptEntry entry)
        {
            InitializeComponent();

            Entry = entry.Clone();

            IndexBox.Text = entry.Index.ToString();
            SeqIdBox.Text = entry.SeqId.ToString();
            VoiceBox.Text = entry.Voice.ToString();
            TextBox.Text = entry.Text;
            TextBox.Focus();
            TextBox.SelectAll();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(VoiceBox.Text.Trim(), out int voice))
                Entry.Voice = voice;

            string newText = TextBox.Text;
            newText = newText.Replace("\r\n", "\n").Replace("\r", "\n");

            WasModified = (Entry.Text != newText || Entry.Voice != voice);
            Entry.Text = newText;

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
