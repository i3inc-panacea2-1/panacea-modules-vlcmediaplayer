using Panacea.Interop;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Vlc.DotNet.Core;
using Vlc.DotNet.Core.Interops.Signatures.LibVlc.Media;
using Vlc.DotNet.Core.Medias;
using Vlc.DotNet.Forms;

namespace VlcMediaPlayer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        VlcControlHeadless _vlc;
        volatile bool _exited = false;
        LocationMedia _currentMedia;

        static string CurrentPath()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            //MessageBox.Show("exit");
            _exited = true;
            base.OnExit(e);
        }

        TcpProcessInteropClient client;

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                if (e.Args.Length == 0)
                {
                    Shutdown();
                    return;
                }
                //MessageBox.Show("edw");
                var source = new TaskCompletionSource<object>();
                var handleSource = new TaskCompletionSource<object>();
                bool exited = false;
                client = new TcpProcessInteropClient(int.Parse(e.Args[0]));
                client.Register("initialize", async payload =>
                {
                    if (exited) return null;
                  
                    source = new TaskCompletionSource<object>();
                    var path = payload[0].ToString();
                    var arguments = payload[1].ToString().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    bool init = false;
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!File.Exists(Path.Combine(path,  "libvlc.dll")))
                        {
                            Current.Shutdown();
                        }
                        var pluginsPath = Path.Combine(path,"plugins");
                        VlcContext.LibVlcDllsPath = path;
                        VlcContext.LibVlcPluginsPath = pluginsPath;

                        VlcContext.StartupOptions.IgnoreConfig = true;
                        VlcContext.StartupOptions.LogOptions.LogInFile = false;
                        VlcContext.StartupOptions.LogOptions.ShowLoggerConsole = false;
                        VlcContext.StartupOptions.LogOptions.Verbosity = VlcLogVerbosities.None;
                        VlcContext.StartupOptions.AddOption("--input-timeshift-granularity=0");
                        VlcContext.StartupOptions.AddOption("--auto-preparse");
                        VlcContext.StartupOptions.AddOption("--album-art=0");
                        //VlcContext.StartupOptions.AddOption("--overlay=1");
                        //VlcContext.StartupOptions.AddOption("--deinterlace=-1");
                        //VlcContext.StartupOptions.AddOption("--network-caching=1500");


                        foreach (var arg in arguments)
                        {
                            try
                            {
                                VlcContext.StartupOptions.AddOption(arg.ToString());
                            }
                            catch
                            {
                            }
                        }
                        try
                        {
                            VlcContext.Initialize();
                            init = true;
                        }
                        catch (Exception ex)
                        {
                            //MessageBox.Show(ex.Message);
                            Application.Current.Shutdown();
                        }

                    }));
                    source.SetResult(null);
                    return init ? new object[] { } : null;

                });
                client.Register("shutdown", async args =>
                {
                    exited = true;
                    if (source != null) await source.Task;
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_vlc != null)
                        {
                            _vlc.Stop();
                            _vlc.Dispose();
                        }
                        if (VlcContext.IsInitialized)
                        {
                            VlcContext.CloseAll();
                        }
                        Shutdown();
                    }));
                    return new object[] { };
                });
                client.Register("handle", async args =>
                {
                    if (exited) return null;
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_vlc != null) _vlc.Dispose();
                        _vlc = new VlcControlHeadless((IntPtr)long.Parse(args[0].ToString()));
                        SetupPlayer();
                    }));
                    return new object[] { };
                });
                client.Subscribe("play", args =>
                {
                    if (!string.IsNullOrEmpty(args[0].ToString()))
                    {
                        _duration = TimeSpan.FromSeconds(0);
                        var split = args[0].ToString().Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                        _currentMedia = new LocationMedia(split[0]);
                        SetHandlersForMedia(_currentMedia);
                        _vlc.Media = _currentMedia;

                        if (split.Length > 1)
                        {
                            foreach (var extra in split.Skip(1))
                            {
                                _currentMedia.AddOption(extra);
                            }

                        }
                    }
                    else
                    {
                        _vlc.Play();
                    }
                });
                client.Subscribe("position", args =>
                {
                    _vlc.Position = float.Parse(args[0].ToString());
                });
                client.Subscribe("pause", args =>
                {
                    _vlc.Pause();
                });
                client.Subscribe("next", args =>
                {
                    try
                    {
                        if (_currentMedia as LocationMedia == null) return;
                        var subitems = (_currentMedia as LocationMedia).SubItems;
                        var cmedia = _vlc.Media as LocationMedia;
                        if (cmedia == null) return;
                        var index = subitems.IndexOf(subitems.First(m => m.MRL == cmedia.MRL));
                        if (subitems.Count > 0 && index < subitems.Count - 1)
                            _vlc.Next();
                    }
                    catch
                    {
                    }
                });
                client.Subscribe("previous", args =>
                {
                    try
                    {
                        if (_currentMedia as LocationMedia == null) return;
                        var subitems = (_currentMedia as LocationMedia).SubItems;
                        var cmedia = _vlc.Media as LocationMedia;
                        if (cmedia == null) return;
                        var index = subitems.IndexOf(subitems.First(m => m.MRL == cmedia.MRL));
                        if (subitems.Count > 0 && index > 0)
                            _vlc.Previous();
                    }
                    catch
                    {
                    }
                });
                client.Subscribe("next-subtitle", args =>
                {
                    if (_vlc.VideoProperties.SpuDescription.Next != null && _vlc.VideoProperties.SpuDescription.Next.Id != _vlc.VideoProperties.CurrentSpuIndex)
                        _vlc.VideoProperties.CurrentSpuIndex = _vlc.VideoProperties.SpuDescription.Next.Id;
                    else _vlc.VideoProperties.CurrentSpuIndex = -1;
                    _vlc.VideoProperties.SetSubtitleFile(null);
                });

                client.Subscribe("set-subtitles", args =>
                {
                    var title = args[0].ToString();
                    if (title == "-1" && _subtitles?.Any(kv => kv.Key.ToString() == title) == true)
                    {
                        _vlc.VideoProperties.CurrentSpuIndex = -1;
                        _vlc.VideoProperties.SetSubtitleFile(null);
                    }
                    else if (_subtitles.Count > 1)
                    {
                        _vlc.VideoProperties.CurrentSpuIndex = _subtitles.Keys.Skip(1).First();
                        _vlc.VideoProperties.SetSubtitleFile(null);
                    }
                });

                if (await client.ConnectAsync(5000))
                {
                    client.Start();
                }
                else
                {
                    Shutdown();
                }
            }
            catch (Exception ex)
            {
                //MessageBox.Show(ex.Message);
            }
        }


        protected async Task Send(string command, params object[] payload)
        {
            await client.PublishAsync(command, payload);
        }

        bool _hasSubtitles = false;
        TimeSpan _duration = TimeSpan.FromSeconds(0);
        VlcTrackDescription _trackDescription;
        Dictionary<int, string> _subtitles;

        void SetupPlayer()
        {
            _vlc.VideoProperties.SetDeinterlaceMode(VlcDeinterlaceModes.Blend);
            _vlc.VideoProperties.AspectRatio = "16:9";
            _vlc.PositionChanged += async (sender, args) =>
            {
                await Send("position", args.Data);


                if (_duration.TotalSeconds != _vlc.Duration.TotalSeconds)
                {
                    _duration = _vlc.Duration;
                    if (_duration.TotalSeconds > 0 && _duration.TotalHours <= 4)
                    {
                        await Send("duration", _duration.ToString());
                        await Send("seekable", _duration.TotalSeconds > 0 ? 1 : 0);
                    }
                }
                if (_vlc.VideoProperties?.SpuDescription?.Id == null)
                {
                    await Send("has-subtitles", false);
                }
                else
                {
                    //
                    if (_vlc.VideoProperties?.SpuDescription?.Id != _trackDescription?.Id)
                    {
                        _trackDescription = _vlc.VideoProperties.SpuDescription;


                        if (_trackDescription == null)
                        {
                            await Send("has-subtitles", false);
                            return;
                        }
                        _subtitles = new Dictionary<int, string>();

                        _subtitles.Add(_trackDescription.Id, _trackDescription.Name);


                        //MessageBox.Show(_vlc.VideoProperties?.SpuDescription?.Name);


                        //MessageBox.Show(_trackDescription.Id.ToString());
                        var descr = _trackDescription;
                        while (descr.Next != null)
                        {
                            descr = descr.Next;
                            //MessageBox.Show(descr.Id.ToString());
                            _subtitles.Add(descr.Id, descr.Name);
                        }
                        if (_subtitles.Count > 0)
                        {
                            //MessageBox.Show(_subtitles.Keys.Count.ToString());
                            //MessageBox.Show(_subtitles.First().Value);
                            await Send("has-subtitles", true);
                            //await Send("cc", _subtitles.Select(kv => kv.Key));
                        }

                    }
                }


            };
            _vlc.SeekableChanged += async (sender, args) => await Send("seekable", args.Data);
            _vlc.Paused += async (sender, args) => await Send("paused", "");
            _vlc.LengthChanged += async (sender, args) =>
            {
                var ts = TimeSpan.FromMilliseconds(args.Data);
                if (ts.TotalSeconds > 0 && ts.TotalHours <= 4)
                {
                    if (args.Data > 0) await Send("duration", ts.ToString());
                }
                else await Send("seekable", 0);
            };
            _vlc.Stopped += async (sender, args) =>
            {
                if (_vlc.Media == null || _vlc.Media.State != States.Stopped) return;
                await Send("stopped", "");
            };
            _vlc.EndReached += async (sender, args) =>
            {
                if (_vlc.Media?.State != States.Ended || (_vlc.Media as LocationMedia)?.SubItems?.Count > 0) return;
                var loc = _vlc.Media as LocationMedia;
                if ((_currentMedia?.SubItems.Count > 1 || loc?.SubItems.Count > 0) && _currentMedia?.SubItems?.Last().MRL != loc?.MRL)
                {
                    await Send("position", "0.0");
                    return;
                }
                await Send("ended", "");
            };
            _vlc.EncounteredError += async (sender, args) =>
            {
                var media = _vlc.Media as LocationMedia;
                if (media != null && media.SubItems.Count == 0)
                {
                    await Send("error", "");
                }
            };
            _vlc.Paused += async (sender, args) => await Send("paused", "");
            _vlc.PausableChanged += async (sender, args) => await Send("pausable", args.Data);
            _vlc.Playing += async (sender, args) =>
            {

                await Send("playing", "");
            };

        }

        void SetHandlersForMedia(MediaBase media)
        {
            media.MediaSubItemAdded += M_MediaSubItemAdded;
            media.MetaChanged += m_MetaChanged;
            media.StateChanged += (oo, ee) => Dispatcher.BeginInvoke(new Action(async () =>
            {
                switch (media.State)
                {
                    case States.Playing:
                        try
                        {
                            if (_currentMedia?.SubItems.Count > 0)
                            {
                                var m = (LocationMedia)media;
                                var lst = ((LocationMedia)_currentMedia).SubItems;
                                if (lst.Count > 1 && lst.Any(mm => mm.MRL == m.MRL))
                                {
                                    await Send("chapterchanged", lst.IndexOf(lst.First(mm => mm.MRL == m.MRL)) + 1);
                                }
                            }
                            _hasSubtitles = _vlc.VideoProperties.SpuDescription != null &&
                                   _vlc.VideoProperties.SpuDescription.Next != null;
                        }
                        catch
                        {

                        }
                        await Send("playing", "");
                        break;
                }
            }), DispatcherPriority.Background);
        }

        private void m_MetaChanged(MediaBase sender, VlcEventArgs<Metadatas> e)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var title = _vlc.Media?.Metadatas?.Title;
                    if (string.IsNullOrEmpty(title)) return;
                    var txt = Encoding.UTF8.GetString(Encoding.Default.GetBytes(title));
                    if (Uri.IsWellFormedUriString(txt, UriKind.Absolute)) return;
                    if (txt?.Length < 320) await Send("nowplaying", txt);
                }
                catch
                {
                    //Collection was modified; enumeration operation may not execute.
                }
            }), DispatcherPriority.Background);

        }

        int _subCounter;

        private async void M_MediaSubItemAdded(MediaBase sender, VlcEventArgs<MediaBase> e)
        {
            _subCounter++;
            await Send("navigatable", _subCounter > 2);
            SetHandlersForMedia(e.Data);
        }
    }
}
