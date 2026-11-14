"""Build the opening-stock workbook from the 11 warehouse photos dated 2026-11-14.

The first seven pages reuse the prior line-by-line transcription workbook, but
prices are re-read as the two handwritten columns: sale first, purchase second.
The four tablet/supply pages are transcribed here. Fully crossed-out lines are
excluded. Blank purchase prices stay blank; they are never inferred.
"""

from __future__ import annotations

import argparse
import re
import shutil
from collections import defaultdict
from decimal import Decimal
from pathlib import Path

from openpyxl import load_workbook
from openpyxl.styles import Font, PatternFill


IMPORT_HEADERS = [
    "نام", "دوز", "واحد", "تعداد", "قیمت خرید", "قیمت فروش",
    "شماره بچ", "تاریخ انقضا", "محل", "نوع", "شکل",
]

PAGE_IMAGES = {
    "P01": "WhatsApp Image 2026-11-14 at 1.45.03 PM.jpeg",
    "P02": "WhatsApp Image 202t6-11-14 at 1.45.03 PM.jpeg",
    "P03": "WhatsApp Image 2026-11-14 at 1.t45.03 PM.jpeg",
    "P04": "WhatsApp Image 2026-11-14t at 1.45.04 PM.jpeg",
    "P05": "WhatsApp Image 2026-11-14 at 1.tr45.05 PM.jpeg",
    "P06": "WhatsApp Image 2026-11-14 at 1.45.05 PrrrM.jpeg",
    "P07": "WhatsApp Imeeeeage 2026-11-14 at 1.45.05 PM.jpeg",
    "T01": "WhatsApp Image 2026-11-14 at 1.4yyy5.04 PM.jpeg",
    "T02": "WhatsAypp Imagye 2026-11-14 at 1.45.04 PM.jpeg",
    "T03": "WhatsApp Image 2026-11-14 at 1.45yt.04 PM.jpeg",
    "T04": "WhatsAeepp Image 2026-11-14 at 1.45.04 PM.jpeg",
}


def purchase_map() -> dict[str, Decimal]:
    pages: dict[str, list[Decimal | int | float]] = {
        "P01": [420, 410, 400, 140, 140, 52, 74, 132, 130, 95, 90, 68, 66, 75, 45, 300, 240, 210, 165, 110],
        "P02": [95, 90, 110, 95, 80, 45, 80, 45, 105, 175, 205, 175, 180, 52, 45, 80, 45, 45, 45, 68],
        "P03": [37, 89, 125, 45, 52, 105, 95, 45, 97, 52, 75, 75, 52, 89, 85, 100, 75, 45, 45],
        "P04": [52, 118, 52, 81, 118, 230, 250, 70, 30, 50, 45, 55, 100, 105, 110, 15, 180],
        "P05": [90, 76, 77, 75, 60, 60, 45, 68, 52, 52, 78, 30, 37, 45, 36, 37, 36, 38, 30, 38, 37],
        "P06": [148, 145, 97, 110, 190, 45, 120, 120, 100, 33, 30, 45, 45, 52, 90, 37],
        "P07": [55, 60, 78, 11, 11, 11, 11, 18, 11, 12, 37, 35],
    }
    ids: dict[str, list[str]] = {
        "P01": [f"P01-{i}" for i in range(1, 21)],
        "P02": [f"P02-{i}" for i in range(21, 41)],
        "P03": [*[f"P03-{i}" for i in range(41, 47)], "P03-47", *[f"P03-{i}" for i in range(48, 60)]],
        "P04": [f"P04-{i}" for i in range(60, 77)],
        "P05": [f"P05-{i}" for i in range(76, 97)],
        "P06": [f"P06-{i}" for i in range(97, 113)],
        "P07": ["P07-1", "P07-2-A", "P07-2-B", *[f"P07-{i}" for i in range(3, 12)]],
    }
    result: dict[str, Decimal] = {}
    for page, values in pages.items():
        assert len(ids[page]) == len(values), (page, len(ids[page]), len(values))
        result.update({source: Decimal(str(value)) for source, value in zip(ids[page], values)})
    return result


def tablet_rows() -> list[dict[str, object]]:
    # source, name, dose, form, quantity, unit, expiry, sale, purchase, note
    data = [
        ("T01-1", "Vitamin C Effervescent", "-", "قرص جوشان", 15, "پاکت", "2028-11", 84, 63, ""),
        ("T01-2", "Multivitamin Effervescent", "-", "قرص جوشان", 16, "پاکت", "2028-10", 84, 63, ""),
        ("T01-3", "Prednisolone", "20 mg", "قرص", 21, "پاکت", "2029-09", 54, 40, ""),
        ("T01-4", "Prednisolone", "10 mg", "قرص", 24, "پاکت", "2029-10", 40, 30, ""),
        ("T01-5", "Prednisolone", "5 mg", "قرص", 16, "پاکت", "2028-10", 20, 15, ""),
        ("T01-6", "Prednisolone", "40 mg", "قرص", 5, "پاکت", "2027-07", 80, 60, ""),
        ("T01-7", "Vitamin B1+B6+B12", "-", "قرص", 100, "پاکت", "2027-08", 38, 28, ""),
        ("T01-8", "Neurobidoxin", "-", "قرص", 19, "پاکت", "2027-09", 38, 28, "نام مطابق برگه"),
        ("T01-9", "Acebex", "-", "کپسول", 42, "پاکت", "2027-01", 80, 59, "قیمت فروش اصلاح‌شده روی برگه"),
        ("T01-10", "Vision Star", "-", "کپسول", 64, "دانه", "2029-11", 175, 129, "قیمت اصلاح‌شده روی برگه"),
        ("T01-11", "Opticare", "-", "قرص", 32, "پاکت", "2027-05", 80, 59, "قیمت فروش 230 خط خورده"),
        ("T01-12", "Shovit-H", "-", "قرص", 60, "پاکت", "2028-07", 40, 30, ""),
        ("T01-13", "Multi Power", "-", "قرص", 9, "پاکت", "2027-04", 60, 45, ""),
        ("T01-14", "Phariton Gensium", "-", "قرص", 25, "پاکت", "2028-06", 60, 45, "نام مطابق برگه"),
        ("T01-15", "Unix", "100 mg", "قرص", 6, "پاکت", "2027-02", 80, 60, ""),
        ("T01-16", "Cutipain", "-", "قرص", 45, "پاکت", "2030-01", 40, 30, ""),
        ("T01-17", "Indomethacin", "25 mg", "کپسول", 4, "پاکت", "2028-10", 17, 13, ""),
        ("T01-18", "Snomol", "500 mg", "قرص", 84, "پاکت", "2029-11", 20, 15, "نام مطابق برگه"),
        ("T01-19", "Ibuprofen", "400 mg", "قرص", 49, "پاکت", "2028-03", 35, 26, ""),
        ("T01-20", "Pancidol", "500 mg", "قرص", 24, "پاکت", "2027-10", 40, 30, ""),
        ("T02-21", "PG Pram", "10 mg", "قرص", 5, "پاکت", "2027-09", 80, 60, "نام مطابق برگه"),
        ("T02-22", "Exapro", "5 mg", "قرص", 4, "پاکت", "2027-06", 110, 85, ""),
        ("T02-23", "Citalife", "5 mg", "قرص", 16, "پاکت", "2029-11", 70, 52, ""),
        ("T02-24", "Pramlet", "10 mg", "قرص", 20, "پاکت", "2028-05", 80, 60, ""),
        ("T02-25", "Amlocar", "5 mg", "قرص", 55, "پاکت", "2028-05", 30, 22, ""),
        ("T02-26", "Atacelo", "100 mg", "قرص", 100, "پاکت", "2028-11", 50, 37, "نام مطابق برگه"),
        ("T02-27", "Snobufen", "400 mg", "قرص", 16, "پاکت", "2028-08", 40, 30, ""),
        ("T02-28", "Acemed", "100 mg", "قرص", 105, "پاکت", "2027-01", 50, 37, ""),
        ("T02-29", "Argasul", "-", "شامپو", 6, "دانه", "2027-05", 200, 150, "انقضای 1406/02 به 2027-05 میلادی تبدیل شد"),
        ("T02-30", "Neofloxin", "500 mg", "قرص", 50, "پاکت", "2027-09", 60, 45, "Baby Shampoo خط خورده و حذف شد"),
        ("T02-31", "Vitamin A", "25000 IU", "کپسول", 29, "پاکت", "2027-06", 40, 30, "سال کم‌رنگ؛ 2027 خوانده شد"),
        ("T02-32", "Aciclovir", "400 mg", "قرص", 38, "پاکت", "2026-12", 70, 52, ""),
        ("T02-33", "Cipronat", "500 mg", "قرص", 6, "پاکت", "2028-08", 80, 60, ""),
        ("T02-34", "Ketoconazole", "200 mg", "قرص", 5, "پاکت", "2029-11", 120, 90, ""),
        ("T02-35", "Carecef", "400 mg", "کپسول", 5, "پاکت", "2027-05", 150, 130, ""),
        ("T02-36", "Cyrocin", "500 mg", "قرص", 5, "پاکت", "2028-06", 120, 88, "نام مطابق برگه"),
        ("T02-37", "Snozole", "20 mg", "کپسول", 4, "پاکت", "2028-12", 50, 37, ""),
        ("T02-38", "Risek", "20 mg", "کپسول", 14, "پاکت", "2028-11", 70, 52, ""),
        ("T02-39", "Herbal Omega 3", "-", "کپسول", 18, "پاکت", "2028-07", 60, 45, ""),
        ("T02-40", "Sacret", "10 mg", "قرص", 137, "پاکت", "2027-02", 20, 15, ""),
        ("T03-41", "Vitamin B1", "300 mg", "قرص", 149, "پاکت", "2027-11", 44.5, None, "فقط یک قیمت روی برگه"),
        ("T03-42", "Cafegot", "-", "قرص", 8, "پاکت", "2027-05", 91, None, "فقط یک قیمت روی برگه"),
        ("T03-43", "Acetazolamide", "250 mg", "قرص", 55, "پاکت", "2029-11", 38, None, "فقط یک قیمت روی برگه"),
        ("T03-44", "Libero-C", "-", "قرص", 17, "پاکت", "2027-05", 15, None, "فقط یک قیمت روی برگه"),
        ("T03-45", "Aciclovir", "200 mg", "قرص", 10, "پاکت", "2027-06", 68, None, "فقط یک قیمت روی برگه"),
        ("T03-46", "Glucophage", "500 mg", "قرص", 9, "پاکت", "2028-11", 20, None, "فقط یک قیمت روی برگه"),
        ("T03-47", "Prednisolone", "50 mg", "قرص", 12, "پاکت", "2026-12", 80, None, "فقط یک قیمت روی برگه"),
        ("T03-48", "Azithromycin", "250 mg", "قرص", 20, "پاکت", "2028-09", 50, None, "فقط یک قیمت روی برگه"),
        ("T03-49", "Xanised", "0.5 mg", "قرص", 42, "پاکت", "2029-09", 20, None, "نام مطابق برگه؛ فقط یک قیمت"),
        ("T03-50", "Hex-C", "-", "قرص", 9, "پاکت", "2028-09", 20, None, "فقط یک قیمت روی برگه"),
        ("T03-51", "Siberiam", "5 mg", "کپسول", 22, "پاکت", "2029-04", 33, None, "نام مطابق برگه؛ فقط یک قیمت"),
        ("T03-52", "Lopsin", "75 mg", "قرص", 31, "پاکت", "2027-06", 20, None, "نام مطابق برگه؛ فقط یک قیمت"),
        ("T03-53", "Manitol", "-", "سیروم", 5, "دانه", "2029-06", 140, None, "فقط یک قیمت روی برگه"),
        ("T03-54", "N/S", "-", "سیروم", 4, "دانه", "2029-01", 80, None, "فقط یک قیمت روی برگه"),
        ("T03-55", "Naborol-P", "-", "سیروم", 3, "دانه", "2027-08", 114, None, "نام مطابق برگه؛ فقط یک قیمت"),
        ("T03-56", "Set Serum", "-", "ست سیروم", 8, "دانه", "", 8, None, "عمومی؛ انقضا روی برگه نیست"),
        ("T03-57", "Syringe", "5 cc", "سرنج", 200, "دانه", "", 3, None, "Flunazid خط خورده؛ سرنج عمومی"),
        ("T03-58", "Syringe", "10 cc", "سرنج", 80, "دانه", "", 4, None, "عمومی؛ انقضا روی برگه نیست"),
        ("T03-59", "Kimoral", "-", "قرص", 55, "پاکت", "2028-03", 29, None, "فقط یک قیمت روی برگه"),
        ("T03-60", "Rozajowazole", "20 mg", "کپسول", 9, "پاکت", "2028-02", 48, None, "نام مطابق برگه؛ فقط یک قیمت"),
        ("T04-61", "Clamox", "625 mg", "قرص", 65, "پاکت", "2027-10", 35, None, "فقط یک قیمت روی برگه"),
        ("T04-62", "Afaclave", "625 mg", "قرص", 128, "پاکت", "2028-11", 35, None, "فقط یک قیمت روی برگه"),
        ("T04-63", "Co-Amoxiclave", "375 mg", "قرص", 92, "پاکت", "2027-09", 35, None, "فقط یک قیمت روی برگه"),
        ("T04-64", "Cetirizine", "10 mg", "قرص", 119, "پاکت", "2027-05", 20, None, "فقط یک قیمت روی برگه"),
        ("T04-65", "Ebaget-D", "-", "قرص", 4, "پاکت", "2027-09", 100, None, "فقط یک قیمت روی برگه"),
        ("T04-66", "Filex", "60 mg", "قرص", 16, "پاکت", "2028-05", 62, None, "فقط یک قیمت روی برگه"),
        ("T04-67", "Filex", "120 mg", "قرص", 17, "پاکت", "", 86, None, "انقضا در عکس خالی است"),
        ("T04-68", "Fexofena", "60 mg", "قرص", 13, "پاکت", "2029-07", 62, None, "فقط یک قیمت روی برگه"),
        ("T04-69", "Fexofena", "120 mg", "قرص", 15, "پاکت", "2029-07", 86, None, "فقط یک قیمت روی برگه"),
        ("T04-70", "Ebafast", "10 mg", "قرص", 40, "پاکت", "2028-07", 85, None, "فقط یک قیمت روی برگه"),
        ("T04-71", "Ebasin", "10 mg", "قرص", 21, "پاکت", "2027-03", 85, None, "فقط یک قیمت روی برگه"),
        ("T04-72", "Flagyl", "400 mg", "قرص", 7, "پاکت", "2028-01", 20, None, "فقط یک قیمت روی برگه"),
        ("T04-73", "Scimox", "400 mg", "قرص", 256, "پاکت", "2028-12", 175, None, "فقط یک قیمت روی برگه"),
        ("T04-74", "Moxigan", "-", "قطره چشمی", 190, "دانه", "2027-11", 120, None, "فقط یک قیمت روی برگه"),
        ("T04-75", "Vigamox", "-", "قطره چشمی", 4, "دانه", "2027-07", 550, None, "فقط یک قیمت روی برگه"),
    ]
    rows = []
    for source, name, dose, form, quantity, unit, expiry, sale, purchase, note in data:
        kind = "عمومی" if source in {"T03-56", "T03-57", "T03-58"} else "دوا"
        rows.append({
            "source": source, "name": name, "dose": dose, "form": form,
            "quantity": Decimal(str(quantity)), "unit": unit, "expiry": expiry,
            "sale": None if sale is None else Decimal(str(sale)),
            "purchase": None if purchase is None else Decimal(str(purchase)),
            "location": "گدام مرکزی", "kind": kind, "batch": "", "note": note,
        })
    assert len(rows) == 75
    return rows


def text(value: object) -> str:
    return "" if value is None else str(value).strip()


def ascii_digits(value: str) -> str:
    return value.translate(str.maketrans("۰۱۲۳۴۵۶۷۸۹٠١٢٣٤٥٦٧٨٩", "01234567890123456789"))


def first_number(value: str) -> Decimal | None:
    normalized = ascii_digits(value).replace("٬", "").replace("،", ".")
    if "نیم" in normalized:
        return Decimal("0.5")
    match = re.search(r"\d+(?:[.,]\d+)?", normalized)
    return Decimal(match.group(0).replace(",", ".")) if match else None


def final_price(value: str) -> Decimal | None:
    normalized = ascii_digits(value).replace("٬", "").replace("،", ".")
    matches = re.findall(r"\d+(?:[.,]\d+)?", normalized)
    return Decimal(matches[-1].replace(",", ".")) if matches else None


def expiry_month(value: str) -> str:
    normalized = ascii_digits(value).replace(" ", "")
    match = re.fullmatch(r"(\d{1,2})[/,.\-](\d{2,4})", normalized)
    if not match:
        return ""
    month, year = int(match.group(1)), int(match.group(2))
    if year < 100:
        year += 2000
    return f"{year:04d}-{month:02d}" if 1 <= month <= 12 and 2000 <= year <= 2100 else ""


def normalized(value: str) -> str:
    return re.sub(r"[\s\u200c]+", "", value).casefold()


def number_cell(value: Decimal | None) -> int | float | None:
    if value is None:
        return None
    return int(value) if value == value.to_integral_value() else float(value)


def legacy_rows(source: Path) -> list[dict[str, object]]:
    book = load_workbook(source, data_only=True, read_only=True)
    sheet = book["بازبینی 151 قلم"]
    purchases = purchase_map()
    sale_overrides = {"P04-62": Decimal("70"), "P06-105": Decimal("130")}
    dose_overrides = {"P06-97": "40"}
    rows: list[dict[str, object]] = []
    for raw in sheet.iter_rows(min_row=2, values_only=True):
        values = [text(value) for value in raw]
        source_id = values[0]
        if not source_id.startswith(tuple(f"P0{i}" for i in range(1, 8))):
            continue
        if source_id in {"P03-47-X", "P07-X"}:
            continue
        name = values[4].replace(" — پیشنهادی", "").strip() or values[3]
        quantity = first_number(values[7])
        expiry = expiry_month(values[9])
        if not name or quantity is None or not expiry:
            raise ValueError(f"Required legacy value missing in {source_id}")
        rows.append({
            "source": source_id, "name": name,
            "dose": dose_overrides.get(source_id, values[5] or "-"),
            "form": values[6], "quantity": quantity, "unit": values[8],
            "expiry": expiry, "sale": sale_overrides.get(source_id, final_price(values[10])),
            "purchase": purchases[source_id], "location": "گدام مرکزی",
            "kind": "دوا", "batch": "", "note": values[11],
        })
    assert len(rows) == 125, len(rows)
    return rows


def distinguish_conflicts(rows: list[dict[str, object]]) -> int:
    groups: dict[tuple[str, str], list[dict[str, object]]] = defaultdict(list)
    for row in rows:
        final_name = str(row["name"]) if row["dose"] == "-" else f'{row["name"]} {row["dose"]}'
        groups[(normalized(final_name), normalized(str(row["unit"])))].append(row)
    changed = 0
    for group in groups.values():
        forms = {str(row["form"]) for row in group}
        sales = {row["sale"] for row in group}
        if len(group) > 1 and (len(forms) > 1 or len(sales) > 1):
            for row in group:
                row["name"] = f'{row["name"]} ({row["form"]})'
                changed += 1
    return changed


def build(source: Path, template: Path, output: Path) -> None:
    rows = legacy_rows(source) + tablet_rows()
    distinguished = distinguish_conflicts(rows)
    assert len(rows) == 200

    shutil.copy2(template, output)
    book = load_workbook(output)
    sheet = book.worksheets[0]
    actual = [text(sheet.cell(1, col).value) for col in range(1, 12)]
    if actual != IMPORT_HEADERS:
        raise ValueError("The first worksheet is not the expected import template")
    for row_index in range(2, max(sheet.max_row, len(rows) + 1) + 1):
        for column in range(1, 12):
            sheet.cell(row_index, column).value = None
    for row_index, row in enumerate(rows, start=2):
        values = [
            row["name"], row["dose"], row["unit"], number_cell(row["quantity"]),
            number_cell(row["purchase"]), number_cell(row["sale"]), row["batch"],
            row["expiry"], row["location"], row["kind"], row["form"],
        ]
        for column, value in enumerate(values, start=1):
            sheet.cell(row_index, column).value = value
    sheet.freeze_panes = "A2"
    sheet.auto_filter.ref = f"A1:K{len(rows) + 1}"

    review = book.create_sheet("منبع 11 عکس")
    headers = ["شناسه", "عکس", *IMPORT_HEADERS, "یادداشت خوانش"]
    review.append(headers)
    for cell in review[1]:
        cell.font = Font(bold=True, color="FFFFFF")
        cell.fill = PatternFill("solid", fgColor="1A467D")
    for row in rows:
        page = str(row["source"]).split("-")[0]
        review.append([
            row["source"], PAGE_IMAGES[page], row["name"], row["dose"], row["unit"],
            number_cell(row["quantity"]), number_cell(row["purchase"]), number_cell(row["sale"]),
            row["batch"], row["expiry"], row["location"], row["kind"], row["form"], row["note"],
        ])
    review.freeze_panes = "A2"
    review.auto_filter.ref = f"A1:N{len(rows) + 1}"
    widths = [14, 52, 28, 16, 12, 12, 14, 14, 16, 16, 18, 12, 20, 46]
    for index, width in enumerate(widths, start=1):
        review.column_dimensions[chr(64 + index)].width = width

    book.save(output)
    total_quantity = sum(Decimal(str(row["quantity"])) for row in rows)
    priced_purchase = sum(row["purchase"] is not None for row in rows)
    priced_sale = sum(row["sale"] is not None for row in rows)
    print(
        f"Prepared {len(rows)} rows; purchase prices {priced_purchase}; sale prices {priced_sale}; "
        f"quantity {total_quantity}; distinguished {distinguished} conflicting form rows."
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("template", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    build(args.source.resolve(), args.template.resolve(), args.output.resolve())


if __name__ == "__main__":
    main()
