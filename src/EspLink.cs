using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace XingyaoPerf
{
    // Sends each sample to the HW-675 desk display (an ESP32-C3 with a small OLED) as one UDP packet.
    // The host comes from settings.ini "esp=" (default hw675.local); an empty value turns it off.
    // Only reads: nothing here writes to firmware or the EC. Name lookups run on the thread pool
    // so a missing display never slows sampling.
    internal sealed class EspLink : IDisposable
    {
        public const int Port = 4210;
        const int RetrySeconds = 30, RefreshSeconds = 300;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly string host;
        readonly UdpClient udp = new UdpClient();
        volatile IPEndPoint target;
        DateTime nextResolve = DateTime.MinValue;
        int resolving;
        bool loggedMissing;

        public EspLink(string host) { this.host = host; }

        public void Send(Snapshot s, string scene)
        {
            if (DateTime.UtcNow >= nextResolve && Interlocked.CompareExchange(ref resolving, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => Resolve());
            var t = target;
            if (t == null) return;
            var sb = new StringBuilder("XP1");
            Add(sb, "c", s.CpuPct, "0.0"); Add(sb, "g", s.GpuPct, "0.0"); Add(sb, "t", s.TempC, "0.0");
            Add(sb, "pw", s.PkgW, "0.0"); Add(sb, "gw", s.GpuW, "0.0"); Add(sb, "sw", s.SysW, "0.0");
            sb.Append("|f0=").Append(s.Fan0 < 0 ? "-" : s.Fan0.ToString(Inv));
            sb.Append("|f1=").Append(s.Fan1 < 0 ? "-" : s.Fan1.ToString(Inv));
            Add(sb, "m", s.MemPct, "0"); Add(sb, "pg", s.PGhz, "0.00"); Add(sb, "eg", s.EGhz, "0.00");
            Add(sb, "cg", s.CpuGhz, "0.00"); Add(sb, "gm", s.GpuMhz, "0"); Add(sb, "ni", s.NetIn, "0"); Add(sb, "no", s.NetOut, "0");
            sb.Append("|sn=").Append((scene ?? "").Replace("|", "/"));
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            try { udp.Send(b, b.Length, t); }
            catch (SocketException) { } // network down or changing; the next sample tries again
            catch (ObjectDisposedException) { }
        }

        static void Add(StringBuilder sb, string key, double v, string fmt)
        {
            sb.Append('|').Append(key).Append('=').Append(double.IsNaN(v) || double.IsInfinity(v) ? "-" : v.ToString(fmt, Inv));
        }

        void Resolve()
        {
            try
            {
                IPAddress ip = null;
                foreach (var a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                if (ip == null) throw new SocketException((int)SocketError.HostNotFound);
                var old = target;
                if (old == null || !old.Address.Equals(ip)) Log.Write("esp display " + host + " at " + ip);
                target = new IPEndPoint(ip, Port);
                loggedMissing = false;
                nextResolve = DateTime.UtcNow.AddSeconds(RefreshSeconds);
            }
            catch (Exception e)
            {
                // Keep sending to the last address: a failed refresh is usually a busy network, not a move.
                if (!loggedMissing) { Log.Write("esp display " + host + " not found: " + e.Message); loggedMissing = true; }
                nextResolve = DateTime.UtcNow.AddSeconds(RetrySeconds);
            }
            finally { Interlocked.Exchange(ref resolving, 0); }
        }

        public void Dispose() { udp.Close(); }
    }
}
