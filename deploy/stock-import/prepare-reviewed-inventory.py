"""Create an import sheet from the reviewed photo transcription workbook.

The source workbook is preserved as evidence. The first worksheet is populated
for OpeningStockSheet; crossed-out rows are excluded. The handwritten price is
treated as sale price, never purchase cost, because the source does not label it
as purchase cost.
"""

from __future__ import annotations

import argparse
import re
import shutil
from collections import defaultdict
from decimal import Decimal
from pathlib import Path

from openpyxl import load_workbook


IMPORT_HEADERS = [
    "نام",
    "دوز",
    "واحد",
    "تعداد",
    "قیمت خرید",
    "قیمت فروش",
    "شماره بچ",
    "تاریخ انقضا",
    "محل",
    "نوع",
    "شکل",
]


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
    if not 1 <= month <= 12 or not 2000 <= year <= 2100:
        return ""
    return f"{year:04d}-{month:02d}"


def normalized(value: str) -> str:
    return re.sub(r"[\s\u200c]+", "", value).casefold()


def number_cell(value: Decimal | None) -> int | float | None:
    if value is None:
        return None
    return int(value) if value == value.to_integral_value() else float(value)


def find_review_sheet(workbook):
    for sheet in workbook.worksheets:
        headers = [text(cell.value) for cell in sheet[1]]
        if "شناسه منبع" in headers and "نام خوانده‌شده" in headers:
            return sheet
    raise ValueError("The reviewed inventory worksheet was not found.")


def prepare(source: Path, output: Path) -> dict[str, int]:
    values_book = load_workbook(source, data_only=True, read_only=True)
    values_sheet = find_review_sheet(values_book)

    rows: list[dict[str, object]] = []
    excluded = 0
    for raw in values_sheet.iter_rows(min_row=2, values_only=True):
        values = [text(value) for value in raw]
        if any("حذف" in values[index] for index in (24, 25, 26)):
            excluded += 1
            continue

        proposed = values[4].replace(" — پیشنهادی", "").strip()
        name = proposed or values[3]
        quantity = first_number(values[7])
        if not name or quantity is None or not values[8]:
            raise ValueError(f"Required source data is missing in {values[0]}.")

        is_drug = values[2] == "دوا/محلول"
        expiry = expiry_month(values[9])
        if is_drug and not expiry:
            raise ValueError(f"Drug expiry is missing or invalid in {values[0]}.")

        rows.append(
            {
                "source": values[0],
                "name": name,
                "dose": values[5] or "-",
                "unit": values[8],
                "quantity": quantity,
                "purchase": None,
                "sale": final_price(values[10]),
                "batch": "",
                "expiry": expiry,
                "location": "دواخانه" if is_drug else "گدام عملیات‌خانه",
                "kind": "دوا" if is_drug else "عمومی",
                "form": values[6],
            }
        )

    # The database has one product per final name and unit. When the same brand
    # appears as both drop and ointment, add the form to prevent merging them.
    product_groups: dict[tuple[str, str], list[dict[str, object]]] = defaultdict(list)
    for row in rows:
        final_name = str(row["name"]) if row["dose"] == "-" else f'{row["name"]} {row["dose"]}'
        product_groups[(normalized(final_name), normalized(str(row["unit"])))].append(row)

    distinguished = 0
    for group in product_groups.values():
        forms = {str(row["form"]) for row in group}
        prices = {row["sale"] for row in group}
        if len(group) > 1 and (len(forms) > 1 or len(prices) > 1):
            for row in group:
                row["name"] = f'{row["name"]} ({row["form"]})'
                distinguished += 1

    shutil.copy2(source, output)
    book = load_workbook(output)
    target = book.worksheets[0]
    actual_headers = [text(target.cell(1, column).value) for column in range(1, 12)]
    if actual_headers != IMPORT_HEADERS:
        raise ValueError("The first worksheet is not the expected Al-Shifa import template.")

    for row_index in range(2, target.max_row + 1):
        for column in range(1, 12):
            target.cell(row_index, column).value = None

    for row_index, row in enumerate(rows, start=2):
        values = [
            row["name"],
            row["dose"],
            row["unit"],
            number_cell(row["quantity"]),
            None,
            number_cell(row["sale"]),
            "",
            row["expiry"],
            row["location"],
            row["kind"],
            row["form"],
        ]
        for column, value in enumerate(values, start=1):
            target.cell(row_index, column).value = value

    target.freeze_panes = "A2"
    target.auto_filter.ref = f"A1:K{len(rows) + 1}"
    book.save(output)
    return {"rows": len(rows), "excluded": excluded, "distinguished": distinguished}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result = prepare(args.source.resolve(), args.output.resolve())
    print(
        f"Prepared {result['rows']} rows; excluded {result['excluded']} crossed-out rows; "
        f"distinguished {result['distinguished']} drop/ointment rows."
    )


if __name__ == "__main__":
    main()
