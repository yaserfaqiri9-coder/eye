# جریان‌های کاری — سیستم شفاخانه چشم الشفا

مدل مرکزی: `Patient → Visit → Department Workflows`

هر عملیات مرتبط به مریض، به همان `PatientId` و در صورت وجود به همان `VisitId` وصل می‌شود. داده مریض دوباره وارد نمی‌شود.

## اصل انعطاف

وضعیت مراجعه **دنباله اجباری نیست**. قوانین انتقال در یک State Machine کوچک در کد تعریف شده و مسیرهای زیر همگی مجازند:

- مریض مستقیم نزد داکتر (بدون معاینه ابتدایی)
- مراجعه بدون آزمایش، دوا یا عینک
- مراجعه فقط برای پیگیری
- خدمت مستقل بدون داکتر
- پرداخت پیش یا پس از خدمت (پرداخت هیچ وضعیت بالینی را مسدود نمی‌کند، مگر تنظیم مرکز چیز دیگری بگوید)

## State Machine مراجعه (Visit)

| از | به |
|---|---|
| Registered | Waiting, InPreExam, ReadyForDoctor, InConsultation, Completed, Cancelled |
| Waiting | InPreExam, ReadyForDoctor, InConsultation, Completed, Cancelled |
| InPreExam | ReadyForDoctor, Waiting, Completed, Cancelled |
| ReadyForDoctor | InConsultation, Waiting, Cancelled |
| InConsultation | AwaitingDiagnostic, FollowUpRequired, Completed, Waiting |
| AwaitingDiagnostic | InConsultation, ReadyForDoctor, Completed, Cancelled |
| FollowUpRequired | Completed |
| Completed | — (اصلاح فقط با ثبت اصلاحیه) |
| Cancelled | — |

کارهای بخش‌ها (آزمایش، دواخانه، عینک، عملیات) وضعیت جداگانه خود را دارند و مراجعه را منتظر نگه نمی‌دارند.

## State Machine بخش‌ها

- **درخواست آزمایش:** Requested → InProgress → ResultReady؛ لغو از Requested یا InProgress → Cancelled
- **نسخه دوا:** Issued → PartiallyDispensed → Dispensed؛ لغو از Issued → Cancelled
- **سفارش عینک:** Draft → Ordered → InWorkshop → Ready → Delivered؛ لغو پیش از Delivered → Cancelled
- **عملیات:** Proposed → Scheduled → InProgress → Completed → FollowUp → Closed؛ لغو پیش از InProgress → Cancelled
- **فاکتور:** Issued → PartiallyPaid → Paid؛ لغو فقط پیش از هر پرداخت → Void؛ پس از پرداخت فقط برگشت وجه
- **جلسه صندوق:** Open → Closed (یک جلسه باز برای هر صندوق)

## سناریوی کامل

1. پذیرش مریض را ثبت می‌کند (`PatientRegistered`) و سیستم موارد احتمالاً تکراری را هشدار می‌دهد.
2. پذیرش مراجعه می‌سازد: Visit ID یکتا و شماره صف روزانه جدا (`VisitCreated`). آیتم هزینه خدمت با منبع مشخص ساخته می‌شود.
3. معاینه ابتدایی ثبت می‌شود (`PreExamCompleted`) و داکتر در صف خود می‌بیند.
4. داکتر معاینه را ثبت می‌کند و در صورت نیاز درخواست آزمایش، نسخه دوا، نسخه عینک، ارجاع عملیات و مراجعه بعدی می‌دهد.
5. هر درخواست در صف بخش خود ظاهر می‌شود؛ هزینه قابل پرداخت در صندوق دیده می‌شود.
6. نتیجه آزمایش (`DiagnosticResultReady`) در پرونده همان مریض برای داکتر ظاهر می‌شود.
7. صندوق دریافت را ثبت می‌کند (`PaymentReceived`) و دفتر مالی ثبت می‌شود.
8. حسابدار و مدیر گزارش را از همان تراکنش‌ها می‌بینند.

## مستقل از مراجعه

فروش مستقل دواخانه، سفارش مستقل عینک، درخواست مجاز مستقل آزمایشگاه و مصارف عمومی بدون Visit ممکن هستند. `VisitId` در این موارد خالی است و `PatientId` اختیاری.

## رویدادها (فقط اعلان، بدون داده محرمانه)

`PatientRegistered`, `VisitCreated`, `VisitStatusChanged`, `PatientCheckedIn`, `PreExamCompleted`, `ConsultationCompleted`, `DiagnosticOrderCreated`, `DiagnosticResultReady`, `PrescriptionCreated`, `OpticalPrescriptionCreated`, `SurgeryScheduled`, `InvoiceIssued`, `PaymentReceived`, `InventoryChanged`

ترتیب: Commit در MySQL → ثبت در `OutboxEvents` در همان تراکنش → ارسال توسط BackgroundService → Frontend از API تازه می‌خواند.

## نیازمند تأیید شفاخانه (پیش از استفاده عملیاتی)

این موارد فرض من هستند و **تأییدشده نیستند**. جریان بالینی تا تأیید، آماده بهره‌برداری اعلام نمی‌شود.

### مسئول طبی

| شناسه | موضوع |
|---|---|
| M-1 | فیلدهای نهایی معاینه ابتدایی (VA، IOP، ...) و واحد و قالب ثبت |
| M-2 | فیلدهای یافته‌های چشم در معاینه داکتر و فهرست/کدگذاری تشخیص |
| M-3 | فیلدهای نسخه عینک و قواعد تأیید |
| M-4 | مراحل و بررسی‌های لازم پیش از عملیات، رضایت‌نامه و گزارش عملیات |
| M-5 | قوانین دسترسی به پرونده و Break-glass |
| M-6 | فهرست خدمات تشخیصی و قالب نتیجه |
| M-7 | درستی وضعیت‌های مراجعه با جریان واقعی |

### مسئول مالی

| شناسه | موضوع |
|---|---|
| F-1 | واحدهای پولی، نرخ تبدیل، قاعده گردکردن |
| F-2 | پرداخت پیش یا پس از خدمت برای هر نوع خدمت |
| F-3 | قواعد تخفیف، بدهی مجاز و مسئول تأیید |
| F-4 | سرفصل‌های حساب (Chart of Accounts) |
| F-5 | خدمات، تعرفه‌ها و روش غیرنقدی |
| F-6 | روش ارزش‌گذاری موجودی (پیش‌فرض: قیمت خرید هر بچ) |
