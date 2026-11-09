// Finds the hospital server on whatever network this computer is on, so moving every computer to another router needs no change.
// The server proves itself by its certificate: only one issued by the hospital's own CA counts, so no other device can pose as it.
// Shared by AlShifa.exe (the desktop icon) and Connect-AlShifa.exe.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace AlShifa.Shared
{
    internal sealed class ServerSettings
    {
        /// <summary>The fixed name in the server certificate. The browser is told which address it has, so no DNS is needed.</summary>
        public string Name;
        public int Port;
        /// <summary>Thumbprint of the hospital CA. A server whose certificate does not chain to it is not ours.</summary>
        public string CaThumbprint;
        /// <summary>Windows name of the server, tried before scanning the network.</summary>
        public string Computer;
        public string Address;

        public static ServerSettings Load(string path)
        {
            var values = File.ReadAllLines(path)
                .Select(l => l.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            Func<string, string> get = key => values.ContainsKey(key) ? values[key] : null;
            return new ServerSettings
            {
                Name = get("name"),
                Port = int.Parse(get("port") ?? "443"),
                CaThumbprint = get("thumbprint"),
                Computer = get("server"),
                Address = get("address"),
            };
        }

        public void Save(string path)
        {
            File.WriteAllText(path, "name=" + Name + "\r\nport=" + Port + "\r\nthumbprint=" + CaThumbprint + "\r\nserver=" + Computer + "\r\naddress=" + Address + "\r\n");
        }

        public string Url
        {
            get { return "https://" + Name + (Port == 443 ? "" : ":" + Port) + "/"; }
        }
    }

    internal static class Discovery
    {
        /// <summary>The server's address on the current network, or null. Quick when the remembered address still holds.</summary>
        public static string Find(ServerSettings settings, string remembered)
        {
            var tried = new HashSet<string>();
            Func<IEnumerable<string>, string> check = candidates =>
            {
                var fresh = candidates.Where(a => !string.IsNullOrEmpty(a) && tried.Add(a)).ToList();
                return OpenPorts(fresh, settings.Port).FirstOrDefault(a => IsServer(a, settings));
            };
            return check(new[] { remembered, settings.Address, "127.0.0.1" })
                ?? check(Resolve(settings.Computer))
                ?? check(Neighbours());
        }

        private static IEnumerable<string> Resolve(string computer)
        {
            if (string.IsNullOrEmpty(computer)) return new string[0];
            try
            {
                return Dns.GetHostAddresses(computer).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            }
            catch (SocketException)
            {
                return new string[0];
            }
        }

        /// <summary>Every other address in this computer's own networks, as /24: what office and hospital routers hand out.</summary>
        private static IEnumerable<string> Neighbours()
        {
            var own = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
                .ToList();
            foreach (var address in own)
            {
                var b = address.GetAddressBytes();
                for (var i = 1; i < 255; i++)
                {
                    if (i == b[3]) continue;
                    yield return b[0] + "." + b[1] + "." + b[2] + "." + i;
                }
            }
        }

        /// <summary>Addresses with the port open. All are tried at once, so a whole network takes about as long as one.</summary>
        private static List<string> OpenPorts(List<string> candidates, int port)
        {
            var attempts = new List<KeyValuePair<string, KeyValuePair<TcpClient, IAsyncResult>>>();
            foreach (var address in candidates)
            {
                var client = new TcpClient();
                try
                {
                    attempts.Add(new KeyValuePair<string, KeyValuePair<TcpClient, IAsyncResult>>(address, new KeyValuePair<TcpClient, IAsyncResult>(client, client.BeginConnect(address, port, null, null))));
                }
                catch (SocketException)
                {
                    client.Close();
                }
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            var open = new List<string>();
            foreach (var attempt in attempts)
            {
                var client = attempt.Value.Key;
                var pending = attempt.Value.Value;
                var left = deadline - DateTime.UtcNow;
                try
                {
                    if (pending.AsyncWaitHandle.WaitOne(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                    {
                        client.EndConnect(pending);
                        open.Add(attempt.Key);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    client.Close();
                }
            }
            return open;
        }

        private static bool IsServer(string address, ServerSettings settings)
        {
            try
            {
                using (var tcp = new TcpClient())
                {
                    var pending = tcp.BeginConnect(address, settings.Port, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(1500)) return false;
                    tcp.EndConnect(pending);
                    tcp.ReceiveTimeout = 4000;
                    tcp.SendTimeout = 4000;
                    var ours = false;
                    RemoteCertificateValidationCallback check = (sender, certificate, chain, errors) =>
                    {
                        if (certificate == null) return false;
                        using (var built = new X509Chain())
                        {
                            built.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            built.Build(new X509Certificate2(certificate));
                            var elements = built.ChainElements;
                            ours = elements.Count > 1 && string.Equals(elements[elements.Count - 1].Certificate.Thumbprint, settings.CaThumbprint, StringComparison.OrdinalIgnoreCase);
                        }
                        return ours;
                    };
                    using (var ssl = new SslStream(tcp.GetStream(), false, check))
                    {
                        ssl.AuthenticateAsClient(settings.Name, null, SslProtocols.Tls12, false);
                    }
                    return ours;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string FindEdge()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
            {
                var registered = key == null ? null : key.GetValue("") as string;
                if (!string.IsNullOrEmpty(registered) && File.Exists(registered)) return registered;
            }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                var path = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>Where the desktop icon, its settings and the logo live on every computer, server included.</summary>
        public static string ProgramFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlShifa"); }
        }
    }
}
