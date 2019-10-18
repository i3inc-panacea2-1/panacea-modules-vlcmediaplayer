using Panacea.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Panacea.Modules.VlcMediaPlayer
{
    public class SubProcessHelper : IDisposable
    {
        Process _process;
        TcpProcessInteropServer _pipe;

        public TcpProcessInteropServer Pipe => _pipe;

        public SubProcessHelper()
        {
            _pipe = new TcpProcessInteropServer(0);
        }
        public async Task Start(string _processPath)
        {
            await Task.Run(() =>
            {
                _process = Process.Start(_processPath, _pipe.ConnectionId);

                try
                {
                    _process.BindToCurrentProcess();
                }
                catch (Win32Exception)
                {
                    if (Debugger.IsAttached) return;
                }
            });
        }

        public void Dispose()
        {
            if (_pipe != null)
            {
                _pipe.ReleaseSubscriptions();
                _pipe.Dispose();
                _pipe = null;
            }
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                    _process.Dispose();

                }
                catch { }
                finally
                {
                    _process = null;
                }
            }
        }

    
    }
}
