using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XuseExplorer.Core;
using XuseExplorer.Core.Scripts;
using XuseExplorer.Models;
using XuseExplorer.Views;

namespace XuseExplorer.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ArchiveManager _archiveManager = new();
        private readonly ScriptManager _scriptManager = new();

        private readonly Stack<(ArchiveFile archive, string displayPath, ArchiveEntry? nestedEntry)> _navigationStack = new();

        public MainViewModel()
        {
            OpenCommand = new RelayCommand(_ => OpenArchive());
            ExtractSelectedCommand = new RelayCommand(_ => ExtractSelected(), _ => SelectedEntry != null && !IsScriptMode);
            ExtractAllCommand = new RelayCommand(_ => ExtractAll(), _ => CurrentArchive != null && !IsScriptMode);
            ImportFileCommand = new RelayCommand(_ => ImportFile(), _ => SelectedEntry != null && !IsScriptMode);
            CloseCommand = new RelayCommand(_ => CloseArchive(), _ => CurrentArchive != null || CurrentScript != null);
            FilterCommand = new RelayCommand(_ => ApplyFilter());
            ClearFilterCommand = new RelayCommand(_ => { FilterText = ""; ApplyFilter(); });
            OpenEntryCommand = new RelayCommand(_ => OpenSelectedEntry(), _ => CanOpenEntry());
            GoBackCommand = new RelayCommand(_ => NavigateBack(), _ => _navigationStack.Count > 0);
            ExportScriptJsonCommand = new RelayCommand(_ => ExportScriptJson(), _ => CurrentScript != null);
            ImportScriptJsonCommand = new RelayCommand(_ => ImportScriptJson(), _ => CurrentScript != null);
            EditScriptEntryCommand = new RelayCommand(_ => EditSelectedScriptEntry(), _ => SelectedScriptEntry != null);
            SaveCommand = new RelayCommand(_ => Save(), _ => CanSave());
            ExtractScriptRawCommand = new RelayCommand(_ => ExtractScriptRaw(), _ => CurrentScript != null);
            ExtractEntryAsJsonCommand = new RelayCommand(_ => ExtractEntryAsJson(), _ => CanExtractEntryAsScript());
            ImportEntryJsonCommand = new RelayCommand(_ => ImportEntryJson(), _ => CanExtractEntryAsScript());
            AudioPlayPauseCommand = new RelayCommand(_ => AudioPlayPause(), _ => IsAudioEntry);
            AudioStopCommand = new RelayCommand(_ => AudioStop(), _ => IsAudioEntry);
            UndoScriptEditCommand = new RelayCommand(_ => UndoScriptEdit(), _ => HasEditHistory);
            BatchExtractCommand = new RelayCommand(_ => BatchExtractSelected(), _ => SelectedEntries.Count > 0 && !IsScriptMode);
            BatchImportCommand = new RelayCommand(_ => BatchImportSelected(), _ => SelectedEntries.Count > 0 && !IsScriptMode);

            _audioPlayer.PlaybackStateChanged += () =>
            {
                IsAudioPlaying = _audioPlayer.IsPlaying;
                CommandManager.InvalidateRequerySuggested();
            };
            _audioPlayer.PositionChanged += () =>
            {
                AudioPositionPercent = _audioPlayer.PositionPercent;
                var cur = _audioPlayer.CurrentPosition;
                var tot = _audioPlayer.TotalDuration;
                AudioPositionText = $"{(int)cur.TotalMinutes}:{cur.Seconds:D2} / {(int)tot.TotalMinutes}:{tot.Seconds:D2}";
            };

            StatusText = "Ready. Open an archive or script to begin.";
        }

        private ArchiveFile? _currentArchive;
        public ArchiveFile? CurrentArchive
        {
            get => _currentArchive;
            set { _currentArchive = value; OnPropertyChanged(); OnPropertyChanged(nameof(ArchiveInfoText)); }
        }

        private ObservableCollection<ArchiveEntry> _entries = new();
        public ObservableCollection<ArchiveEntry> Entries
        {
            get => _entries;
            set { _entries = value; OnPropertyChanged(); }
        }

        private ObservableCollection<ArchiveEntry> _filteredEntries = new();
        public ObservableCollection<ArchiveEntry> FilteredEntries
        {
            get => _filteredEntries;
            set { _filteredEntries = value; OnPropertyChanged(); }
        }

        private ObservableCollection<FolderNode> _folderTree = new();
        public ObservableCollection<FolderNode> FolderTree
        {
            get => _folderTree;
            set { _folderTree = value; OnPropertyChanged(); }
        }

        private ArchiveEntry? _selectedEntry;
        public ArchiveEntry? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                _selectedEntry = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsSelectedEntryScript));
                UpdatePreview();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private List<ArchiveEntry> _selectedEntries = new();
        public List<ArchiveEntry> SelectedEntries
        {
            get => _selectedEntries;
            set { _selectedEntries = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
        }

        public bool IsSelectedEntryScript
        {
            get
            {
                if (SelectedEntry == null) return false;
                string ext = Path.GetExtension(SelectedEntry.Name).ToLowerInvariant();
                return ext is ".cd" or ".cd3";
            }
        }

        private FolderNode? _selectedFolder;
        public FolderNode? SelectedFolder
        {
            get => _selectedFolder;
            set
            {
                _selectedFolder = value;
                OnPropertyChanged();
                if (value != null)
                    FilteredEntries = value.Entries;
            }
        }

        private string _filterText = "";
        public string FilterText
        {
            get => _filterText;
            set { _filterText = value; OnPropertyChanged(); }
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        private bool _isProgressVisible;
        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            set { _isProgressVisible = value; OnPropertyChanged(); }
        }

        private ImageSource? _previewImage;
        public ImageSource? PreviewImage
        {
            get => _previewImage;
            set { _previewImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImagePreview)); }
        }

        private string _previewText = "";
        public string PreviewText
        {
            get => _previewText;
            set { _previewText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTextPreview)); }
        }

        private string _hexPreview = "";
        public string HexPreview
        {
            get => _hexPreview;
            set { _hexPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHexPreview)); }
        }

        private string _entryInfoText = "";
        public string EntryInfoText
        {
            get => _entryInfoText;
            set { _entryInfoText = value; OnPropertyChanged(); }
        }

        public bool HasSelection => SelectedEntry != null;
        public bool HasImagePreview => PreviewImage != null;
        public bool HasTextPreview => !string.IsNullOrEmpty(PreviewText);
        public bool HasHexPreview => !string.IsNullOrEmpty(HexPreview);
        public bool CanGoBack => _navigationStack.Count > 0;

        private readonly AudioPlayerService _audioPlayer = new();
        private bool _isAudioEntry;
        public bool IsAudioEntry
        {
            get => _isAudioEntry;
            set { _isAudioEntry = value; OnPropertyChanged(); }
        }

        private string _audioStatusText = "";
        public string AudioStatusText
        {
            get => _audioStatusText;
            set { _audioStatusText = value; OnPropertyChanged(); }
        }

        private string _audioPositionText = "0:00 / 0:00";
        public string AudioPositionText
        {
            get => _audioPositionText;
            set { _audioPositionText = value; OnPropertyChanged(); }
        }

        private double _audioPositionPercent;
        public double AudioPositionPercent
        {
            get => _audioPositionPercent;
            set
            {
                if (Math.Abs(_audioPositionPercent - value) > 0.01)
                {
                    _audioPositionPercent = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isAudioPlaying;
        public bool IsAudioPlaying
        {
            get => _isAudioPlaying;
            set { _isAudioPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayPauseIcon)); }
        }

        public string PlayPauseIcon => IsAudioPlaying ? "⏸" : "▶";

        public ICommand AudioPlayPauseCommand { get; }
        public ICommand AudioStopCommand { get; }

        private ScriptFile? _currentScript;
        public ScriptFile? CurrentScript
        {
            get => _currentScript;
            set
            {
                _currentScript = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsScriptMode));
                OnPropertyChanged(nameof(ArchiveInfoText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private ObservableCollection<ScriptEntry> _scriptEntries = new();
        public ObservableCollection<ScriptEntry> ScriptEntries
        {
            get => _scriptEntries;
            set { _scriptEntries = value; OnPropertyChanged(); }
        }

        private ObservableCollection<ScriptEntry> _filteredScriptEntries = new();
        public ObservableCollection<ScriptEntry> FilteredScriptEntries
        {
            get => _filteredScriptEntries;
            set { _filteredScriptEntries = value; OnPropertyChanged(); }
        }

        private ScriptEntry? _selectedScriptEntry;
        public ScriptEntry? SelectedScriptEntry
        {
            get => _selectedScriptEntry;
            set
            {
                _selectedScriptEntry = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasScriptSelection));
                UpdateScriptPreview();
            }
        }

        public bool IsScriptMode => CurrentScript != null;
        public bool HasScriptSelection => SelectedScriptEntry != null;

        private bool _isScriptDirty;
        public bool IsScriptDirty
        {
            get => _isScriptDirty;
            set
            {
                _isScriptDirty = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ScriptDirtyIndicator));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ScriptDirtyIndicator => IsScriptDirty ? " *" : "";

        private bool _isArchiveDirty;
        public bool IsArchiveDirty
        {
            get => _isArchiveDirty;
            set
            {
                _isArchiveDirty = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ArchiveDirtyIndicator));
                OnPropertyChanged(nameof(ArchiveInfoText));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ArchiveDirtyIndicator => IsArchiveDirty ? " *" : "";

        private readonly Dictionary<ArchiveEntry, byte[]> _pendingArchiveEdits = new();

        private bool _isVideoEntry;
        public bool IsVideoEntry
        {
            get => _isVideoEntry;
            set { _isVideoEntry = value; OnPropertyChanged(); }
        }

        private string _videoStatusText = "";
        public string VideoStatusText
        {
            get => _videoStatusText;
            set { _videoStatusText = value; OnPropertyChanged(); }
        }

        private string? _videoTempPath;
        public string? VideoTempPath => _videoTempPath;

        private bool _isVideoPlaying;
        public bool IsVideoPlaying
        {
            get => _isVideoPlaying;
            set { _isVideoPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(VideoPlayPauseIcon)); }
        }

        public string VideoPlayPauseIcon => IsVideoPlaying ? "⏸" : "▶";

        private double _videoPositionPercent;
        public double VideoPositionPercent
        {
            get => _videoPositionPercent;
            set { _videoPositionPercent = value; OnPropertyChanged(); }
        }

        private string _videoPositionText = "";
        public string VideoPositionText
        {
            get => _videoPositionText;
            set { _videoPositionText = value; OnPropertyChanged(); }
        }

        private ObservableCollection<ScriptEditRecord> _editHistory = new();
        public ObservableCollection<ScriptEditRecord> EditHistory
        {
            get => _editHistory;
            set { _editHistory = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasEditHistory)); }
        }

        public bool HasEditHistory => EditHistory.Count > 0 && IsScriptMode;

        private ScriptEditRecord? _selectedEditRecord;
        public ScriptEditRecord? SelectedEditRecord
        {
            get => _selectedEditRecord;
            set { _selectedEditRecord = value; OnPropertyChanged(); }
        }

        private ScriptEditRecord? _selectedEntryEditRecord;
        public ScriptEditRecord? SelectedEntryEditRecord
        {
            get => _selectedEntryEditRecord;
            set
            {
                _selectedEntryEditRecord = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedEntryEdit));
                OnPropertyChanged(nameof(SelectedEntryEditRecords));
            }
        }

        public bool HasSelectedEntryEdit => SelectedEntryEditRecord != null;

        public List<ScriptEditRecord> SelectedEntryEditRecords
        {
            get
            {
                if (SelectedScriptEntry == null) return new();
                var entry = SelectedScriptEntry;
                return EditHistory
                    .Where(r => r.EntryIndex == entry.Index && r.SeqId == entry.SeqId)
                    .ToList();
            }
        }

        private string _breadcrumbText = "";
        public string BreadcrumbText
        {
            get => _breadcrumbText;
            set { _breadcrumbText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBreadcrumb)); }
        }

        public bool HasBreadcrumb => !string.IsNullOrEmpty(BreadcrumbText);

        public string ArchiveInfoText
        {
            get
            {
                if (CurrentScript != null)
                    return $"{CurrentScript.FileName}{ScriptDirtyIndicator}  |  Format: {CurrentScript.FormatTag}  |  " +
                           $"Text blocks: {CurrentScript.Entries.Count}  |  " +
                           $"Size: {FormatSize(CurrentScript.FileSize)}" +
                           (IsArchiveDirty ? "  |  Archive: unsaved *" : "");

                if (CurrentArchive == null) return "";
                return $"{CurrentArchive.FileName}{ArchiveDirtyIndicator}  |  Format: {CurrentArchive.FormatTag}  |  " +
                       $"Entries: {CurrentArchive.Entries.Count}  |  " +
                       $"Size: {FormatSize(CurrentArchive.FileSize)}";
            }
        }

        public string FileFilter => _archiveManager.FileFilter;

        public ICommand OpenCommand { get; }
        public ICommand ExtractSelectedCommand { get; }
        public ICommand ExtractAllCommand { get; }
        public ICommand ImportFileCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand FilterCommand { get; }
        public ICommand ClearFilterCommand { get; }
        public ICommand OpenEntryCommand { get; }
        public ICommand GoBackCommand { get; }
        public ICommand ExportScriptJsonCommand { get; }
        public ICommand ImportScriptJsonCommand { get; }
        public ICommand EditScriptEntryCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ExtractScriptRawCommand { get; }
        public ICommand ExtractEntryAsJsonCommand { get; }
        public ICommand ImportEntryJsonCommand { get; }
        public ICommand UndoScriptEditCommand { get; }
        public ICommand BatchExtractCommand { get; }
        public ICommand BatchImportCommand { get; }

        public void OpenArchive()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = FileFilter,
                Title = "Open Xuse Archive / Script"
            };

            if (dlg.ShowDialog() != true) return;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext is ".cd" or ".cd3")
            {
                LoadScript(dlg.FileName);
                return;
            }

            if (ext == ".bin")
            {
                try
                {
                    var header = new byte[4];
                    using (var fs = File.OpenRead(dlg.FileName))
                        fs.Read(header, 0, 4);
                    uint sig = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
                    if (sig == 0x49524F4E)
                    {
                        LoadScript(dlg.FileName);
                        return;
                    }
                    if (sig == 0xBA010000)
                    {
                        PlayStandaloneVideo(dlg.FileName);
                        return;
                    }
                }
                catch { }
            }

            if (ext == ".dat")
            {
                try
                {
                    var header = new byte[4];
                    using (var fs = File.OpenRead(dlg.FileName))
                        fs.Read(header, 0, 4);
                    if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0xBA)
                    {
                        PlayStandaloneVideo(dlg.FileName);
                        return;
                    }
                }
                catch { }
            }

            LoadArchive(dlg.FileName);
        }

        public void LoadScript(string filePath)
        {
            try
            {
                StatusText = $"Opening script {Path.GetFileName(filePath)}...";
                var script = _scriptManager.OpenScript(filePath);
                if (script == null)
                {
                    string errorDetail = "";
                    if (_scriptManager.LastOpenErrors.Count > 0)
                        errorDetail = "\n\nDiagnostics:\n" + string.Join("\n", _scriptManager.LastOpenErrors);
                    MessageBox.Show(
                        $"Could not recognize the script format.\nThe file may be unsupported or corrupted.{errorDetail}",
                        "Open Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText = "Failed to open script.";
                    return;
                }

                CloseArchive();
                CurrentScript = script;
                ScriptEntries = new ObservableCollection<ScriptEntry>(script.Entries);
                FilteredScriptEntries = new ObservableCollection<ScriptEntry>(script.Entries);

                var rootNode = new FolderNode
                {
                    Name = script.FileName,
                    FullPath = "",
                    Icon = "📜",
                    IsExpanded = true
                };

                var textNode = new FolderNode
                {
                    Name = $"📜 Text blocks ({script.Entries.Count})",
                    FullPath = "text",
                    Icon = "📜"
                };
                rootNode.Children.Add(textNode);
                FolderTree.Clear();
                FolderTree.Add(rootNode);

                StatusText = $"Loaded {script.Entries.Count} text blocks from {script.FileName} [{script.FormatTag}]";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening script:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Error opening script.";
            }
        }

        public void LoadArchive(string filePath)
        {
            try
            {
                StatusText = $"Opening {Path.GetFileName(filePath)}...";
                var archive = _archiveManager.OpenArchive(filePath);
                if (archive == null)
                {
                    string errorDetail = "";
                    if (_archiveManager.LastOpenErrors.Count > 0)
                        errorDetail = "\n\nDiagnostics:\n" + string.Join("\n", _archiveManager.LastOpenErrors);
                    MessageBox.Show(
                        $"Could not recognize the archive format.\nThe file may be unsupported or corrupted.{errorDetail}",
                        "Open Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText = "Failed to open archive.";
                    return;
                }

                CloseArchive();
                CurrentArchive = archive;
                CurrentArchive.DisplayPath = archive.FileName;
                Entries = new ObservableCollection<ArchiveEntry>(archive.Entries);
                FilteredEntries = new ObservableCollection<ArchiveEntry>(archive.Entries);
                BuildFolderTree(archive.Entries);

                StatusText = $"Loaded {archive.Entries.Count} entries from {archive.FileName} [{archive.FormatTag}]";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening archive:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Error opening archive.";
            }
        }

        public void CloseArchive()
        {
            if (IsArchiveDirty || IsScriptDirty)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save the archive before closing?",
                    "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes)
                {
                    if (IsScriptDirty) SaveScript();
                    if (IsArchiveDirty) SaveArchive();
                }
            }

            while (_navigationStack.Count > 0)
            {
                var (arc, _, _) = _navigationStack.Pop();
                arc.Dispose();
            }

            CurrentArchive?.Dispose();
            CurrentArchive = null;
            CurrentScript = null;
            IsScriptDirty = false;
            IsArchiveDirty = false;
            _pendingArchiveEdits.Clear();
            StopAndUnloadAudio();
            StopAndUnloadVideo();
            EditHistory.Clear();
            OnPropertyChanged(nameof(HasEditHistory));
            Entries.Clear();
            FilteredEntries.Clear();
            ScriptEntries.Clear();
            FilteredScriptEntries.Clear();
            SelectedScriptEntry = null;
            FolderTree.Clear();
            PreviewImage = null;
            PreviewText = "";
            HexPreview = "";
            EntryInfoText = "";
            BreadcrumbText = "";
            SelectedEntry = null;
            StatusText = "Ready. Open an archive or script to begin.";
            OnPropertyChanged(nameof(CanGoBack));
        }

        public bool CanOpenEntry()
        {
            if (CurrentArchive == null || SelectedEntry == null) return false;
            string ext = Path.GetExtension(SelectedEntry.Name).ToLowerInvariant();
            return ext is ".arc" or ".xarc" or ".wag" or ".4ag" or ".004" or ".bin" or ".wvb" or ".gd"
                or ".cd" or ".cd3" or ".aif" or ".dat";
        }

        public void OpenSelectedEntry()
        {
            if (CurrentArchive == null || SelectedEntry == null) return;

            string ext = Path.GetExtension(SelectedEntry.Name).ToLowerInvariant();
            if (ext is ".cd" or ".cd3")
            {
                TryOpenEntryAsScript();
            }
            else if (ext == ".bin")
            {
                try
                {
                    byte[] data;
                    if (_pendingArchiveEdits.TryGetValue(SelectedEntry, out var staged))
                        data = staged;
                    else
                        data = _archiveManager.ExtractEntry(CurrentArchive, SelectedEntry);
                    if (data.Length >= 4)
                    {
                        uint sig = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                        if (sig == 0x49524F4E)
                        {
                            TryOpenEntryAsScript();
                            return;
                        }
                        if (sig == 0xBA010000)
                        {
                            TryLoadVideo(data, SelectedEntry.Name);
                            return;
                        }
                    }
                }
                catch { }
                OpenSelectedAsArchive();
            }
            else if (ext == ".dat")
            {
                try
                {
                    byte[] data;
                    if (_pendingArchiveEdits.TryGetValue(SelectedEntry, out var staged))
                        data = staged;
                    else
                        data = _archiveManager.ExtractEntry(CurrentArchive, SelectedEntry);
                    if (IsStandaloneVideo(data))
                    {
                        TryLoadVideo(data, SelectedEntry.Name);
                        return;
                    }
                }
                catch { }
                OpenSelectedAsArchive();
            }
            else
            {
                OpenSelectedAsArchive();
            }
        }

        public void OpenSelectedAsArchive()
        {
            if (CurrentArchive == null || SelectedEntry == null) return;

            try
            {
                StatusText = $"Opening nested archive {SelectedEntry.Name}...";

                var data = _archiveManager.ExtractEntry(CurrentArchive, SelectedEntry);
                string entryName = SelectedEntry.Name;
                var nested = _archiveManager.OpenArchiveFromData(data, entryName);

                if (nested == null)
                {
                    string errorDetail = "";
                    if (_archiveManager.LastOpenErrors.Count > 0)
                        errorDetail = "\n\nDiagnostics:\n" + string.Join("\n", _archiveManager.LastOpenErrors);
                    MessageBox.Show(
                        $"Could not open '{entryName}' as an archive.\nIt may not be a supported archive format.{errorDetail}",
                        "Open Nested Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText = "Failed to open nested archive.";
                    return;
                }

                string parentPath = CurrentArchive.DisplayPath;
                if (string.IsNullOrEmpty(parentPath))
                    parentPath = CurrentArchive.FileName;
                nested.DisplayPath = $"{parentPath} → {entryName}";

                _navigationStack.Push((CurrentArchive, parentPath, SelectedEntry));

                CurrentArchive = nested;
                Entries = new ObservableCollection<ArchiveEntry>(nested.Entries);
                FilteredEntries = new ObservableCollection<ArchiveEntry>(nested.Entries);
                FilterText = "";
                BuildFolderTree(nested.Entries);
                BreadcrumbText = nested.DisplayPath;

                StatusText = $"Opened nested archive: {nested.DisplayPath} [{nested.FormatTag}] ({nested.Entries.Count} entries)";
                OnPropertyChanged(nameof(CanGoBack));
                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening nested archive:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Error opening nested archive.";
            }
        }

        public void NavigateBack()
        {
            if (_navigationStack.Count == 0) return;

            CurrentArchive?.Dispose();
            CurrentArchive = null;
            CurrentScript = null;
            ScriptEntries.Clear();
            FilteredScriptEntries.Clear();
            SelectedScriptEntry = null;
            EditHistory.Clear();
            OnPropertyChanged(nameof(HasEditHistory));
            StopAndUnloadVideo();

            var (parentArchive, parentPath, _) = _navigationStack.Pop();
            CurrentArchive = parentArchive;
            Entries = new ObservableCollection<ArchiveEntry>(parentArchive.Entries);
            FilteredEntries = new ObservableCollection<ArchiveEntry>(parentArchive.Entries);
            FilterText = "";
            BuildFolderTree(parentArchive.Entries);

            if (_navigationStack.Count > 0)
                BreadcrumbText = parentArchive.DisplayPath;
            else
                BreadcrumbText = "";

            StatusText = $"Navigated back to {parentArchive.FileName} [{parentArchive.FormatTag}] ({parentArchive.Entries.Count} entries)";
            OnPropertyChanged(nameof(CanGoBack));
            CommandManager.InvalidateRequerySuggested();
        }

        public bool TryOpenEntryAsArchive()
        {
            if (CurrentArchive == null || SelectedEntry == null) return false;

            string ext = Path.GetExtension(SelectedEntry.Name).ToLowerInvariant();
            if (ext is ".cd" or ".cd3")
            {
                return TryOpenEntryAsScript();
            }

            if (CanOpenEntry())
            {
                OpenSelectedAsArchive();
                return true;
            }

            return false;
        }

        public async void ExtractSelected()
        {
            if (CurrentArchive == null || SelectedEntry == null) return;

            bool isP = IsObfuscatedPng(SelectedEntry);
            string defaultName = isP
                ? Path.ChangeExtension(SelectedEntry.Name, ".png")
                : SelectedEntry.Name;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = defaultName,
                Title = "Extract File"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                IsProgressVisible = true;
                ProgressValue = 0;
                StatusText = $"Extracting {SelectedEntry.Name}...";

                var entry = SelectedEntry;
                var archive = CurrentArchive;
                var pending = _pendingArchiveEdits;
                bool convertP = isP;
                await Task.Run(() =>
                {
                    byte[] data;
                    if (pending.TryGetValue(entry, out var staged))
                        data = staged;
                    else
                        data = _archiveManager.ExtractEntry(archive, entry);

                    if (convertP)
                        data = PToPng(data);

                    File.WriteAllBytes(dlg.FileName, data);
                });

                ProgressValue = 100;
                StatusText = $"Extracted {SelectedEntry.Name} successfully.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting file:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Extraction failed.";
            }
            finally
            {
                IsProgressVisible = false;
            }
        }

        public async void ExtractAll()
        {
            if (CurrentArchive == null) return;

            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select output folder for extraction"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            try
            {
                IsProgressVisible = true;
                ProgressValue = 0;
                int total = CurrentArchive.Entries.Count;

                var progress = new Progress<(int current, int total, string name)>(p =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    StatusText = $"Extracting ({p.current}/{p.total}): {p.name}";
                });

                var archive = CurrentArchive;
                string outputDir = dlg.SelectedPath;

                await Task.Run(() => _archiveManager.ExtractAll(archive, outputDir, progress));

                ProgressValue = 100;
                StatusText = $"Extracted all {total} entries successfully.";

                MessageBox.Show($"Successfully extracted {total} files to:\n{outputDir}",
                    "Extraction Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during extraction:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Extraction failed.";
            }
            finally
            {
                IsProgressVisible = false;
            }
        }

        public void ImportFile()
        {
            if (CurrentArchive == null || SelectedEntry == null) return;

            bool isP = IsObfuscatedPng(SelectedEntry);
            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Select replacement file for {SelectedEntry.Name}",
                Filter = isP ? "PNG Images (*.png)|*.png|All Files (*.*)|*.*" : "All Files (*.*)|*.*"
            };
            if (openDlg.ShowDialog() != true) return;

            try
            {
                byte[] newData = File.ReadAllBytes(openDlg.FileName);

                if (isP)
                    newData = PngToP(newData);

                _pendingArchiveEdits[SelectedEntry] = newData;
                SelectedEntry.Size = (uint)newData.Length;
                SelectedEntry.IsModified = true;

                IsArchiveDirty = true;
                OnPropertyChanged(nameof(ArchiveInfoText));

                UpdatePreview();

                StatusText = $"Imported {Path.GetFileName(openDlg.FileName)} → {SelectedEntry.Name} (in memory, Ctrl+S to save archive)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing file:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool IsObfuscatedPng(ArchiveEntry entry)
        {
            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext == ".p") return true;
            return entry.Type == "image" && ext != ".png" && ext != ".jpg" && ext != ".bmp" && ext != ".gif" && ext != ".tif";
        }

        private static byte[] PToPng(byte[] pData)
        {
            if (pData.Length > 4 && pData[0] == 0x4E && pData[1] == 0x47 && pData[2] == 0x0D && pData[3] == 0x0A)
            {
                var png = new byte[pData.Length + 2];
                png[0] = 0x89;
                png[1] = 0x50;
                Array.Copy(pData, 0, png, 2, pData.Length);
                return png;
            }
            return pData;
        }

        private static byte[] PngToP(byte[] pngData)
        {
            if (pngData.Length > 4 && pngData[0] == 0x89 && pngData[1] == 0x50)
            {
                var p = new byte[pngData.Length - 2];
                Array.Copy(pngData, 2, p, 0, p.Length);
                return p;
            }
            return pngData;
        }

        public async void BatchExtractSelected()
        {
            if (CurrentArchive == null || SelectedEntries.Count == 0) return;

            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select output folder for batch extraction"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string outputDir = dlg.SelectedPath;
            var entries = SelectedEntries.ToList();
            int count = entries.Count;
            int done = 0;
            int errors = 0;

            try
            {
                IsProgressVisible = true;
                ProgressValue = 0;

                var archive = CurrentArchive;
                var pending = _pendingArchiveEdits;

                await Task.Run(() =>
                {
                    foreach (var entry in entries)
                    {
                        try
                        {
                            byte[] data;
                            if (pending.TryGetValue(entry, out var staged))
                                data = staged;
                            else
                                data = _archiveManager.ExtractEntry(archive, entry);

                            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                            bool isP = entry.Type == "image" && ext != ".png" && ext != ".jpg" && ext != ".bmp" && ext != ".gif" && ext != ".tif";
                            if (ext == ".p") isP = true;

                            if (ext is ".cd" or ".cd3")
                            {
                                var script = _scriptManager.OpenScriptFromData(data, entry.Name);
                                if (script != null)
                                {
                                    string json = ScriptManager.ExportToJson(script);
                                    string jsonName = Path.ChangeExtension(entry.Name, ".json");
                                    File.WriteAllText(Path.Combine(outputDir, jsonName), json, System.Text.Encoding.UTF8);
                                }
                                else
                                {
                                    File.WriteAllBytes(Path.Combine(outputDir, entry.Name), data);
                                }
                            }
                            else if (isP)
                            {
                                data = PToPng(data);
                                string pngName = Path.ChangeExtension(entry.Name, ".png");
                                File.WriteAllBytes(Path.Combine(outputDir, pngName), data);
                            }
                            else
                            {
                                File.WriteAllBytes(Path.Combine(outputDir, entry.Name), data);
                            }
                        }
                        catch
                        {
                            errors++;
                        }

                        done++;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProgressValue = (double)done / count * 100;
                            StatusText = $"Extracting ({done}/{count})...";
                        });
                    }
                });

                StatusText = errors > 0
                    ? $"Batch extracted {done - errors}/{count} files ({errors} errors)"
                    : $"Batch extracted {count} files to {outputDir}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during batch extraction:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProgressVisible = false;
            }
        }

        public async void BatchImportSelected()
        {
            if (CurrentArchive == null || SelectedEntries.Count == 0) return;

            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder containing replacement files"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string inputDir = dlg.SelectedPath;
            var entries = SelectedEntries.ToList();
            int imported = 0;
            int skipped = 0;

            try
            {
                IsProgressVisible = true;
                ProgressValue = 0;

                var archive = CurrentArchive;
                int count = entries.Count;
                int done = 0;

                await Task.Run(() =>
                {
                    foreach (var entry in entries)
                    {
                        try
                        {
                            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                            string? matchPath = null;
                            bool isScript = ext is ".cd" or ".cd3";
                            bool isP = ext == ".p" || (entry.Type == "image" && ext != ".png" && ext != ".jpg" && ext != ".bmp" && ext != ".gif" && ext != ".tif");

                            if (isScript)
                            {
                                string jsonName = Path.ChangeExtension(entry.Name, ".json");
                                string candidate = Path.Combine(inputDir, jsonName);
                                if (File.Exists(candidate)) matchPath = candidate;
                            }
                            else if (isP)
                            {
                                string pngName = Path.ChangeExtension(entry.Name, ".png");
                                string candidate = Path.Combine(inputDir, pngName);
                                if (File.Exists(candidate)) matchPath = candidate;
                            }

                            if (matchPath == null)
                            {
                                string candidate = Path.Combine(inputDir, entry.Name);
                                if (File.Exists(candidate)) matchPath = candidate;
                            }

                            if (matchPath == null)
                            {
                                skipped++;
                            }
                            else
                            {
                                byte[] newData;

                                if (isScript && matchPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                {
                                    byte[] origData;
                                    if (_pendingArchiveEdits.TryGetValue(entry, out var staged))
                                        origData = staged;
                                    else
                                        origData = _archiveManager.ExtractEntry(archive, entry);

                                    var script = _scriptManager.OpenScriptFromData(origData, entry.Name);
                                    if (script == null) { skipped++; done++; continue; }

                                    string json = File.ReadAllText(matchPath, System.Text.Encoding.UTF8);
                                    newData = _scriptManager.ImportFromJson(script, json);
                                }
                                else if (isP && matchPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                {
                                    newData = PngToP(File.ReadAllBytes(matchPath));
                                }
                                else
                                {
                                    newData = File.ReadAllBytes(matchPath);
                                }

                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    _pendingArchiveEdits[entry] = newData;
                                    entry.Size = (uint)newData.Length;
                                    entry.IsModified = true;
                                });
                                imported++;
                            }
                        }
                        catch
                        {
                            skipped++;
                        }

                        done++;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProgressValue = (double)done / count * 100;
                            StatusText = $"Importing ({done}/{count})...";
                        });
                    }
                });

                if (imported > 0)
                {
                    IsArchiveDirty = true;
                    OnPropertyChanged(nameof(ArchiveInfoText));
                    UpdatePreview();
                }

                StatusText = $"Batch import: {imported} replaced, {skipped} skipped (Ctrl+S to save)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during batch import:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProgressVisible = false;
            }
        }

        public void ApplyFilter()
        {
            if (IsScriptMode)
            {
                if (string.IsNullOrWhiteSpace(FilterText))
                {
                    FilteredScriptEntries = new ObservableCollection<ScriptEntry>(ScriptEntries);
                }
                else
                {
                    string filter = FilterText.ToLowerInvariant();
                    FilteredScriptEntries = new ObservableCollection<ScriptEntry>(
                        ScriptEntries.Where(e =>
                            e.Text.ToLowerInvariant().Contains(filter) ||
                            e.SeqId.ToString().Contains(filter)));
                }
                StatusText = $"Showing {FilteredScriptEntries.Count} of {ScriptEntries.Count} text blocks";
                return;
            }

            if (CurrentArchive == null) return;

            var source = _selectedFolder?.Entries ?? Entries;

            if (string.IsNullOrWhiteSpace(FilterText))
            {
                FilteredEntries = new ObservableCollection<ArchiveEntry>(source);
            }
            else
            {
                string filter = FilterText.ToLowerInvariant();
                FilteredEntries = new ObservableCollection<ArchiveEntry>(
                    source.Where(e =>
                        e.Name.ToLowerInvariant().Contains(filter) ||
                        e.Type.ToLowerInvariant().Contains(filter) ||
                        e.Extension.ToLowerInvariant().Contains(filter)));
            }

            StatusText = $"Showing {FilteredEntries.Count} of {Entries.Count} entries";
        }

        private void UpdatePreview()
        {
            PreviewImage = null;
            PreviewText = "";
            HexPreview = "";
            EntryInfoText = "";
            StopAndUnloadAudio();
            StopAndUnloadVideo();

            if (CurrentArchive == null || SelectedEntry == null) return;

            var entry = SelectedEntry;
            EntryInfoText = $"Name: {entry.Name}\n" +
                           $"Type: {entry.TypeDisplay}\n" +
                           $"Size: {entry.SizeDisplay}\n" +
                           $"Offset: 0x{entry.Offset:X8}\n" +
                           $"Format: {entry.FormatTag}";

            string entryExt = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (entryExt is ".cd" or ".cd3")
            {
                EntryInfoText += "\n\n� Double-click to open as script\n" +
                                 "Right-click → \"Extract as JSON\" / \"Import JSON\"";
            }
            else if (CanOpenEntry())
            {
                EntryInfoText += "\n\n� Double-click or right-click → \"Open\"";
            }

            try
            {
                byte[] data;
                if (_pendingArchiveEdits.ContainsKey(entry))
                    data = _pendingArchiveEdits[entry];
                else
                    data = _archiveManager.ExtractEntry(CurrentArchive, entry);

                if (data.Length > 4 && data[0] == 0x4E && data[1] == 0x47 && data[2] == 0x0D && data[3] == 0x0A)
                {
                    var pngData = new byte[data.Length + 2];
                    pngData[0] = 0x89;
                    pngData[1] = 0x50;
                    Array.Copy(data, 0, pngData, 2, data.Length);
                    data = pngData;
                }

                if (IsStandaloneVideo(data))
                {
                    TryLoadVideo(data, entry.Name);
                    HexPreview = FormatHex(data, Math.Min(data.Length, 512));
                    return;
                }

                BitmapImage? img = null;
                bool isImage = entry.Type == "image" || TryLoadImage(data, out img);
                if (isImage)
                {
                    if (img == null) TryLoadImage(data, out img);

                    PreviewImage = img;
                    if (img != null)
                    {
                        EntryInfoText += $"\nDimensions: {(int)img.Width}×{(int)img.Height}";
                    }
                }

                if (entry.Type == "audio")
                {
                    TryLoadAudio(data, entry.Name);
                }

                if (PreviewImage == null && data.Length < 1024 * 1024 && IsLikelyText(data))
                {
                    PreviewText = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 8192));
                }

                HexPreview = FormatHex(data, Math.Min(data.Length, 512));
            }
            catch (Exception ex)
            {
                EntryInfoText += $"\n\nPreview error: {ex.Message}";
            }
        }

        private void TryLoadAudio(byte[] data, string nameHint)
        {
            try
            {
                _audioPlayer.Load(data, nameHint);
                IsAudioEntry = true;
                AudioStatusText = _audioPlayer.FormatDescription;
                var dur = _audioPlayer.TotalDuration;
                AudioPositionText = $"0:00 / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                AudioPositionPercent = 0;
                EntryInfoText += $"\n{_audioPlayer.FormatDescription}\nDuration: {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
            }
            catch (Exception ex)
            {
                IsAudioEntry = false;
                AudioStatusText = $"Cannot play: {ex.Message}";
            }
        }

        private void StopAndUnloadAudio()
        {
            _audioPlayer.Unload();
            IsAudioEntry = false;
            IsAudioPlaying = false;
            AudioStatusText = "";
            AudioPositionText = "0:00 / 0:00";
            AudioPositionPercent = 0;
        }

        private void AudioPlayPause()
        {
            _audioPlayer.TogglePlayPause();
        }

        private void AudioStop()
        {
            _audioPlayer.Stop();
        }

        public void SeekAudio(double percent)
        {
            _audioPlayer.PositionPercent = percent;
        }

        private static bool TryLoadImage(byte[] data, out BitmapImage? image)
        {
            image = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLikelyText(byte[] data)
        {
            int textChars = 0;
            int check = Math.Min(data.Length, 512);
            for (int i = 0; i < check; i++)
            {
                byte b = data[i];
                if (b == 0) return false;
                if ((b >= 32 && b < 127) || b == '\n' || b == '\r' || b == '\t' || b >= 128)
                    textChars++;
            }
            return check > 0 && (double)textChars / check > 0.8;
        }

        private static string FormatHex(byte[] data, int length)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < length; i += 16)
            {
                sb.Append($"{i:X8}  ");
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < length)
                        sb.Append($"{data[i + j]:X2} ");
                    else
                        sb.Append("   ");
                    if (j == 7) sb.Append(' ');
                }
                sb.Append(" |");
                for (int j = 0; j < 16 && i + j < length; j++)
                {
                    byte b = data[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine("|");
            }
            return sb.ToString();
        }

        private void UpdateScriptPreview()
        {
            PreviewImage = null;
            HexPreview = "";

            if (SelectedScriptEntry == null)
            {
                PreviewText = "";
                EntryInfoText = "";
                SelectedEntryEditRecord = null;
                return;
            }

            var entry = SelectedScriptEntry;
            EntryInfoText = $"Index: {entry.Index}\n" +
                           $"Seq ID: {entry.SeqId}\n" +
                           $"Voice: {entry.Voice}\n" +
                           $"Lines: {entry.Text.Split('\n').Length}";

            PreviewText = entry.Text;

            SelectedEntryEditRecord = EditHistory.FirstOrDefault(
                r => r.EntryIndex == entry.Index && r.SeqId == entry.SeqId);
        }

        public void ExportScriptJson()
        {
            if (CurrentScript == null) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.ChangeExtension(CurrentScript.FileName, ".json"),
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Export Script as JSON"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                string json = ScriptManager.ExportToJson(CurrentScript);
                File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                StatusText = $"Exported {CurrentScript.Entries.Count} text blocks to {Path.GetFileName(dlg.FileName)}";
                MessageBox.Show($"Exported {CurrentScript.Entries.Count} text blocks to:\n{dlg.FileName}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting script:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ImportScriptJson()
        {
            if (CurrentScript == null) return;

            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Import Modified Script JSON"
            };

            if (openDlg.ShowDialog() != true) return;

            try
            {
                string json = File.ReadAllText(openDlg.FileName, System.Text.Encoding.UTF8);
                var importedEntries = ScriptManager.ParseJsonEntries(CurrentScript, json);

                int updated = 0;
                foreach (var imported in importedEntries)
                {
                    var existing = CurrentScript.Entries.Find(e =>
                        e.Index == imported.Index &&
                        (imported.SeqId == 0 || e.SeqId == imported.SeqId));
                    if (existing == null) continue;

                    bool changed = false;
                    string oldText = existing.Text;
                    int oldVoice = existing.Voice;

                    if (existing.Text != imported.Text)
                    {
                        existing.Text = imported.Text;
                        changed = true;
                    }
                    if (imported.Voice != 0 && existing.Voice != imported.Voice)
                    {
                        existing.Voice = imported.Voice;
                        changed = true;
                    }

                    if (changed)
                    {
                        EditHistory.Insert(0, new ScriptEditRecord
                        {
                            EntryIndex = existing.Index,
                            SeqId = existing.SeqId,
                            OldText = oldText,
                            NewText = existing.Text,
                            OldVoice = oldVoice,
                            NewVoice = existing.Voice,
                            Timestamp = DateTime.Now
                        });
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    IsScriptDirty = true;
                    OnPropertyChanged(nameof(HasEditHistory));

                    FilteredScriptEntries = new ObservableCollection<ScriptEntry>(
                        string.IsNullOrWhiteSpace(FilterText)
                            ? ScriptEntries
                            : ScriptEntries.Where(e =>
                                e.Text.ToLowerInvariant().Contains(FilterText.ToLowerInvariant()) ||
                                e.SeqId.ToString().Contains(FilterText)));
                }

                StatusText = $"Imported JSON: {updated} entries updated (Ctrl+S to save)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing script JSON:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void EditSelectedScriptEntry()
        {
            if (CurrentScript == null || SelectedScriptEntry == null) return;

            string oldText = SelectedScriptEntry.Text;
            int oldVoice = SelectedScriptEntry.Voice;

            var dlg = new EditScriptEntryDialog(SelectedScriptEntry)
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true && dlg.WasModified)
            {
                var edited = dlg.Entry;
                SelectedScriptEntry.Voice = edited.Voice;
                SelectedScriptEntry.Text = edited.Text;

                int masterIdx = CurrentScript.Entries.FindIndex(e => e.Index == edited.Index && e.SeqId == edited.SeqId);
                if (masterIdx >= 0)
                {
                    CurrentScript.Entries[masterIdx].Voice = edited.Voice;
                    CurrentScript.Entries[masterIdx].Text = edited.Text;
                }

                EditHistory.Insert(0, new ScriptEditRecord
                {
                    EntryIndex = SelectedScriptEntry.Index,
                    SeqId = SelectedScriptEntry.SeqId,
                    OldText = oldText,
                    NewText = edited.Text,
                    OldVoice = oldVoice,
                    NewVoice = edited.Voice,
                    Timestamp = DateTime.Now
                });
                OnPropertyChanged(nameof(HasEditHistory));

                IsScriptDirty = true;
                UpdateScriptPreview();
                OnPropertyChanged(nameof(ArchiveInfoText));
                StatusText = $"Edited entry #{SelectedScriptEntry.Index} (Seq {SelectedScriptEntry.SeqId})";
            }
        }

        public void UndoScriptEdit()
        {
            if (CurrentScript == null || EditHistory.Count == 0) return;

            var target = SelectedEditRecord ?? EditHistory[0];
            int targetIdx = EditHistory.IndexOf(target);
            if (targetIdx < 0) return;

            for (int i = 0; i <= targetIdx; i++)
                ApplyUndo(EditHistory[i]);

            for (int i = targetIdx; i >= 0; i--)
                EditHistory.RemoveAt(i);

            OnPropertyChanged(nameof(HasEditHistory));
            SelectedEditRecord = null;

            if (EditHistory.Count == 0)
                IsScriptDirty = false;

            UpdateScriptPreview();
            OnPropertyChanged(nameof(ArchiveInfoText));
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Undid edit on entry #{target.EntryIndex} (Seq {target.SeqId})";
        }

        public void UndoSingleRecord(ScriptEditRecord record)
        {
            if (CurrentScript == null) return;

            int recordIdx = EditHistory.IndexOf(record);
            if (recordIdx < 0) return;

            for (int i = 0; i <= recordIdx; i++)
                ApplyUndo(EditHistory[i]);

            for (int i = recordIdx; i >= 0; i--)
                EditHistory.RemoveAt(i);

            OnPropertyChanged(nameof(HasEditHistory));
            SelectedEditRecord = null;

            if (EditHistory.Count == 0)
                IsScriptDirty = false;

            UpdateScriptPreview();
            OnPropertyChanged(nameof(ArchiveInfoText));
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Rolled back to before edit on entry #{record.EntryIndex}";
        }

        private void ApplyUndo(ScriptEditRecord record)
        {
            var displayEntry = FilteredScriptEntries.FirstOrDefault(e => e.Index == record.EntryIndex && e.SeqId == record.SeqId);
            if (displayEntry != null)
            {
                displayEntry.Text = record.OldText;
                displayEntry.Voice = record.OldVoice;
            }

            var scriptEntry = ScriptEntries.FirstOrDefault(e => e.Index == record.EntryIndex && e.SeqId == record.SeqId);
            if (scriptEntry != null && scriptEntry != displayEntry)
            {
                scriptEntry.Text = record.OldText;
                scriptEntry.Voice = record.OldVoice;
            }

            if (CurrentScript != null)
            {
                int masterIdx = CurrentScript.Entries.FindIndex(e => e.Index == record.EntryIndex && e.SeqId == record.SeqId);
                if (masterIdx >= 0)
                {
                    CurrentScript.Entries[masterIdx].Text = record.OldText;
                    CurrentScript.Entries[masterIdx].Voice = record.OldVoice;
                }
            }
        }

        public void RollbackToRecord(ScriptEditRecord record)
        {
            if (CurrentScript == null) return;

            ApplyUndo(record);

            var entryRecords = EditHistory
                .Where(r => r.EntryIndex == record.EntryIndex && r.SeqId == record.SeqId)
                .ToList();

            int clickedIdx = entryRecords.IndexOf(record);

            for (int i = clickedIdx; i >= 0; i--)
                EditHistory.Remove(entryRecords[i]);

            OnPropertyChanged(nameof(HasEditHistory));
            SelectedEditRecord = null;

            if (EditHistory.Count == 0)
                IsScriptDirty = false;

            UpdateScriptPreview();
            OnPropertyChanged(nameof(ArchiveInfoText));
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"Rolled back entry #{record.EntryIndex} (Seq {record.SeqId})";
        }

        public event Action<Uri>? VideoSourceReady;

        private void TryLoadVideo(byte[] data, string nameHint)
        {
            try
            {
                string ext = ".mpg";
                if (nameHint.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    ext = ".mpg";

                _videoTempPath = Path.Combine(Path.GetTempPath(), $"xuse_preview_{Guid.NewGuid():N}{ext}");
                File.WriteAllBytes(_videoTempPath, data);

                IsVideoEntry = true;
                IsVideoPlaying = false;
                VideoPositionPercent = 0;
                VideoPositionText = "0:00 / 0:00";
                VideoStatusText = $"{nameHint}  ({data.Length:N0} bytes)";

                VideoSourceReady?.Invoke(new Uri(_videoTempPath));
            }
            catch
            {
                IsVideoEntry = false;
            }
        }

        private void StopAndUnloadVideo()
        {
            IsVideoPlaying = false;
            IsVideoEntry = false;
            VideoStatusText = "";
            VideoPositionPercent = 0;
            VideoPositionText = "";

            VideoSourceReady?.Invoke(null!);

            if (_videoTempPath != null && File.Exists(_videoTempPath))
            {
                try
                {
                    if (_videoTempPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                        File.Delete(_videoTempPath);
                }
                catch { }
            }
            _videoTempPath = null;
        }

        public void OpenVideoInExternalPlayer()
        {
            if (_videoTempPath == null || !File.Exists(_videoTempPath)) return;

            try
            {
                Process.Start(new ProcessStartInfo(_videoTempPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open video player:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool IsStandaloneVideo(byte[] data)
        {
            return data.Length > 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0xBA;
        }

        public void PlayStandaloneVideo(string filePath)
        {
            try
            {
                _videoTempPath = filePath;
                IsVideoEntry = true;
                IsVideoPlaying = false;
                VideoPositionPercent = 0;
                VideoPositionText = "0:00 / 0:00";
                VideoStatusText = $"{Path.GetFileName(filePath)}  ({new FileInfo(filePath).Length:N0} bytes)";

                VideoSourceReady?.Invoke(new Uri(filePath));
            }
            catch { }
        }

        public void Save()
        {
            if (IsScriptMode && CurrentScript != null && IsScriptDirty)
            {
                SaveScript();
            }
            else if (!IsScriptMode && IsArchiveDirty)
            {
                SaveArchive();
            }
        }

        private bool CanSave()
        {
            if (IsScriptMode) return CurrentScript != null && IsScriptDirty;
            return IsArchiveDirty;
        }

        public void SaveScript()
        {
            if (CurrentScript == null) return;

            try
            {
                byte[] rebuilt = _scriptManager.Rebuild(CurrentScript, CurrentScript.Entries);

                if (CurrentScript.IsInsideArchive)
                {
                    var parentEntry = CurrentScript.ParentEntry!;

                    CurrentScript.RawData = rebuilt;
                    CurrentScript.FileSize = rebuilt.Length;
                    parentEntry.Size = (uint)rebuilt.Length;

                    _pendingArchiveEdits[parentEntry] = rebuilt;
                    parentEntry.IsModified = true;

                    IsScriptDirty = false;
                    IsArchiveDirty = true;
                    OnPropertyChanged(nameof(ArchiveInfoText));

                    StatusText = $"Script updated in memory: {CurrentScript.FileName} ({rebuilt.Length:N0} bytes). Go back and Ctrl+S to save archive.";
                }
                else
                {
                    if (string.IsNullOrEmpty(CurrentScript.FilePath) || !File.Exists(CurrentScript.FilePath))
                    {
                        var dlg = new Microsoft.Win32.SaveFileDialog
                        {
                            FileName = CurrentScript.FileName,
                            Filter = "Script Files (*.cd;*.cd3)|*.cd;*.cd3|All Files (*.*)|*.*",
                            Title = "Save Script"
                        };
                        if (dlg.ShowDialog() != true) return;
                        CurrentScript.FilePath = dlg.FileName;
                    }

                    File.WriteAllBytes(CurrentScript.FilePath, rebuilt);

                    CurrentScript.RawData = rebuilt;
                    CurrentScript.FileSize = rebuilt.Length;
                    IsScriptDirty = false;
                    OnPropertyChanged(nameof(ArchiveInfoText));

                    StatusText = $"Saved {CurrentScript.FileName} ({rebuilt.Length:N0} bytes)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving script:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void SaveArchive()
        {
            if (!IsArchiveDirty || _pendingArchiveEdits.Count == 0) return;

            ArchiveFile? rootArchive = null;
            var nestedChain = new List<(ArchiveFile archive, ArchiveEntry entry)>();

            var stackItems = _navigationStack.ToArray();
            for (int si = stackItems.Length - 1; si >= 0; si--)
            {
                var (arc, _, nestedEntry) = stackItems[si];
                if (rootArchive == null && !string.IsNullOrEmpty(arc.FilePath) && File.Exists(arc.FilePath))
                    rootArchive = arc;
                if (nestedEntry != null)
                    nestedChain.Add((arc, nestedEntry));
            }

            if (rootArchive == null)
            {
                ArchiveFile? archive = CurrentScript?.ParentArchive;
                if (archive == null && CurrentArchive != null && !string.IsNullOrEmpty(CurrentArchive.FilePath))
                    archive = CurrentArchive;
                if (archive != null && !string.IsNullOrEmpty(archive.FilePath) && File.Exists(archive.FilePath))
                    rootArchive = archive;
            }

            if (rootArchive == null)
            {
                MessageBox.Show("Cannot save: no parent archive found.", "Save Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(rootArchive.FilePath) || !File.Exists(rootArchive.FilePath))
            {
                MessageBox.Show("Cannot save: the archive file is not available on disk.",
                    "Save Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                bool isNestedEdit = CurrentArchive != null && CurrentArchive.IsNested && nestedChain.Count > 0;

                if (isNestedEdit)
                {
                    SaveNestedArchive(rootArchive, nestedChain);
                }
                else
                {
                    SaveDirectEdits(rootArchive);
                }

                foreach (var entry in _pendingArchiveEdits.Keys)
                    entry.IsModified = false;

                _pendingArchiveEdits.Clear();
                IsArchiveDirty = false;
                OnPropertyChanged(nameof(ArchiveInfoText));

                StatusText = $"Archive saved: {rootArchive.FileName} ({rootArchive.FileSize:N0} bytes)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving archive:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveDirectEdits(ArchiveFile archive)
        {
            string archivePath = archive.FilePath;
            string tempArchive = Path.Combine(Path.GetTempPath(), $"xuse_arc_{Guid.NewGuid():N}.tmp");

            foreach (var (entry, newData) in _pendingArchiveEdits)
            {
                try
                {
                    _archiveManager.ImportFileData(archive, entry, newData, tempArchive);

                    File.Copy(tempArchive, archivePath, overwrite: true);

                    var reloaded = _archiveManager.OpenArchive(archivePath);
                    if (reloaded != null)
                    {
                        foreach (var re in reloaded.Entries)
                        {
                            var match = archive.Entries.FirstOrDefault(e => e.Name == re.Name);
                            if (match != null)
                            {
                                match.Offset = re.Offset;
                                match.Size = re.Size;
                            }
                        }
                        archive.FileSize = reloaded.FileSize;
                        reloaded.Dispose();
                    }
                }
                finally
                {
                    if (File.Exists(tempArchive)) File.Delete(tempArchive);
                }
            }
        }

        private void SaveNestedArchive(ArchiveFile rootArchive, List<(ArchiveFile archive, ArchiveEntry entry)> nestedChain)
        {
            var innermostEntry = nestedChain[nestedChain.Count - 1].entry;
            var innermostParent = nestedChain[nestedChain.Count - 1].archive;

            byte[] innerData = _archiveManager.ExtractEntry(innermostParent, innermostEntry);

            byte[] rebuiltData = RebuildArchiveInMemory(innerData, innermostEntry.Name, _pendingArchiveEdits);

            byte[] currentData = rebuiltData;
            for (int i = nestedChain.Count - 1; i >= 0; i--)
            {
                var (parentArchive, parentEntry) = nestedChain[i];

                if (i == 0)
                {
                    string archivePath = parentArchive.FilePath;
                    string tempArchive = Path.Combine(Path.GetTempPath(), $"xuse_arc_{Guid.NewGuid():N}.tmp");

                    try
                    {
                        _archiveManager.ImportFileData(parentArchive, parentEntry, currentData, tempArchive);
                        File.Copy(tempArchive, archivePath, overwrite: true);

                        var reloaded = _archiveManager.OpenArchive(archivePath);
                        if (reloaded != null)
                        {
                            foreach (var re in reloaded.Entries)
                            {
                                var match = parentArchive.Entries.FirstOrDefault(e => e.Name == re.Name);
                                if (match != null)
                                {
                                    match.Offset = re.Offset;
                                    match.Size = re.Size;
                                }
                            }
                            parentArchive.FileSize = reloaded.FileSize;
                            reloaded.Dispose();
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempArchive)) File.Delete(tempArchive);
                    }
                }
                else
                {
                    var intermediateParent = nestedChain[i - 1].archive;
                    var intermediateEntry = nestedChain[i - 1].entry;
                    byte[] intermediateData = _archiveManager.ExtractEntry(intermediateParent, intermediateEntry);

                    var edits = new Dictionary<ArchiveEntry, byte[]> { { parentEntry, currentData } };
                    currentData = RebuildArchiveInMemory(intermediateData, intermediateEntry.Name, edits);
                }
            }

            if (CurrentArchive != null && CurrentArchive.IsNested)
            {
                try
                {
                    var freshInnerData = _archiveManager.ExtractEntry(innermostParent, innermostEntry);
                    var freshMs = new MemoryStream(freshInnerData);
                    var freshArchive = _archiveManager.OpenArchiveFromData(freshInnerData, innermostEntry.Name);
                    if (freshArchive != null)
                    {
                        foreach (var fe in freshArchive.Entries)
                        {
                            var match = CurrentArchive.Entries.FirstOrDefault(e => e.Name == fe.Name);
                            if (match != null)
                            {
                                match.Offset = fe.Offset;
                                match.Size = fe.Size;
                            }
                        }
                        CurrentArchive.DataStream?.Dispose();
                        CurrentArchive.DataStream = freshArchive.DataStream;
                        freshArchive.DataStream = null;
                        CurrentArchive.FileSize = freshArchive.FileSize;
                        CurrentArchive.DecryptionKey = freshArchive.DecryptionKey;
                        freshArchive.Dispose();
                    }
                }
                catch {  }
            }
        }

        private byte[] RebuildArchiveInMemory(byte[] archiveData, string virtualName, Dictionary<ArchiveEntry, byte[]> edits)
        {
            using var inputMs = new MemoryStream(archiveData);
            var tempArchive = _archiveManager.OpenArchiveFromData(archiveData, virtualName);
            if (tempArchive == null)
                throw new InvalidOperationException($"Could not re-open nested archive '{virtualName}' for rebuild.");

            try
            {
                using var outputMs = new MemoryStream();

                var reader = _archiveManager.Readers.FirstOrDefault(r => r.Tag == tempArchive.FormatTag);
                if (reader == null || !reader.CanImport)
                    throw new NotSupportedException($"Format '{tempArchive.FormatTag}' does not support import.");

                byte[] currentData = archiveData;

                foreach (var (editEntry, newData) in edits)
                {
                    var matchEntry = tempArchive.Entries.FirstOrDefault(e => e.Name == editEntry.Name);
                    if (matchEntry == null)
                        throw new InvalidOperationException($"Could not find entry '{editEntry.Name}' in nested archive.");

                    using var currentInput = new MemoryStream(currentData);
                    using var currentOutput = new MemoryStream();
                    reader.ImportEntry(tempArchive, matchEntry, newData, currentInput, currentOutput);
                    currentData = currentOutput.ToArray();

                    tempArchive.Dispose();
                    tempArchive = _archiveManager.OpenArchiveFromData(currentData, virtualName);
                    if (tempArchive == null)
                        throw new InvalidOperationException($"Failed to reopen nested archive after edit.");
                }

                return currentData;
            }
            finally
            {
                tempArchive?.Dispose();
            }
        }

        public void ExtractScriptRaw()
        {
            if (CurrentScript == null) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = CurrentScript.FileName,
                Filter = "Script Files (*.cd;*.cd3)|*.cd;*.cd3|All Files (*.*)|*.*",
                Title = "Extract Script File"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                byte[] data = IsScriptDirty
                    ? _scriptManager.Rebuild(CurrentScript, CurrentScript.Entries)
                    : CurrentScript.RawData;
                File.WriteAllBytes(dlg.FileName, data);
                StatusText = $"Extracted {CurrentScript.FileName} ({data.Length:N0} bytes)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting script:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool CanExtractEntryAsScript()
        {
            if (CurrentArchive == null || SelectedEntry == null) return false;
            string ext = Path.GetExtension(SelectedEntry.Name).ToLowerInvariant();
            return ext is ".cd" or ".cd3";
        }

        public void ExtractEntryAsJson()
        {
            if (CurrentArchive == null || SelectedEntry == null) return;

            try
            {
                var data = _archiveManager.ExtractEntry(CurrentArchive, SelectedEntry);
                var script = _scriptManager.OpenScriptFromData(data, SelectedEntry.Name);
                if (script == null)
                {
                    MessageBox.Show($"Could not parse '{SelectedEntry.Name}' as a script file.",
                        "Parse Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = Path.ChangeExtension(SelectedEntry.Name, ".json"),
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    Title = "Export Script Entry as JSON"
                };

                if (dlg.ShowDialog() != true) return;

                string json = ScriptManager.ExportToJson(script);
                File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                StatusText = $"Exported {script.Entries.Count} text blocks from {SelectedEntry.Name} to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting script as JSON:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ImportEntryJson()
        {
            if (CurrentArchive == null || SelectedEntry == null) return;

            try
            {
                byte[] data;
                if (_pendingArchiveEdits.ContainsKey(SelectedEntry))
                    data = _pendingArchiveEdits[SelectedEntry];
                else
                    data = _archiveManager.ExtractEntry(CurrentArchive, SelectedEntry);

                var script = _scriptManager.OpenScriptFromData(data, SelectedEntry.Name);
                if (script == null)
                {
                    MessageBox.Show($"Could not parse '{SelectedEntry.Name}' as a script file.",
                        "Parse Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var openDlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                    Title = $"Select JSON to import into {SelectedEntry.Name}"
                };
                if (openDlg.ShowDialog() != true) return;

                string json = File.ReadAllText(openDlg.FileName, System.Text.Encoding.UTF8);
                byte[] rebuilt = _scriptManager.ImportFromJson(script, json);

                _pendingArchiveEdits[SelectedEntry] = rebuilt;
                SelectedEntry.Size = (uint)rebuilt.Length;
                SelectedEntry.IsModified = true;

                IsArchiveDirty = true;
                OnPropertyChanged(nameof(ArchiveInfoText));

                StatusText = $"Imported JSON into {SelectedEntry.Name} (in memory, Ctrl+S to save archive)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing JSON into archive entry:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public bool TryOpenEntryAsScript()
        {
            if (CurrentArchive == null || SelectedEntry == null) return false;

            string ext = Path.GetExtension(SelectedEntry.Name).ToLowerInvariant();
            if (ext is not ".cd" and not ".cd3" and not ".bin") return false;

            try
            {
                var parentArchive = CurrentArchive;
                var parentEntry = SelectedEntry;

                var data = _archiveManager.ExtractEntry(parentArchive, parentEntry);
                var script = _scriptManager.OpenScriptFromData(data, parentEntry.Name);
                if (script == null) return false;

                script.ParentArchive = parentArchive;
                script.ParentEntry = parentEntry;

                string parentPath = parentArchive.DisplayPath;
                if (string.IsNullOrEmpty(parentPath))
                    parentPath = parentArchive.FileName;
                _navigationStack.Push((parentArchive, parentPath, parentEntry));

                CurrentArchive = null;
                CurrentScript = script;
                ScriptEntries = new ObservableCollection<ScriptEntry>(script.Entries);
                FilteredScriptEntries = new ObservableCollection<ScriptEntry>(script.Entries);
                Entries.Clear();
                FilteredEntries.Clear();
                SelectedEntry = null;
                FilterText = "";

                BreadcrumbText = $"{parentPath} → {script.FileName}";

                var rootNode = new FolderNode
                {
                    Name = script.FileName,
                    FullPath = "",
                    Icon = "📜",
                    IsExpanded = true
                };
                FolderTree.Clear();
                FolderTree.Add(rootNode);

                StatusText = $"Opened script: {script.FileName} [{script.FormatTag}] ({script.Entries.Count} text blocks)";
                OnPropertyChanged(nameof(CanGoBack));
                CommandManager.InvalidateRequerySuggested();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void BuildFolderTree(List<ArchiveEntry> entries)
        {
            FolderTree.Clear();

            var contentNode = new FolderNode
            {
                Name = CurrentArchive?.FileName ?? "Archive",
                FullPath = "",
                Icon = "📦",
                IsExpanded = true
            };

            var byType = entries.GroupBy(e => e.Type).OrderBy(g => g.Key);
            foreach (var group in byType)
            {
                var typeNode = new FolderNode
                {
                    Name = $"{GetTypeIcon(group.Key)} {group.Key} ({group.Count()})",
                    FullPath = group.Key,
                    Icon = GetTypeIcon(group.Key)
                };

                foreach (var entry in group)
                {
                    typeNode.Entries.Add(entry);
                    contentNode.Entries.Add(entry);
                }

                contentNode.Children.Add(typeNode);
            }

            if (_navigationStack.Count > 0)
            {
                var parentNames = _navigationStack
                    .Select(s => s.archive.FileName)
                    .Reverse()
                    .ToList();

                FolderNode? outerRoot = null;
                FolderNode? current = null;
                foreach (var parentName in parentNames)
                {
                    var parentNode = new FolderNode
                    {
                        Name = parentName,
                        FullPath = parentName,
                        Icon = "📦",
                        IsExpanded = true
                    };

                    if (outerRoot == null)
                        outerRoot = parentNode;
                    else
                        current!.Children.Add(parentNode);

                    current = parentNode;
                }

                current!.Children.Add(contentNode);
                FolderTree.Add(outerRoot!);
            }
            else
            {
                FolderTree.Add(contentNode);
            }
        }

        private static string GetTypeIcon(string type) => type switch
        {
            "image" => "🖼",
            "audio" => "🔊",
            "script" => "📜",
            "video" => "🎬",
            "archive" => "📦",
            _ => "📄"
        };

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

        public void HandleFileDrop(string[] files)
        {
            if (files.Length > 0)
            {
                string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                if (ext is ".cd" or ".cd3")
                    LoadScript(files[0]);
                else
                    LoadArchive(files[0]);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
