// Activates patient calling on the host: copies clips you hold a licence for into the system. Uses build-voice-pack.ps1, which checks every file.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AlShifa.ImportVoice
{
    internal static class Program
    {
        private const string Title = "فعال‌سازی فراخوان صوتی الشفا";
        private const MessageBoxOptions Rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            var tool = @"C:\AlShifa\scripts\build-voice-pack.ps1";
            if (!File.Exists(tool))
            {
                Show("این برنامه باید روی لپ‌تاپ میزبان و بعد از نصب سیستم اجرا شود.", MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new FolderBrowserDialog { Description = "پوشه فایل‌های صوتی دارای مجوز (Microsoft Farid) را انتخاب کنید:", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;

                string output;
                var imported = Run(tool, "-Mode Import -SourceDir \"" + dialog.SelectedPath + "\"", out output);
                if (imported == 0)
                {
                    Show("فراخوان صوتی فعال شد. همه فایل‌ها کامل و سالم هستند.\n\n" + output, MessageBoxIcon.Information);
                }
                else
                {
                    Show("فراخوان صوتی هنوز کامل نیست. فایل‌های سالم وارد شدند و بقیه سیستم عادی کار می‌کند.\n\n" + output, MessageBoxIcon.Warning);
                }
            }
        }

        private static int Run(string tool, string arguments, out string output)
        {
            var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var start = new ProcessStartInfo(shell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tool + "\" " + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var process = Process.Start(start))
            {
                var errors = process.StandardError.ReadToEndAsync();
                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                output += errors.Result;
                return process.ExitCode;
            }
        }

        private static void Show(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(text, Title, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, Rtl);
        }
    }
}
