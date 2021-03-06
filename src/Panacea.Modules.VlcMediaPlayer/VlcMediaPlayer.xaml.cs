﻿using System;
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
using System.ComponentModel;

namespace Panacea.Modules.VlcMediaPlayer
{
    /// <summary>
    /// Interaction logic for VlcMediaPlayer.xaml
    /// </summary>
    public partial class VlcMediaPlayerControl : IMediaPlayerPlugin, IMediaPlayer
    {
        public IMediaPlayer GetMediaPlayer() => this;

        string _processPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            "VlcProcess.exe");


        public VlcMediaPlayerControl(PanaceaServices core)
        {
            InitializeComponent();
            _core = core;
        }

        [PanaceaInject("VlcParameters", "Parameters to pass to the VLC subprocess. Make sure that the parameters are valid for the VlcBinaries plugin that will be loaded.", "VlcParameters=\"--vout=direct3d9 --visual-80-bands=1\"")]
        protected string VlcParameters { get; set; } = "";

        public event EventHandler<bool> IsPausableChanged;

        public bool CanPlayChannel(MediaItem channel)
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



        bool _opening = false;
        public async Task Play(MediaItem channel)
        {
            try
            {
                _opening = true;
                var plugin = _core.PluginLoader.GetPlugins<IVlcBinariesPlugin>().FirstOrDefault();
                if (plugin == null)
                {
                    throw new Exception("No VLC binaries plugin found");
                }
                var binariesPath = plugin.GetBinariesPath();

                _currentChannel = channel;

                IsPlaying = true;
                HasSubtitles = false;
                IsPlaying = false;

                CleanUp();
                _cts = new CancellationTokenSource();
                var cts = _cts;
                await SetupPipe(cts);
                if (cts.IsCancellationRequested) return;
                if (_subProcess != null)
                {
                    HasNextChanged?.Invoke(this, false);
                    HasPreviousChanged?.Invoke(this, false);


                    var res = await _subProcess.Pipe.CallAsync("initialize", binariesPath, VlcParameters);
                    if (cts.IsCancellationRequested) return;
                    //CaptureMousePanel.BringToFront();
                    if (res == null)
                    {
                        OnError(new Exception("Could not initialize"));
                        CleanUp();
                        return;
                    }

                    res = await _subProcess.Pipe.CallAsync("handle", pictureBox.Handle);
                    if (cts.IsCancellationRequested) return;
                    if (res == null)
                    {
                        OnError(new Exception("Could not set handle"));
                        CleanUp();
                        return;
                    }

                    await SendToSubProcess("play", channel.GetMRL() + " " + channel.GetExtras());

                }
                else
                {
                    throw new Exception("Pipe not connected");
                }
            }
            catch (AggregateException ex)
            {
                if (ex.InnerException is ObjectDisposedException) return;
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
            finally
            {

            }
        }

        protected void CleanUp()
        {
            _connected = false;

            Debug.WriteLine("Cleanup");
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }

            if (_subProcess != null)
            {
                _subProcess.Pipe.Error -= _pipe_Error;
                _subProcess.Pipe.Closed -= _pipe_Closed;
                _subProcess.Dispose();
                _subProcess = null;
            }
        }
        SubProcessHelper _subProcess;
        CancellationTokenSource _cts;
        protected async Task SetupPipe(CancellationTokenSource cts)
        {
            try
            {
                _subProcess = new SubProcessHelper();
                var _pipe = _subProcess.Pipe;

                _pipe.Closed += _pipe_Closed;
                _pipe.Subscribe("opening", args =>
                {

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        OnOpening();
                    }), DispatcherPriority.Background);
                });
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
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (TimeSpan.TryParse(args[0].ToString(), out TimeSpan res))
                            {
                                OnDurationChanged(res);
                            }
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("position", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (float.TryParse(args[0].ToString(),
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out float res))
                            {
                                _position = res;
                                OnPositionChanged(_position);
                            }
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("playing", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            IsPlaying = true;
                            OnPlaying();
                        }), DispatcherPriority.Background);
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("seekable", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (int.TryParse(args[0].ToString(), out int res))
                            {
                                IsSeekable = res == 1;
                                OnIsSeekableChanged(IsSeekable);
                            }
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("nowplaying", args =>
                {
                    try
                    {
                        if (args.Length == 0) return;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            OnNowPlaying(args != null ? args[0].ToString() : "");
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("navigatable", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            bool val = bool.Parse(args[0].ToString());
                            HasNext = HasPrevious = val;
                            HasNextChanged?.Invoke(this, val);
                            HasPreviousChanged?.Invoke(this, val);
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("stopped", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _opening = false;
                            IsPlaying = false;
                            OnStopped();
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("ended", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _opening = false;
                            IsPlaying = false;
                            OnEnded();
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("error", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _opening = false;
                            OnError(new Exception("Error from subprocess"));
                        }));
                    }
                    catch (TaskCanceledException) { }
                    CleanUp();
                });
                _pipe.Subscribe("paused", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            IsPlaying = false;
                            OnPaused();
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("pausable", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _pausable = args[0].ToString() == "1";
                            if (IsPlaying) OnPlaying();
                            IsPausableChanged?.Invoke(this, _pausable);
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("chapter", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {

                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                _pipe.Subscribe("chapterchanged", args =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (int.TryParse(args[0].ToString(), out int res))
                            {
                                OnChapterChanged(res);
                            }
                        }));
                    }
                    catch (TaskCanceledException) { }
                });
                await _subProcess.Start(_processPath);
                if (cts.IsCancellationRequested) return;
                if (_subProcess.Pipe != null)
                {
                    var res = await _pipe.ConnectAsync(15000);
                    if (cts.IsCancellationRequested) return;
                    if (res)
                    {
                        _subProcess.Pipe.Error += _pipe_Error;
                        _subProcess.Pipe.Start();
                        _connected = true;
                    }
                    else
                    {
                        throw new Exception("Pipe did not connect");
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
                Dispatcher.BeginInvoke(new Action(() => OnError(e)));
            }
        }

        private void _pipe_Closed(object sender, EventArgs e)
        {

            CleanUp();
            if (IsPlaying)
            {
                Dispatcher.BeginInvoke(new Action(() => OnError(new Exception("Pipe closed"))));
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
                if (_subProcess == null || !_connected) return;
                _core.Logger.Debug(this, command);
                await _subProcess.Pipe.PublishAsync(command, payload);
            }
            catch (Exception ex)
            {
                //CleanUp();
                OnError(ex);
            }
        }


        public void Stop()
        {
            Debug.WriteLine(IsPlaying);
            Debug.WriteLine(_opening);
            if (!IsPlaying && !_opening) return;

            IsPlaying = false;
            CleanUp();

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
            //CaptureMousePanel.BringToFront();
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