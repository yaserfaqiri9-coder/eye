// For staff computers: trusts the hospital's certificate, finds the server and installs the desktop icon (AlShifa.exe). Installs no server parts.
// Runs from the folder the host installer created, next to alshifa-ca.cer and connect.ini. The certificate is never fetched
// over the network: a USB stick carried by hand is what makes it safe to trust, and connect.ini pins exactly which one.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using AlShifa.Shared;

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
                var launcherPath = Path.Combine(folder, "AlShifa.exe");
                if (!File.Exists(settingsPath) || !File.Exists(certificatePath) || !File.Exists(launcherPath))
                {
                    throw new UserMessage("فایل‌های اتصال کنار این برنامه نیست. پوشه AlShifa-Staff-Connect را از دسکتاپ کمپیوتر سرور با فلش بیاورید و این برنامه را از داخل همان پوشه اجرا کنید.");
                }

                var settings = ServerSettings.Load(settingsPath);
                if (string.IsNullOrEmpty(settings.Name)) throw new UserMessage("این پوشه از نسخه قدیمی است. پوشه AlShifa-Staff-Connect را دوباره از دسکتاپ کمپیوتر سرور بیاورید.");

                // Before looking for the server: the certificate is what tells the real server apart from any other device.
                Say("نصب گواهی امن...", Color.Black);
                using (var certificate = new X509Certificate2(certificatePath))
                {
                    if (!string.Equals(certificate.Thumbprint, settings.CaThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UserMessage("گواهی این پوشه با تنظیمات آن یکی نیست و نصب نشد. پوشه را دوباره از کمپیوتر سرور بیاورید.");
                    }

                    using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadWrite);
                        store.Add(certificate);
                    }
                }

                Say("پیدا کردن سرور در شبکه...", Color.Black);
                var found = Discovery.Find(settings, null);
                if (found == null)
                {
                    throw new UserMessage("سرور پیدا نشد. این کمپیوتر را به همان روتر سرور وصل کنید (نه Wi-Fi مهمان)، VPN را خاموش کنید و روشن‌بودن کمپیوتر سرور را بررسی کنید.");
                }

                var shortcut = Install(settings, found, folder);

                Say("اتصال کامل شد. آیکون «سیستم الشفا» روی دسکتاپ است و صفحه ورود باز می‌شود.", Color.DarkGreen);
                // This process is elevated. Through Explorer, the program starts as the ordinary signed-in user.
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + shortcut + "\"");
            }
            catch (UserMessage message)
            {
                Fail(message.Message);
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

        // The USB stick goes away, so the program, its settings and the logo are kept on this computer. Same layout as the server.
        private static string Install(ServerSettings settings, string address, string folder)
        {
            var keep = Discovery.ProgramFolder;
            Directory.CreateDirectory(keep);
            var launcher = Path.Combine(keep, "AlShifa.exe");
            File.Copy(Path.Combine(folder, "AlShifa.exe"), launcher, true);
            var icon = Path.Combine(keep, "alshifa.ico");
            var iconSource = Path.Combine(folder, "alshifa.ico");
            if (File.Exists(iconSource)) File.Copy(iconSource, icon, true);
            settings.Address = address;
            settings.Save(Path.Combine(keep, "alshifa.ini"));

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            var path = Path.Combine(desktop, "سیستم الشفا.lnk");
            // WScript.Shell saves through the ANSI code page, so a Persian name fails on an English Windows. Saved under an ASCII name, then renamed.
            var temp = Path.Combine(desktop, "alshifa-" + Guid.NewGuid().ToString("N") + ".lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(shellType);
            var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { temp });
            var linkType = link.GetType();
            Action<string, string> set = (name, value) => linkType.InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value });
            set("TargetPath", launcher);
            set("WorkingDirectory", keep);
            set("Description", "AlShifa managed shortcut");
            set("IconLocation", (File.Exists(icon) ? icon : launcher) + ",0");
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            // Older versions made a browser-tab link. One icon on the desktop, not two.
            var tab = Path.Combine(desktop, "سیستم الشفا.url");
            if (File.Exists(tab)) File.Delete(tab);
            return path;
        }

        private sealed class UserMessage : Exception
        {
            public UserMessage(string message) : base(message) { }
        }
    }
}
