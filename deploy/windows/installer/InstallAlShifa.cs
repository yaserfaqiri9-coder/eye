// The double-click installer for the hospital host: asks only what it must, then runs the scripts in deploy\windows.
// The whole package is appended to this exe, so one file on a USB stick is enough and no internet is needed.
// Built by build-installer.ps1 with the C# compiler that ships in Windows, so it needs nothing beyond .NET Framework 4.7.2.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AlShifa.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    /// <summary>What is already on this computer. Decides whether the installer may touch anything at all.</summary>
    internal enum HostState { Fresh, Installed, Unfinished, Occupied }

    internal static class Host
    {
        public const string Root = @"C:\AlShifa";
        public static readonly string SetupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AlShifaSetup");
        public static readonly string WorkDir = Path.Combine(SetupDir, "work");
        // Written by setup.ps1 just before its first change, removed on success. Only this marker allows a rollback.
        public static readonly string Marker = Path.Combine(SetupDir, "install-in-progress.txt");

        public static HostState Detect()
        {
            if (File.Exists(Marker)) return HostState.Unfinished;
            bool services = ServiceController.GetServices().Any(s => s.ServiceName == "AlShifaApi" || s.ServiceName == "AlShifaMySQL");
            if (services && File.Exists(Path.Combine(Root, @"app\appsettings.Production.json"))) return HostState.Installed;
            if (services) return HostState.Occupied;
            if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any()) return HostState.Occupied;
            return HostState.Fresh;
        }

        /// <summary>Addresses staff could reach, most likely first: a real adapter with a gateway, cable before Wi-Fi.</summary>
        public static List<string> DetectAddresses()
        {
            var virtualAdapter = new Regex("Hyper-V|VMware|VirtualBox|TAP|TUN|Tailscale|WireGuard|VPN|Virtual", RegexOptions.IgnoreCase);
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                    .Select(a => new
                    {
                        Address = a.Address.ToString(),
                        Virtual = virtualAdapter.IsMatch(n.Description),
                        Gateway = n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork),
                        Wireless = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    }))
                .ToList();
            return candidates
                .OrderBy(c => c.Virtual).ThenByDescending(c => c.Gateway).ThenBy(c => c.Wireless)
                .Select(c => c.Address).Distinct().ToList();
        }

        private static int? DiskIndex(string driveLetter)
        {
            try
            {
                var query = "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + driveLetter + ":'} WHERE AssocClass=Win32_LogicalDiskToPartition";
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject partition in searcher.Get())
                    {
                        return Convert.ToInt32(partition["DiskIndex"]);
                    }
                }
            }
            catch (ManagementException) { }
            return null;
        }

        /// <summary>Drives on another physical disk. D: is often a second partition of C:, which would not protect anything.</summary>
        public static List<BackupDrive> DetectBackupDrives()
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory).Substring(0, 1);
            var running = Path.GetPathRoot(Application.ExecutablePath).Substring(0, 1);
            var systemDisk = DiskIndex(system);
            var result = new List<BackupDrive>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)) continue;
                var letter = drive.Name.Substring(0, 1);
                if (string.Equals(letter, system, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(letter, running, StringComparison.OrdinalIgnoreCase)) continue;
                var disk = DiskIndex(letter);
                if (disk == null || disk == systemDisk) continue;
                result.Add(new BackupDrive
                {
                    Letter = letter,
                    Label = string.Format("{0}:  {1}  ({2} گیگابایت خالی)", letter, drive.VolumeLabel, drive.AvailableFreeSpace / (1024L * 1024 * 1024)),
                });
            }
            return result;
        }
    }

    internal sealed class BackupDrive
    {
        // Null means no separate disk: backups stay on this computer's own disk.
        public string Letter;
        public string Label;
        public override string ToString() { return Label; }
    }

    /// <summary>The package sits after the program in this exe: zip, its length, its SHA-256, then a fixed mark.</summary>
    internal static class Payload
    {
        private static readonly byte[] Mark = Encoding.ASCII.GetBytes("ALSHIFA1");

        public static void Extract(Action<string> log)
        {
            if (Directory.Exists(Host.WorkDir)) Directory.Delete(Host.WorkDir, true);
            Directory.CreateDirectory(Host.WorkDir);
            var zip = Path.Combine(Host.SetupDir, "payload.zip");
            using (var exe = File.OpenRead(Application.ExecutablePath))
            {
                var tail = new byte[48];
                exe.Seek(-48, SeekOrigin.End);
                ReadExactly(exe, tail);
                if (!tail.Skip(40).SequenceEqual(Mark)) throw new InvalidDataException("این فایل نصب کامل نیست. آن را دوباره از فلش اصلی کپی کنید.");
                var length = BitConverter.ToInt64(tail, 0);
                var expected = tail.Skip(8).Take(32).ToArray();

                exe.Seek(-48 - length, SeekOrigin.End);
                using (var sha = SHA256.Create())
                using (var output = File.Create(zip))
                {
                    var buffer = new byte[1024 * 1024];
                    var left = length;
                    while (left > 0)
                    {
                        var read = exe.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read == 0) throw new InvalidDataException("این فایل نصب کامل نیست. آن را دوباره از فلش اصلی کپی کنید.");
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        output.Write(buffer, 0, read);
                        left -= read;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    if (!sha.Hash.SequenceEqual(expected)) throw new InvalidDataException("فایل نصب خراب یا دستکاری شده است و هیچ تغییری انجام نشد. آن را دوباره از فلش اصلی کپی کنید.");
                }
            }
            log("Package integrity check passed.");
            ZipFile.ExtractToDirectory(zip, Host.WorkDir);
            File.Delete(zip);
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            var done = 0;
            while (done < buffer.Length)
            {
                var read = stream.Read(buffer, done, buffer.Length - done);
                if (read == 0) throw new InvalidDataException("این فایل نصب کامل نیست.");
                done += read;
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private const int FieldWidth = 620;
        private readonly Font titleFont = new Font("Segoe UI", 15F, FontStyle.Bold);
        private readonly Font bodyFont = new Font("Segoe UI", 10F);
        private readonly Panel pageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 8) };
        private readonly FlowLayoutPanel buttonBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(16, 10, 16, 8), Name = "buttonBar" };

        private readonly string logPath;
        private readonly object logLock = new object();
        private HostState state;
        private bool busy;
        private bool secretUnconfirmed;
        private bool backupOnSameDisk;
        private Label driveHint;

        private TextBox passwordBox, passwordAgainBox, logBox;
        private ComboBox addressBox, driveBox;
        private Label errorLabel, statusLabel;
        private ProgressBar progressBar;
        private ListView stepList;
        private string lanIp;

        // The lines the scripts print, and the step of the list each one means.
        private static readonly string[] InstallSteps =
        {
            "بررسی پیش‌نیازهای سیستم",
            "نصب برنامه‌ها و وابستگی‌های لازم",
            "نصب فایل‌های برنامه و دیتابیس",
            "ساخت گواهی HTTPS داخلی",
            "نصب و ایمن‌سازی MySQL",
            "ساخت دیتابیس، Migration و مدیر سیستم",
            "راه‌اندازی سرویس‌های Windows",
            "تنظیم Windows Firewall",
            "تنظیم خواب Windows",
            "فعال‌سازی و آزمایش بکاپ رمزگذاری‌شده",
            "ساخت میان‌بر و پوشه کارمندان",
            "بررسی سلامت سرویس‌ها",
        };

        private static readonly Dictionary<string, int> InstallMarks = new Dictionary<string, int>
        {
            { "##STEP check", 0 },
            { "##STEP deps", 1 },
            { "folders and secrets", 2 },
            { "copying application files", 2 },
            { "HTTPS certificate", 3 },
            { "database service", 4 },
            { "application configuration", 5 },
            { "database schema and first administrator", 5 },
            { "application service", 6 },
            { "firewall", 7 },
            { "power settings", 8 },
            { "backup task", 9 },
            { "first backup and restore test", 9 },
            { "##STEP shortcut", 10 },
            { "##STEP health", 11 },
        };

        private static readonly string[] UpdateSteps =
        {
            "متوقف‌کردن برنامه",
            "بکاپ تازه و آزمایش بازیابی آن",
            "جایگزینی فایل‌های برنامه",
            "اعمال Migration",
            "بررسی سلامت سرویس‌ها",
        };

        private static readonly Dictionary<string, int> UpdateMarks = new Dictionary<string, int>
        {
            { "== Stop the application", 0 },
            { "== Backup before changing anything", 1 },
            { "== Swap the application files", 2 },
            { "== Migrate", 3 },
            { "##STEP health", 4 },
        };

        // Preflight reports in English for the technical log. These explain each line to the person at the laptop.
        private static readonly Dictionary<string, string> PreflightHelp = new Dictionary<string, string>
        {
            { "Windows", "این نسخه Windows پشتیبانی نمی‌شود. Windows 10 یا 11 نسخه ۶۴ بیتی لازم است." },
            { "Install folder", "پوشه C:\\AlShifa خالی نیست. چیزی از آن پاک نشد." },
            { "Disk space", "روی درایو C حداقل ۲۰ گیگابایت جای خالی لازم است." },
            { "Port", "درگاه شبکه‌ای که سیستم لازم دارد را برنامه دیگری گرفته است." },
            { "LAN address", "این آدرس IP روی این لپ‌تاپ نیست. اتصال شبکه را بررسی کنید." },
            { "Network profile", "شبکه روی حالت Public است. آن را Private کنید." },
            { "MySQL files", "فایل‌های MySQL اجرا نشدند." },
            { "Backup disk", "هارد بکاپ وصل نیست یا روی همان دیسک ویندوز است." },
            { "GnuPG", "ابزار رمزگذاری بکاپ کار نکرد." },
        };

        public SetupForm()
        {
            Directory.CreateDirectory(Host.SetupDir);
            logPath = Path.Combine(Host.SetupDir, "setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

            Text = "نصب سیستم الشفا";
            Font = bodyFont;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 600);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(pageHost);
            Controls.Add(buttonBar);
            FormClosing += OnClosing;
            Shown += delegate { ShowStart(); };
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy && !secretUnconfirmed) return;
            e.Cancel = true;
            var message = busy
                ? "نصب در حال انجام است. لطفاً تا پایان آن صبر کنید و لپ‌تاپ را خاموش نکنید."
                : "اول رمز بازیابی بکاپ را بنویسید یا چاپ کنید و گزینه تأیید را بزنید. بدون این رمز هیچ بکاپی باز نمی‌شود.";
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }

        // ---------- Page building ----------

        private FlowLayoutPanel NewPage(string title)
        {
            pageHost.Controls.Clear();
            buttonBar.Controls.Clear();
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            pageHost.Controls.Add(page);
            page.Controls.Add(new Label { Text = title, Font = titleFont, AutoSize = true, Margin = new Padding(0, 0, 0, 10), Name = "pageTitle" });
            return page;
        }

        private Label AddText(FlowLayoutPanel page, string text, Color? color = null, string name = null)
        {
            var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(FieldWidth, 0), Margin = new Padding(0, 2, 0, 8) };
            if (color.HasValue) label.ForeColor = color.Value;
            if (name != null) label.Name = name;
            page.Controls.Add(label);
            return label;
        }

        private Button AddButton(string text, string name, EventHandler click)
        {
            var button = new Button { Text = text, Name = name, AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(6, 0, 6, 0) };
            button.Click += click;
            buttonBar.Controls.Add(button);
            return button;
        }

        // ---------- First page: what this computer holds decides what may happen ----------

        private void ShowStart()
        {
            try { state = Host.Detect(); }
            catch (Exception ex) { ShowFailure("بررسی این کمپیوتر ناموفق بود.", ex.Message, false); return; }
            WriteLog("Detected state: " + state);

            switch (state)
            {
                case HostState.Fresh: ShowSettings(); break;
                case HostState.Installed: ShowUpdateOffer(); break;
                case HostState.Unfinished: ShowUnfinished(); break;
                default: ShowOccupied(); break;
            }
        }

        private void ShowOccupied()
        {
            var page = NewPage("نصب انجام نمی‌شود");
            AddText(page, "پوشه C:\\AlShifa یا سرویس‌های الشفا روی این کمپیوتر هست، ولی نصب کاملی شناخته نشد. برای حفظ اطلاعات، هیچ چیزی تغییر داده نشد و چیزی پاک نشد.");
            AddText(page, "با مسئول فنی تماس بگیرید. راهنما: DEPLOYMENT-WINDOWS.md");
            AddButton("بستن", "btnClose", delegate { Close(); });
        }

        private void ShowUnfinished()
        {
            var page = NewPage("نصب قبلی ناتمام مانده است");
            AddText(page, "نصب قبلی روی این کمپیوتر کامل نشد. می‌توانید آن را برگردانید. سرویس‌ها، فایروال و زمان‌بندی بکاپ حذف می‌شوند و پوشه C:\\AlShifa فقط تغییر نام می‌دهد؛ چیزی پاک نمی‌شود.");
            AddButton("برگشت نصب ناتمام", "btnRollback", delegate { RunRollback(); });
            AddButton("بستن", "btnClose", delegate { Close(); });
        }

        private void ShowUpdateOffer()
        {
            var page = NewPage("سیستم الشفا از قبل نصب است");
            AddText(page, "اطلاعات موجود حفظ می‌شود. اگر ادامه دهید، برنامه موقتاً متوقف می‌شود، یک بکاپ تازه گرفته و آزمایش می‌شود و فقط بعد از موفقیت آن، برنامه به این نسخه به‌روز می‌شود.");
            AddText(page, "اگر بکاپ روی هارد جداگانه است، هارد باید وصل باشد. بهتر است این کار بیرون از ساعت کاری انجام شود. بدون بکاپ معتبر، دیتابیس تغییر نمی‌کند.");
            AddButton("به‌روزرسانی با بکاپ", "btnUpdate", delegate { RunUpdate(); });
            AddButton("بستن بدون تغییر", "btnClose", delegate { Close(); });
        }

        private void ShowSettings()
        {
            var page = NewPage("نصب سیستم الشفا");
            AddText(page, "این لپ‌تاپ میزبان سیستم می‌شود. برای نصب، اینترنت لازم نیست.");

            AddText(page, "نام کاربری مدیر سیستم: admin");
            AddText(page, "رمز مدیر سیستم:");
            passwordBox = new TextBox { Width = FieldWidth, UseSystemPasswordChar = true, Name = "txtPassword", RightToLeft = RightToLeft.No };
            page.Controls.Add(passwordBox);
            AddText(page, "تکرار رمز:");
            passwordAgainBox = new TextBox { Width = FieldWidth, UseSystemPasswordChar = true, Name = "txtPasswordAgain", RightToLeft = RightToLeft.No };
            page.Controls.Add(passwordAgainBox);

            AddText(page, "آدرس این لپ‌تاپ در شبکه شفاخانه:").Margin = new Padding(0, 12, 0, 4);
            addressBox = new ComboBox { Width = FieldWidth, DropDownStyle = ComboBoxStyle.DropDown, Name = "cmbAddress", RightToLeft = RightToLeft.No };
            foreach (var address in Host.DetectAddresses()) addressBox.Items.Add(address);
            if (addressBox.Items.Count > 0) addressBox.SelectedIndex = 0;
            page.Controls.Add(addressBox);
            AddText(page, "اگر این آدرس بعداً عوض شود یا روتر عوض شود، آیکون «سیستم الشفا» سرور را خودش دوباره پیدا می‌کند.", Color.DimGray);

            AddText(page, "محل بکاپ:").Margin = new Padding(0, 8, 0, 4);
            driveBox = new ComboBox { Width = FieldWidth, DropDownStyle = ComboBoxStyle.DropDownList, Name = "cmbDrive" };
            page.Controls.Add(driveBox);
            driveHint = AddText(page, "", Color.DarkGoldenrod, "lblDriveHint");
            driveBox.SelectedIndexChanged += delegate
            {
                var drive = driveBox.SelectedItem as BackupDrive;
                driveHint.Text = drive != null && drive.Letter == null
                    ? "هشدار: بکاپ روی همین دیسک از خرابی یا دزدی لپ‌تاپ محافظت نمی‌کند. نصب ادامه می‌یابد، ولی بهتر است بعداً هارد جداگانه وصل شود."
                    : "";
            };
            RefreshDrives();

            errorLabel = AddText(page, "", Color.Firebrick, "lblError");

            AddButton("شروع نصب", "btnInstall", delegate { StartInstall(); });
            AddButton("به‌روزکردن فهرست هارد", "btnRefresh", delegate { RefreshDrives(); });
            AddButton("انصراف", "btnClose", delegate { Close(); });
        }

        private void RefreshDrives()
        {
            driveBox.Items.Clear();
            foreach (var drive in Host.DetectBackupDrives()) driveBox.Items.Add(drive);
            driveBox.Items.Add(new BackupDrive { Letter = null, Label = "روی همین کمپیوتر، بدون هارد جداگانه (C:\\AlShifaBackups)" });
            driveBox.SelectedIndex = 0;
        }

        private void StartInstall()
        {
            var password = passwordBox.Text;
            var address = addressBox.Text.Trim();
            IPAddress parsed;
            string problem = null;
            // Any password the hospital chooses, as in the application itself. Only an empty one is refused.
            if (password.Length == 0)
                problem = "رمز مدیر را بنویسید.";
            else if (password != passwordAgainBox.Text)
                problem = "رمز و تکرار آن یکسان نیستند.";
            else if (!IPAddress.TryParse(address, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
                problem = "آدرس شبکه درست نیست. لپ‌تاپ را به روتر وصل کنید و آدرس را انتخاب کنید.";

            if (problem != null)
            {
                errorLabel.Text = problem;
                return;
            }

            lanIp = address;
            var letter = ((BackupDrive)driveBox.SelectedItem).Letter;
            backupOnSameDisk = letter == null;
            var destination = (backupOnSameDisk ? "C" : letter) + @":\AlShifaBackups";
            var arguments = string.Format("-Mode Install -Root \"{0}\" -LanIp {1} -BackupDestination \"{2}\" -ShortcutName \"{3}\" -InstallerFolder \"{4}\"", Host.Root, address, destination, "سیستم الشفا", Path.GetDirectoryName(Application.ExecutablePath).TrimEnd('\\'));
            if (backupOnSameDisk) arguments += " -AllowSameDisk";
            var environment = new Dictionary<string, string> { { "ALSHIFA_ADMIN_PASSWORD", password } };
            passwordBox.Text = passwordAgainBox.Text = "";
            ShowProgress("در حال نصب سیستم الشفا", InstallSteps);
            RunSetup(arguments, environment, InstallMarks, InstallSteps.Length, FinishInstall);
        }

        private void RunUpdate()
        {
            ShowProgress("در حال به‌روزرسانی", UpdateSteps);
            RunSetup(string.Format("-Mode Update -Root \"{0}\"", Host.Root), null, UpdateMarks, UpdateSteps.Length, FinishUpdate);
        }

        private void RunRollback()
        {
            ShowProgress("در حال برگشت نصب", new[] { "حذف سرویس‌ها، فایروال و زمان‌بندی بکاپ" });
            RunSetup(string.Format("-Mode Rollback -Root \"{0}\"", Host.Root), null, new Dictionary<string, int>(), 1, FinishRollback);
        }

        // ---------- Running ----------

        private void ShowProgress(string title, string[] steps)
        {
            var page = NewPage(title);
            statusLabel = AddText(page, "آماده‌سازی فایل‌های نصب...", null, "lblStatus");
            progressBar = new ProgressBar { Width = FieldWidth, Height = 16, Style = ProgressBarStyle.Marquee, Margin = new Padding(0, 0, 0, 8), RightToLeftLayout = true };
            page.Controls.Add(progressBar);
            stepList = new ListView { Width = FieldWidth, Height = 300, View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, Name = "lstSteps", RightToLeftLayout = true };
            stepList.Columns.Add("", FieldWidth - 30);
            foreach (var step in steps) stepList.Items.Add(new ListViewItem("○  " + step));
            page.Controls.Add(stepList);
            logBox = new TextBox { Width = FieldWidth, Height = 90, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, RightToLeft = RightToLeft.No, Name = "txtLog", Font = new Font("Consolas", 8.5F) };
            page.Controls.Add(logBox);
        }

        private void RunSetup(string arguments, Dictionary<string, string> environment, Dictionary<string, int> marks, int stepCount, Action<SetupOutcome> finished)
        {
            busy = true;
            var thread = new Thread(() =>
            {
                var outcome = new SetupOutcome();
                try
                {
                    Payload.Extract(Log);
                    var script = Path.Combine(Host.WorkDir, @"scripts\setup.ps1");
                    var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                    var start = new ProcessStartInfo(shell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" " + arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Host.WorkDir,
                    };
                    if (environment != null)
                    {
                        foreach (var pair in environment) start.EnvironmentVariables[pair.Key] = pair.Value;
                    }

                    using (var process = Process.Start(start))
                    {
                        process.ErrorDataReceived += (s, e) =>
                        {
                            if (e.Data == null) return;
                            lock (outcome.Lines) outcome.Lines.Add(e.Data);
                            Log(e.Data);
                        };
                        process.BeginErrorReadLine();
                        string line;
                        while ((line = process.StandardOutput.ReadLine()) != null)
                        {
                            lock (outcome.Lines) outcome.Lines.Add(line);
                            Log(line);
                            HandleLine(line, marks, stepCount, outcome);
                        }
                        process.WaitForExit();
                        outcome.ExitCode = process.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    outcome.ExitCode = -1;
                    outcome.Fatal = ex.Message;
                    Log("ERROR: " + ex);
                }

                BeginInvoke((Action)(() =>
                {
                    busy = false;
                    finished(outcome);
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void HandleLine(string line, Dictionary<string, int> marks, int stepCount, SetupOutcome outcome)
        {
            var key = line.Trim();
            if (key.StartsWith("##WARN ")) outcome.Warnings.Add(key.Substring(7));
            if (key == "##VOICE pending") outcome.VoicePending = true;
            if (key == "##HEALTH attention") outcome.HealthAttention = true;
            if (key.StartsWith("RESULT: backup restored and verified")) outcome.BackupVerified = true;
            if (key.StartsWith("##URL ")) outcome.Url = key.Substring(6);
            if (key.StartsWith("##STAFF ")) outcome.StaffFolder = key.Substring(8);

            int step;
            if (!marks.TryGetValue(key, out step)) return;
            BeginInvoke((Action)(() =>
            {
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Maximum = stepCount;
                progressBar.Value = Math.Min(step, stepCount);
                statusLabel.Text = stepList.Items[step].Text.Substring(3).Trim() + "...";
                for (var i = 0; i < stepList.Items.Count; i++)
                {
                    var text = stepList.Items[i].Text.Substring(3);
                    stepList.Items[i].Text = (i < step ? "✓  " : i == step ? "►  " : "○  ") + text;
                }
            }));
        }

        private void Log(string line)
        {
            WriteLog(line);
            if (logBox == null || line.StartsWith("##")) return;
            BeginInvoke((Action)(() => logBox.AppendText(line + Environment.NewLine)));
        }

        private void WriteLog(string line)
        {
            lock (logLock) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine, Encoding.UTF8);
        }

        // ---------- Finishing ----------

        private void FinishInstall(SetupOutcome outcome)
        {
            if (outcome.ExitCode != 0)
            {
                ShowFailure("نصب کامل نشد.", Explain(outcome), File.Exists(Host.Marker));
                return;
            }

            var page = NewPage(outcome.HealthAttention ? "نصب شد، ولی یک مورد نیاز به توجه دارد" : "نصب با موفقیت انجام شد");
            AddText(page, "آدرس سیستم: " + outcome.Url + "     نام کاربری: admin");
            if (new Uri(outcome.Url).Port != 443) AddText(page, "درگاه 443 روی این کمپیوتر در اختیار برنامه دیگری است یا ممکن است باشد. آن برنامه دست نخورد و سیستم الشفا روی درگاه " + new Uri(outcome.Url).Port + " نصب شد. این درگاه ثابت می‌ماند.");
            AddText(page, outcome.BackupVerified
                ? "بکاپ رمزگذاری‌شده گرفته شد و بازیابی آزمایشی آن تأیید شد."
                : "بکاپ و بازیابی تأیید نشد. پیش از استفاده واقعی این مورد باید رفع شود.", outcome.BackupVerified ? Color.DarkGreen : Color.Firebrick);
            if (outcome.HealthAttention) AddText(page, "بررسی سلامت موردی را گزارش کرد. پیش از شروع کار با مریض، گزارش نصب را به مسئول فنی نشان دهید.", Color.Firebrick);
            foreach (var warning in outcome.Warnings) AddText(page, warning, Color.DarkGoldenrod);
            if (backupOnSameDisk) AddText(page, "هشدار: بکاپ فقط روی همین کمپیوتر است (C:\\AlShifaBackups). اگر دیسک خراب یا لپ‌تاپ دزدیده شود، بکاپ هم از دست می‌رود. پیش از کار با مریض واقعی هارد جداگانه وصل کنید.", Color.Firebrick);
            if (outcome.VoicePending) AddText(page, "فراخوان صوتی: در انتظار فعال‌سازی. بقیه سیستم کامل کار می‌کند. برای فعال‌سازی، Import-AlShifa-Voice.exe را از فلش اجرا کنید.", Color.DarkGoldenrod);

            AddText(page, "رمز بازیابی بکاپ (بدون آن هیچ بکاپی باز نمی‌شود):").Margin = new Padding(0, 10, 0, 4);
            var secret = File.ReadAllText(Path.Combine(Host.Root, @"secrets\backup-password.txt"));
            var secretBox = new TextBox { Width = FieldWidth, ReadOnly = true, Text = secret, RightToLeft = RightToLeft.No, Font = new Font("Consolas", 13F, FontStyle.Bold), Name = "txtBackupPassword", TextAlign = HorizontalAlignment.Center };
            page.Controls.Add(secretBox);
            AddText(page, "این رمز را چاپ یا روی کاغذ بنویسید و بیرون از ساختمان نگه دارید.", Color.Firebrick);

            AddText(page, "اتصال کارمندان: پوشه «" + Path.GetFileName(outcome.StaffFolder ?? "") + "» روی دسکتاپ را با فلش به هر لپ‌تاپ ببرید و Connect-AlShifa.exe را از آن اجرا کنید.");

            var confirm = new CheckBox { Text = "رمز بازیابی را نوشتم یا چاپ کردم", AutoSize = true, Name = "chkSaved", Margin = new Padding(0, 8, 0, 8) };
            page.Controls.Add(confirm);

            AddButton("چاپ رمز", "btnPrint", delegate { PrintSecret(secret); });
            var open = AddButton("پایان و باز کردن سیستم", "btnFinish", delegate
            {
                Cleanup();
                OpenInUserBrowser(outcome.Url);
                Close();
            });
            open.Enabled = false;
            secretUnconfirmed = true;
            confirm.CheckedChanged += delegate
            {
                open.Enabled = confirm.Checked;
                secretUnconfirmed = !confirm.Checked;
            };
        }

        private void FinishUpdate(SetupOutcome outcome)
        {
            if (outcome.ExitCode != 0)
            {
                ShowFailure("به‌روزرسانی کامل نشد.", Explain(outcome), false);
                return;
            }

            var page = NewPage(outcome.HealthAttention ? "به‌روزرسانی شد، ولی یک مورد نیاز به توجه دارد" : "به‌روزرسانی با موفقیت انجام شد");
            AddText(page, "اطلاعات قبلی حفظ شد و بکاپ پیش از به‌روزرسانی روی هارد بکاپ است.");
            if (outcome.HealthAttention) AddText(page, "بررسی سلامت موردی را گزارش کرد. گزارش را به مسئول فنی نشان دهید.", Color.Firebrick);
            if (outcome.VoicePending) AddText(page, "فراخوان صوتی: در انتظار فعال‌سازی.", Color.DarkGoldenrod);
            AddButton("پایان و باز کردن سیستم", "btnFinish", delegate
            {
                Cleanup();
                if (!string.IsNullOrEmpty(outcome.Url)) OpenInUserBrowser(outcome.Url);
                Close();
            });
        }

        private void FinishRollback(SetupOutcome outcome)
        {
            if (outcome.ExitCode != 0)
            {
                ShowFailure("برگشت نصب کامل نشد.", Explain(outcome), false);
                return;
            }

            var page = NewPage("برگشت انجام شد");
            AddText(page, "سرویس‌ها، فایروال، زمان‌بندی بکاپ و میان‌بر حذف شدند و تنظیم خواب برگشت. چیزی پاک نشد؛ پوشه قبلی با نام تازه کنار C:\\AlShifa مانده است. Visual C++ و Gpg4win نصب می‌مانند و مشکلی ایجاد نمی‌کنند.");
            AddText(page, "می‌توانید مشکل را رفع کنید و نصب را دوباره اجرا کنید.");
            // The log stays in the setup folder: the folder it would normally move to has just been renamed.
            AddButton("بستن", "btnClose", delegate
            {
                if (Directory.Exists(Host.WorkDir)) Directory.Delete(Host.WorkDir, true);
                Close();
            });
        }

        private void ShowFailure(string title, string detail, bool canRollback)
        {
            var page = NewPage(title);
            AddText(page, canRollback
                ? "نصب وسط کار متوقف شد. چیزی پاک نشده است. می‌توانید تغییرات را برگردانید."
                : "اطلاعات موجود دست نخورده است.");
            AddText(page, "علت:");
            var box = new TextBox { Width = FieldWidth, Height = 240, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = detail, Name = "txtFailure", RightToLeft = RightToLeft.Yes };
            page.Controls.Add(box);
            AddText(page, "گزارش کامل: " + logPath, Color.DimGray);
            if (canRollback) AddButton("برگشت تغییرات", "btnRollback", delegate { RunRollback(); });
            AddButton("باز کردن گزارش", "btnLog", delegate { Process.Start("notepad.exe", "\"" + logPath + "\""); });
            AddButton("بستن", "btnClose", delegate { Close(); });
        }

        private static string Explain(SetupOutcome outcome)
        {
            if (outcome.Fatal != null) return outcome.Fatal;
            var result = new StringBuilder();
            string[] lines;
            lock (outcome.Lines) lines = outcome.Lines.ToArray();
            if (lines.Contains("##ERROR vc-runtime-missing"))
            {
                return "این لپ‌تاپ Microsoft Visual C++ Redistributable ندارد و MySQL بدون آن اجرا نمی‌شود. هیچ تغییری انجام نشد.\r\n\r\nفایل vc_redist.x64.exe را از سایت Microsoft بگیرید، کنار Install-AlShifa.exe بگذارید و نصب را دوباره اجرا کنید.";
            }
            foreach (var line in lines.Where(l => l.StartsWith("FAIL ")))
            {
                var help = PreflightHelp.FirstOrDefault(p => line.Substring(5).TrimStart().StartsWith(p.Key)).Value;
                if (help != null) result.AppendLine("• " + help);
                result.AppendLine(line);
            }
            foreach (var line in lines.Where(l => l.StartsWith("##ERROR ") || l.StartsWith("Reason: ") || l.StartsWith("INSTALL STOPPED")))
            {
                result.AppendLine(line.StartsWith("##ERROR ") ? line.Substring(8) : line);
            }
            return result.Length > 0 ? result.ToString() : string.Join(Environment.NewLine, lines.Reverse().Take(12).Reverse());
        }

        /// <summary>Removes the unpacked files. The log, which holds no passwords, moves next to the other logs.</summary>
        private void Cleanup()
        {
            try
            {
                var logs = Path.Combine(Host.Root, "logs");
                if (Directory.Exists(logs)) File.Copy(logPath, Path.Combine(logs, Path.GetFileName(logPath)), true);
                Directory.Delete(Host.SetupDir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>This process is elevated. Through Explorer, the browser starts as the ordinary signed-in user.</summary>
        private static void OpenInUserBrowser(string url)
        {
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + url + "\"");
        }

        private void PrintSecret(string secret)
        {
            using (var document = new PrintDocument())
            using (var dialog = new PrintDialog { Document = document, UseEXDialog = true })
            {
                document.PrintPage += (s, e) =>
                {
                    using (var big = new Font("Consolas", 16F, FontStyle.Bold))
                    using (var small = new Font("Segoe UI", 11F))
                    {
                        e.Graphics.DrawString("Al-Shifa backup recovery password", small, Brushes.Black, e.MarginBounds.Left, e.MarginBounds.Top);
                        e.Graphics.DrawString(secret, big, Brushes.Black, e.MarginBounds.Left, e.MarginBounds.Top + 40);
                        e.Graphics.DrawString("Keep outside the building. Backups cannot be opened without it.", small, Brushes.Black, e.MarginBounds.Left, e.MarginBounds.Top + 90);
                    }
                };
                if (dialog.ShowDialog(this) == DialogResult.OK) document.Print();
            }
        }
    }

    internal sealed class SetupOutcome
    {
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public int ExitCode;
        public string Fatal;
        public bool VoicePending;
        public bool HealthAttention;
        public bool BackupVerified;
        public string Url;
        public string StaffFolder;
    }
}
