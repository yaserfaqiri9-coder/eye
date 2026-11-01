# سیستم مدیریت شفاخانه چشم الشفا

توسعه‌دهنده: Novatech Technology Solutions

سیستم چندکاربره و چندبخشی برای مدیریت پذیرش، معاینه، آزمایش، عملیات، دواخانه، عینک، صندوق، حسابداری و گدام با یک Backend و یک دیتابیس مرکزی.

**وضعیت:** همه ماژول‌ها روی ماشین توسعه اجرا و آزمایش شده‌اند، ولی برای بهره‌برداری آماده اعلام نشده است: فیلدهای بالینی و مالی منتظر تأیید شفاخانه‌اند و استقرار روی سرور واقعی آزمایش نشده است. وضعیت دقیق در [PROGRESS.md](PROGRESS.md) است.

## اجرای محلی

روی `start.bat` دوبار کلیک کنید. دیتابیس، API و رابط کاربری را بالا می‌آورد و مرورگر را باز می‌کند.

یا جداگانه:

```powershell
scripts\dev-db.ps1        # MySQL روی 127.0.0.1:3307
scripts\dev-api.ps1       # API روی 5080
cd frontend; npm run dev  # رابط کاربری روی http://localhost:5173
```

مستندات API در محیط توسعه: `http://localhost:5080/openapi/v1.json`

## اسناد

| سند | محتوا |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | معماری، نسخه‌ها، تصمیم‌ها |
| [DATABASE.md](DATABASE.md) | ERD و جدول‌ها |
| [WORKFLOWS.md](WORKFLOWS.md) | جریان‌های کاری و موارد نیازمند تأیید |
| [PERMISSIONS.md](PERMISSIONS.md) | ماتریس صلاحیت‌ها |
| [PROGRESS.md](PROGRESS.md) | وضعیت پروژه |
| [TESTING.md](TESTING.md) | سناریوهای آزمایش |
| [DEPLOYMENT.md](DEPLOYMENT.md) | نصب، پشتیبان‌گیری و بازیابی |

## ساختار

- `backend/` : ASP.NET Core 10 و تست‌ها
- `frontend/` : Vue 3
- `deploy/` : پیکربندی Nginx، systemd و پشتیبان‌گیری
- `scripts/` : اجرای محیط توسعه، آزمون بار و تمرین بازیابی
- `.tools/` : SDK و MySQL محلی توسعه (در Git نیست)
