# استقرار روی لپ‌تاپ شفاخانه (Windows)

راهنمای کامل مسئول IT. برای نصب کوتاه: `INSTALL-SIMPLE.md`. برای آزمایش روی محل: `PILOT-CHECKLIST.md`. یک لپ‌تاپ میزبان است و بقیه فقط مرورگر باز می‌کنند. یک برنامه، یک دیتابیس، بدون همگام‌سازی.

```
لپ‌تاپ‌های کارمندان ──HTTPS (LAN)──┐
                                   ├──> AlShifa.Api (سرویس Windows) ──> MySQL (فقط 127.0.0.1)
رئیس شفاخانه ──Tailscale (رمزشده)──┘
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

## ۶. دسترسی رئیس شفاخانه از آلمان (Tailscale)

به حساب Tailscale نیاز دارد. دیتابیس و پورت عمومی باز نمی‌شود و برنامه همان برنامه است. Funnel را **هرگز** فعال نکنید.

1. در Tailscale با حسابی وارد شوید که MFA دارد (Google یا Microsoft با تأیید دومرحله‌ای).
2. در Admin Console: `DNS` → MagicDNS و HTTPS Certificates را روشن کنید. `Settings` → Device Approval را روشن کنید.
3. در `Access Controls` قانون پیش‌فرض (همه به همه) را با این عوض کنید:

   ```json
   {
     "groups": { "group:director": ["director@example.com"] },
     "tagOwners": { "tag:alshifa-host": ["autogroup:admin"] },
     "grants": [
       { "src": ["group:director"], "dst": ["tag:alshifa-host"], "ip": ["tcp:443"] }
     ]
   }
   ```

4. روی میزبان، Tailscale را نصب کنید و با دسترسی Administrator:

   ```powershell
   tailscale up --advertise-tags=tag:alshifa-host --hostname=alshifa
   tailscale set --unattended=true
   tailscale serve --bg http://127.0.0.1:5080
   tailscale serve status
   tailscale funnel status
   ```

   در `funnel status` نباید چیزی عمومی باشد. برای نام `alshifa.<tailnet>.ts.net` گواهی عمومی صادر می‌شود، پس این نام در فهرست‌های عمومی گواهی دیده می‌شود، ولی بدون مجوز Tailscale قابل دسترسی نیست.

   برنامه هم روی پورت 443 گوش می‌دهد. انتظار می‌رود Serve درخواست‌های Tailscale را جدا بگیرد، ولی این آزمایش نشده است. اگر آدرس باز نشد، از `tailscale serve --bg --https=8443 http://127.0.0.1:5080` و همان پورت در قانون `grants` و آدرس استفاده کنید.

5. روی دستگاه رئیس، Tailscale را با همان ایمیل نصب کنید و دستگاه را در Admin Console تأیید کنید. آدرس: `https://alshifa.<tailnet>.ts.net/`.
6. در برنامه برای او حساب با نقش **مدیر (Manager)** و رمز قوی بسازید. VPN جای مجوز برنامه را نمی‌گیرد.

## ۷. پشتیبان‌گیری

| تنظیم | مقدار | دلیل |
|---|---|---|
| تکرار | هر ۴ ساعت، و پس از روشن‌شدن اگر اجرایی جا مانده باشد | بیشترین داده از دست‌رفته در خرابی کامل میزبان: حدود ۴ ساعت. اگر کم است، در Task کمتر کنید |
| نگهداری | همه تا ۷ روز، سپس یکی در روز تا ۳۵ روز | چند نقطه بازگشت، حتی برای اشتباهی که دیر فهمیده شود |
| محل | هارد بیرونی روی میزبان + USB چرخشی | دیسک جدا الزامی است و اسکریپت همان دیسک را رد می‌کند |
| محتوا | `db.sql`، فایل‌های ضمیمه، کلیدهای نشست | پایگاه داده به‌صورت Snapshot سازگار بدون قفل‌کردن کاربران |
| رمزنگاری | استاندارد OpenPGP با GnuPG: AES-256، کلید از رمز با SHA-512، بررسی یکپارچگی | روشی آزموده و شناخته‌شده. فایل با Kleopatra یا `gpg` روی هر کامپیوتری و فقط با رمز چاپ‌شده باز می‌شود. خرابی یا تغییر فایل رد می‌شود. پس از هر پشتیبان، نسخه روی مقصد باز و با اصل مقایسه می‌شود |
| اطلاع از خطا | هشدار قرمز در برنامه برای نقش‌های دارای `settings.view`، Application Log در Windows | پس از ۲۴ ساعت بدون پشتیبان موفق هم هشدار می‌دهد |

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

## ۸. کارهای روزمره

| کار | دستور (Administrator) |
|---|---|
| بررسی سلامت | `C:\AlShifa\scripts\health-check.ps1` |
| ری‌استارت امن برنامه | `Restart-Service AlShifaApi` (کاربران وارد می‌مانند؛ کاری که هنگام ری‌استارت در حال ذخیره باشد خطا می‌دهد و باید بررسی شود) |
| خطاها | Event Viewer → Windows Logs → Application، منبع `AlShifa.Api` و `AlShifaBackup`. لاگ MySQL: `C:\AlShifa\logs\mysql.err` |
| به‌روزرسانی (بیرون از ساعت کاری) | `<release>\scripts\update.ps1 -BackupDestination F:\AlShifaBackups` |

### فراخوان صوتی و مجموعه صوتی

فراخوان روزمره اینترنت نمی‌خواهد. فایل‌های صوتی یک بار ساخته می‌شوند و در `C:\AlShifa\storage\voice` می‌مانند (۱۰۵۳ فایل: شماره ۱ تا ۹۹۹، هفت بخش، اتاق ۱ تا ۲۰، هر مقصد با حالت عادی و «دوباره»). متن‌ها در `scripts\voice-phrases.json` است.

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

## ۹. آزمایش روی محل

چک‌لیست قابل اجرا و نتیجه‌های درست: `PILOT-CHECKLIST.md`. هنوز هیچ‌کدام انجام نشده است.
