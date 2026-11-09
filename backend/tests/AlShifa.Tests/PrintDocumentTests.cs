using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Reports;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class PrintDocumentTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Payment_receipt_title_comes_from_its_persisted_source_link()
    {
        var cashier = await api.FeeCashierAsync();
        var ids = await SeedClassifiedReceiptsAsync();

        foreach (var (paymentId, expectedTitle) in ids)
        {
            var document = await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/receipt/{paymentId}", JsonOptions);
            Assert.NotNull(document);
            Assert.Equal(expectedTitle, document.Title);
        }
    }

    [Fact]
    public async Task Printing_is_read_only_and_reports_refunds_voids_and_refund_aware_balance()
    {
        var cashier = await api.FeeCashierAsync();
        long invoiceId;
        long paymentId;
        long refundId;
        long voidInvoiceId;

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.CashSessions.Where(s => s.ClosedAt == null).OrderByDescending(s => s.Id).FirstAsync();

            var invoice = NewInvoice(session.OpenedByUserId, 300, 200, 50, InvoiceStatus.PartiallyPaid);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            db.InvoiceItems.Add(NewLine(invoice.Id, "خدمت ثبت‌شده", 300, null, null));
            var payment = NewPayment(invoice.Id, session, 200, 50);
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            var refund = new Refund { RefundNumber = Unique("RF"), PaymentId = payment.Id, Amount = 50, Reason = "برگشت آزمایشی", ApprovedByUserId = session.OpenedByUserId };
            db.Refunds.Add(refund);

            var voidInvoice = NewInvoice(session.OpenedByUserId, 0, 0, 0, InvoiceStatus.Void);
            db.Invoices.Add(voidInvoice);
            await db.SaveChangesAsync();
            db.InvoiceItems.Add(new InvoiceItem
            {
                InvoiceId = voidInvoice.Id,
                Description = "خدمت باطل‌شده",
                Quantity = 1,
                UnitPrice = 90,
                LineTotal = 90,
                RevenueAccount = Accounts.ServiceRevenue,
                IsVoid = true,
            });
            await db.SaveChangesAsync();
            invoiceId = invoice.Id;
            paymentId = payment.Id;
            refundId = refund.Id;
            voidInvoiceId = voidInvoice.Id;
        }

        var before = await CountsAsync();
        var invoiceDocument = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/invoice/{invoiceId}", JsonOptions))!;
        var receiptDocument = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/receipt/{paymentId}", JsonOptions))!;
        var refundDocument = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/refund/{refundId}", JsonOptions))!;
        var voidDocument = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/invoice/{voidInvoiceId}", JsonOptions))!;
        var after = await CountsAsync();

        Assert.Equal(before, after);
        Assert.Contains(invoiceDocument.Fields, f => f.Label == "وضعیت" && f.Value == "پرداخت جزئی پس از برگشت");
        Assert.Contains(invoiceDocument.Totals!, f => f.Label == "برگشت وجه" && f.Value!.Contains("50"));
        Assert.Contains(invoiceDocument.Totals!, f => f.Label == "مانده" && f.Value!.Contains("150"));
        Assert.Contains(receiptDocument.Fields, f => f.Label == "وضعیت" && f.Value == "برگشت جزئی");
        Assert.Contains(receiptDocument.Totals!, f => f.Label == "دریافت خالص" && f.Value!.Contains("150"));
        Assert.Contains(receiptDocument.Totals!, f => f.Label == "مانده فاکتور" && f.Value!.Contains("150"));
        Assert.Equal("رسید برگشت وجه", refundDocument.Title);
        Assert.Contains(refundDocument.Totals!, f => f.Label == "مبلغ برگشت" && f.Value!.Contains("50"));
        Assert.Contains(voidDocument.Fields, f => f.Label == "وضعیت" && f.Value == "باطل");
        Assert.Contains(voidDocument.Fields, f => f.Label == "آیتم‌های باطل‌شده" && f.Value == "1");
    }

    [Fact]
    public async Task Pharmacy_sale_prints_the_stored_invoice_lines_and_status()
    {
        var userName = Unique("pharm");
        var userId = await api.CreateUserAsync(userName, Roles.Pharmacist);
        var pharmacist = await api.LoginAsync(userName);
        long saleId;
        string invoiceNumber;

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoice = NewInvoice(userId, 600, 300, 0, InvoiceStatus.PartiallyPaid);
            invoice.Discount = 50;
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            db.InvoiceItems.Add(new InvoiceItem
            {
                InvoiceId = invoice.Id, Description = "Tobramycin eye drops", Quantity = 2,
                UnitPrice = 300, LineTotal = 600, RevenueAccount = Accounts.PharmacyRevenue,
            });
            var sale = new Sale { SaleNumber = Unique("PH"), Kind = SaleKind.Pharmacy, InvoiceId = invoice.Id, CreatedByUserId = userId };
            db.Sales.Add(sale);
            await db.SaveChangesAsync();
            saleId = sale.Id;
            invoiceNumber = invoice.InvoiceNumber;
        }

        var document = (await pharmacist.GetFromJsonAsync<PrintDocument>($"/api/print/sale/{saleId}", JsonOptions))!;
        Assert.Equal("فاکتور دواخانه", document.Title);
        Assert.Equal(invoiceNumber, document.Number);
        Assert.Equal("a4", document.Format);
        Assert.Equal(["دوا", "تعداد", "قیمت واحد", "مبلغ"], document.Headers);
        Assert.Contains(document.Rows!, row => row[0] == "Tobramycin eye drops" && row[1] == "2" && row[2]!.Contains("300") && row[3]!.Contains("600"));
        Assert.Contains(document.Totals!, f => f.Label == "مانده" && f.Value!.Contains("250"));
    }

    private async Task<List<(long PaymentId, string Title)>> SeedClassifiedReceiptsAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.CashSessions.Where(s => s.ClosedAt == null).OrderByDescending(s => s.Id).FirstAsync();
        var userId = session.OpenedByUserId;
        var departmentId = await db.Departments.Select(d => d.Id).FirstAsync();
        var patient = new Patient { FileNumber = Unique("PF"), FirstName = "مریض چاپ", Gender = Gender.Other, CreatedByUserId = userId };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        var result = new List<(long, string)>();

        var visitInvoice = await AddInvoiceAsync(db, session, userId);
        var token = new VisitToken { TokenDate = DateOnly.FromDateTime(DateTime.UtcNow), DepartmentId = departmentId, InvoiceId = visitInvoice.Invoice.Id, CreatedByUserId = userId };
        db.VisitTokens.Add(token);
        await db.SaveChangesAsync();
        db.InvoiceItems.Add(NewLine(visitInvoice.Invoice.Id, "فیس معاینه", 100, nameof(VisitToken), token.Id));
        await db.SaveChangesAsync();
        result.Add((visitInvoice.Payment.Id, "رسید فیس معاینه داکتر"));

        foreach (var (kind, title) in new[]
        {
            (DiagnosticKind.Refraction, "رسید پرداخت رفرکشن"),
            (DiagnosticKind.Imaging, "رسید پرداخت تصویربرداری"),
            (DiagnosticKind.Lab, "رسید لابراتوار"),
        })
        {
            var diagnosticInvoice = await AddInvoiceAsync(db, session, userId);
            var test = new DiagnosticTest { Name = $"آزمایش {kind} {Unique("T")}", Kind = kind, IsActive = true };
            db.DiagnosticTests.Add(test);
            await db.SaveChangesAsync();
            var order = new DiagnosticOrder { OrderNumber = Unique("LAB"), PatientId = patient.Id, TestId = test.Id, RequestedByUserId = userId, InvoiceId = diagnosticInvoice.Invoice.Id };
            db.DiagnosticOrders.Add(order);
            await db.SaveChangesAsync();
            db.InvoiceItems.Add(NewLine(diagnosticInvoice.Invoice.Id, test.Name, 100, nameof(DiagnosticOrder), order.Id));
            await db.SaveChangesAsync();
            result.Add((diagnosticInvoice.Payment.Id, title));
        }

        var surgeryInvoice = await AddInvoiceAsync(db, session, userId);
        db.InvoiceItems.Add(NewLine(surgeryInvoice.Invoice.Id, "عملیات ثبت‌شده", 100, nameof(SurgeryCase), 987654321));
        await db.SaveChangesAsync();
        result.Add((surgeryInvoice.Payment.Id, "رسید خدمات عملیات"));
        return result;
    }

    private static async Task<(Invoice Invoice, Payment Payment)> AddInvoiceAsync(AppDbContext db, CashSession session, long userId)
    {
        var invoice = NewInvoice(userId, 100, 100, 0, InvoiceStatus.Paid);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        var payment = NewPayment(invoice.Id, session, 100, 0);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return (invoice, payment);
    }

    private static Invoice NewInvoice(long userId, decimal total, decimal paid, decimal refunded, InvoiceStatus status) => new()
    {
        InvoiceNumber = Unique("INV"),
        Total = total,
        Paid = paid,
        Refunded = refunded,
        Status = status,
        IssuedByUserId = userId,
    };

    private static Payment NewPayment(long invoiceId, CashSession session, decimal amount, decimal refunded) => new()
    {
        ReceiptNumber = Unique("RC"),
        InvoiceId = invoiceId,
        Amount = amount,
        Refunded = refunded,
        Method = PaymentMethod.Cash,
        CashSessionId = session.Id,
        ReceivedByUserId = session.OpenedByUserId,
    };

    private static InvoiceItem NewLine(long invoiceId, string description, decimal amount, string? sourceType, long? sourceId) => new()
    {
        InvoiceId = invoiceId,
        Description = description,
        Quantity = 1,
        UnitPrice = amount,
        LineTotal = amount,
        RevenueAccount = Accounts.ServiceRevenue,
        SourceType = sourceType,
        SourceId = sourceId,
    };

    private async Task<(int Invoices, int Items, int Payments, int Refunds)> CountsAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Invoices.CountAsync(), await db.InvoiceItems.CountAsync(), await db.Payments.CountAsync(), await db.Refunds.CountAsync());
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];
}
