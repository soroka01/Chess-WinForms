using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ChessGame
{
    /// <summary>Shared constants and text clean-up of the network layer (the connections themselves are the room transports).</summary>
    public static class NetLink
    {
        public const int DefaultPort = 5757;

        /// <summary>Keeps only characters that are safe to show and to send in one line.</summary>
        public static string Sanitize(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_' || ch == '.')
                    sb.Append(ch);
                if (sb.Length >= 24) break;
            }
            return sb.ToString().Trim();
        }
    }

    /// <summary>Helpers for finding this computer's addresses on the local network.</summary>
    public static class Lan
    {
        public const int DiscoveryPort = 5758;

        public static List<IPAddress> LocalAddresses()
        {
            List<IPAddress> result = new List<IPAddress>();
            foreach (UnicastIPAddressInformation ua in Interfaces())
                result.Add(ua.Address);
            return result;
        }

        public static List<IPAddress> BroadcastAddresses()
        {
            List<IPAddress> result = new List<IPAddress>();
            foreach (UnicastIPAddressInformation ua in Interfaces())
            {
                if (ua.IPv4Mask == null) continue;
                byte[] a = ua.Address.GetAddressBytes();
                byte[] m = ua.IPv4Mask.GetAddressBytes();
                byte[] b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                result.Add(new IPAddress(b));
            }
            return result;
        }

        static List<UnicastIPAddressInformation> Interfaces()
        {
            List<UnicastIPAddressInformation> result = new List<UnicastIPAddressInformation>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) result.Add(ua);
                }
            }
            catch (Exception) { }
            return result;
        }
    }

    /// <summary>Announces a waiting game once a second by UDP broadcast so that others can find it without typing an IP.</summary>
    public sealed class HostAnnouncer : IDisposable
    {
        readonly UdpClient udp;
        readonly System.Threading.Timer timer;
        readonly byte[] payload;

        public HostAnnouncer(int tcpPort, string name)
        {
            payload = Encoding.UTF8.GetBytes("CHESS1|" + tcpPort + "|" + NetLink.Sanitize(name));
            udp = new UdpClient();
            udp.EnableBroadcast = true;
            timer = new System.Threading.Timer(delegate(object state) { Announce(); }, null, 0, 1000);
        }

        void Announce()
        {
            List<IPAddress> targets = Lan.BroadcastAddresses();
            targets.Add(IPAddress.Broadcast);
            targets.Add(IPAddress.Loopback);   // another window on this computer
            foreach (IPAddress ip in targets)
            {
                try { udp.Send(payload, payload.Length, new IPEndPoint(ip, Lan.DiscoveryPort)); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            timer.Dispose();
            try { udp.Close(); } catch (Exception) { }
        }
    }

    /// <summary>Listens for HostAnnouncer broadcasts. Found is raised on the creating (UI) thread.</summary>
    public sealed class HostFinder : IDisposable
    {
        /// <summary>name, IP address, TCP port</summary>
        public event Action<string, string, int> Found;

        readonly SynchronizationContext ui;
        Socket socket;
        volatile bool stopped;

        public HostFinder()
        {
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, Lan.DiscoveryPort));
                Thread t = new Thread(ReceiveLoop);
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception)
            {
                // discovery is a convenience only; typing the address still works
                if (socket != null) { try { socket.Close(); } catch (Exception) { } socket = null; }
            }
        }

        public bool IsRunning { get { return socket != null; } }

        void ReceiveLoop()
        {
            byte[] buf = new byte[256];
            while (!stopped)
            {
                try
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    int n = socket.ReceiveFrom(buf, ref remote);
                    string text = Encoding.UTF8.GetString(buf, 0, n);
                    string[] parts = text.Split('|');
                    int port;
                    if (parts.Length != 3 || parts[0] != "CHESS1" || !int.TryParse(parts[1], out port) || port < 1 || port > 65535)
                        continue;
                    string name = NetLink.Sanitize(parts[2]);
                    string address = ((IPEndPoint)remote).Address.ToString();
                    ui.Post(delegate(object state) { if (!stopped && Found != null) Found(name, address, port); }, null);
                }
                catch (Exception)
                {
                    if (stopped) return;
                    Thread.Sleep(200);
                }
            }
        }

        public void Dispose()
        {
            stopped = true;
            try { if (socket != null) socket.Close(); } catch (Exception) { }
        }
    }
}
