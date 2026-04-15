using System;
using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Vorbis;

namespace XuseExplorer.Core
{
    public class AudioPlayerService : IDisposable
    {
        private WaveOutEvent? _waveOut;
        private WaveStream? _reader;
        private readonly DispatcherTimer _positionTimer;

        public event Action? PlaybackStateChanged;
        public event Action? PositionChanged;

        public AudioPlayerService()
        {
            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _positionTimer.Tick += (_, _) => PositionChanged?.Invoke();
        }

        public bool IsLoaded => _reader != null;
        public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
        public bool IsPaused => _waveOut?.PlaybackState == PlaybackState.Paused;
        public bool IsStopped => _waveOut == null || _waveOut.PlaybackState == PlaybackState.Stopped;

        public TimeSpan CurrentPosition => _reader?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan TotalDuration => _reader?.TotalTime ?? TimeSpan.Zero;

        public double PositionPercent
        {
            get
            {
                if (_reader == null || _reader.TotalTime.TotalSeconds < 0.01) return 0;
                return _reader.CurrentTime.TotalSeconds / _reader.TotalTime.TotalSeconds * 100;
            }
            set
            {
                if (_reader == null) return;
                var target = TimeSpan.FromSeconds(value / 100.0 * _reader.TotalTime.TotalSeconds);
                _reader.CurrentTime = target;
                PositionChanged?.Invoke();
            }
        }

        public void Load(byte[] data, string nameHint = "")
        {
            Stop();
            DisposeReader();

            var ms = new MemoryStream(data);

            try
            {
                string ext = Path.GetExtension(nameHint).ToLowerInvariant();

                if (ext == ".ogg" || IsOggData(data))
                {
                    _reader = new VorbisWaveReader(ms);
                }
                else if (ext == ".mp3" || IsMp3Data(data))
                {
                    _reader = new Mp3FileReader(ms);
                }
                else
                {
                    _reader = new WaveFileReader(ms);
                }

                _waveOut = new WaveOutEvent();
                _waveOut.Init(_reader);
                _waveOut.PlaybackStopped += OnPlaybackStopped;

                PlaybackStateChanged?.Invoke();
            }
            catch
            {
                DisposeReader();
                throw;
            }
        }

        public void Play()
        {
            if (_waveOut == null || _reader == null) return;

            if (_waveOut.PlaybackState == PlaybackState.Stopped)
            {
                if (_reader.Position >= _reader.Length)
                    _reader.Position = 0;
            }

            _waveOut.Play();
            _positionTimer.Start();
            PlaybackStateChanged?.Invoke();
        }

        public void Pause()
        {
            _waveOut?.Pause();
            _positionTimer.Stop();
            PlaybackStateChanged?.Invoke();
        }

        public void Stop()
        {
            _waveOut?.Stop();
            _positionTimer.Stop();
            if (_reader != null)
                _reader.Position = 0;
            PlaybackStateChanged?.Invoke();
            PositionChanged?.Invoke();
        }

        public void TogglePlayPause()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _positionTimer.Stop();
            PlaybackStateChanged?.Invoke();
            PositionChanged?.Invoke();
        }

        public void Unload()
        {
            Stop();
            DisposeReader();
            PlaybackStateChanged?.Invoke();
        }

        private void DisposeReader()
        {
            _waveOut?.Dispose();
            _waveOut = null;
            _reader?.Dispose();
            _reader = null;
        }

        public void Dispose()
        {
            _positionTimer.Stop();
            DisposeReader();
        }

        private static bool IsOggData(byte[] data)
            => data.Length >= 4 && data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53;

        private static bool IsMp3Data(byte[] data)
            => data.Length >= 3 && ((data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) ||
                                    (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33));

        public string FormatDescription
        {
            get
            {
                if (_reader == null) return "";
                var wf = _reader.WaveFormat;
                string type = _reader switch
                {
                    VorbisWaveReader => "OGG Vorbis",
                    Mp3FileReader => "MP3",
                    WaveFileReader => "WAV",
                    _ => "Audio"
                };
                return $"{type} | {wf.SampleRate} Hz | {wf.Channels}ch | {wf.BitsPerSample}bit";
            }
        }
    }
}
