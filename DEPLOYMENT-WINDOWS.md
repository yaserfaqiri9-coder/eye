# استقرار روی لپ‌تاپ شفاخانه (Windows)

راهنمای کامل مسئول IT. برای نصب کوتاه: `INSTALL-SIMPLE.md`. برای آزمایش روی محل: `PILOT-CHECKLIST.md`. یک لپ‌تاپ میزبان است و بقیه فقط مرورگر باز می‌کنند. یک برنامه، یک دیتابیس، بدون همگام‌سازی.

```
لپ‌تاپ‌های کارمندان ──HTTPS (LAN)──┐
                                   ├──> AlShifa.Api (سرویس Windows) ──> MySQL (فقط 127.0.0.1)
رئیس شفاخانه ──Tailscale (رمزشده)──┘
داکتر، گزارش آنلاین ──Cloudflare Access (MFA)──> Tunnel خروجی ──> 127.0.0.1:5081 ──> همان برنامه، فقط گزارش‌ها
```

قطع انترنت: کار داخل شفاخانه ادامه دارد؛ فقط دسترسی از راه دور قطع می‌شود و پس از وصل‌شدن، خودش برمی‌گردد. چیزی برای همگام‌سازی وجود ندارد.

## وضعیت آزمایش این راهنما

| بخش | وضعیت |
|---|---|
| برنامه منتشرشده، HTTPS، پشتیبان، بازیابی، kill ناگهانی | روی ماشین توسعه با `scripts/pilot/smoke.ps1` اجرا و پاس شد |
| `preflight.ps1` | روی ماشین توسعه اجرا شد (فقط می‌خواند) |
| `Install-AlShifa.exe` | ساخت، بررسی Hash و امضا، رد بسته دستکاری‌شده و توقف در preflight روی ماشین توسعه آزمایش شد. **نصب کامل هنوز اجرا نشده.** `PILOT-CHECKLIST.md` بخش «نصب‌کننده» |
| `Connect-AlShifa.exe` | رد میزبان ناموجود و گواهی نامطابق آزمایش شد. **نصب گواهی و اتصال واقعی آزمایش نشده** |
| `install.ps1`، `update.ps1`، `uninstall.ps1` (سرویس، دیوار آتش، برق، Task) | **اجرا نشده.** به دسترسی Administrator نیاز دارد. اول روی لپ‌تاپ آزمایشی اجرا کنید |
| Gpg4win | **آزمایش نشده.** روی ماشین توسعه GnuPG داخل Git آزمایش شد؛ `preflight.ps1` روی لپ‌تاپ واقعی یک رمزنگاری و بازگشایی واقعی انجام می‌دهد |
| ری‌استارت واقعی، کامپیوتر دوم، Tailscale | **آزمایش نشده.** چک‌لیست پایین باید روی محل انجام شود |
| گزارش آنلاین (Cloudflare)، بخش ۷ | بخش Backend (توکن Access، مسیرهای مجاز، نقش) با تست خودکار پاس شد. **`remote-access.ps1`، Tunnel، Access و MFA واقعی اجرا نشده‌اند** |

## ۱. پیش‌نیازها

- لپ‌تاپ میزبان: Windows 10/11 نسخه 64 بیتی، حداقل 8GB حافظه، SSD.
- هارد بیرونی برای پشتیبان (دیسک جدا) و یک USB دوم برای نگهداری بیرون از ساختمان. بدون آن، نصب با `-AllowSameDisk` پشتیبان را روی همان دیسک می‌گیرد؛ این پشتیبان در خرابی دیسک یا دزدی لپ‌تاپ از بین می‌رود. برای جابه‌جایی بعدی، Task را با مقصد تازه و بدون `-AllowSameDisk` تنظیم کنید:

  ```powershell
  Set-ScheduledTask -TaskName 'AlShifa Backup' -Action (New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File "C:\AlShifa\scripts\backup.ps1" -Root "C:\AlShifa" -Destination "F:\AlShifaBackups"')
  ```
- روتر و سوییچ و لپ‌تاپ میزبان پشت UPS. باتری لپ‌تاپ از میزبان محافظت می‌کند، نه از شبکه.
- فایل ZIP رسمی MySQL 8.4 از `dev.mysql.com` (پس از بررسی امضا یا MD5 رسمی) و Visual C++ Redistributable در صورت نیاز.
- Gpg4win از `gpg4win.org` برای رمزنگاری پشتیبان. روی کامپیوتری که قرار است پشتیبان را باز کند هم لازم است.
- روی میزبان BitLocker را روشن کنید. دیتابیس روی دیسک رمز نشده است؛ بدون BitLocker دزدی لپ‌تاپ یعنی افشای پرونده‌ها.
- کارمندان روی لپ‌تاپ میزبان حساب Administrator نداشته باشند. به این شکل نمی‌توانند سرویس‌ها را متوقف کنند یا فایل رمزها را بخوانند.

## ۲. روتر

1. لپ‌تاپ میزبان را با کیبل به روتر وصل کنید (Wi-Fi برای میزبان توصیه نمی‌شود).
2. در روتر برای MAC آدرس کارت شبکه میزبان **DHCP Reservation** بگذارید (مثلاً `192.168.1.10`).
3. هیچ Port Forwarding نسازید. درگاه HTTPS و MySQL نباید به انترنت باز شود. `Install-AlShifa.exe` اگر درگاه‌های پیش‌فرض (443، 5080، 3307) اشغال باشند درگاه آزاد برمی‌دارد و آن را در `C:\AlShifa\app\appsettings.Production.json` و `mysql\alshifa.ini` ثابت می‌کند. اگر برنامه دیگری مانند Apache روی کمپیوتر نصب باشد، حتی خاموش، از 443 استفاده نمی‌شود چون ممکن است بعداً روشن شود. در دستورهای پایین، به‌جای 5080 و 443 همان درگاه‌ها را بگذارید.

## ۳. ساخت بسته (روی کامپیوتر توسعه)

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-release.ps1
```

پوشه `release\alshifa-<تاریخ>` را به میزبان کپی کنید. بدون رمز و بدون داده است و به نصب .NET یا Node نیاز ندارد.

برای نصب با یک فایل:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\build-installer.ps1
```

پوشه `release\AlShifa-Installer-<تاریخ>` را می‌سازد. MySQL و Gpg4win یک بار از ناشر دریافت می‌شوند و Hash و امضای آن‌ها با `deploy\windows\prerequisites.json` مقایسه می‌شود؛ هر ناهماهنگی ساخت را متوقف می‌کند. Microsoft توزیع Visual C++ Redistributable را به دارندگان مجوز Visual Studio محدود کرده است، پس این فایل داخل بسته نیست و نصب‌کننده اگر لازم باشد آن را از کنار خودش برمی‌دارد و امضای Microsoft را بررسی می‌کند. دارنده مجوز می‌تواند با `-IncludeVisualCppRuntime` آن را داخل بسته بگذارد. MySQL و Gpg4win تحت GPL هستند؛ نشانی کد منبع در `THIRD-PARTY-NOTICES.txt` است و هر کس بسته را به دیگری می‌دهد باید شرایط GPL را رعایت کند. `Install-AlShifa.exe` امضای دیجیتال ندارد، پس Windows ناشر را نامشخص نشان می‌دهد.

## ۴. نصب روی میزبان

PowerShell را **Run as Administrator** باز کنید:

```powershell
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
Set-ExecutionPolicy -Scope Process Bypass
cd <پوشه release>\scripts
.\install.ps1 -LanIp 192.168.1.10 -MySqlZipFolder C:\Temp\mysql-8.4.x-winx64 `
    -BackupDestination F:\AlShifaBackups -BackupOptionalDestination G:\AlShifaBackups
```

پیش از آن `preflight.ps1` را با همین پارامترها اجرا کنید؛ چیزی را تغییر نمی‌دهد. `install.ps1` در `C:\AlShifa` نصب می‌کند: سرویس‌های `AlShifaMySQL` و `AlShifaApi` (شروع خودکار، ری‌استارت پس از خطا)، گواهی HTTPS، دیوار آتش فقط برای شبکه محلی، جلوگیری از خواب، Task پشتیبان و اولین پشتیبان و بازیابی آزمایشی. روی نصب موجود اجرا نمی‌شود و داده‌ای را پاک نمی‌کند. اگر وسط کار ایستاد، `uninstall.ps1 -MoveFolderAside` سرویس‌ها، دیوار آتش، Task و تنظیم برق را برمی‌گرداند و پوشه را فقط تغییر نام می‌دهد.

بعد از نصب:

- فایل `C:\AlShifa\secrets\backup-password.txt` را چاپ کنید و بیرون از ساختمان نگه دارید. **بدون آن هیچ پشتیبانی باز نمی‌شود.**
- در Windows Update ساعت‌های فعال (Active Hours) را روی ساعت کاری بگذارید.
- کاربران و نقش‌ها را در برنامه بسازید.

## ۵. لپ‌تاپ‌های کارمندان

برای هر لپ‌تاپ فقط یک بار. فایل `C:\AlShifa\certs\alshifa-ca.cer` را با USB بیاورید و در PowerShell با دسترسی Administrator:

```powershell
certutil -addstore -f Root alshifa-ca.cer
```

بعد یک میانبر روی دسکتاپ به `https://192.168.1.10/` بسازید. Edge و Chrome از همین گواهی استفاده می‌کنند؛ Firefox را جدا تنظیم کنید. اگر مرورگر هشدار امنیتی داد، آن را رد نکنید؛ یعنی گواهی نصب نیست.

## ۶. دسترسی کامل رئیس شفاخانه از بیرون (Tailscale)

رئیس از هر جای دنیا همه سیستم را مثل داخل شفاخانه می‌بیند. هیچ پورتی در روتر باز نمی‌شود و دیتابیس بیرون نمی‌رود. فقط دستگاه‌هایی که با همان حساب Tailscale وارد شده‌اند به سیستم می‌رسند و باز هم نام کاربری و رمز برنامه لازم است. با قطع انترنت فقط دسترسی بیرونی قطع می‌شود و کار داخل شفاخانه ادامه دارد. Funnel را **هرگز** فعال نکنید.

1. یک حساب Tailscale برای شفاخانه بسازید (tailscale.com) با حساب Google یا Microsoft که **تأیید دومرحله‌ای** دارد. هر کس این حساب را داشته باشد به سیستم می‌رسد.
2. در Admin Console، صفحه `DNS`: گزینه‌های **MagicDNS** و **HTTPS Certificates** را روشن کنید.
3. روی کمپیوتر سرور، Tailscale for Windows را از tailscale.com/download نصب کنید.
4. PowerShell را با **Run as administrator** باز کنید و اجرا کنید:

   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\AlShifa\scripts\director-access.ps1
   ```

   اگر صفحه یا لینک ورود آمد، با حساب شفاخانه وارد شوید. اگر لینک فعال‌سازی HTTPS آمد، آن را باز و تأیید کنید. در پایان آدرسی مثل `https://alshifa.<tailnet>.ts.net:8443/` چاپ می‌شود. اسکریپت بررسی می‌کند که Funnel خاموش باشد و فعال‌شدن را در `C:\AlShifa\logs\director-access.log` ثبت می‌کند.
5. روی موبایل یا لپ‌تاپ رئیس، برنامه Tailscale را نصب کنید، با **همان حساب** وارد شوید و آدرس مرحله ۴ را باز کنید.
6. در برنامه برای رئیس حساب با نقش **مدیر (Manager)** و رمز قوی بسازید. Tailscale جای رمز برنامه را نمی‌گیرد.

**قطع فوری:** `director-access.ps1 -Disable` (با Administrator). برای بستن دسترسی یک دستگاه، آن را در Admin Console حذف کنید.

**آزمایش‌نشده:** این اسکریپت هنوز روی کمپیوتری با Tailscale اجرا نشده است. پیش از استفاده واقعی یک بار از بیرون شفاخانه، مثلاً با انترنت موبایل، آزمایش کنید.

## ۷. گزارش آنلاین برای داکتران (Cloudflare Tunnel و Access)

**فقط پس از تأیید کتبی مسئول شفاخانه.** تا وقتی `remote-access.ps1` اجرا نشود، هیچ چیزی از بیرون در دسترس نیست.

چه چیزی ساخته می‌شود:

- `cloudflared` به‌صورت سرویس Windows فقط اتصال **خروجی** به Cloudflare می‌سازد. هیچ پورتی در روتر یا دیوار آتش باز نمی‌شود. MySQL مثل قبل فقط روی `127.0.0.1` است و در Tunnel نیست.
- Tunnel فقط یک نام (مثلاً `reports.example.org`) را به `http://127.0.0.1:5081` می‌فرستد. برنامه این پورت را فقط روی همین کامپیوتر باز می‌کند و چیز دیگری به آن وصل نمی‌شود.
- Cloudflare Access پیش از رسیدن هر درخواست، ورود با MFA و ایمیل مجاز را می‌خواهد.
- برنامه خودش هم دوباره بررسی می‌کند. هر درخواست روی 5081 باید توکن امضاشده Access همین Application را داشته باشد (امضا، `aud`، `iss`، زمان انقضا). اگر نداشته باشد، 403 می‌گیرد. اگر تنظیم Cloudflare اشتباه شود یا برنامه‌ای روی همین کامپیوتر مستقیم به پورت وصل شود، باز هم چیزی باز نمی‌شود.
- روی این کانال فقط صفحه‌های برنامه، ورود و خروج، تغییر رمز و هفت گزارش کاری جواب می‌گیرند: `dashboard`، `overview`، `patients`، `doctors`، `diagnostics`، `surgeries`، `diseases`. بقیه، از جمله پرونده مریض، پول، چاپ، فایل‌ها و هر تغییر، 404 می‌گیرند.
- ورود آنلاین فقط برای حسابی پذیرفته می‌شود که **تنها** نقش «بیننده گزارش‌ها (آنلاین)» (`DoctorReportsViewer`) را داشته باشد. این نقش فقط `reports.view` دارد: شمارش‌ها، بدون نام مریض و بدون پول.
- **قطع انترنت:** کار داخل شفاخانه از این کانال نمی‌گذرد. سرویس برنامه به `cloudflared` وابسته نیست و درخواست‌های شبکه داخلی هیچ بررسی Cloudflare ندارند. با قطع انترنت فقط گزارش آنلاین قطع می‌شود و پس از وصل‌شدن، خودش برمی‌گردد.

### پیش‌نیاز

- دامنه‌ای که DNS آن در Cloudflare است.
- Cloudflare Zero Trust (طرح رایگان تا ۵۰ کاربر کافی است). نام تیم را داشته باشید: `<team>.cloudflareaccess.com`.
- یک Identity Provider که MFA دارد و آن را به Cloudflare اعلام می‌کند، مثلاً Microsoft Entra ID با MFA اجباری. **One-time PIN ایمیلی MFA نیست.** آن را خاموش کنید.

### گام ۱: Access، پیش از Tunnel

در Zero Trust:

1. `Settings → Authentication`: Identity Provider را اضافه کنید و One-time PIN را حذف کنید.
2. `Access → Applications → Add → Self-hosted`:
   - Domain: `reports.example.org`
   - Session Duration: `8 hours` یا کمتر
   - Identity providers: فقط همان IdP، با Instant Auth
3. Policy با Action `Allow`:
   - Include: `Emails`، فهرست ایمیل داکتران مجاز (نه Everyone و نه دامنه کامل)
   - Require: `Authentication method` = `mfa`
4. از صفحه Application، مقدار **Application Audience (AUD) Tag** را بردارید (۶۴ حرف).

### گام ۲: ساخت Tunnel (روی لپ‌تاپ مسئول IT، نه میزبان)

نسخه مشخصی از `cloudflared.exe` را از صفحه رسمی Cloudflare (GitHub `cloudflare/cloudflared` Releases) بگیرید و Hash آن را بررسی کنید.

```powershell
cloudflared tunnel login                       # فایل cert.pem می‌سازد. روی میزبان نگذارید
cloudflared tunnel create alshifa-reports      # UUID و فایل <UUID>.json را چاپ می‌کند
cloudflared tunnel route dns alshifa-reports reports.example.org
```

`route dns` را فقط پس از گام ۱ اجرا کنید. پس از آن، نام عمومی است و فقط Access جلوی آن است.

### گام ۳: روی میزبان (Administrator)

فایل `<UUID>.json` و `cloudflared.exe` را با USB به میزبان ببرید، بعد:

```powershell
C:\AlShifa\scripts\remote-access.ps1 -ApprovedBy "<نام تأییدکننده، تاریخ>" `
  -Hostname reports.example.org -TunnelId <UUID> -CredentialsFile E:\<UUID>.json `
  -TeamDomain <team>.cloudflareaccess.com -Audience <AUD Tag> -Cloudflared E:\cloudflared.exe
```

اسکریپت به این ترتیب کار می‌کند:

1. فایل‌ها را در `C:\AlShifa\cloudflared` می‌گذارد. فقط Administrators و SYSTEM به آن دسترسی دارند.
2. `config.yml` را می‌سازد و با `cloudflared ingress validate` بررسی می‌کند.
3. پورت 5081 و `RemoteAccess` را به `appsettings.Production.json` اضافه می‌کند. نسخه قبلی در `.before-remote-access` می‌ماند.
4. برنامه را دوباره راه می‌اندازد. اگر پورت 5081 بدون توکن جواب دیگری جز 403 بدهد، تنظیمات را برمی‌گرداند و متوقف می‌شود.
5. سرویس `Cloudflared` را نصب و روشن می‌کند.
6. نام عمومی را بدون ورود باز می‌کند. اگر به صفحه ورود `cloudflareaccess.com` نرود، Tunnel را فوراً **متوقف** می‌کند.
7. فعال‌شدن و نام تأییدکننده را در `C:\AlShifa\logs\remote-access.log` ثبت می‌کند.

### گام ۴: حساب‌ها

برای هر داکتر در برنامه حسابی جدا با **فقط** نقش «بیننده گزارش‌ها (آنلاین)» و رمز قوی بسازید، و ایمیل او را در Policy گام ۱ بگذارید. برای بستن دسترسی یک نفر، ایمیل او را از Policy بردارید **و** حسابش را در برنامه غیرفعال کنید.

### قطع فوری

```powershell
C:\AlShifa\scripts\remote-access.ps1 -Disable
```

سرویس Tunnel را متوقف و غیرفعال می‌کند. کار داخل شفاخانه ادامه دارد. از بیرون هم می‌توانید در Zero Trust، Tunnel یا Application را غیرفعال کنید.

### لاگ امنیتی

| کجا | چه چیزی |
|---|---|
| Zero Trust → Logs → Access | هر ورود و رد، با ایمیل، IP و کشور |
| برنامه → Audit | `Login` با «Online, Cloudflare Access: ایمیل»، `LoginFailed`، `RemoteLoginDenied` |
| Event Viewer → Application، منبع `AlShifa.Api` | هشدار `Remote request refused` برای هر درخواست بدون توکن معتبر یا بیرون از گزارش‌ها |
| `C:\AlShifa\logs\cloudflared.log` | اتصال و قطع Tunnel |
| `C:\AlShifa\logs\remote-access.log` | فعال و غیرفعال‌شدن، و تأییدکننده |

هر ماه Logهای Access را ببینید و فهرست ایمیل‌های Policy را با داکتران فعلی مقایسه کنید.

### پشتیبان و بازیابی

داده تازه‌ای ساخته نمی‌شود. پشتیبان و تمرین ماهانه بازیابی (بخش ۸) همان است. `appsettings.Production.json` در `update.ps1` حفظ می‌شود. فایل اعتبار Tunnel عمداً در پشتیبان نیست، چون رمز است. اگر میزبان از دست برود، در Zero Trust یک Tunnel تازه بسازید و گام ۲ و ۳ را تکرار کنید. Tunnel قبلی را حذف کنید.

### آزمایش روی محل

| # | آزمایش | نتیجه درست |
|---|---|---|
| 1 | از انترنت بیرونی، `https://reports.example.org/` | صفحه ورود Cloudflare Access |
| 2 | ورود با ایمیل مجاز **بدون** MFA | رد می‌شود |
| 3 | ورود با ایمیلی که در Policy نیست | رد می‌شود |
| 4 | ایمیل مجاز با MFA، بعد حساب «بیننده گزارش‌ها» | گزارش‌های کاری باز می‌شوند. پول، مریضان و چاپ دیده نمی‌شوند |
| 5 | همان‌جا با حساب مدیر | «این حساب برای دسترسی آنلاین مجاز نیست» |
| 6 | روی میزبان: `Invoke-WebRequest http://127.0.0.1:5081/api/auth/csrf` | 403 |
| 7 | `Test-NetConnection <IP عمومی> -Port 3307` از بیرون | بسته |
| 8 | کابل انترنت روتر را بکشید، از لپ‌تاپ پذیرش مریض ثبت کنید | ثبت می‌شود |
| 9 | انترنت را وصل کنید | گزارش آنلاین در چند دقیقه برمی‌گردد |
| 10 | `remote-access.ps1 -Disable`، بعد آزمایش 1 | دیگر باز نمی‌شود. داخل شفاخانه کار ادامه دارد |

## ۸. پشتیبان‌گیری

| تنظیم | مقدار | دلیل |
|---|---|---|
| تکرار | هر ۴ ساعت، و پس از روشن‌شدن اگر اجرایی جا مانده باشد | بیشترین داده از دست‌رفته در خرابی کامل میزبان: حدود ۴ ساعت. اگر کم است، در Task کمتر کنید |
| نگهداری | همه تا ۷ روز، سپس یکی در روز تا ۳۵ روز | چند نقطه بازگشت، حتی برای اشتباهی که دیر فهمیده شود |
| محل | هارد بیرونی روی میزبان + USB چرخشی | دیسک جدا الزامی است و اسکریپت همان دیسک را رد می‌کند |
| محتوا | `db.sql`، فایل‌های ضمیمه، کلیدهای نشست | پایگاه داده به‌صورت Snapshot سازگار بدون قفل‌کردن کاربران |
| رمزنگاری | استاندارد OpenPGP با GnuPG: AES-256، کلید از رمز با SHA-512، بررسی یکپارچگی | روشی آزموده و شناخته‌شده. فایل با Kleopatra یا `gpg` روی هر کامپیوتری و فقط با رمز چاپ‌شده باز می‌شود. خرابی یا تغییر فایل رد می‌شود. پس از هر پشتیبان، نسخه روی مقصد باز و با اصل مقایسه می‌شود |
| اطلاع از خطا | هشدار قرمز در برنامه برای نقش‌های دارای `settings.view`، Application Log در Windows | پس از ۲۴ ساعت بدون پشتیبان موفق هم هشدار می‌دهد |
| نمایش و اجرای دستی | **تنظیمات ← پشتیبان‌گیری**: وضعیت، مقصد، فهرست فایل‌ها، و دکمه «پشتیبان‌گیری همین حالا» برای `settings.edit` | برنامه حق اجرای چیزی را ندارد: رویداد 4100 در Application Log می‌نویسد و Task همان `AlShifa Backup` با Trigger آن رویداد اجرا می‌شود. `update.ps1` این Trigger را به نصب‌های قبلی اضافه می‌کند |

- **هر هفته:** USB را عوض کنید و یکی را بیرون از ساختمان بگذارید.
- **هر ماه:** `C:\AlShifa\scripts\restore-test.ps1 -Backup F:\AlShifaBackups` را اجرا کنید. این آزمایش در دیتابیس جدا انجام می‌شود و حساب آن به دیتابیس اصلی دسترسی ندارد.
- **بیرون از محل (اختیاری):** برای `-BackupOptionalDestination` می‌توانید پوشه‌ای بدهید که با ابزار دیگری به بیرون همگام می‌شود. فایل از قبل رمز شده است و نبود آن کار را متوقف نمی‌کند.

### بازیابی واقعی

فقط پس از تصمیم مسئول و با یک نسخه از وضعیت فعلی. دیتابیس جدید بسازید، بررسی کنید، بعد جایگزین کنید:

```powershell
# 1) توقف برنامه؛ کاربران پیام قطع اتصال می‌بینند
Stop-Service AlShifaApi
# 2) باز کردن پشتیبان روی دیسک رمزشده
C:\AlShifa\scripts\expand-backup.ps1 -BackupFile F:\AlShifaBackups\<فایل> -OutputDir C:\AlShifa\backup-staging\recovery -PasswordFile C:\AlShifa\secrets\backup-password.txt
# 3) از وضعیت فعلی هم پشتیبان بگیرید اگر دیتابیس هنوز بالا است
# 4) با رمز root در C:\AlShifa\secrets\mysql-root.txt
C:\AlShifa\mysql\bin\mysql.exe --protocol=TCP -h 127.0.0.1 -P 3307 -u root -p -e "DROP DATABASE alshifa; CREATE DATABASE alshifa CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"
C:\AlShifa\mysql\bin\mysql.exe --protocol=TCP -h 127.0.0.1 -P 3307 -u root -p --default-character-set=utf8mb4 alshifa -e "source C:/AlShifa/backup-staging/recovery/db.sql"
# 5) فایل‌ها
robocopy C:\AlShifa\backup-staging\recovery\storage C:\AlShifa\storage /E
Start-Service AlShifaApi
C:\AlShifa\scripts\health-check.ps1
# 6) پوشه recovery داده خوانا دارد و باید پاک شود
Remove-Item C:\AlShifa\backup-staging\recovery -Recurse -Force
```

روی لپ‌تاپ جایگزین: اول `install.ps1` را اجرا کنید، رمز چاپ‌شده را در یک فایل بدون خط اضافه بگذارید و همین مراحل را انجام دهید. گواهی جدید ساخته می‌شود و باید دوباره روی لپ‌تاپ کارمندان نصب شود.

## ۹. کارهای روزمره

| کار | دستور (Administrator) |
|---|---|
| بررسی سلامت | `C:\AlShifa\scripts\health-check.ps1` |
| ری‌استارت امن برنامه | `Restart-Service AlShifaApi` (کاربران وارد می‌مانند؛ کاری که هنگام ری‌استارت در حال ذخیره باشد خطا می‌دهد و باید بررسی شود) |
| خطاها | Event Viewer → Windows Logs → Application، منبع `AlShifa.Api` و `AlShifaBackup`. لاگ MySQL: `C:\AlShifa\logs\mysql.err` |
| به‌روزرسانی (بیرون از ساعت کاری) | `<release>\scripts\update.ps1 -BackupDestination F:\AlShifaBackups` |

### فراخوان صوتی و مجموعه صوتی

فراخوان روزمره اینترنت نمی‌خواهد. فایل‌های صوتی یک بار ساخته می‌شوند و در `C:\AlShifa\storage\voice` می‌مانند (۱۵۴ فایل: شماره ۱ تا ۱۰۰، هفت بخش، اتاق ۱ تا ۲۰، هر مقصد با حالت عادی و «دوباره»). شماره نوبت بالاتر از ۱۰۰ فراخوان صوتی نمی‌شود. متن‌ها در `scripts\voice-phrases.json` است.

**مجوز:** صدا `fa-IR-FaridNeural` از Azure Speech است. فایل‌ها را با منبع Azure خود شفاخانه بسازید و پیش از استفاده شرایط فعلی Microsoft را بررسی کنید. راه‌های غیررسمی استفاده از این صدا مجوز ندارند و این ابزار از آن‌ها استفاده نمی‌کند.

روی هر رایانه دارای اینترنت (با کلید خودتان؛ کلید جایی ذخیره نمی‌شود و اجرای قطع‌شده بدون هزینه دوباره ادامه می‌یابد):

```powershell
$env:AZURE_SPEECH_KEY = '<کلید>'
<release>\scripts\build-voice-pack.ps1 -Mode Generate -Region <region> -VoiceDir E:\voice
```

سپس روی میزبان بدون اینترنت (Administrator):

```powershell
<release>\scripts\build-voice-pack.ps1 -Mode Import -SourceDir E:\voice
<release>\scripts\build-voice-pack.ps1 -Mode Check
```

اگر فایل‌ها از منبع مجاز دیگری می‌آیند، `-Mode Export -ExportFile clips.csv` فهرست نام فایل و متن را می‌دهد. قالب MP3 لازم است.

**همراه با نصب:** `build-installer.ps1 -VoicePack <پوشه فایل‌ها>` فایل‌ها را داخل نصب‌کننده می‌گذارد و هنگام نصب خودکار وارد می‌شوند. این کار فایل‌ها را همراه نصب‌کننده توزیع می‌کند، پس مجوز صدا را پیش از آن بررسی کنید.

**ساخت رایگان با Piper:** `deploy\windows\build-voice-pack-piper.py` فایل‌ها را بدون کلید و حساب با صدای محلی Piper می‌سازد و غلط‌های تلفظ شناخته‌شده را اصلاح می‌کند. صدای پیشنهادی `fa_IR-amir-medium` است، با تلفظ خود صدا و بدون تغییر: برای مراجعان افغانستان از همه قابل‌فهم‌تر است. فقط سرعت آن کمی کم شده (`LENGTH_SCALE = 1.2`). متن شماره «نوبت شماره {0}،» است. پیش از استفاده مجوز صدا را بررسی کنید: داده `amir` با CC0 آمده، اما از صدای Lessac ساخته شده که داده‌اش فقط مجوز تحقیقاتی دارد. پذیرش این خطر با مسئول شفاخانه است. فایل‌های آماده با صدای amir در پوشه `voice/amir` همین repo هستند و با `Import-AlShifa-Voice.exe` یا `build-installer.ps1 -VoicePack voice\amir` نصب می‌شوند.

**رایانه اسپیکر:** یک کاربر با نقش «مرکز پخش صوت» بسازید، در Edge یا Chrome صفحه «مرکز پخش صوت» را باز کنید، «فعال‌سازی صدا در این رایانه» را بزنید و «آزمایش صدا» را امتحان کنید. پنجره باز و در پیش‌زمینه بماند. پس از هر Reload یا روشن‌شدن دوباره، یک بار دیگر صدا را فعال کنید. شماره اتاق‌ها در «تنظیمات ← فراخوان» است و هر داکتر باید اتاق داشته باشد.

### گواهی HTTPS

- گواهی محلی است و به انترنت نیاز ندارد. تا ۵ سال اعتبار دارد و `health-check.ps1` از ۶۰ روز پیش از پایان هشدار می‌دهد.
- کلید خصوصی CA پس از صدور گواهی از بین می‌رود. پس دزدیدن میزبان اجازه ساختن گواهی معتبر برای سایت‌های دیگر را نمی‌دهد. بهای آن: برای تمدید یا تغییر آدرس، گواهی تازه باید روی همه لپ‌تاپ‌ها نصب شود.
- کلید گواهی سرور در `server.pfx` است و رمز آن در `appsettings.Production.json`. هر دو فقط برای SYSTEM، Administrators و سرویس قابل خواندن‌اند. محافظت در برابر دزدی لپ‌تاپ با BitLocker است.

وقتی آدرس لپ‌تاپ اصلی عوض شد یا گواهی رو به پایان است:

```powershell
$p = (Get-Content C:\AlShifa\app\appsettings.Production.json -Raw | ConvertFrom-Json).Kestrel.Endpoints.Lan.Certificate.Password
C:\AlShifa\scripts\new-lan-certificate.ps1 -IpAddress <آدرس جدید> -OutputDir C:\AlShifa\certs -PfxPassword $p
Restart-Service AlShifaApi
# سپس alshifa-ca.cer جدید را روی همه لپ‌تاپ‌ها نصب کنید
```

### Rollback

`update.ps1` نسخه قبلی را در `C:\AlShifa\app.previous` نگه می‌دارد. اگر Migration اجرا نشده باشد: سرویس را متوقف کنید، `app` را با `app.previous` عوض کنید و سرویس را روشن کنید. اگر Migration اجرا شده باشد، نسخه قبلی با دیتابیس جدید بالا نمی‌آید (برنامه با پیام «The database is newer» متوقف می‌شود) و باید پشتیبان ابتدای به‌روزرسانی طبق «بازیابی واقعی» برگردانده شود. MySQL تغییر ساختار را برنمی‌گرداند، پس پشتیبان تنها راه بازگشت است. بخش `Down` در Migrationها جدول `VisitTokens` و `Refractions` را حذف می‌کند و روی دیتابیس واقعی نباید استفاده شود.

برنامه روی دیتابیس واقعی خودسرانه Migration اجرا نمی‌کند: اجرای خودکار فقط در Development و Testing و فقط روی دیتابیس با پسوند `_dev`، `_test` یا `_load` است. `--migrate` روی دیتابیس دارای داده بدون `--backup-taken` رد می‌شود و این گزینه را فقط `update.ps1` پس از پشتیبان و آزمون بازیابی می‌دهد. اگر برنامه و دیتابیس هم‌نسخه نباشند، برنامه شروع نمی‌شود و `/health` مقدار `Unhealthy` می‌دهد.

## ۱۰. آزمایش روی محل

چک‌لیست قابل اجرا و نتیجه‌های درست: `PILOT-CHECKLIST.md`. هنوز هیچ‌کدام انجام نشده است.
