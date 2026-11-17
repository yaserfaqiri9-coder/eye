export { addDays, periodChoices, previousRange, quickRange, type DateRange, type PeriodKind } from '@/utils/period'

export interface ReportEntry {
  key: string
  name: string
  /** One plain line on what the report shows, for a user who does not know the report names. */
  hint: string
  /** Any one of these opens the report; the server checks the same. */
  needs: string[]
  /** Shows the present moment, so it has no period to choose. */
  noPeriod?: boolean
}

export interface ReportGroup {
  /** Names the group's tab in the address, so a reload or back lands on the same tab. */
  key: string
  title: string
  reports: ReportEntry[]
}

/** Fifteen plain reports, each one list with the detail behind its few figures. */
export const reportGroups: ReportGroup[] = [
  {
    key: 'clinic',
    title: 'کار شفاخانه',
    reports: [
      { key: 'patients', name: 'مراجعات مریضان', hint: 'هر مراجعه با نام مریض، بخش، داکتر و وضعیت', needs: ['patients.view'] },
      { key: 'doctors', name: 'کار داکتران', hint: 'هر داکتر چند مریض دید و چه کاری ثبت کرد', needs: ['reports.view'] },
      { key: 'surgeries', name: 'عملیات‌ها', hint: 'هر عملیات با مریض، نوع، چشم و وضعیت', needs: ['surgery.view'] },
      { key: 'diagnostics', name: 'آزمایش و عکس‌برداری', hint: 'هر درخواست آزمایش، رفرکشن و عکس‌برداری', needs: ['diagnostics.view'] },
      { key: 'diseases', name: 'بیماری‌های شایع', hint: 'کدام تشخیص بیشتر ثبت شده است', needs: ['reports.view'] },
    ],
  },
  {
    key: 'money',
    title: 'پول و حساب',
    reports: [
      { key: 'cash', name: 'دریافت‌ها', hint: 'هر پولی که در صندوق گرفته شد', needs: ['ledger.view'] },
      { key: 'outstanding', name: 'طلبات', hint: 'فاکتورهایی که هنوز پرداخت نشده‌اند', needs: ['ledger.view'], noPeriod: true },
      { key: 'expenses', name: 'مصارف', hint: 'هر مصرف ثبت‌شده', needs: ['ledger.view'] },
      { key: 'profit-loss', name: 'مفاد و ضرر کل سیستم', hint: 'عواید خالص، قیمت خرید فروش‌ها، مصارف و مفاد ثبت‌شده', needs: ['ledger.view'] },
      { key: 'revenue-sources', name: 'عواید بخش‌ها', hint: 'عواید خدمات هر بخش، دواخانه و عینک با کسورات مشترک', needs: ['ledger.view'] },
      { key: 'expense-summary', name: 'خلاصه مصارف به تفکیک نوع', hint: 'جمع مصارف هر نوع با برگشت اسناد و مبلغ خالص', needs: ['ledger.view'] },
      { key: 'daily-finance', name: 'عملکرد مالی روزانه', hint: 'مقایسه روزانه عواید خالص، مصارف و مفاد', needs: ['ledger.view'] },
      { key: 'cash-flow', name: 'گردش حساب صندوق', hint: 'ورودی، خروجی و مانده دفتری حساب صندوق', needs: ['ledger.view'] },
    ],
  },
  {
    key: 'stock',
    title: 'دوا و گدام',
    reports: [
      { key: 'sales', name: 'فروش دواخانه و عینک', hint: 'هر فروش با اجناس و مبلغ آن', needs: ['pharmacy.view', 'optical.view'] },
      { key: 'stock', name: 'موجودی گدام', hint: 'هر جنس با موجودی، تاریخ ختم و ارزش', needs: ['inventory.view'], noPeriod: true },
    ],
  },
]
