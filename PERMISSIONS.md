# ماتریس صلاحیت‌ها — سیستم شفاخانه چشم الشفا

## اصول

- قالب هر صلاحیت: `module.action` (مثال: `patients.create`).
- Actionها: `view`, `create`, `edit`, `approve`, `cancel`, `print`, `export`, `refund`, `manage`.
- همه صلاحیت‌ها در Backend با Policy اجرا می‌شوند. مخفی‌کردن منو فقط برای راحتی کاربر است، نه امنیت.
- هر کاربر می‌تواند چند نقش داشته باشد؛ صلاحیت نهایی اجتماع صلاحیت نقش‌هاست.
- اصل Least Privilege: نقش `SuperAdmin` سیستم را مدیریت می‌کند و **به‌طور پیش‌فرض به داده طبی دسترسی ندارد**.

## ماژول‌ها

| ماژول | محدوده |
|---|---|
| `patients` | هویت مریض، جستجو، کارت |
| `visits` | مراجعه، صف، ارجاع |
| `appointments` | نوبت |
| `preexam` | معاینه ابتدایی |
| `consultation` | معاینه داکتر، تشخیص، پلان درمان، پیگیری |
| `clinicalrecord` | خلاصه و سوابق پرونده طبی |
| `prescriptions` | نسخه دوا |
| `opticalrx` | نسخه عینک |
| `diagnostics` | درخواست و نتیجه آزمایش |
| `surgery` | عملیات |
| `pharmacy` | فروش و تحویل دوا |
| `optical` | سفارش و فروش عینک |
| `inventory` | جنس، خرید، موجودی |
| `invoices` | فاکتور |
| `payments` | دریافت و برگشت وجه |
| `cash` | صندوق و جلسه صندوق |
| `ledger` | دفتر مالی |
| `expenses` | مصارف |
| `reports` | گزارش عملیاتی (`reports.view`) و مالی (`reports.manage`) |
| `users` | کاربران، نقش‌ها |
| `settings` | تنظیمات |
| `audit` | مشاهده Audit |

## ماتریس نقش‌ها

حروف: V=view، C=create، E=edit، A=approve، X=cancel، P=print، O=export، R=refund، M=manage

| ماژول | SuperAdmin | Manager | Reception | Triage | Doctor | Lab | Surgery | Pharmacist | Optical | Cashier | Accountant | Inventory |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| patients | V | V | VCEP | V | V | V | V | V | V | V | | |
| visits | V | V | VCEXP | VE | VE | V | V | | V | V | | |
| appointments | | V | VCEXP | | V | | V | | | | | |
| preexam | | | | VCE | V | | V | | | | | |
| consultation | | | | | VCEAP | | V | | | | | |
| clinicalrecord | | | | V | V | | V | | | | | |
| prescriptions | | | | | VCEXP | | | VP | | | | |
| opticalrx | | | | | VCEXP | | | | VP | | | |
| diagnostics | | V | | | VCXP | VCEAP | V | | | | | |
| surgery | | V | | | VCEAXP | | VEAP | | | | | |
| pharmacy | | V | | | | | | VCEXP | | V | V | V |
| optical | | V | | | | | | | VCEXP | V | V | |
| inventory | | V | | | | | | VC | VC | | V | VCEAXPO |
| invoices | | VO | | | | | | VC | VC | VCXP | VAO | |
| payments | | VO | | | | | | | | VCP | VAR | |
| cash | | V | | | | | | | | VCE | V | |
| ledger | | V | | | | | | | | | VCAO | |
| expenses | | V | | | | | | | | | VCEAX | |
| reports | | VPO | V | | V | V | V | V | V | V | VPOM | V |
| users | VCEM | V | | | | | | | | | | |
| settings | VEM | V | | | | | | | | | V | |
| audit | V | V | | | | | | | | | V | |

توضیح: `Manager` برای نظارت فقط آمار و گزارش می‌بیند و داده طبی جزئی را نمی‌بیند؛ `diagnostics` و `surgery` برای او فقط در سطح وضعیت و شمارش است.

## کنترل سطح داده

- **پرونده طبی:** داشتن `clinicalrecord.view` کافی نیست. کاربر باید به مریض مربوط باشد: داکتر تعیین‌شده در یکی از مراجعات آن مریض، نویسنده یکی از معاینات او، یا مریض امروز مراجعه باز داشته باشد. فرض فعلی این است که هر دارنده این صلاحیت به مراجعه باز دسترسی دارد (نیازمند تأیید M-5).
- **Break-glass:** در صورت نیاز اضطراری، کاربر دارای `clinicalrecord.view` با ثبت دلیل، دسترسی موقت ۴ ساعته می‌گیرد. هر مشاهده در `AuditLogs` ثبت می‌شود و برای مدیر قابل بررسی است. مدت و قواعد این سازوکار نیازمند تأیید مسئول طبی است.
- **یادداشت محرمانه داکتر:** فقط برای داکتر همان مراجعه و کاربران دارای دسترسی طبی مرتبط برگردانده می‌شود؛ در DTO آزمایشگاه، صندوق و دواخانه وجود ندارد.
- **DTO جدا برای هر بخش:** آزمایشگاه فقط درخواست و مشخصات لازم را می‌بیند، صندوق فقط نام خدمت و مبلغ را.

## SignalR

- Groupها سمت سرور و از روی صلاحیت کاربر ساخته می‌شوند: `dept:reception`، `dept:triage`، `dept:doctor`، `dept:lab`، `dept:surgery`، `dept:pharmacy`، `dept:optical`، `dept:cashier`، `dept:finance`، `dept:inventory`، `dept:management` و `user:{id}`.
- Client نمی‌تواند خودش عضو Group شود.
- رویدادها فقط نوع رویداد و شناسه دارند (بدون نام، تشخیص، یا مبلغ). داده واقعی با API و بررسی صلاحیت خوانده می‌شود.

## محیط عملیاتی

- حساب بذر و رمز نمونه فقط در Development ساخته می‌شوند.
- در Production اولین Super Admin با دستور CLI و رمز واردشده توسط مسئول ساخته می‌شود.
