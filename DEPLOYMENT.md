# استقرار

**برای لپ‌تاپ شفاخانه (Windows) راهنمای اصلی `DEPLOYMENT-WINDOWS.md` است.** این فایل مسیر جایگزین روی سرور Linux است و پشتیبان `deploy/backup.sh` آن رمزنگاری ندارد.

**وضعیت: روی هیچ سرور Linux اجرا نشده است.** این راهنما و فایل‌های `deploy/` پیش‌نویس هستند و باید روی سرور واقعی آزمایش شوند. استقرار فقط با مجوز صریح انجام می‌شود.

آنچه آزمایش شده: build نسخه Release در حالت Production روی Windows، با هدرهای پروکسی شبیه‌سازی‌شده؛ و پشتیبان‌گیری و بازیابی با `mysqldump` روی MySQL توسعه.

## پیش‌نیازها

Linux، .NET 10 Runtime (ASP.NET Core)، MySQL 8.4، Nginx، گواهی HTTPS.

## ۱. دیتابیس

MySQL فقط روی `127.0.0.1` گوش بدهد و پورت 3306 به بیرون باز نباشد.

```sql
CREATE DATABASE alshifa CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'alshifa'@'localhost' IDENTIFIED BY '<رمز قوی>';
GRANT ALL PRIVILEGES ON alshifa.* TO 'alshifa'@'localhost';
```

زمان سرور MySQL را روی UTC بگذارید: `default-time-zone=+00:00`.

## ۲. Backend

```bash
# روی ماشین build
cd backend && dotnet publish src/AlShifa.Api -c Release -o publish

# روی سرور
useradd --system --home /opt/alshifa alshifa
mkdir -p /opt/alshifa/api /var/lib/alshifa/storage /var/lib/alshifa/keys /etc/alshifa
chown -R alshifa:alshifa /var/lib/alshifa
chmod 700 /var/lib/alshifa/keys
```

فایل `/etc/alshifa/api.env` با دسترسی `600`:

```
ConnectionStrings__Default=Server=127.0.0.1;Port=3306;Database=alshifa;User=alshifa;Password=<رمز>;
```

`deploy/alshifa-api.service` را در `/etc/systemd/system/` بگذارید.

## ۳. Migration و اولین مدیر

در Production هیچ Migration خودکار یا حساب نمونه‌ای وجود ندارد.

```bash
set -a; . /etc/alshifa/api.env; set +a
export ASPNETCORE_ENVIRONMENT=Production
read -s ALSHIFA_ADMIN_PASSWORD; export ALSHIFA_ADMIN_PASSWORD
ALSHIFA_ADMIN_USER=admin dotnet AlShifa.Api.dll --create-admin
unset ALSHIFA_ADMIN_PASSWORD
systemctl enable --now alshifa-api
```

برای نسخه‌های بعدی، پیش از به‌روزرسانی پشتیبان بگیرید و `dotnet AlShifa.Api.dll --migrate` را اجرا کنید.

## ۴. Frontend و Nginx

```bash
cd frontend && npm ci && npm run build
# محتوای dist را در /var/www/alshifa کپی کنید
```

`deploy/nginx-alshifa.conf` و `deploy/alshifa-proxy.conf` را نصب و `hospital.example` را با نام واقعی عوض کنید. HSTS و CSP در Nginx تنظیم می‌شود. پس از نصب، CSP و WebSocket را با مرورگر واقعی بررسی کنید چون هنوز آزمایش نشده‌اند.

## ۵. پشتیبان‌گیری

`deploy/backup.sh` را روزانه اجرا کنید. فایل `/etc/alshifa/backup.cnf` با دسترسی `600`:

```
[client]
user=alshifa_backup
password=<رمز>
```

نسخه پشتیبان را به بیرون از سرور منتقل کنید. کلیدهای `/var/lib/alshifa/keys` را هم نگه دارید؛ بدون آن‌ها فقط نشست‌ها از بین می‌روند، نه داده.

## ۶. بازیابی

```bash
systemctl stop alshifa-api
gunzip -c alshifa-db-<زمان>.sql.gz | mysql --defaults-extra-file=/etc/alshifa/backup.cnf alshifa
tar -xzf alshifa-files-<زمان>.tar.gz -C /var/lib/alshifa
systemctl start alshifa-api
```

بازیابی را ماهی یک بار روی دیتابیس آزمایشی تمرین کنید؛ نسخه پشتیبانی که بازیابی نشده، پشتیبان مطمئن نیست.

## چک‌لیست پیش از بهره‌برداری

- [ ] موارد M-1 تا M-7 و F-1 تا F-6 در `WORKFLOWS.md` تأیید شده است
- [ ] `scripts/load/run-load.ps1` یا معادل آن روی سرور واقعی تکرار شده است
- [ ] HTTPS، WebSocket و CSP با مرورگر واقعی بررسی شده است
- [ ] چاپ روی چاپگرهای واقعی شفاخانه بررسی شده است
- [ ] پشتیبان خودکار فعال و یک بازیابی واقعی انجام شده است
- [ ] داده آزمایشی به Production منتقل نشده است
