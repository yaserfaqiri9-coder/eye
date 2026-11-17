# Builds one SQL file that brings the StockReference sheet (224 batches, 223 products) and the GenericUpdate sheet
# (generic names) into an Al-Shifa database, the same way the opening-stock importer records stock:
# Products -> StockBatches -> one StockMovements row (Adjustment, SourceType OpeningStock) per batch, plus AuditLogs,
# an InventoryChanged outbox event and, when the server sends changes online, SyncChanges rows.
#
#   python make-stock-reference-sql.py AlShifa_Generic_Update.xlsx AlShifa_224_Stock_With_Generic.sql
#
# The SQL runs in one transaction and can be run again: a batch is keyed on [ASH2611/<SourceRow>] in its movement,
# and a product that already has stock before the run gets no new batch (only its generic name).
import sys
from datetime import datetime
from decimal import Decimal

import openpyxl

DATASET = 'ASH2611'
SOURCE_FILE = 'AlShifa_Generic_Update.xlsx'
UNIT = 'عدد'
DRUG_FORMS = {
    'قطره چشمی', 'تابلیت', 'پماد چشمی', 'شربت/سوسپانسیون خوراکی', 'کپسول', 'آمپول/تزریقی', 'سرم', 'ویال',
    'قطره خوراکی', 'تابلیت جوشان', 'محلول', 'شامپو',
}
GENERAL_FORMS = {
    'مواد عملیات', 'سرنج', 'دستکش', 'لنز داخل چشمی', 'نخ جراحی', 'لنز تماسی', 'تیغه', 'لنز', 'سوزن', 'ماسک', 'ست سرم',
}
LOCATIONS = {'pharmacy', 'surgery'}


def q(value):
    if value is None:
        return 'NULL'
    if isinstance(value, (int, Decimal)):
        return str(value)
    return "'" + str(value).replace('\\', '\\\\').replace("'", "''") + "'"


def number(value):
    d = Decimal(str(value))
    return d.quantize(Decimal(1)) if d == d.to_integral_value() else d.normalize()


def parse(existing_name):
    """'Glaupin | 1% | قطره چشمی' -> ('Glaupin 1%', 'قطره چشمی'); 'دستکش | دستکش، سایز/ظرفیت: 7' -> ('دستکش 7', 'دستکش')."""
    parts = [p.strip() for p in existing_name.split(' | ')]
    if len(parts) not in (2, 3):
        raise ValueError(f'Unexpected name: {existing_name}')
    name = ' '.join(parts[:-1])
    form = parts[-1]
    if '، سایز/ظرفیت:' in form:
        form, size = form.split('، سایز/ظرفیت:')
        name = f'{name} {size.strip()}'
    form = form.split('؛')[0].strip()  # 'قطره خوراکی؛ نیاز به تأیید' keeps the form only
    if form in DRUG_FORMS:
        kind = 'Drug'
    elif form in GENERAL_FORMS:
        kind = 'General'
    else:
        raise ValueError(f'Unknown form: {form}')
    return name, form, kind


def main(src, out):
    wb = openpyxl.load_workbook(src, data_only=True)
    stock = [r for r in wb['StockReference'].iter_rows(min_row=2, values_only=True) if r[1]]
    generic = {str(r[0]).strip(): r for r in wb['GenericUpdate'].iter_rows(min_row=2, values_only=True) if r[0]}

    rows, products = [], {}
    for r in stock:
        source_row, code, existing, qty, cost, sale, expiry, location = r[:8]
        code = code.strip()
        if code not in generic:
            raise ValueError(f'{code} missing from GenericUpdate')
        if location not in LOCATIONS:
            raise ValueError(f'{source_row}: unknown location {location}')
        if Decimal(str(cost)) != 0:
            raise ValueError(f'{source_row}: purchase cost is not zero')
        name, form, kind = parse(existing)
        g = generic[code]
        gen = str(g[2]).strip() if g[3] == 1 and g[2] and str(g[2]).strip() else None
        product = dict(code=code, name=name, form=form, kind=kind, sale=number(sale), generic=gen)
        if code in products and products[code] != product:
            raise ValueError(f'{code}: rows disagree on the product')
        products[code] = product
        rows.append(dict(source_row=str(source_row).strip(), code=code, qty=number(qty), sale=number(sale),
                         expiry=expiry.strftime('%Y-%m-%d') if isinstance(expiry, datetime) else None, location=location))

    # The same brand may come as a drop and an ointment; name and form together must be unique.
    names = {}
    for p in products.values():
        key = (p['name'].lower(), p['form'])
        if key in names:
            raise ValueError(f"Two products named {p['name']} ({p['form']}): {names[key]}, {p['code']}")
        names[key] = p['code']
    if len({r['source_row'] for r in rows}) != len(rows):
        raise ValueError('SourceRow repeats')

    with_generic = sum(1 for p in products.values() if p['generic'])
    L = []
    w = L.append
    w(f'-- Al-Shifa: opening stock from {SOURCE_FILE} (StockReference + GenericUpdate).')
    w(f'-- {len(products)} products, {len(rows)} batches, {with_generic} generic names. Purchase cost 0 for all, so nothing is posted to the ledger.')
    w('-- One transaction: an error leaves nothing behind. Running it again adds nothing.')
    w('-- A product whose code already has stock before this file runs gets no new batch; only its generic name is set.')
    w('-- Take a backup first. Run with:  mysql --default-character-set=utf8mb4 -u alshifa -p alshifa < this-file.sql')
    w('')
    w('SET NAMES utf8mb4;')
    w("SET @import_user := 'admin';  -- the account shown in the history as having recorded this stock")
    w('')
    w('DROP TEMPORARY TABLE IF EXISTS _ash_guard, _ash_pre, _ash_gen, _ash_edited;')
    w('CREATE TEMPORARY TABLE _ash_guard (import_user_found TINYINT NOT NULL, pharmacy_location_found TINYINT NOT NULL, surgery_location_found TINYINT NOT NULL);')
    w('CREATE TEMPORARY TABLE _ash_pre (Code VARCHAR(40) NOT NULL PRIMARY KEY);')
    w('CREATE TEMPORARY TABLE _ash_gen (Code VARCHAR(40) NOT NULL PRIMARY KEY, Generic VARCHAR(200) NOT NULL);')
    w('CREATE TEMPORARY TABLE _ash_edited (Id INT NOT NULL PRIMARY KEY);')
    w('')
    w('START TRANSACTION;')
    w('')
    w('SET @uid := (SELECT Id FROM AspNetUsers WHERE UserName = @import_user AND IsActive = 1 LIMIT 1);')
    w("SET @loc_pharmacy := (SELECT Id FROM StockLocations WHERE Code = 'pharmacy');")
    w("SET @loc_surgery := (SELECT Id FROM StockLocations WHERE Code = 'surgery');")
    w('-- Stops here ("Column ... cannot be null") when the account or a location is missing. Nothing is changed then.')
    w('INSERT INTO _ash_guard VALUES (IF(@uid IS NULL, NULL, 1), IF(@loc_pharmacy IS NULL, NULL, 1), IF(@loc_surgery IS NULL, NULL, 1));')
    w("-- Changes go online only on a server that sends them (it has push.* sync state).")
    w("SET @sync := EXISTS (SELECT 1 FROM SyncStates WHERE `Key` LIKE 'push.%');")
    w('SET @p0 := (SELECT COALESCE(MAX(Id), 0) FROM Products);')
    w('SET @m0 := (SELECT COALESCE(MAX(Id), 0) FROM StockMovements);')
    w('SET @a0 := (SELECT COALESCE(MAX(Id), 0) FROM AuditLogs);')
    w('')
    w('-- Codes that already hold stock: their batches are not added again.')
    w(f"INSERT INTO _ash_pre SELECT p.Code FROM Products p WHERE p.Code LIKE '{DATASET}-%' AND EXISTS (SELECT 1 FROM StockBatches b WHERE b.ProductId = p.Id);")
    w('')
    w('-- Products (new codes only).')
    for p in products.values():
        active = 1 if p['sale'] > 0 else 0
        details = f"[{DATASET}/{p['code']}] موجودی اولیه از {SOURCE_FILE}" + ('' if active else '؛ غیرفعال تا تعیین قیمت فروش')
        w('INSERT INTO Products (Code, Name, Kind, Category, Unit, ReorderLevel, SalePrice, IsActive, Version, DefaultDose, GenericName) '
          f"SELECT {q(p['code'])}, {q(p['name'])}, {q(p['kind'])}, {q(p['form'])}, {q(UNIT)}, 0, {p['sale']}, {active}, 0, NULL, {q(p['generic'])} "
          f"FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM Products WHERE Code = {q(p['code'])});")
        w('SET @p := IF(ROW_COUNT() = 1, LAST_INSERT_ID(), NULL);')
        w("INSERT INTO AuditLogs (UserId, Action, Entity, EntityId, Details, IpAddress, CreatedAt) "
          f"SELECT @uid, 'ProductCreated', 'Product', @p, {q(details)}, NULL, UTC_TIMESTAMP(6) FROM DUAL WHERE @p IS NOT NULL;")
    w('')
    w('-- Generic names for products that existed before this file (new ones got theirs above). UpdateGeneric=0 rows are not listed: kept as they are.')
    gens = [(c, p['generic']) for c, p in products.items() if p['generic']]
    w('INSERT INTO _ash_gen (Code, Generic) VALUES')
    w(',\n'.join(f'  ({q(c)}, {q(g)})' for c, g in gens) + ';')
    w('INSERT INTO _ash_edited SELECT p.Id FROM Products p JOIN _ash_gen g ON g.Code = p.Code WHERE NOT (p.GenericName <=> g.Generic);')
    w('UPDATE Products p JOIN _ash_gen g ON g.Code = p.Code SET p.GenericName = g.Generic, p.Version = p.Version + 1 WHERE p.Id IN (SELECT Id FROM _ash_edited);')
    w("INSERT INTO AuditLogs (UserId, Action, Entity, EntityId, Details, IpAddress, CreatedAt) "
      f"SELECT @uid, 'ProductEdited', 'Product', Id, '[{DATASET}/generic] نام جنریک از {SOURCE_FILE}', NULL, UTC_TIMESTAMP(6) FROM _ash_edited;")
    w('')
    w('-- Batches: one per sheet row, recorded once, with its opening movement.')
    for r in rows:
        key = f"[{DATASET}/{r['source_row']}]"
        reason = f"{key} موجودی اولیه؛ {SOURCE_FILE} ردیف {r['source_row']}؛ قیمت خرید صفر"
        loc = '@loc_pharmacy' if r['location'] == 'pharmacy' else '@loc_surgery'
        w(f"SET @p := (SELECT Id FROM Products WHERE Code = {q(r['code'])});")
        w('INSERT INTO StockBatches (ProductId, LocationId, LotNumber, ExpiryDate, Quantity, UnitCost) '
          f"SELECT @p, {loc}, '', {q(r['expiry'])}, {r['qty']}, 0 FROM DUAL "
          f"WHERE {q(r['code'])} NOT IN (SELECT Code FROM _ash_pre) "
          f"AND NOT EXISTS (SELECT 1 FROM StockMovements WHERE SourceType = 'OpeningStock' AND Reason LIKE {q(key + '%')});")
        w('SET @b := IF(ROW_COUNT() = 1, LAST_INSERT_ID(), NULL);')
        w("INSERT INTO StockMovements (BatchId, Kind, Quantity, SourceType, SourceId, Reason, UserId, CreatedAt) "
          f"SELECT @b, 'Adjustment', {r['qty']}, 'OpeningStock', @b, {q(reason)}, @uid, UTC_TIMESTAMP(6) FROM DUAL WHERE @b IS NOT NULL;")
        w("INSERT INTO AuditLogs (UserId, Action, Entity, EntityId, Details, IpAddress, CreatedAt) "
          f"SELECT @uid, 'OpeningStockImported', 'StockBatch', @b, {q(reason)}, NULL, UTC_TIMESTAMP(6) FROM DUAL WHERE @b IS NOT NULL;")
    w('')
    w('-- Screens refresh; the online copy hears of every new or changed row.')
    w(f"SET @new_batches := (SELECT COUNT(*) FROM StockMovements WHERE Id > @m0 AND Reason LIKE '[{DATASET}/%');")
    w("INSERT INTO OutboxEvents (Type, Payload, `Groups`, CreatedAt, ProcessedAt) "
      "SELECT 'InventoryChanged', '{\"source\":\"opening-sheet\"}', 'dept:pharmacy,dept:optical,dept:inventory', UTC_TIMESTAMP(6), NULL FROM DUAL WHERE @new_batches > 0;")
    w("INSERT INTO SyncChanges (TableName, KeyJson, CreatedAt) "
      f"SELECT 'Products', CONCAT('[', Id, ']'), UTC_TIMESTAMP(6) FROM Products WHERE @sync AND ((Id > @p0 AND Code LIKE '{DATASET}-%') OR Id IN (SELECT Id FROM _ash_edited));")
    w("INSERT INTO SyncChanges (TableName, KeyJson, CreatedAt) "
      f"SELECT 'StockBatches', CONCAT('[', SourceId, ']'), UTC_TIMESTAMP(6) FROM StockMovements WHERE @sync AND Id > @m0 AND Reason LIKE '[{DATASET}/%';")
    w("INSERT INTO SyncChanges (TableName, KeyJson, CreatedAt) "
      f"SELECT 'StockMovements', CONCAT('[', Id, ']'), UTC_TIMESTAMP(6) FROM StockMovements WHERE @sync AND Id > @m0 AND Reason LIKE '[{DATASET}/%';")
    w("INSERT INTO SyncChanges (TableName, KeyJson, CreatedAt) "
      f"SELECT 'AuditLogs', CONCAT('[', Id, ']'), UTC_TIMESTAMP(6) FROM AuditLogs WHERE @sync AND Id > @a0 AND Details LIKE '[{DATASET}/%';")
    w('')
    w('COMMIT;')
    w('')
    w('-- Result')
    w(f"SELECT COUNT(*) AS products_{DATASET} FROM Products WHERE Code LIKE '{DATASET}-%';")
    w(f"SELECT COUNT(*) AS batches_{DATASET}, SUM(b.Quantity) AS total_quantity FROM StockBatches b JOIN Products p ON p.Id = b.ProductId WHERE p.Code LIKE '{DATASET}-%';")
    w(f"SELECT COUNT(*) AS with_generic FROM Products WHERE Code LIKE '{DATASET}-%' AND GenericName IS NOT NULL;")
    w('SELECT @new_batches AS batches_added_now, (SELECT COUNT(*) FROM _ash_edited) AS generic_names_changed_on_existing;')
    w('DROP TEMPORARY TABLE IF EXISTS _ash_guard, _ash_pre, _ash_gen, _ash_edited;')

    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(L) + '\n')
    print(f'products {len(products)}, batches {len(rows)}, with generic {with_generic}, '
          f"drug {sum(p['kind'] == 'Drug' for p in products.values())}, general {sum(p['kind'] == 'General' for p in products.values())}, "
          f"inactive {sum(p['sale'] == 0 for p in products.values())}")


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
