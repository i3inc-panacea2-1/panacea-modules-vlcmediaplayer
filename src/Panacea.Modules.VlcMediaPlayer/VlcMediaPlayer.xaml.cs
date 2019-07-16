using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;
using UserControl = System.Windows.Controls.UserControl;
using System.Security.Cryptography;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.IO.Pipes;
using Panacea.Interop;

using System.Globalization;
using Panacea.Modularity.Media;
using Panacea.Core;
using Panacea.Modularity.VlcMediaPlayer;
using Panacea.Modularity.Media.Channels;
using System.Reflection;
using Panacea.Modularity;

namespace Panacea.Modules.VlcMediaPlayer
{
    /// <summary>
    /// Interaction logic for VlcMediaPlayer.xaml
    /// </summary>
    public partial class VlcMediaPlayerControl : IMediaPlayerPlugin
    {
        string _processPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            "VlcProcess.exe");
        TcpProcessInteropServer _pipe;

        public VlcMediaPlayerControl(PanaceaServices core)
        {
            InitializeComponent();
            _core = core;
        }

        public event EventHandler<bool> IsPausableChanged;

        public bool CanPlayChannel(object channel)
        {
            return new List<Type>()
            {
                typeof (IptvMedia),
                typeof (DvbtMedia)
            }.Contains(channel.GetType());
        }

        Task IPlugin.BeginInit()
        {
            return Task.CompletedTask;
        }

        public bool IsSeekable { get; private set; }
        public bool HasSubtitles { get; private set; }

        private MediaItem _currentChannel;

        Process _process;


        public async void Play(MediaItem channel)
        {
            try
            {
                var plugin = _core.PluginLoader.GetPlugins<IVlcBinariesPlugin>().FirstOrDefault();
                if (plugin == null)
                {
                    Error?.Invoke(this, new Exception("No VLC binaries plugin found"));
                    return;
                }
                var binariesPath = plugin.GetBinariesPath();

                _currentChannel = channel;

                IsPlaying = true;
                HasSubtitles = false;
                IsPlaying = false;
                OnOpening();
                CleanUp();
                await SetupPipe();

                if (_pipe != null)
                {
                    HasNextChanged?.Invoke(this, false);
                    HasPreviousChanged?.Invoke(this, false);

                    var pipe = _pipe;
                    var res = await pipe.CallAsync("initialize", binariesPath, /*(Utils.StartupArgs["vlc-params"] ?? */ "");
                    if (res == null && pipe == _pipe)
                    {
                        OnError(new Exception("Could not initialize"));
                        CleanUp();
                        return;
                    }
                    if (_pipe != pipe) return;
                    res = await _pipe.CallAsync("handle", pictureBox.Handle);
                    if (res == null && pipe == _pipe)
                    {
                        OnError(new Exception("Could not set handle"));
                        CleanUp();
                        return;
                    }
                    if (_pipe != pipe) return;
                    await SendToSubProcess("play", channel.GetMRL() + " " + channel.GetExtras());
                }
                else
                {
                    throw new Exception("Pipe not connected");
                }
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        protected void CleanUp()
        {
            _connected = false;
            lock (_lock)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts = null;
                }

                if (_pipe != null)
                {
                    _pipe.ReleaseSubscriptions();
                    _pipe.Closed -= _pipe_Closed;
                    _pipe.Error -= _pipe_Error;
                    _pipe.Dispose();
                    _pipe = null;
                }

                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.Dispose();
                    _process = null;
                }
            }
        }

        CancellationTokenSource _cts;
        protected async Task SetupPipe()
        {
            try
            {
                _pipe = new TcpProcessInteropServer(0);
                _pipe.Closed += _pipe_Closed;

                _pipe.Subscribe("has-subtitles", args =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (bool.TryParse(args[0].ToString(), out bool res))
                        {
                            HasSubtitles = res;
                            OnHasSubtitlesChanged(HasSubtitles);
                        }
                    }), DispatcherPriority.Background);
                });
                _pipe.Subscribe("duration", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (TimeSpan.TryParse(args[0].ToString(), out TimeSpan res))
                        {
                            OnDurationChanged(res);
                        }
                    });
                });
                _pipe.Subscribe("position", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (float.TryParse(args[0].ToString(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float res))
                        {
                            _position = res;
                            OnPositionChanged(_position);
                        }
                    });
                });
                _pipe.Subscribe("playing", args =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        IsPlaying = true;
                        OnPlaying();
                    }), DispatcherPriority.Background);
                });
                _pipe.Subscribe("seekable", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (int.TryParse(args[0].ToString(), out int res))
                        {
                            IsSeekable = res == 1;
                            OnIsSeekableChanged(IsSeekable);
                        }
                    });
                });
                _pipe.Subscribe("nowplaying", args =>
                {
                    if (args.Length == 0) return;
                    Dispatcher.Invoke(() =>
                    {
                        OnNowPlaying(args != null ? args[0].ToString() : "");
                    });
                });
                _pipe.Subscribe("navigatable", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        bool val = bool.Parse(args[0].ToString());
                        HasNext = HasPrevious = val;
                        HasNextChanged?.Invoke(this, val);
                        HasPreviousChanged?.Invoke(this, val);
                    });
                });
                _pipe.Subscribe("stopped", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        IsPlaying = false;
                        OnStopped();
                    });
                });
                _pipe.Subscribe("ended", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        IsPlaying = false;
                        OnEnded();
                    });
                });
                _pipe.Subscribe("error", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        OnError(new Exception("Error from subprocess"));
                    });
                    CleanUp();
                });
                _pipe.Subscribe("paused", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        IsPlaying = false;
                        OnPaused();
                    });
                });
                _pipe.Subscribe("pausable", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _pausable = args[0].ToString() == "1";
                        if (IsPlaying) OnPlaying();
                        IsPausableChanged?.Invoke(this, _pausable);
                    });
                });
                _pipe.Subscribe("chapter", args =>
                {
                    Dispatcher.Invoke(() =>
                    {

                    });
                });
                _pipe.Subscribe("chapterchanged", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (int.TryParse(args[0].ToString(), out int res))
                        {
                            OnChapterChanged(res);
                        }
                    });
                });
                await Task.Run(() =>
                {
                    lock (_lock)
                    {
                        _process = Process.Start(_processPath, _pipe.ConnectionId);
                        _process.WaitForInputIdle();
                        _process.BindToCurrentProcess();
                    }
                });
                if (_pipe != null)
                {
                    if (await _pipe.ConnectAsync(15000))
                    {
                        _pipe.Error += _pipe_Error;
                        _pipe?.Start();
                        _connected = true;
                    }
                    else
                    {
                        OnError(new Exception("Pipe did not connect"));
                    }
                }


            }
            catch (ObjectDisposedException)
            {
                //disposed before process attached
            }

        }

        bool _connected;

        private void _pipe_Error(object sender, Exception e)
        {
            if (Debugger.IsAttached) Debugger.Break();
            CleanUp();

            if (IsPlaying)
            {
                Dispatcher.Invoke(() => OnError(e));
            }
        }

        private void _pipe_Closed(object sender, EventArgs e)
        {
            
            CleanUp();
            if (IsPlaying)
            {
                Dispatcher.Invoke(() => OnError(new Exception("Pipe closed")));
            }

        }

        public async void SetSubtitles(bool val)
        {
            await SendToSubProcess("set-subtitles", val ? "1" : "-1");
        }

        protected async Task SendToSubProcess(string command, params object[] payload)
        {
            try
            {
                if (_pipe == null || !_connected) return;

                await _pipe.PublishAsync(command, payload);
            }
            catch (Exception ex)
            {
                //CleanUp();
                OnError(ex);
            }
        }

        static readonly object _lock = new object();
        public void Stop()
        {
            lock (_lock)
            {
                IsPlaying = false;
                CleanUp();
            }
            OnStopped();
        }

        public async void Pause()
        {
            await SendToSubProcess("pause", "");
        }


        public bool IsPlaying
        {
            get;
            protected set;
        }

        bool _pausable;
        public bool IsPausable
        {
            get { return _pausable; }
        }

        public async void Play()
        {
            await SendToSubProcess("play", "");

        }

        public void Dispose()
        {

        }

        float _position;
        private readonly PanaceaServices _core;

        public float Position
        {
            get { return _position; }
            set
            {
                if (Position > 1) return;
                SendToSubProcess("position", value);
            }
        }

        public bool HasNext
        {
            get; set;
        }

        public bool HasPrevious
        {
            get; set;
        }

        public async void Next()
        {
            await SendToSubProcess("next", "");
        }

        public async void Previous()
        {
            await SendToSubProcess("previous", "");
        }

        private void FormsHost_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_currentChannel != null)
            {
                //Play(_currentChannel);
            }
        }

        private void pictureBox_Click(object sender, EventArgs e)
        {
            OnClick();
        }

        public async void NextSubtitle()
        {
            await SendToSubProcess("next-subtitle", "");
        }

        private void FormsHost_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OnClick();
        }

        private void pictureBox_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            OnClick();
        }

        private void Panel_Click(object sender, EventArgs e)
        {
            OnClick();
        }

        public event EventHandler Click;
        protected void OnClick()
        {
            Click?.Invoke(this, null);
        }

        public event EventHandler Stopped;
        protected void OnStopped()
        {
            Stopped?.Invoke(this, null);
        }

        public event EventHandler<Exception> Error;
        protected void OnError(Exception ex)
        {
            Error?.Invoke(this, ex);
        }

        public event EventHandler Paused;
        protected void OnPaused()
        {
            Paused?.Invoke(this, null);
        }

        public event EventHandler<float> PositionChanged;
        protected void OnPositionChanged(float e)
        {
            PositionChanged?.Invoke(this, e);
        }

        public event EventHandler Ended;
        protected void OnEnded()
        {
            Ended?.Invoke(this, null);
        }

        public event EventHandler Playing;
        protected void OnPlaying()
        {
            Playing?.Invoke(this, null);
        }

        public event EventHandler<string> NowPlaying;
        protected void OnNowPlaying(string str)
        {
            NowPlaying?.Invoke(this, str);
        }

        public event EventHandler<int> ChapterChanged;
        protected void OnChapterChanged(int i)
        {
            ChapterChanged?.Invoke(this, i);
        }

        public event EventHandler Opening;
        protected void OnOpening()
        {
            Opening?.Invoke(this, null);
        }

        public event EventHandler<bool> HasSubtitlesChanged;
        protected void OnHasSubtitlesChanged(bool val)
        {
            HasSubtitles = val;
            HasSubtitlesChanged?.Invoke(this, val);
        }

        public TimeSpan Duration { get; private set; }

        public FrameworkElement VideoControl => this;

        public event EventHandler<TimeSpan> DurationChanged;
        protected void OnDurationChanged(TimeSpan duration)
        {
            Duration = duration;
            DurationChanged?.Invoke(this, duration);
        }

        public event EventHandler<bool> IsSeekableChanged;
        public event EventHandler<bool> HasNextChanged;
        public event EventHandler<bool> HasPreviousChanged;

        protected void OnIsSeekableChanged(bool seek)
        {
            IsSeekable = seek;
            IsSeekableChanged?.Invoke(this, seek);
        }

        public bool HasMoreChapters()
        {
            return false;
        }

        Task IPlugin.EndInit()
        {
            return Task.CompletedTask;
        }

        public Task Shutdown()
        {
            return Task.CompletedTask;
        }
    }
}