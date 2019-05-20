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

namespace Panacea.Modules.VlcMediaPlayer
{
    /// <summary>
    /// Interaction logic for VlcMediaPlayer.xaml
    /// </summary>
    public partial class VlcMediaPlayerControl : IMediaPlayerPlugin
    {
        #region private members

        private List<Type> _supportedTypes = new List<Type>()
        {
            typeof (IPTVChannel),
            typeof (DVBTChannel),
            typeof (FileChannel)
        };
        private bool _initialized = false;
        string _processPath = Path.Combine(Utils.Path(), "Plugins\\Vlc\\", "VlcMediaPlayer.exe");
        TcpProcessInteropServer _pipe;
        #endregion private members

        public VlcMediaPlayerControl(PanaceaServices core)
        {
            InitializeComponent();
            _core = core;
        }

        public Task BeginInit()
        {
            return Task.CompletedTask;
        }

        bool _isSeekable;
        public override bool IsSeekable => _isSeekable;

        private MediaItem _currentChannel;

        Process _process;


        public async override Task Play(MediaItem channel)
        {
            var plugin = _core.PluginLoader.GetPlugins<IVlcBinariesPlugin>().FirstOrDefault();
            if(plugin == null)
            {
                Error?.Invoke(this, new Exception("No VLC binaries plugin found"));
                return;
            }
            var binariesPath = plugin.GetBinariesPath();

            _currentChannel = channel;

            IsPlaying = true;
            HasSubtitles = false;
            IsPlaying = false;
            OnNavigatableChanged(false);

            CleanUp();
            SetupPipe();
            OnOpening();
            var pipe = _pipe;
            _logger.Debug(this, "initialize");
            var res = await pipe.CallAsync("initialize", binariesPath, /*(Utils.StartupArgs["vlc-params"] ?? */ "");
            if (res == null && pipe == _pipe)
            {
                OnError(new Exception());
                CleanUp();
                return;
            }
            if (_pipe != pipe) return;
            res = await _pipe.CallAsync("handle", pictureBox.Handle);
            if (res == null && pipe == _pipe)
            {
                OnError(new Exception());
                CleanUp();
                return;
            }
            if (_pipe != pipe) return;
            _logger.Debug(this, "play");
            await SendToSubProcess("play", channel.GetMRL() + " " + channel.GetExtras());
        }

        protected void CleanUp()
        {
            Debug.WriteLine("clean");
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
        protected void SetupPipe()
        {
            try
            {
                Debug.WriteLine("setup");
                _pipe = new TcpProcessInteropServer(0);
                _pipe.Closed += _pipe_Closed;
                _pipe.Subscribe("has-subtitles", args =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (bool.TryParse(args[0].ToString(), out bool res))
                        {
                            HasSubtitles = res;
                            OnSubtitlesChanged(HasSubtitles);
                        }
                    }), DispatcherPriority.Background);
                });
                _pipe.Subscribe("duration", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (TimeSpan.TryParse(args[0].ToString(), out TimeSpan res))
                        {
                            _duration = res;
                            OnDuarationChanged(_duration);
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
                            _isSeekable = res == 1;
                            OnSeekableChanged(_isSeekable);
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
                        if (bool.TryParse(args[0].ToString(), out bool res))
                        {
                            OnNavigatableChanged(res);
                        }
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
                        OnError(new Exception());
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
                _pipe.Subscribe("cc", args =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        OnSubtitlesTitlesChanged((args[0] as List<object>) != null ? (args[0] as List<object>).Select(i => i.ToString()).ToList() : new List<string>());
                    });
                });
                _process = Process.Start(_processPath, _pipe.ConnectionId);
                _process.WaitForInputIdle();
                _process.BindToCurrentProcess();
                if (_pipe.Connect())
                {
                    _pipe.Start();
                }
                else
                {
                    OnError(new Exception());
                }
            }
            catch (ObjectDisposedException)
            {
                //disposed before process attached
            }
        }

        private void _pipe_Closed(object sender, EventArgs e)
        {
            _pipe.Closed -= _pipe_Closed;
            var pipe = sender as TcpProcessInteropServer;
            if (pipe != null)
            {
                CleanUp();
            }
            if (IsPlaying)
            {
                Dispatcher.Invoke(() => OnError(new Exception()));
            }
        }

        public override async void SetSubtitles(string title)
        {
            await SendToSubProcess("set-subtitles", title);
        }

        protected async Task SendToSubProcess(string command, params object[] payload)
        {
            try
            {
                await _pipe.PublishAsync(command, payload);
            }
            catch (Exception ex)
            {
                //CleanUp();
                OnError(ex);
            }
        }

        static object _lock = new object();
        public override void Stop()
        {
            lock (_lock)
            {
                IsPlaying = false;
                if (_process == null) return;
                if (!_process.HasExited) _process.Kill();
                _process.Dispose();
                _process = null;
            }
            OnStopped();
        }

        public override async void Pause()
        {
            await SendToSubProcess("pause", "");
        }

        public override ReadOnlyCollection<Type> SupportedChannels
        {
            get { return _supportedTypes.AsReadOnly(); }
        }

        public override bool IsPlaying
        {
            get;
            protected set;
        }

        TimeSpan _duration;
        public override TimeSpan Duration
        {
            get { return _duration; }
        }

        bool _pausable;
        public override bool IsPausable
        {
            get { return _pausable; }
        }

        public override async void Play()
        {
            await SendToSubProcess("play", "");

        }

        public override void Dispose()
        {

        }

        float _position;
        private readonly PanaceaServices _core;

        public override float Position
        {
            get { return _position; }
            set
            {
                if (Position > 1) return;
                SendToSubProcess("position", value);
            }
        }

        public override bool HasNext
        {
            get { return true; }
        }

        public override bool HasPrevious
        {
            get { return true; }
        }

        public override async void Next()
        {
            await SendToSubProcess("next", "");
        }

        public override async void Previous()
        {
            await SendToSubProcess("previous", "");
        }

        private async void FormsHost_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            if (_currentChannel != null)
            {
                await Play(_currentChannel);
            }
        }

        private void pictureBox_Click(object sender, EventArgs e)
        {
            OnClick();
        }

        public override async void NextSubtitle()
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
    }
}