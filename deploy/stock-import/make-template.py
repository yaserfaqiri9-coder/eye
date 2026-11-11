# Builds stock-template.xlsx and stock-template.csv. Run: python make-template.py <output folder>
import csv, sys, os
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.worksheet.datavalidation import DataValidation

out = sys.argv[1] if len(sys.argv) > 1 else "."
headers = ["نام", "دوز", "واحد", "تعداد", "قیمت خرید", "قیمت فروش", "شماره بچ", "تاریخ انقضا", "محل", "نوع", "شکل"]
widths = [28, 12, 10, 10, 12, 12, 14, 14, 18, 10, 16]
locations = ["گدام مرکزی", "دواخانه", "بخش عینک", "گدام عملیات\u200cخانه"]
kinds = ["دوا", "فریم", "عدسیه", "عمومی"]
examples = [
    ["Timolol", "0.5%", "دانه", 24, 85, 120, "TM2405", "2027-08", "دواخانه", "دوا", "قطره چشمی"],
    ["Tropicamide", "1%", "دانه", 10, 60, 90, "TR118", "2028-02-29", "گدام مرکزی", "دوا", "قطره چشمی"],
    ["Artificial Tears", "-", "دانه", 30, 40, 65, "AT77", "2027-12", "دواخانه", "دوا", "قطره چشمی"],
    ["گاز استریل", "", "بسته", 50, 15, "", "", "", "گدام عملیات\u200cخانه", "عمومی", ""],
    ["Viscoelastic", "2%", "عدد", 6, "", "", "VE-31", "2027-05", "گدام عملیات\u200cخانه", "دوا", "سرنج"],
]

wb = Workbook()
def sheet(ws, rows):
    ws.sheet_view.rightToLeft = True
    ws.append(headers)
    for i, w in enumerate(widths):
        ws.column_dimensions[chr(65 + i)].width = w
    for c in ws[1]:
        c.font = Font(bold=True, color="FFFFFF")
        c.fill = PatternFill("solid", fgColor="1F6F8B")
        c.alignment = Alignment(horizontal="center")
    for r in rows:
        ws.append(r)
    # Text, so a month like 2028-11 stays a month (end of month) and batch numbers keep leading zeros.
    for row in range(2, 1001):
        ws[f"G{row}"].number_format = "@"
        ws[f"H{row}"].number_format = "@"
    for col, values in (("I", locations), ("J", kinds)):
        dv = DataValidation(type="list", formula1='"' + ",".join(values) + '"', allow_blank=True)
        dv.error = "از فهرست انتخاب کنید"
        ws.add_data_validation(dv)
        dv.add(f"{col}2:{col}1000")
    ws.freeze_panes = "A2"

sheet(wb.active, []); wb.active.title = "موجودی"
sheet(wb.create_sheet("نمونه"), examples)
help_ws = wb.create_sheet("راهنما")
help_ws.sheet_view.rightToLeft = True
help_ws.column_dimensions["A"].width = 16; help_ws.column_dimensions["B"].width = 90
for row in [
    ("ستون", "قاعده"),
    ("نام", "الزامی. نام جنس بدون دوز."),
    ("دوز", "برای دوا الزامی (مثلاً 0.5%). اگر دوا دوز ندارد علامت - بنویسید. برای مواد خالی بماند."),
    ("واحد", "الزامی: دانه، عدد، بسته، بکس، قطی، جوره ..."),
    ("تعداد", "الزامی، بزرگ‌تر از صفر."),
    ("قیمت خرید", "قیمت یک واحد به افغانی. اگر معلوم نیست خالی بماند: موجودی بدون ارزش ثبت می‌شود و در حساب‌ها نمی‌رود."),
    ("قیمت فروش", "قیمت فروش یک واحد. اگر خالی بماند جنس جدید غیرفعال (غیرقابل فروش) ثبت می‌شود تا قیمت تعیین شود."),
    ("شماره بچ", "اختیاری."),
    ("تاریخ انقضا", "میلادی: 2028-11-30 یا فقط ماه 2028-11 (آخر ماه ثبت می‌شود). برای دوا الزامی. تاریخ شمسی پذیرفته نمی‌شود."),
    ("محل", "یکی از: " + "، ".join(locations)),
    ("نوع", "یکی از: " + "، ".join(kinds)),
    ("شکل", "اختیاری، مثلاً قطره چشمی، پماد، تابلیت."),
    ("", "فقط برگه اول (موجودی) خوانده می‌شود. هر ردیف یک بچ در یک محل است."),
]:
    help_ws.append(row)
help_ws["A1"].font = help_ws["B1"].font = Font(bold=True)

os.makedirs(out, exist_ok=True)
wb.save(os.path.join(out, "stock-template.xlsx"))
with open(os.path.join(out, "stock-example.csv"), "w", newline="", encoding="utf-8-sig") as f:
    w = csv.writer(f); w.writerow(headers); w.writerows(examples)
print("ok")
