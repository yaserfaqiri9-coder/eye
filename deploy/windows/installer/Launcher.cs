// AlShifa.exe: what the desktop icon runs on every computer, server included. Finds the server on the current network and opens
// the system in its own window. Moving to another router or getting another address needs nothing from anyone.
// Runs as the signed-in user; installs nothing. Its folder holds alshifa.ini, written by Connect-AlShifa.exe or the installer.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AlShifa.Shared;

namespace AlShifa.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ServerSettings settings;
            try
            {
                settings = ServerSettings.Load(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "alshifa.ini"));
            }
            catch (Exception)
            {
                Tell("تنظیمات این کمپیوتر پیدا نشد. Connect-AlShifa.exe را از پوشه AlShifa-Staff-Connect دوباره اجرا کنید.");
                return;
            }

            var edge = Discovery.FindEdge();
            if (edge == null)
            {
                Tell("Microsoft Edge روی این کمپیوتر نیست. Edge را نصب کنید و دوباره روی آیکون کلیک کنید.");
                return;
            }

            // Per user: the last address that worked, so the next click is instant.
            var memoryFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlShifa");
            var memory = Path.Combine(memoryFolder, "server-address.txt");
            string remembered = null;
            try { if (File.Exists(memory)) remembered = File.ReadAllText(memory).Trim(); } catch (IOException) { }

            string found = null;
            using (var wait = new WaitForm())
            {
                var worker = new Thread(() =>
                {
                    found = Discovery.Find(settings, remembered);
                    wait.BeginInvoke((Action)wait.Close);
                }) { IsBackground = true };
                wait.Shown += delegate { worker.Start(); };
                Application.Run(wait);
            }

            if (found == null)
            {
                Tell("سرور سیستم الشفا پیدا نشد.\r\n\r\n۱. کمپیوتر سرور روشن باشد.\r\n۲. این کمپیوتر به همان روتر سرور وصل باشد (کیبل یا Wi-Fi شفاخانه، نه Wi-Fi مهمان).\r\n۳. روتر روشن باشد.\r\n\r\nبعد دوباره روی آیکون کلیک کنید.");
                return;
            }

            try
            {
                Directory.CreateDirectory(memoryFolder);
                File.WriteAllText(memory, found);
            }
            catch (IOException) { }

            // A profile of its own: these settings always apply, and the user's other Edge windows stay as they are.
            // The browser is told the server's current address for the fixed name in its certificate, so no DNS or hosts file is involved.
            var profile = Path.Combine(memoryFolder, "Edge");
            var arguments = "--user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --no-proxy-server" +
                " --host-resolver-rules=\"MAP " + settings.Name + " " + found + "\" --app=" + settings.Url + " --start-maximized";
            Process.Start(new ProcessStartInfo(edge, arguments) { UseShellExecute = false });
        }

        private static void Tell(string text)
        {
            MessageBox.Show(text, "سیستم الشفا", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
    }

    internal sealed class WaitForm : Form
    {
        public WaitForm()
        {
            Text = "سیستم الشفا";
            Font = new Font("Segoe UI", 11F);
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(360, 90);
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Controls.Add(new Label { Text = "در حال اتصال به سیستم الشفا...", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        }
    }
}
