// For staff laptops: trusts the hospital's certificate, checks the connection and adds a desktop shortcut. Installs no server parts.
// Runs from the folder the host installer created, next to alshifa-ca.cer and connect.ini. The certificate is never fetched
// over the network: a USB stick carried by hand is what makes it safe to trust, and connect.ini pins exactly which one.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;

namespace AlShifa.Connect
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ConnectForm());
        }
    }

    internal sealed class ConnectForm : Form
    {
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Name = "lblStatus", Margin = new Padding(0, 8, 0, 8) };
        private readonly Button connect = new Button { Text = "اتصال به سیستم", AutoSize = true, MinimumSize = new Size(130, 32), Name = "btnConnect" };
        private readonly Button close = new Button { Text = "بستن", AutoSize = true, MinimumSize = new Size(90, 32), Name = "btnClose", Margin = new Padding(8, 0, 8, 0) };

        public ConnectForm()
        {
            Text = "اتصال به سیستم الشفا";
            Font = new Font("Segoe UI", 10F);
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 18, 24, 8) };
            page.Controls.Add(new Label { Text = "اتصال این لپ‌تاپ به سیستم الشفا", Font = new Font("Segoe UI", 14F, FontStyle.Bold), AutoSize = true });
            page.Controls.Add(new Label { Text = "این برنامه گواهی امن شفاخانه را نصب می‌کند، اتصال را آزمایش می‌کند و میان‌بر «سیستم الشفا» را روی دسکتاپ می‌سازد. MySQL یا سرور روی این لپ‌تاپ نصب نمی‌شود.", AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 8) });
            page.Controls.Add(status);
            Controls.Add(page);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 10, 16, 8) };
            bar.Controls.Add(connect);
            bar.Controls.Add(close);
            Controls.Add(bar);

            connect.Click += delegate { Start(); };
            close.Click += delegate { Close(); };
        }

        private void Say(string text, Color color)
        {
            BeginInvoke((Action)(() =>
            {
                status.ForeColor = color;
                status.Text = text;
            }));
        }

        private void Start()
        {
            connect.Enabled = false;
            var thread = new Thread(Run) { IsBackground = true };
            thread.Start();
        }

        private void Run()
        {
            try
            {
                var folder = Path.GetDirectoryName(Application.ExecutablePath);
                var settingsPath = Path.Combine(folder, "connect.ini");
                var certificatePath = Path.Combine(folder, "alshifa-ca.cer");
                if (!File.Exists(settingsPath) || !File.Exists(certificatePath))
                {
                    throw new UserMessage("فایل‌های اتصال کنار این برنامه نیست. پوشه AlShifa-Staff-Connect را از دسکتاپ لپ‌تاپ میزبان با فلش بیاورید و این برنامه را از داخل همان پوشه اجرا کنید.");
                }

                var settings = File.ReadAllLines(settingsPath)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
                var address = settings["address"];
                var port = int.Parse(settings["port"]);
                var url = settings["url"];

                Say("بررسی شبکه...", Color.Black);
                if (!CanReach(address, port))
                {
                    throw new UserMessage("لپ‌تاپ میزبان (" + address + ") پیدا نشد. به همان روتر شفاخانه وصل شوید، Wi-Fi مهمان و VPN را خاموش کنید و روشن‌بودن لپ‌تاپ میزبان را بررسی کنید.");
                }

                Say("نصب گواهی امن...", Color.Black);
                using (var certificate = new X509Certificate2(certificatePath))
                {
                    if (!string.Equals(certificate.Thumbprint, settings["thumbprint"], StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UserMessage("گواهی این پوشه با آدرس ثبت‌شده یکی نیست و نصب نشد. پوشه را دوباره از لپ‌تاپ میزبان بیاورید.");
                    }

                    using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        store.Add(certificate);
                    }
                }

                Say("آزمایش اتصال امن...", Color.Black);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/health");
                request.Timeout = 15000;
                request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new UserMessage("سیستم پاسخ درست نداد.");
                }

                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                File.WriteAllText(Path.Combine(desktop, "سیستم الشفا.url"), "[InternetShortcut]\r\nURL=" + url + "\r\n[AlShifa]\r\nManaged=1\r\n");

                Say("اتصال کامل شد. میان‌بر «سیستم الشفا» روی دسکتاپ است و صفحه ورود باز می‌شود.", Color.DarkGreen);
                // This process is elevated. Through Explorer, the browser starts as the ordinary signed-in user.
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + url + "\"");
            }
            catch (UserMessage message)
            {
                Fail(message.Message);
            }
            catch (WebException ex)
            {
                Fail("لپ‌تاپ میزبان پیدا شد، ولی اتصال امن برقرار نشد. آدرس لپ‌تاپ میزبان عوض نشده باشد و ساعت هر دو لپ‌تاپ درست باشد. (" + ex.Status + ")");
            }
            catch (Exception ex)
            {
                Fail("اتصال ناموفق بود: " + ex.Message);
            }
        }

        private void Fail(string text)
        {
            Say(text, Color.Firebrick);
            BeginInvoke((Action)(() => connect.Enabled = true));
        }

        private static bool CanReach(string address, int port)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    var attempt = client.BeginConnect(address, port, null, null);
                    if (!attempt.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(6))) return false;
                    client.EndConnect(attempt);
                    return true;
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        private sealed class UserMessage : Exception
        {
            public UserMessage(string message) : base(message) { }
        }
    }
}
