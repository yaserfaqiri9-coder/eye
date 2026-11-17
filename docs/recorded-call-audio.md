# صدای ضبط‌شدهٔ فراخوان — نسخهٔ قابل بررسی

تاریخ: 2026-11-17. نسخهٔ بسته: `recorded-c98dd13ed170`.

پیاده‌سازی در سورس انجام شده و یک بستهٔ هماهنگ API و رابط ساخته شده است:
`.artifacts/recorded-audio/review/backend/`؛ رابط و تمام WAVها داخل `wwwroot` همین پوشه قرار دارند.
این پوشه خودکار اجرا یا مستقر نشده است. هیچ مایگریشن، بازنشانی، واردکردن اطلاعات، یا نوشتن آزمایشی در دیتابیس اجرا نشد. نسخهٔ ساخته‌شده شامل سایر تغییرات قبلی همین ورک‌اسپیس نیز هست؛ مخزن Git برای تفکیک یک commit در این مسیر موجود نیست.

## منبع شماره و مقصد

`CallEndpoints` شماره را از `Eligible(...).QueueNumber` می‌گیرد. `DestinationAsync` شناسهٔ داخلی مقصد را از تنظیمات بخش یا اتاق همان داکتر حل می‌کند. هنگام ثبت رویداد، `PatientCall.QueueNumber`، `PatientCall.Destination`، `PatientCall.Desk` و شناسهٔ مستقل رویداد ذخیره می‌شوند؛ این منطق قبلی حفظ شده است.

`RecordedClips` دقیقاً از این snapshot دو مسیر WAV تولید می‌کند. پاسخ `POST /api/calls/station/next` شامل `Id` و `Clips` است؛ در حالت محلی، پاسخ ثبت فراخوان `LocalClips` را برمی‌گرداند. رویداد SignalR یعنی `PatientCallRequested` فقط پمپ را بیدار می‌کند؛ متن نمایشی و انتخاب فعلی صفحه مقصد صوت را تعیین نمی‌کنند.

| شناسهٔ داخلی | فایل مقصد |
| --- | --- |
| `vision` یا `Desk=Vision` در اتاق اختصاصی | `destination_initial_exam.wav` |
| `room-NN` با `Desk=Doctor` | `destination_doctor.wav` |
| `refraction` یا اتاق اختصاصی همان Desk | `destination_refraction.wav` |
| `imaging` یا اتاق اختصاصی همان Desk | `destination_imaging.wav` |
| `pharmacy` یا اتاق اختصاصی همان Desk | `destination_pharmacy.wav` |
| `optical` یا اتاق اختصاصی همان Desk | `destination_optical.wav` |
| `reception` یا اتاق اختصاصی همان Desk | `destination_reception.wav` |

اتاق واقعی داکتر برای مسیردهی و نمایش حفظ می‌شود؛ صدای عمومی داکتر شمارهٔ اتاق را نمی‌گوید. `cashier` و `surgery` در بسته موجود و در resolver شناخته‌شده‌اند، ولی در enum و جریان فعلی فراخوان Desk مستقلی ندارند؛ برای آن‌ها صف یا مسیردهی جدید ساخته نشده است. `Lab` در سیستم وجود دارد اما بسته صدای آزمایشگاه ندارد.

## پخش و خطا

- تمام ۶۸ WAV با SHA-256 اصلی manifest تطبیق داده شد. فرمت همه: PCM 16-bit، mono، 48000 Hz.
- ۵۰ شماره + ۹ مقصد در آماده‌سازی مرورگر بارگذاری و Decode می‌شوند. ۹ فایل department همراه بسته نگهداری می‌شوند اما در اعلان پخش نمی‌شوند.
- دو buffer بدون تغییر نمونه‌ها، فید، یا نرمال‌سازی به یک AudioBuffer متصل می‌شوند؛ فقط ۳۰ میلی‌ثانیه سکوت افزوده می‌شود. سکوت لبه‌های اصلی نیز حفظ شده است (میانه حدود ۴۵ + ۶۵ میلی‌ثانیه).
- یک AudioBufferSource برای کل اعلان؛ Gain پیش‌فرض ۱ و فقط کنترل بلندی کلی قبلی اپراتور. صف مشترک در هر تب، کپی مسیرها هنگام enqueue، جلوگیری از تکرار بر مبنای Id رویداد، و یادآوری رویدادهای موفق در sessionStorage. فراخوان مجدد Id جدید می‌گیرد و همان عبارت ضبط‌شده را تکرار می‌کند.
- cache نام‌دار `alshifa-queue-recorded-c98dd13ed170`؛ مسیرهای نسخه‌دار فایل‌های قدیمی را کنار می‌گذارند. پس از آماده‌شدن کامل، فقط cacheهای قدیمی خانوادهٔ `alshifa-queue-` پاک می‌شوند. Service Worker قبلی در پروژه وجود ندارد.
- فایل ناموجود یا Decode ناموفق: هیچ نیم‌جمله‌ای شروع نمی‌شود؛ خطا و بارگذاری مجدد قابل دیدن است. برای تکرار اعلان ناموفق باید از همان بخش «فراخوان مجدد» زده شود. خطای قبلی صف را متوقف نمی‌کند.
- شمارهٔ کمتر از ۱، بیشتر از ۵۰، و مقصد بدون فایل: `Clips=[]`؛ ثبت معتبر فراخوان ادامه می‌یابد و پخش با دلیل مشخص ناموفق گزارش می‌شود. هیچ تبدیل به شماره/بخش دیگر و هیچ fallback به MP3 قدیمی وجود ندارد. endpoint قدیمی MP3 برای سازگاری ابزار نصب قبلی باقی است اما جریان جدید آن را مصرف نمی‌کند.
- آفلاین بودن اینترنت مانع پخش فایل‌های آماده نیست. دریافت فراخوان تازه هنوز به ارتباط با سرور محلی نیاز دارد. شروع صدای مرورگر نیازمند کلیک است. CacheStorage به secure context مثل localhost یا HTTPS وابسته است؛ بدون آن حافظهٔ همین تب و فایل‌های سرور محلی استفاده می‌شود.

## محدودهٔ نمایش و دسترسی

منطق `Eligible`، مسئولیت داکتر، مجوزهای بخش‌ها، گروه‌های SignalR و تصاحب تک‌مرکز صوت تغییر نکرده‌اند. نمایشگر فعلی عمومیِ مرکز فراخوان است و از `/api/calls/display` می‌خواند؛ فیلتر اختصاصیِ یک بخش برای تلویزیون در ساختار فعلی وجود ندارد و اضافه نشده است. حالت عادی تلویزیون passive باقی می‌ماند؛ فقط فعال‌سازی صریح یا حالت قبلی Windows-TV می‌تواند مرکز صوت شود. یک نمایشگر passive اعلان را مصرف نمی‌کند و مانع مرکز صوت دیگر نمی‌شود.

## فایل‌های سورس تغییرکرده

- `backend/src/AlShifa.Api/Features/Calls/PatientCall.cs`
- `backend/src/AlShifa.Api/Features/Calls/CallEndpoints.cs`
- `frontend/src/features/calls/clipPlayer.ts`
- `frontend/src/features/calls/audioStation.ts`
- `frontend/src/features/calls/useCallDesk.ts`
- `frontend/src/features/calls/AudioStationPage.vue`
- `frontend/src/features/calls/CallScreenPage.vue`
- `frontend/public/audio/recorded-c98dd13ed170/` (۶۸ WAV، manifest و CSV اصلی)
- `backend/tests/AlShifa.Tests/RecordedVoicePackTests.cs`
- `backend/tests/AlShifa.Tests/CallTests.cs` (به‌روزرسانی انتظارهای MP3 قبلی)
- `frontend/e2e/recorded-audio.spec.ts`
- `frontend/e2e/recorded-audio-ui.spec.ts`
- `frontend/e2e/audio-station.spec.ts` (انتظار یک buffer به‌جای دو پخش؛ حذف ساخت MP3 مصنوعی)
- همین سند.

## شواهد آزمایش

1. ۲۵ تست مستقل .NET بدون ApiFixture و بدون اتصال دیتابیس: شماره و مقصد واقعی، اتاق‌های متعدد داکتر، Vision با اتاق اختصاصی، شماره/مقصد پشتیبانی‌نشده، ۶۸ hash و فرمت WAV، و اجرای واقعی عضویت EventsHub با مجوزهای متفاوت.
2. ۱۰ تست مرورگر Edge با Web Audio واقعی و فایل‌های اصلی: شماره‌های ۱، ۲۵، ۵۰ و شش ترکیب مقصد؛ تطابق نمونه‌به‌نمونهٔ PCM؛ یک source؛ صف سریع؛ شمارهٔ ۱۲ برای داکتر و رفراکشن؛ تغییر آرایهٔ انتخاب پس از enqueue؛ رویداد تکراری؛ فراخوان مجدد؛ شکست فایل و تلاش دوباره؛ ادامهٔ صف پس از خطا؛ آماده‌سازی ۵۹ صدا و پخش با یک player تازه در حالت قطع کامل شبکه؛ dedup در player تازهٔ همان تب.
3. چهار تست رابط روی سورس و دوباره روی خروجی ساخته‌شده: TV passive فقط‌خواندنی، حفظ نام مریض و اتاق از پاسخ سرور، قطع و وصل نمایش، عدم تصاحب مرکز، خطای فایل و تلاش مجدد، گزارش ناموفق شمارهٔ ۵۱ و ادامهٔ اعلان بعدی، جلوگیری از دسترسی نمایشگر بدون احراز هویت. تمام درخواست‌های `/api/` در مرورگر intercept شده‌اند؛ چیزی به دیتابیس شفاخانه نرسیده است.
4. `vue-tsc` و Vite build موفق؛ backend build و publish موفق. hash تمام ۶۸ WAV در بستهٔ نهایی نیز تأیید شد.

دستورهای قابل تکرار (فقط مجموعه‌های مستقل بالا):

```powershell
.tools/dotnet/dotnet.exe test backend/tests/AlShifa.Tests/AlShifa.Tests.csproj --no-restore --filter FullyQualifiedName~RecordedVoicePackTests
# با Vite dev روی پورت 5199 و از پوشهٔ frontend:
$env:ALSHIFA_E2E_BASE='http://127.0.0.1:5199'
../.tools/node/node.exe node_modules/playwright/cli.js test e2e/recorded-audio.spec.ts e2e/recorded-audio-ui.spec.ts
```

مجموعه‌های یکپارچهٔ قدیمی `CallTests` و `audio-station.spec.ts` به دیتابیس آزمایشی قابل دورریختن نیاز دارند؛ در این کار اجرا نشدند تا هیچ بیمار، نوبت، پرداخت یا فراخوان آزمایشی در دیتابیس موجود ساخته نشود. تمام سورس تست‌های .NET کامپایل شد.

## محدودیت بازبینی شنیداری

مرورگر واقعاً اعلان‌های کامل را پردازش و به خروجی AudioContext ارسال کرد و اتصال دیجیتال بررسی شد. شنیدن و ارزیابی انسانیِ تلفظ و کیفیت از بلندگوی واقعی سالن از این محیط قابل تأیید نیست. پنج WAV کامل برای شنیدن در `.artifacts/recorded-audio/samples/` آماده است: ۱ داکتر، ۲۵ رفراکشن، ۵۰ دواخانه، ۱۲ داکتر و ۱۲ رفراکشن. برای پذیرش نهایی صدا باید همین نمونه‌ها روی بلندگوی مقصد شنیده شوند.
