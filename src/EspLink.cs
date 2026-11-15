using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace XingyaoPerf
{
    // Sends each sample to the desk displays (ESP32 boards: HW-675 OLED, T-Display) as one UDP packet.
    // settings.ini "esp=":
    //   broadcast (default) - directed broadcast on every LAN interface with a gateway, so any number of
    //                         displays on the home network receive it without knowing their addresses;
    //   a host name or IP   - unicast to that one device (the 0.9.25 behaviour);
    //   empty               - off.
    // Only reads: nothing here writes to firmware or the EC. Address lookups run on the thread pool
    // so a missing display or a changing network never slows sampling.
    internal sealed class EspLink : IDisposable
    {
        public const int Port = 4210;
        public const string Broadcast = "broadcast";
        const int RetrySeconds = 30, RefreshSeconds = 300, BroadcastRefreshSeconds = 30;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly string host;
        readonly bool broadcast;
        readonly UdpClient udp = new UdpClient();
        volatile IPEndPoint[] targets;
        DateTime nextResolve = DateTime.MinValue;
        int resolving;
        bool loggedMissing;
        string lastTargets = "";

        public EspLink(string host)
        {
            this.host = host;
            broadcast = string.Equals(host, Broadcast, StringComparison.OrdinalIgnoreCase) || host == "*";
            if (broadcast) udp.EnableBroadcast = true;
        }

        public void Send(Snapshot s, string scene)
        {
            if (DateTime.UtcNow >= nextResolve && Interlocked.CompareExchange(ref resolving, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => Resolve());
            var t = targets;
            if (t == null || t.Length == 0) return;
            var sb = new StringBuilder("XP1");
            Add(sb, "c", s.CpuPct, "0.0"); Add(sb, "g", s.GpuPct, "0.0"); Add(sb, "t", s.TempC, "0.0");
            Add(sb, "pw", s.PkgW, "0.0"); Add(sb, "gw", s.GpuW, "0.0"); Add(sb, "sw", s.SysW, "0.0");
            sb.Append("|f0=").Append(s.Fan0 < 0 ? "-" : s.Fan0.ToString(Inv));
            sb.Append("|f1=").Append(s.Fan1 < 0 ? "-" : s.Fan1.ToString(Inv));
            Add(sb, "m", s.MemPct, "0"); Add(sb, "pg", s.PGhz, "0.00"); Add(sb, "eg", s.EGhz, "0.00");
            Add(sb, "cg", s.CpuGhz, "0.00"); Add(sb, "gm", s.GpuMhz, "0"); Add(sb, "ni", s.NetIn, "0"); Add(sb, "no", s.NetOut, "0");
            sb.Append("|sn=").Append((scene ?? "").Replace("|", "/"));
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            foreach (var ep in t)
            {
                try { udp.Send(b, b.Length, ep); }
                catch (SocketException) { } // network down or changing; the next sample tries again
                catch (ObjectDisposedException) { return; }
            }
        }

        static void Add(StringBuilder sb, string key, double v, string fmt)
        {
            sb.Append('|').Append(key).Append('=').Append(double.IsNaN(v) || double.IsInfinity(v) ? "-" : v.ToString(fmt, Inv));
        }

        void Resolve()
        {
            try
            {
                IPEndPoint[] found = broadcast ? BroadcastTargets() : new[] { new IPEndPoint(Lookup(host), Port) };
                if (found.Length == 0) throw new SocketException((int)SocketError.NetworkUnreachable);
                string desc = Describe(found);
                if (desc != lastTargets) Log.Write("esp display " + host + " -> " + desc);
                lastTargets = desc;
                targets = found;
                loggedMissing = false;
                nextResolve = DateTime.UtcNow.AddSeconds(broadcast ? BroadcastRefreshSeconds : RefreshSeconds);
            }
            catch (Exception e)
            {
                // Unicast keeps sending to the last address: a failed refresh is usually a busy network, not a move.
                // Broadcast with no LAN interface has nothing to send to.
                if (broadcast) { targets = null; lastTargets = ""; }
                if (!loggedMissing) { Log.Write("esp display " + host + " not found: " + e.Message); loggedMissing = true; }
                nextResolve = DateTime.UtcNow.AddSeconds(RetrySeconds);
            }
            finally { Interlocked.Exchange(ref resolving, 0); }
        }

        static IPAddress Lookup(string name)
        {
            foreach (var a in Dns.GetHostAddresses(name))
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // Subnet broadcast address of each IPv4 address on an up interface that has a default gateway
        // (the home LAN; skips loopback, Hyper-V / WSL switches and other gateway-less virtual adapters).
        static IPEndPoint[] BroadcastTargets()
        {
            var list = new List<IPEndPoint>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                bool gateway = false;
                foreach (var g in props.GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) { gateway = true; break; }
                if (!gateway) continue;
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    byte[] ip = u.Address.GetAddressBytes(), mask = u.IPv4Mask.GetAddressBytes();
                    if (ip[0] == 169 && ip[1] == 254) continue;
                    if (mask[0] == 0) continue; // no usable mask
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var ep = new IPEndPoint(new IPAddress(bc), Port);
                    bool dup = false;
                    foreach (var e in list) if (e.Equals(ep)) { dup = true; break; }
                    if (!dup) list.Add(ep);
                }
            }
            return list.ToArray();
        }

        static string Describe(IPEndPoint[] eps)
        {
            var sb = new StringBuilder();
            foreach (var e in eps) { if (sb.Length > 0) sb.Append(", "); sb.Append(e.Address); }
            return sb.ToString();
        }

        public void Dispose() { udp.Close(); }
    }
}
