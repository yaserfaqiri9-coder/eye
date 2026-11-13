using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Tests;

/// <summary>
/// The pharmacy flow: a prescription is only a reference; a bill reserves stock and goes to the cashier;
/// stock leaves and the drug counts as dispensed only when a paid bill is handed over.
/// </summary>
[Collection("api")]
public sealed class PharmacyFlowTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Name(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private sealed record Setup(long PatientId, string FileNumber, long VisitId, long RxId, string RxNumber, long ItemId, int ProductId, string Drug, HttpClient Doctor, HttpClient Pharmacist);

    /// <summary>A patient with a prescription of one drug the pharmacy stocks.</summary>
    private async Task<Setup> PrescriptionAsync(decimal prescribed, decimal stock, string? father = null, string? phone = null, DateOnly? validUntil = null)
    {
        var inventory = await UserAsync(Roles.Inventory);
        var drug = "قطره " + Name("d");
        var product = await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest(drug, ProductKind.Drug, null, "عدد", 0, 100));
        var productId = JsonDocument.Parse(await product.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest("تأمین", "pharmacy",
            [new PurchaseLineInput(productId, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), stock, 40)])))).StatusCode);

        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest(Name("احمد"), "کریمی", father, Gender.Male, null, 40, phone, null))));
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(ApiFixture.Keyed("/api/visits", new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == "داکتر").Id, null, null, null, await api.PaidTokenAsync()))));
        var doctor = await UserAsync(Roles.Doctor);
        var rx = await Created<PrescriptionDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput(drug, "یک قطره", "چشم", "۵ روز", null, prescribed)], validUntil))));
        return new(patient.Id, patient.FileNumber, visit.Id, rx.Id, rx.RxNumber, rx.Items[0].Id, productId, drug, doctor, await UserAsync(Roles.Pharmacist));
    }

    private static HttpRequestMessage Bill(Setup s, decimal quantity) =>
        ApiFixture.Keyed("/api/pharmacy/sales", new CreateSaleRequest(s.PatientId, s.RxId, [new SaleLineInput(s.ProductId, quantity, s.ItemId)]));

    private Task<decimal> OnHand(int productId) => api.InDbAsync(db => db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity));

    private Task<decimal> Dispensed(long itemId) => api.InDbAsync(db => db.Set<MedicalPrescriptionItem>().Where(i => i.Id == itemId).Select(i => i.DispensedQuantity).SingleAsync());

    private Task<PharmacyRxDetail> Detail(HttpClient client, long rxId) => client.GetFromJsonAsync<PharmacyRxDetail>($"/api/pharmacy/prescriptions/{rxId}", JsonOptions)!;

    private Task<int> SaleVersion(long saleId) => api.InDbAsync(db => db.Sales.Where(s => s.Id == saleId).Select(s => s.Version).SingleAsync());

    [Fact]
    public async Task Drug_search_ignores_letter_case_and_outer_spaces_and_keeps_the_stored_name()
    {
        var inventory = await UserAsync(Roles.Inventory);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var stored = $"Vigamox{tag} Eye Drops";
        var created = await Created<JsonElement>(await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest(stored, ProductKind.Drug, "قطره چشمی", "قطی", 0, 100)));
        var id = created.GetProperty("id").GetInt32();
        var code = created.GetProperty("code").GetString()!;
        Assert.Matches("^[0-9]{1,4}$", code);

        async Task<PagedResult<ProductRow>> FindAsync(string q) =>
            (await inventory.GetFromJsonAsync<PagedResult<ProductRow>>($"/api/inventory/products?pageSize=50&q={Uri.EscapeDataString(q)}", JsonOptions))!;

        foreach (var q in new[] { $"vigamox{tag}", $"Vigamox{tag}", $"VIGAMOX{tag}", $"  vIgAmOx{tag}  " })
        {
            var page = await FindAsync(q);
            var row = Assert.Single(page.Items);
            Assert.Equal(id, row.Id);
            Assert.Equal(1, page.Total);
            Assert.Equal(stored, row.Name); // the stored spelling is untouched
        }

        // The short code finds it too; other codes may start with the same digits.
        Assert.Contains((await FindAsync(code)).Items, r => r.Id == id);

        // Wildcards are plain text and never widen the search.
        Assert.Empty((await FindAsync($"%amox{tag}")).Items);
        Assert.Empty((await FindAsync($"_igamox{tag}")).Items);
        Assert.Equal(stored, await api.InDbAsync(db => db.Products.Where(p => p.Id == id).Select(p => p.Name).SingleAsync()));

        // Doctors pick drug names for a prescription the same way.
        var doctor = await UserAsync(Roles.Doctor);
        var names = await doctor.GetFromJsonAsync<List<string>>($"/api/prescriptions/drugs?q={Uri.EscapeDataString($"VIGAMOX{tag}")}");
        Assert.Equal([stored], names);
    }

    [Fact]
    public async Task Drug_form_is_kept_apart_from_the_stock_unit_and_shown_in_pharmacy_stock()
    {
        var inventory = await UserAsync(Roles.Inventory);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var form = $"قطره چشمی {tag}";
        var created = await Created<JsonElement>(await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest($"دوای {tag}", ProductKind.Drug, form, "قطی", 0, 50)));
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest("تأمین", "pharmacy",
            [new PurchaseLineInput(id, $"L{tag}", DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), 12, 20)])))).StatusCode);

        var row = Assert.Single((await inventory.GetFromJsonAsync<PagedResult<ProductRow>>($"/api/inventory/products?q={Uri.EscapeDataString($"دوای {tag}")}", JsonOptions))!.Items);
        Assert.Equal(("Drug", form, "قطی", 12m), (row.Kind, row.Category, row.Unit, row.OnHand));

        var pharmacy = await inventory.GetFromJsonAsync<LocationDetail>("/api/inventory/locations/pharmacy", JsonOptions);
        var stock = Assert.Single(pharmacy!.Stock, s => s.ProductId == id);
        Assert.Equal(("Drug", form, "قطی", 12m), (stock.Kind, stock.Category, stock.Unit, stock.Quantity));

        Assert.Contains(form, (await inventory.GetFromJsonAsync<List<string>>("/api/inventory/drug-forms"))!);
    }

    [Fact]
    public async Task A_prescription_alone_creates_no_bill_or_debt_and_stays_in_the_patients_history()
    {
        var s = await PrescriptionAsync(2, 10);

        Assert.False(await api.InDbAsync(db => db.Sales.AnyAsync(x => x.PrescriptionId == s.RxId)));
        Assert.False(await api.InDbAsync(db => db.InvoiceItems.AnyAsync(i => i.Invoice!.PatientId == s.PatientId && i.SourceType == nameof(SaleItem))));
        Assert.Equal(10, await OnHand(s.ProductId));

        var history = await s.Pharmacist.GetFromJsonAsync<JsonElement>($"/api/pharmacy/patients/{s.PatientId}/prescriptions", JsonOptions);
        var row = Assert.Single(history.GetProperty("prescriptions").EnumerateArray());
        Assert.Equal("AwaitingVisit", row.GetProperty("stage").GetString());
        var detail = await Detail(s.Pharmacist, s.RxId);
        Assert.Equal(("Available", 2m, 10m, 100m), (detail.Items[0].Stock, detail.Items[0].Remaining, detail.Items[0].Available, detail.Items[0].SalePrice!.Value));
    }

    [Fact]
    public async Task Stock_leaves_only_when_a_paid_bill_is_handed_over_and_only_once()
    {
        var s = await PrescriptionAsync(2, 10);
        var sale = await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 2)));

        Assert.Equal((10m, 0m), (await OnHand(s.ProductId), await Dispensed(s.ItemId)));
        var queues = await s.Pharmacist.GetFromJsonAsync<PharmacyQueues>("/api/pharmacy/queues", JsonOptions);
        Assert.Contains(queues!.AwaitingPayment, b => b.SaleId == sale.Id);
        Assert.Equal("AwaitingPayment", (await Detail(s.Pharmacist, s.RxId)).Prescription.Stage);

        var early = await s.Pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/deliver", new DeliverSaleRequest(await SaleVersion(sale.Id)));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("payment_required", await early.Content.ReadAsStringAsync());

        await api.PayAndDeliverAsync(s.Pharmacist, sale.Id);
        Assert.Equal((8m, 2m), (await OnHand(s.ProductId), await Dispensed(s.ItemId)));
        Assert.Equal("Delivered", (await Detail(s.Pharmacist, s.RxId)).Prescription.Stage);
        Assert.True(await api.InDbAsync(db => db.LedgerTransactions.AnyAsync(t => t.Kind == "Cogs" && t.SourceType == nameof(Sale) && t.SourceId == sale.Id)));

        var twice = await s.Pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/deliver", new DeliverSaleRequest(await SaleVersion(sale.Id)));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(8, await OnHand(s.ProductId));

        // Reported as a sale only now that it was delivered.
        Assert.NotNull(await api.InDbAsync(db => db.Sales.Where(x => x.Id == sale.Id).Select(x => x.DeliveredAt).SingleAsync()));
    }

    [Fact]
    public async Task A_partial_purchase_keeps_the_rest_for_later_and_a_bill_holds_its_stock_until_cancelled()
    {
        var s = await PrescriptionAsync(5, 3);
        var first = await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 2)));

        // Another patient cannot take the two units the first bill holds.
        var walkIn = await s.Pharmacist.SendAsync(ApiFixture.Keyed("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(s.ProductId, 2)])));
        Assert.Contains("insufficient_stock", await walkIn.Content.ReadAsStringAsync());

        // Cancelling the unpaid bill frees its stock and dispenses nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{first.Id}/return", new ReturnSaleRequest(await SaleVersion(first.Id), "مریض نخواست"))).StatusCode);
        Assert.Equal((3m, 0m), (await OnHand(s.ProductId), await Dispensed(s.ItemId)));

        var bought = await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 2)));
        await api.PayAndDeliverAsync(s.Pharmacist, bought.Id);
        var detail = await Detail(s.Pharmacist, s.RxId);
        Assert.Equal(("Partial", 2m, 3m), (detail.Prescription.Stage, detail.Items[0].Dispensed, detail.Items[0].Remaining));
        Assert.Equal("Short", detail.Items[0].Stock);

        // The rest cannot exceed what the doctor prescribed.
        var over = await s.Pharmacist.SendAsync(Bill(s, 4));
        Assert.Contains("over_dispense", await over.Content.ReadAsStringAsync());
        var rest = await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 1)));
        await api.PayAndDeliverAsync(s.Pharmacist, rest.Id);
        Assert.Equal(3m, await Dispensed(s.ItemId));
    }

    [Fact]
    public async Task A_lapsed_prescription_waits_for_the_doctor_and_the_pharmacy_cannot_lift_it()
    {
        var s = await PrescriptionAsync(2, 10, validUntil: KabulClock.Today);
        await api.InDbAsync(db => db.MedicalPrescriptions.Where(p => p.Id == s.RxId).ExecuteUpdateAsync(u => u.SetProperty(p => p.ValidUntil, KabulClock.Today.AddDays(-1))));

        var refused = await s.Pharmacist.SendAsync(Bill(s, 1));
        Assert.Contains("prescription_expired", await refused.Content.ReadAsStringAsync());
        Assert.Equal("NeedsDoctorReview", (await Detail(s.Pharmacist, s.RxId)).Prescription.Stage);

        // The pharmacy has no right to change the prescription's validity.
        var version = await api.InDbAsync(db => db.MedicalPrescriptions.Where(p => p.Id == s.RxId).Select(p => p.Version).SingleAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Pharmacist.PostAsJsonAsync($"/api/prescriptions/{s.RxId}/reapprove", new ReapprovePrescriptionRequest(KabulClock.Today.AddDays(7), "تمدید", version))).StatusCode);

        var renewed = await s.Doctor.PostAsJsonAsync($"/api/prescriptions/{s.RxId}/reapprove", new ReapprovePrescriptionRequest(KabulClock.Today.AddDays(7), "معاینه شد، ادامه دهد", version));
        Assert.True(renewed.StatusCode == HttpStatusCode.NoContent, await renewed.Content.ReadAsStringAsync());
        await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 1)));
    }

    [Fact]
    public async Task Decisions_are_recorded_with_who_and_when_and_never_change_the_prescription()
    {
        var s = await PrescriptionAsync(2, 10);
        async Task<HttpResponseMessage> Decide(PharmacyRxStatus status, string? note)
        {
            var version = await api.InDbAsync(db => db.MedicalPrescriptions.Where(p => p.Id == s.RxId).Select(p => p.Version).SingleAsync());
            return await s.Pharmacist.PostAsJsonAsync($"/api/pharmacy/prescriptions/{s.RxId}/status", new SetPharmacyStatusRequest(status, note, version), JsonOptions);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await Decide(PharmacyRxStatus.BuyingOutside, "نسخه چاپی گرفت")).StatusCode);
        var detail = await Detail(s.Pharmacist, s.RxId);
        Assert.Equal(("BuyingOutside", "Issued", 2m), (detail.Prescription.Stage, detail.Prescription.Status, detail.Items[0].Quantity));
        var recorded = Assert.Single(detail.Events);
        Assert.NotNull(recorded.UserName);

        // The patient changed their mind and came back: a bill is still possible.
        await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 1)));

        // Once the pharmacy asks the doctor, only the doctor clears it.
        Assert.Equal(HttpStatusCode.NoContent, (await Decide(PharmacyRxStatus.NeedsDoctorReview, "دوز روشن نیست")).StatusCode);
        Assert.Contains("doctor_review_required", await (await s.Pharmacist.SendAsync(Bill(s, 1))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(PharmacyRxStatus.AwaitingDecision, null)).StatusCode);
        Assert.Contains((await s.Pharmacist.GetFromJsonAsync<PharmacyQueues>("/api/pharmacy/queues", JsonOptions))!.NeedsDoctor, r => r.Id == s.RxId);
    }

    [Fact]
    public async Task Another_drug_cannot_be_given_in_place_of_the_prescribed_one()
    {
        var s = await PrescriptionAsync(2, 10);
        var inventory = await UserAsync(Roles.Inventory);
        var other = await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest("دیگر " + Name("x"), ProductKind.Drug, null, "عدد", 0, 50));
        var otherId = JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest(null, "pharmacy", [new PurchaseLineInput(otherId, "L", null, 5, 10)])));

        var swapped = await s.Pharmacist.SendAsync(ApiFixture.Keyed("/api/pharmacy/sales", new CreateSaleRequest(s.PatientId, s.RxId, [new SaleLineInput(otherId, 1, s.ItemId)])));
        Assert.Contains("substitution_not_allowed", await swapped.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unpaid_bill_goes_out_only_with_the_approval_right_and_a_reason()
    {
        var s = await PrescriptionAsync(2, 10);
        var sale = await Created<SaleRow>(await s.Pharmacist.SendAsync(Bill(s, 1)));

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/deliver", new DeliverSaleRequest(await SaleVersion(sale.Id), "مریض اورژانسی"))).StatusCode);

        var head = await UserAsync(Roles.Pharmacist, Roles.Manager);
        var done = await head.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/deliver", new DeliverSaleRequest(await SaleVersion(sale.Id), "مریض اورژانسی، پرداخت فردا"));
        Assert.True(done.StatusCode == HttpStatusCode.NoContent, await done.Content.ReadAsStringAsync());
        Assert.Equal(9, await OnHand(s.ProductId));
        Assert.Equal("مریض اورژانسی، پرداخت فردا", await api.InDbAsync(db => db.Sales.Where(x => x.Id == sale.Id).Select(x => x.UnpaidDeliveryReason).SingleAsync()));
    }

    [Fact]
    public async Task Search_finds_the_patient_by_father_phone_in_persian_digits_or_any_number_on_their_papers()
    {
        var father = Name("ولي");
        var phone = "07" + Random.Shared.Next(10000000, 99999999);
        var s = await PrescriptionAsync(1, 5, father: father, phone: phone);
        async Task<List<PharmacyPatientHit>> Find(string q) => (await s.Pharmacist.GetFromJsonAsync<List<PharmacyPatientHit>>($"/api/pharmacy/search?q={Uri.EscapeDataString(q)}", JsonOptions))!;

        var persianDigits = string.Concat(phone.Select(c => (char)('۰' + (c - '0'))));
        Assert.Contains(await Find(persianDigits), p => p.Id == s.PatientId);
        Assert.Contains(await Find(father.Replace('ي', 'ی')), p => p.Id == s.PatientId && p.FatherName == father);
        Assert.Contains(await Find(s.RxNumber), p => p.Id == s.PatientId);
        Assert.Contains(await Find(s.FileNumber.ToLowerInvariant()), p => p.Id == s.PatientId);
        Assert.Contains(await Find(s.RxNumber.Split('-')[1]), p => p.Id == s.PatientId);
    }
}
