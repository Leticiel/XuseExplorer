using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using XuseExplorer.Models;
using XuseExplorer.ViewModels;

namespace XuseExplorer
{
    public partial class MainWindow : Window
    {
        private MainViewModel ViewModel => (MainViewModel)DataContext;

        private GridViewColumnHeader? _lastSortHeader;
        private ListSortDirection _lastSortDirection = ListSortDirection.Ascending;
        private readonly DispatcherTimer _videoTimer;

        private LibVLC? _libVlc;
        private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;

        public MainWindow()
        {
            InitializeComponent();
            FileListView.SizeChanged += FileListView_SizeChanged;

            _videoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _videoTimer.Tick += VideoTimer_Tick;

            ViewModel.VideoSourceReady += OnVideoSourceReady;
        }

        private void FileListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (FileListView.View is not GridView gridView) return;
            if (gridView.Columns.Count < 5) return;

            double otherWidths = 0;
            for (int i = 0; i < gridView.Columns.Count; i++)
            {
                if (i != 1)
                    otherWidths += gridView.Columns[i].ActualWidth;
            }

            double available = FileListView.ActualWidth - otherWidths - SystemParameters.VerticalScrollBarWidth - 10;
            if (available > 100)
                gridView.Columns[1].Width = available;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                ViewModel.HandleFileDrop(files);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderNode node)
            {
                ViewModel.SelectedFolder = node;
            }
        }

        private void FilterBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ViewModel.ApplyFilter();
            }
        }

        private void ListView_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel.SelectedEntry != null)
            {
                if (ViewModel.CanOpenEntry())
                {
                    ViewModel.OpenSelectedEntry();
                }
                else
                {
                    ViewModel.ExtractSelected();
                }
            }
        }

        private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView lv)
            {
                ViewModel.SelectedEntries = lv.SelectedItems.Cast<ArchiveEntry>().ToList();
            }
        }

        private void ScriptListView_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel.SelectedScriptEntry != null)
            {
                ViewModel.EditSelectedScriptEntry();
            }
        }

        private void HistoryListBox_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem is ScriptEditRecord record)
            {
                ViewModel.RollbackToRecord(record);
            }
        }

        private void EnsureLibVlc()
        {
            if (_libVlc == null)
            {
                LibVLCSharp.Shared.Core.Initialize();
                _libVlc = new LibVLC("--no-video-title-show");
            }
        }

        private void OnVideoSourceReady(Uri? uri)
        {
            if (uri == null)
            {
                StopVlcPlayer();
                return;
            }

            EnsureLibVlc();

            StopVlcPlayer();

            _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVlc!);
            VlcVideoView.MediaPlayer = _vlcPlayer;

            var media = new Media(_libVlc!, uri);
            _vlcPlayer.Media = media;

            _vlcPlayer.LengthChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var dur = TimeSpan.FromMilliseconds(e.Length);
                    ViewModel.VideoPositionText = $"0:00 / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                    ViewModel.VideoStatusText += $"  |  {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                });
            };

            _vlcPlayer.EndReached += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _videoTimer.Stop();
                    ViewModel.IsVideoPlaying = false;
                    ViewModel.VideoPositionPercent = 100;
                });
            };
        }

        private void StopVlcPlayer()
        {
            _videoTimer.Stop();
            if (_vlcPlayer != null)
            {
                if (_vlcPlayer.IsPlaying)
                    _vlcPlayer.Stop();
                VlcVideoView.MediaPlayer = null;
                _vlcPlayer.Dispose();
                _vlcPlayer = null;
            }
        }

        private void VideoPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_vlcPlayer == null) return;

            if (ViewModel.IsVideoPlaying)
            {
                _vlcPlayer.Pause();
                _videoTimer.Stop();
                ViewModel.IsVideoPlaying = false;
            }
            else
            {
                _vlcPlayer.Play();
                _videoTimer.Start();
                ViewModel.IsVideoPlaying = true;
            }
        }

        private void VideoStop_Click(object sender, RoutedEventArgs e)
        {
            if (_vlcPlayer == null) return;

            var player = _vlcPlayer;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                player.Stop();
                Dispatcher.BeginInvoke(() =>
                {
                    _videoTimer.Stop();
                    ViewModel.IsVideoPlaying = false;
                    ViewModel.VideoPositionPercent = 0;
                    if (_vlcPlayer != null && _vlcPlayer.Length > 0)
                    {
                        var dur = TimeSpan.FromMilliseconds(_vlcPlayer.Length);
                        ViewModel.VideoPositionText = $"0:00 / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
                    }
                });
            });
        }

        private void VideoTimer_Tick(object? sender, EventArgs e)
        {
            if (_vlcPlayer == null || _vlcPlayer.Length <= 0) return;

            var posMs = _vlcPlayer.Time;
            var durMs = _vlcPlayer.Length;
            var pos = TimeSpan.FromMilliseconds(posMs);
            var dur = TimeSpan.FromMilliseconds(durMs);

            if (durMs > 0)
                ViewModel.VideoPositionPercent = (double)posMs / durMs * 100;

            ViewModel.VideoPositionText = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2} / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
        }

        private bool _isVideoSeeking;

        private void VideoSeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isVideoSeeking = true;
        }

        private void VideoSeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isVideoSeeking && sender is Slider slider && _vlcPlayer != null && _vlcPlayer.Length > 0)
            {
                _vlcPlayer.Position = (float)(slider.Value / 100.0);
            }
            _isVideoSeeking = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            _videoTimer.Stop();
            if (_vlcPlayer != null)
            {
                _vlcPlayer.Stop();
                _vlcPlayer.Dispose();
                _vlcPlayer = null;
            }
            _libVlc?.Dispose();
            _libVlc = null;
            base.OnClosed(e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaxRestore_Click(sender, e);
            }
            else
            {
                DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaxRestore_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Maximized)
            {
                BorderThickness = new Thickness(8);
                MaxRestoreBtn.Content = "❐";
            }
            else
            {
                BorderThickness = new Thickness(0);
                MaxRestoreBtn.Content = "☐";
            }
        }

        private void VideoOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenVideoInExternalPlayer();
        }

        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header) return;
            if (header.Role == GridViewColumnHeaderRole.Padding) return;

            string? sortBy = header.Tag as string;
            if (string.IsNullOrEmpty(sortBy)) return;

            ListSortDirection direction;
            if (header == _lastSortHeader)
                direction = _lastSortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            else
                direction = ListSortDirection.Ascending;

            if (_lastSortHeader != null)
            {
                string oldText = _lastSortHeader.Content?.ToString() ?? "";
                _lastSortHeader.Content = oldText.TrimEnd(' ', '▲', '▼');
            }

            var listView = ViewModel.IsScriptMode ? ScriptListView : FileListView;
            var view = CollectionViewSource.GetDefaultView(listView.ItemsSource);
            if (view == null) return;

            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(sortBy, direction));
            view.Refresh();

            string arrow = direction == ListSortDirection.Ascending ? " ▲" : " ▼";
            string headerText = header.Content?.ToString()?.TrimEnd(' ', '▲', '▼') ?? sortBy;
            header.Content = headerText + arrow;

            _lastSortHeader = header;
            _lastSortDirection = direction;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Xuse Explorer - by Leticiel\n\n" +
                "A GUI tool for viewing, editing, and extracting files from\n" +
                "Xuse/Eternal game resource archives and scripts.\n\n" +
                "Supported formats:\n" +
                "• WAG/4AG/004 - Encrypted resource archives\n" +
                "• 002/003 - Bitmap archives\n" +
                "• ARC/XARC - XArc v2 archives\n" +
                "• BIN - Audio archives\n" +
                "• WVB - Audio resource archives\n" +
                "• GD - Resource archives\n" +
                "• BG/H - Bitmap archives\n" +
                "• CD - Xuse script files\n" +
                "• CD3 - Xuse script files\n\n" +
                "Archive features:\n" +
                "• Nested archive browsing\n" +
                "• Image/text/hex preview\n" +
                "• Audio playback (OGG, WAV, MP3)\n" +
                "• Single & batch extraction\n" +
                "• File import/replacement\n" +
                "• Drag & drop support\n" +
                "• Search and filter",
                "About Xuse Explorer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private bool _isSeeking;

        private void AudioSeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }

        private void AudioSeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSeeking && sender is Slider slider)
            {
                ViewModel.SeekAudio(slider.Value);
            }
            _isSeeking = false;
        }
    }
}
