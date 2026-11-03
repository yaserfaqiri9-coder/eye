# معماری — سیستم شفاخانه چشم الشفا

## ترکیب کلی

```
Browser (Vue 3 SPA) ──HTTPS──> Nginx ──> ASP.NET Core 10 ──> MySQL 8.4
        ▲                                      │
        └──────── SignalR (فقط اعلان) ─────────┘
```

روی لپ‌تاپ شفاخانه Nginx نیست: وقتی `wwwroot/index.html` کنار برنامه باشد، همان ASP.NET Core صفحه‌ها را هم با HTTPS سرو می‌کند (`DEPLOYMENT-WINDOWS.md`). در محیط توسعه و پشت Nginx این حالت خاموش است.

- **Modular Monolith**: یک پروژه Backend، یک Frontend، یک دیتابیس.
- بدون Microservices، Repository/UoW اضافی، CQRS/MediatR، Kafka، RabbitMQ، Redis.

## نسخه‌های ثابت

| جزء | نسخه | دلیل |
|---|---|---|
| .NET SDK | 10.0.403 | در `global.json` ثابت شده |
| ASP.NET Core / EF Core | 10.0.12 | `MySql.EntityFrameworkCore` 10.0.12 به EF Core 10.0.12 وابسته است؛ همه پکیج‌ها هم‌نسخه می‌مانند |
| MySql.EntityFrameworkCore | 10.0.12 | Provider رسمی |
| MySQL | 8.4.11 LTS | |
| Vue / Router / Pinia | 3.5.44 / 5.4.0 / 4.1.0 | |
| Vite / TypeScript / vue-tsc | 8.3.5 / 6.0.3 / 3.3.14 | TypeScript 7 با vue-tsc 3.3.14 اجرا نمی‌شود (خطای `./lib/tsc`) |
| Bootstrap | 5.3.8 | RTL داخلی Bootstrap |
| Remix Icon | 4.9.1 | آیکون‌ها، همان مجموعه قالب XRay، با مجوز Apache |
| @microsoft/signalr | 10.0.11 | |
| Vazirmatn | 33.0.3 | فونت فارسی با مجوز OFL |
| Poppins (`@fontsource/poppins`) | 5.3.0 | فقط حروف و عددهای لاتین، با مجوز OFL |
| Playwright | 1.64.1 | E2E |

## ساختار پوشه

```
backend/
  AlShifa.slnx
  src/AlShifa.Api/
    Program.cs
    Infrastructure/      DbContext, Outbox, Idempotency, Numbering, Errors
    Features/
      Auth/ Users/ Patients/ Visits/ Appointments/
      PreExam/ Consultations/ Prescriptions/ Optical/
      Diagnostics/ Surgery/ Pharmacy/ Inventory/
      Billing/ Cash/ Ledger/ Expenses/ Reports/ Settings/
      Realtime/ Audit/
  tests/AlShifa.Tests/   Integration روی MySQL واقعی
frontend/
  src/
    design/              Tokens و CSS مشترک
    components/          کامپوننت‌های مشترک
    layouts/ router/ stores/ api/ i18n/
    features/<module>/   صفحات هر بخش (Lazy)
deploy/                  nginx, systemd, backup
scripts/                 اجرای محیط توسعه
```

هر Feature شامل Endpoint، DTO، Validation و Service خودش است. منطق تجاری در Backend است؛ کامپوننت Vue فقط نمایش و ارسال درخواست می‌دهد.

## تصمیم‌های کلیدی

| موضوع | تصمیم | دلیل |
|---|---|---|
| Authentication | Identity + Cookie با `HttpOnly`, `Secure`, `SameSite=Strict` | توکن در JavaScript نگهداری نمی‌شود |
| CSRF | Antiforgery با هدر `X-XSRF-TOKEN` | درخواست‌های تغییردهنده |
| Authorization | Policy پویا `perm:module.action` از Claimهای نقش + بررسی سطح داده | اجرا در Backend |
| غیرفعال‌سازی کاربر | بررسی `SecurityStamp` و وضعیت در هر درخواست | قطع فوری دسترسی |
| Concurrency | `Version` و HTTP 409 با پیام فارسی | |
| Idempotency | هدر `Idempotency-Key` برای پرداخت، فروش و ... | جلوگیری از تکرار |
| Realtime | Outbox + BackgroundService + SignalR، بدون داده محرمانه | Commit مقدم بر اعلان |
| API Errors | ProblemDetails با `code` و `message` فارسی | پیام یکدست |
| OpenAPI | `Microsoft.AspNetCore.OpenApi` فقط در Development | |
| Ledger | دفتر دوطرفه غیرقابل ویرایش؛ اصلاح با تراکنش برگشتی | مانع دوباره‌شماری |
| زمان | UTC در دیتابیس، `Asia/Kabul` در نمایش | |
| تقویم | شمسی افغانی (نام ماه‌های دری) با `Intl` مرورگر | بدون وابستگی اضافی |
| فایل ضمیمه | بیرون از wwwroot، نام تصادفی، سفید-فهرست نوع و سقف حجم | دسترسی فقط از API |

## طراحی UI

مرجع: XRay (`templates.iqonic.design/xray/html/`). مقدارها از computed style هر ۷۳ صفحه قالب در 1600×900 خوانده شده است (`frontend/src/design/tokens.css`) و چون سیستم راست‌به‌چپ است، چیدمان آینه شده است. هیچ کد، تصویر یا دارایی از قالب برداشته نشده است. آیکون‌ها Remix Icon و فونت لاتین Poppins از npm است (هر دو همان‌هایی که قالب استفاده می‌کند)؛ متن دری با Vazirmatn نمایش داده می‌شود چون Poppins حروف فارسی ندارد.

## ساختار واقعی Backend

`Features/` شامل `Auth`, `Users`, `Patients`, `Visits`, `Clinical`, `Finance`, `Inventory`, `Realtime`, `Reports`, `Settings` است. موارد مشترک در `Infrastructure/` است.

## محیط توسعه روی این ماشین

- `.tools/dotnet` : SDK محلی (.NET سیستم 8 است و تغییر نمی‌کند).
- `.tools/mysql-8.4.11-winx64` : MySQL 8.4 مستقل روی `127.0.0.1:3307`.
- سرویس `mysql` روی 3306 **MariaDB 10.4 متعلق به XAMPP** است و این پروژه به آن دست نمی‌زند.
