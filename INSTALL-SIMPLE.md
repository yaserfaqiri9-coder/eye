# نصب ساده: یک لپ‌تاپ اصلی و بقیه از طریق روتر

یک لپ‌تاپ برنامه و دیتابیس را نگه می‌دارد. بقیه فقط مرورگر باز می‌کنند و چیزی نصب نمی‌کنند. همه به یک روتر وصل‌اند. انترنت برای کار لازم نیست.

**این نصب هنوز روی هیچ لپ‌تاپ واقعی اجرا نشده است.** اول روی یک لپ‌تاپ آزمایشی و با داده ساختگی انجام دهید.

## آنچه روی USB لازم دارید

| چه | از کجا |
|---|---|
| پوشه `alshifa-<تاریخ>` | از کامپیوتر توسعه: `deploy\windows\build-release.ps1` |
| MySQL 8.4 نسخه ZIP، بازشده | dev.mysql.com |
| Gpg4win (برای رمز پشتیبان) | gpg4win.org |
| Visual C++ Redistributable x64 | microsoft.com |

به‌علاوه یک **هارد بیرونی** برای پشتیبان.

## بخش ۱: روتر (یک بار)

1. لپ‌تاپ اصلی را با **کیبل** به روتر وصل کنید.
2. وارد صفحه تنظیمات روتر شوید. بخش **DHCP Reservation** (یا Address Reservation) را پیدا کنید.
3. برای این لپ‌تاپ یک آدرس ثابت بگذارید، مثلاً `192.168.1.10`. لپ‌تاپ را ری‌استارت کنید.
4. اگر روتر گزینه **AP Isolation** یا **Client Isolation** دارد، خاموش کنید. روشن بودن آن لپ‌تاپ‌های Wi-Fi را از هم جدا می‌کند.
5. **Port Forwarding نسازید.**

در این راهنما به‌جای `192.168.1.10` آدرس واقعی خود را بگذارید.

## بخش ۲: لپ‌تاپ اصلی (یک بار)

1. VC++ و Gpg4win را نصب کنید. هر VPN دیگر (جز Tailscale) را خاموش کنید. BitLocker را روشن کنید.
2. هارد بیرونی را وصل کنید. فرض: حرف آن `F:` است.
3. PowerShell را **Run as Administrator** باز کنید و به پوشه `scripts` بروید:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   cd C:\Temp\alshifa-<تاریخ>\scripts
   Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private
   ```

4. بررسی بدون تغییر. هر خط `FAIL` را پیش از ادامه درست کنید:

   ```powershell
   .\preflight.ps1 -LanIp 192.168.1.10 -MySqlZipFolder C:\Temp\mysql-8.4.x-winx64 -BackupDestination F:\AlShifaBackups
   ```

5. نصب. از شما رمز مدیر سیستم پرسیده می‌شود:

   ```powershell
   .\install.ps1 -LanIp 192.168.1.10 -MySqlZipFolder C:\Temp\mysql-8.4.x-winx64 -BackupDestination F:\AlShifaBackups
   ```

6. در پایان باید همه خطوط `OK` باشد.
7. **فایل `C:\AlShifa\secrets\backup-password.txt` را چاپ کنید** و بیرون از ساختمان نگه دارید. بدون این رمز هیچ پشتیبانی باز نمی‌شود.
8. درستی کار را از لپ‌تاپ دوم بررسی کنید (بخش ۳).

**اگر نصب خطا داد:** خود پیام می‌گوید کجا ایستاد. برای برگشت:

```powershell
.\uninstall.ps1 -MoveFolderAside
```

چیزی پاک نمی‌شود. پوشه با نام جدید کنار می‌رود.

## بخش ۳: هر لپ‌تاپ دیگر

1. به همان روتر وصل شوید (کیبل یا Wi-Fi). Wi-Fi مهمان استفاده نکنید. VPN را خاموش کنید.
2. فایل `C:\AlShifa\certs\alshifa-ca.cer` را از لپ‌تاپ اصلی با USB بیاورید. این فایل راز نیست.
3. PowerShell را با Administrator باز کنید:

   ```powershell
   certutil -addstore -f Root E:\alshifa-ca.cer
   ```

4. Edge یا Chrome را باز کنید و بروید به: `https://192.168.1.10/`
5. صفحه ورود باید **بدون هشدار امنیتی** باز شود. یک میانبر روی دسکتاپ بسازید.

## اگر باز نشد

| نشانه | کار |
|---|---|
| «This site can't be reached» | روی لپ‌تاپ دوم: `Test-NetConnection 192.168.1.10 -Port 443`. اگر ناموفق بود: کیبل، Wi-Fi مهمان، AP Isolation، پروفایل شبکه لپ‌تاپ اصلی (باید Private باشد) |
| هشدار «Not secure» | گواهی روی این لپ‌تاپ نصب نیست، یا آدرس لپ‌تاپ اصلی عوض شده است. هشدار را رد نکنید |
| «سیستم بارگذاری نشد» یا خطای سرور | روی لپ‌تاپ اصلی: `C:\AlShifa\scripts\health-check.ps1` |
| ساعت لپ‌تاپ اشتباه است | ساعت را درست کنید؛ گواهی به ساعت حساس است |

## سه قانون

- لپ‌تاپ اصلی را خاموش نکنید و کارمندان روی آن Administrator نباشند. بستن درب مشکلی ندارد.
- روتر و سوییچ را به UPS وصل کنید.
- پیش از هر کار با مریض واقعی، `PILOT-CHECKLIST.md` را کامل کنید.

جزئیات، بازیابی و به‌روزرسانی: `DEPLOYMENT-WINDOWS.md`.
