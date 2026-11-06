using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Payment before fee-bearing services, a doctor's own records, audited handover, and Vision before the doctor.</summary>
[Collection("api")]
public sealed class CriticalRulesTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<(HttpClient Client, long UserId)> UserAsync(params string[] roles)
    {
        var name = Name("u");
        var id = await api.CreateUserAsync(name, roles);
        return (await api.LoginAsync(name), id);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task AssertStatus(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertRefused(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.Conflict)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, text);
        Assert.Contains(code, text);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task AssertLedgerReconciles()
    {
        var result = await InDbAsync(db => FinanceEndpoints.ReconcileAsync(db, default));
        Assert.True(result.Balanced, JsonSerializer.Serialize(result));
    }

    private async Task<long> DoctorStaffAsync(long? userId) => await InDbAsync(async db =>
    {
        var staff = new Staff { FullName = Name("داکتر"), IsDoctor = true, IsActive = true, UserId = userId };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff.Id;
    });

    private async Task<(PatientDto Patient, VisitRow Visit)> VisitAsync(long? doctorStaffId = null, string department = "doctor", int? serviceId = null, bool vision = true)
    {
        var desk = await api.FeeDeskAsync();
        var patient = await Read<PatientDto>(await desk.SendAsync(ApiFixture.Keyed("/api/patients",
            new SavePatientRequest(Name("احمد"), null, null, Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var visit = await Read<VisitRow>(await desk.SendAsync(ApiFixture.Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync(department), doctorStaffId, null, serviceId, await api.PaidTokenAsync(department)))), HttpStatusCode.Created);
        if (vision)
        {
            await api.CompleteVisionAsync(visit.Id);
            visit = (await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!;
        }

        return (patient, visit);
    }

    private async Task<int> NewServiceAsync(decimal price)
    {
        var (admin, _) = await UserAsync(Roles.SuperAdmin);
        var response = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest(Name("S"), Name("خدمت"), null, true, price));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<int> NewTestAsync(int? serviceId)
    {
        var (admin, _) = await UserAsync(Roles.SuperAdmin);
        var response = await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest(Name("OCT"), true, serviceId));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> CashierAsync()
    {
        var (cashier, _) = await UserAsync(Roles.Cashier);
        var registerId = await InDbAsync(async db =>
        {
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            return register.Id;
        });
        await AssertStatus(await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0)), HttpStatusCode.Created);
        return cashier;
    }

    private Task<long> InvoiceOfAsync(string sourceType, long sourceId) => InDbAsync(db =>
        db.InvoiceItems.Where(i => i.SourceType == sourceType && i.SourceId == sourceId).Select(i => i.InvoiceId).SingleAsync());

    private Task<InvoiceStatus> InvoiceStatusAsync(long id) => InDbAsync(db => db.Invoices.Where(i => i.Id == id).Select(i => i.Status).SingleAsync());

    private static async Task<PaymentRow> PayAsync(HttpClient cashier, long invoiceId, decimal amount) =>
        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(invoiceId, amount, PaymentMethod.Cash, null))), HttpStatusCode.Created);

    private async Task RefundAsync(long paymentId, decimal amount)
    {
        var (accountant, _) = await UserAsync(Roles.Accountant);
        await AssertStatus(await accountant.SendAsync(ApiFixture.Keyed("/api/refunds", new RefundRequest(paymentId, amount, "برگشت آزمایشی"))), HttpStatusCode.Created);
    }

    private static SaveConsultationRequest Exam(int? version = null) => new("یافته", null, null, null, null, version);

    [Fact]
    public async Task A_lab_test_with_a_fee_starts_only_after_full_payment_and_a_refund_closes_it_again()
    {
        var (lab, _) = await UserAsync(Roles.Lab);
        var cashier = await CashierAsync();
        var (patient, visit) = await VisitAsync();
        var testId = await NewTestAsync(await NewServiceAsync(300));

        var order = await Read<DiagnosticOrderDto>(await lab.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null))), HttpStatusCode.Created);
        Assert.False(order.Paid);

        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/start", new ChangeOrderStatusRequest(order.Version)), "payment_required");
        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", new SaveResultRequest("نتیجه", order.Version)), "payment_required");

        var invoiceId = await InvoiceOfAsync(nameof(DiagnosticOrder), order.Id);
        await PayAsync(cashier, invoiceId, 100);
        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/start", new ChangeOrderStatusRequest(order.Version)), "payment_required");
        await PayAsync(cashier, invoiceId, 200);
        await AssertStatus(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/start", new ChangeOrderStatusRequest(order.Version)), HttpStatusCode.NoContent);

        var refunded = await Read<DiagnosticOrderDto>(await lab.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null))), HttpStatusCode.Created);
        var payment = await PayAsync(cashier, await InvoiceOfAsync(nameof(DiagnosticOrder), refunded.Id), 300);
        await RefundAsync(payment.Id, 300);
        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{refunded.Id}/start", new ChangeOrderStatusRequest(refunded.Version)), "payment_required");

        var free = await Read<DiagnosticOrderDto>(await lab.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, await NewTestAsync(null), null))), HttpStatusCode.Created);
        await AssertStatus(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{free.Id}/start", new ChangeOrderStatusRequest(free.Version)), HttpStatusCode.NoContent);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_surgery_needs_a_fee_and_starts_only_after_full_payment()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (team, _) = await UserAsync(Roles.Surgery);
        var cashier = await CashierAsync();
        var (_, visit) = await VisitAsync();
        var serviceId = await NewServiceAsync(5000);

        await AssertRefused(await doctor.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(visit.Id, "نوع", EyeSide.OD))), "service_required", HttpStatusCode.BadRequest);

        var surgery = await Read<SurgeryRow>(await doctor.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(visit.Id, "نوع", EyeSide.OD, serviceId))), HttpStatusCode.Created);

        await AssertRefused(await team.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", new SaveSurgeryRequest(DateTime.UtcNow.AddDays(1), null, true, "گزارش زودهنگام", null, surgery.Version)), "payment_required");
        await AssertStatus(await team.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", new SaveSurgeryRequest(DateTime.UtcNow.AddDays(1), null, true, null, null, surgery.Version)), HttpStatusCode.NoContent);
        await AssertStatus(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Scheduled, surgery.Version + 1)), HttpStatusCode.NoContent);
        await AssertRefused(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.InProgress, surgery.Version + 2)), "payment_required");

        var invoiceId = await InvoiceOfAsync(nameof(SurgeryCase), surgery.Id);
        await PayAsync(cashier, invoiceId, 4000);
        await AssertRefused(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.InProgress, surgery.Version + 2)), "payment_required");
        await PayAsync(cashier, invoiceId, 1000);
        await AssertStatus(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.InProgress, surgery.Version + 2)), HttpStatusCode.NoContent);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_surgery_package_charges_only_its_fee_and_takes_its_supplies_from_the_chosen_store()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (team, _) = await UserAsync(Roles.Surgery);
        var (inventory, _) = await UserAsync(Roles.Inventory);
        var cashier = await CashierAsync();
        var (_, visit) = await VisitAsync();
        var serviceId = await NewServiceAsync(5000);

        async Task<int> SupplyAsync(decimal stock, decimal cost)
        {
            var response = await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest(Name("D"), "جنس " + Name("n"), ProductKind.General, null, "عدد", 0, 0));
            var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
            await AssertStatus(await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest(null, "surgery",
                [new PurchaseLineInput(id, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), stock, cost)]))), HttpStatusCode.Created);
            return id;
        }

        Task<decimal> OnHandAsync(int productId) => InDbAsync(db => db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity));

        var lens = await SupplyAsync(5, 800);
        var drops = await SupplyAsync(1, 50);
        var package = await Read<PackageRow>(await team.PostAsJsonAsync("/api/surgery-packages",
            new SavePackageRequest(Name("آب مروارید"), serviceId, "surgery", [new PackageItemInput(lens, 1), new PackageItemInput(drops, 2)])), HttpStatusCode.Created);
        Assert.Equal(5000m, package.Price);
        Assert.Equal(2, package.Items.Count);

        var surgery = await Read<SurgeryRow>(await doctor.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(visit.Id, package.Name, EyeSide.OD, null, package.Id))), HttpStatusCode.Created);
        var invoiceId = await InvoiceOfAsync(nameof(SurgeryCase), surgery.Id);

        // Only the package fee is invoiced: its supplies are inside it.
        Assert.Equal(5000m, await InDbAsync(db => db.Invoices.Where(i => i.Id == invoiceId).Select(i => i.Total).SingleAsync()));
        await AssertRefused(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/supplies", new RecordSuppliesRequest("surgery", [new PackageItemInput(lens, 1)], surgery.Version)), "case_not_started");

        await AssertStatus(await team.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", new SaveSurgeryRequest(DateTime.UtcNow.AddDays(1), null, true, null, null, surgery.Version)), HttpStatusCode.NoContent);
        await AssertStatus(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Scheduled, surgery.Version + 1)), HttpStatusCode.NoContent);
        await PayAsync(cashier, invoiceId, 5000);
        await AssertStatus(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.InProgress, surgery.Version + 2)), HttpStatusCode.NoContent);
        await AssertRefused(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Completed, surgery.Version + 3)), "supplies_required");

        // A short item names itself and nothing at all leaves stock.
        var shortItem = await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/supplies", new RecordSuppliesRequest("surgery", [new PackageItemInput(lens, 1), new PackageItemInput(drops, 2)], surgery.Version + 3));
        await AssertRefused(shortItem, "insufficient_stock");
        Assert.Equal(5m, await OnHandAsync(lens));

        var recorded = await Read<SuppliesRow>(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/supplies",
            new RecordSuppliesRequest("surgery", [new PackageItemInput(lens, 1), new PackageItemInput(drops, 1)], surgery.Version + 3)));
        Assert.True(recorded.Recorded);
        Assert.Equal(850m, recorded.Items.Sum(i => i.Cost));
        Assert.Equal(4m, await OnHandAsync(lens));
        Assert.Equal(0m, await OnHandAsync(drops));

        await AssertRefused(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/supplies", new RecordSuppliesRequest("surgery", [new PackageItemInput(lens, 1)], surgery.Version + 4)), "supplies_recorded");
        await AssertStatus(await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Completed, surgery.Version + 4)), HttpStatusCode.NoContent);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Cancelling_a_surgery_takes_back_an_unpaid_fee_and_a_paid_one_stops_it_until_refunded()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var cashier = await CashierAsync();
        var serviceId = await NewServiceAsync(5000);

        var (_, unpaidVisit) = await VisitAsync();
        var unpaid = await Read<SurgeryRow>(await doctor.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(unpaidVisit.Id, "نوع", EyeSide.OD, serviceId))), HttpStatusCode.Created);
        var unpaidInvoice = await InvoiceOfAsync(nameof(SurgeryCase), unpaid.Id);
        await AssertStatus(await doctor.PostAsJsonAsync($"/api/surgeries/{unpaid.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Cancelled, unpaid.Version)), HttpStatusCode.NoContent);
        Assert.Equal(InvoiceStatus.Void, await InvoiceStatusAsync(unpaidInvoice));

        var (_, paidVisit) = await VisitAsync();
        var paid = await Read<SurgeryRow>(await doctor.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(paidVisit.Id, "نوع", EyeSide.OD, serviceId))), HttpStatusCode.Created);
        var payment = await PayAsync(cashier, await InvoiceOfAsync(nameof(SurgeryCase), paid.Id), 5000);
        await AssertRefused(await doctor.PostAsJsonAsync($"/api/surgeries/{paid.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Cancelled, paid.Version)), "line_already_paid");
        await RefundAsync(payment.Id, 5000);
        await AssertStatus(await doctor.PostAsJsonAsync($"/api/surgeries/{paid.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Cancelled, paid.Version)), HttpStatusCode.NoContent);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task An_unpaid_extra_service_leaves_Vision_free_but_holds_the_exam_and_closes_with_the_visit()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var cashier = await CashierAsync();
        var serviceId = await NewServiceAsync(400);

        var (_, visit) = await VisitAsync(serviceId: serviceId);
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), visit.Status);
        await AssertRefused(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()), "payment_required");

        var desk = await api.FeeDeskAsync();
        await AssertRefused(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.InConsultation, visit.Version)), "payment_required");

        await PayAsync(cashier, await InvoiceOfAsync(nameof(Visit), visit.Id), 400);
        await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));

        var (_, cancelled) = await VisitAsync(serviceId: serviceId);
        var openInvoice = await InvoiceOfAsync(nameof(Visit), cancelled.Id);
        await AssertStatus(await desk.PostAsJsonAsync($"/api/visits/{cancelled.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, cancelled.Version)), HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Void, await InvoiceStatusAsync(openInvoice));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_doctor_cannot_change_the_record_of_another_doctors_patient()
    {
        var (owner, ownerUser) = await UserAsync(Roles.Doctor);
        var (other, _) = await UserAsync(Roles.Doctor);
        var (patient, visit) = await VisitAsync(await DoctorStaffAsync(ownerUser));
        var serviceId = await NewServiceAsync(100);
        var testId = await NewTestAsync(null);

        await AssertRefused(await other.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()), "not_responsible_doctor", HttpStatusCode.Forbidden);
        var consult = await Read<ConsultationDto>(await owner.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));

        await AssertRefused(await other.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam(consult.Version)), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(consult.Version, false)), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.SendAsync(ApiFixture.Keyed("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput("دوا", null, null, null, null, 1)]))), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.SendAsync(ApiFixture.Keyed("/api/optical-prescriptions", new CreateOpticalRxRequest(visit.Id, null, null, null, null, null, null, null, null, null, null))), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null))), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(visit.Id, "نوع", EyeSide.OD, serviceId))), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.PostAsJsonAsync("/api/follow-ups", new CreateFollowUpRequest(visit.Id, KabulClock.Today.AddDays(5), null)), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await AssertRefused(await other.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.AwaitingDiagnostic, consult.Version)), "not_responsible_doctor", HttpStatusCode.Forbidden);

        // Reading in an emergency is not writing.
        await AssertStatus(await other.PostAsJsonAsync($"/api/clinical/patients/{patient.Id}/emergency-access", new EmergencyAccessRequest("اضطرار آزمایشی")), HttpStatusCode.NoContent);
        await AssertRefused(await other.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam(consult.Version)), "not_responsible_doctor", HttpStatusCode.Forbidden);

        var finalized = await Read<ConsultationDto>(await owner.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(consult.Version, true)));
        await AssertRefused(await other.PostAsJsonAsync($"/api/consultations/{visit.Id}/amend", new AmendRequest("TreatmentPlan", "تغییر", "دلیل آزمایشی", finalized.Version)), "not_responsible_doctor", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_first_doctor_to_take_an_unassigned_patient_keeps_them()
    {
        var (first, _) = await UserAsync(Roles.Doctor);
        var (second, _) = await UserAsync(Roles.Doctor);
        var (_, visit) = await VisitAsync();

        var consult = await Read<ConsultationDto>(await first.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));
        await AssertRefused(await second.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam(consult.Version)), "not_responsible_doctor", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Handing_over_a_patient_is_limited_to_the_responsible_doctor_or_the_manager_and_is_audited()
    {
        var (owner, ownerUser) = await UserAsync(Roles.Doctor);
        var (next, nextUser) = await UserAsync(Roles.Doctor);
        var (manager, _) = await UserAsync(Roles.Manager);
        var ownerStaff = await DoctorStaffAsync(ownerUser);
        var nextStaff = await DoctorStaffAsync(nextUser);
        var noAccount = await DoctorStaffAsync(null);
        var (_, visit) = await VisitAsync(ownerStaff);
        var consult = await Read<ConsultationDto>(await owner.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));
        var desk = await api.FeeDeskAsync();

        async Task<int> VersionAsync() => (await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!.Version;

        // Taking a patient for oneself, or moving them from the desk, is not a handover.
        await AssertRefused(await next.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(nextStaff, "می‌خواهم بگیرم", await VersionAsync())), "transfer_not_allowed", HttpStatusCode.Forbidden);
        await AssertRefused(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(nextStaff, "تغییر از پذیرش", await VersionAsync())), "transfer_not_allowed", HttpStatusCode.Forbidden);

        await AssertStatus(await owner.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(nextStaff, "  ", await VersionAsync())), HttpStatusCode.BadRequest);
        await AssertRefused(await owner.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(noAccount, "داکتر بدون حساب", await VersionAsync())), "doctor_no_account");

        const string reason = "رفتن داکتر به عملیات";
        var moved = await Read<VisitRow>(await owner.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(nextStaff, reason, await VersionAsync())));
        Assert.Equal(nextStaff, moved.DoctorStaffId);

        var key = visit.Id.ToString();
        var audited = await InDbAsync(db => db.AuditLogs.Where(a => a.Action == "VisitDoctorTransferred" && a.EntityId == key).Select(a => new { a.UserId, a.Details }).SingleAsync());
        Assert.Equal(ownerUser, audited.UserId);
        Assert.Contains(reason, audited.Details);
        Assert.Contains($"{ownerStaff} => {nextStaff}", audited.Details);

        // The record travels with the patient and only the new doctor may change it.
        await AssertRefused(await owner.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam(consult.Version)), "not_responsible_doctor", HttpStatusCode.Forbidden);
        var continued = await Read<ConsultationDto>(await next.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam(consult.Version)));
        Assert.Equal(consult.Id, continued.Id);

        // A doctor who cannot hand over: the manager moves the patient without any access to the record.
        await AssertStatus(await manager.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(ownerStaff, "غیبت داکتر", await VersionAsync())), HttpStatusCode.OK);
        await AssertStatus(await manager.GetAsync($"/api/consultations/{visit.Id}/chart"), HttpStatusCode.Forbidden);

        var last = await Read<ConsultationDto>(await owner.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(continued.Version, true)));
        Assert.NotNull(last.FinalizedAt);
        await AssertRefused(await manager.PostAsJsonAsync($"/api/visits/{visit.Id}/transfer-doctor", new TransferDoctorRequest(nextStaff, "پس از بستن", await VersionAsync())), "visit_closed");
    }

    [Fact]
    public async Task Nobody_reaches_the_doctor_by_hand_before_Vision_is_complete()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (triage, _) = await UserAsync(Roles.Triage);
        var desk = await api.FeeDeskAsync();

        // Choosing the doctor's department at the desk is not a way around it either.
        var (_, visit) = await VisitAsync(department: "doctor", vision: false);
        Assert.DoesNotContain(nameof(VisitStatus.ReadyForDoctor), visit.NextStatuses);
        Assert.DoesNotContain(nameof(VisitStatus.InConsultation), visit.NextStatuses);
        Assert.Contains((await triage.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=triage", JsonOptions))!, v => v.Id == visit.Id);
        Assert.DoesNotContain((await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions))!, v => v.Id == visit.Id);

        await AssertRefused(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.ReadyForDoctor, visit.Version)), "vision_required");
        await AssertRefused(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.InConsultation, visit.Version)), "vision_required");
        await AssertRefused(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()), "vision_required");

        var waiting = await Read<VisitRow>(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Waiting, visit.Version)));
        await AssertRefused(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.ReadyForDoctor, waiting.Version)), "vision_required");

        // An empty form is not Vision.
        var blank = await Read<PreExamDto>(await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest("شکایت", null, null, null, null, null, null, null, null)));
        await AssertRefused(await triage.PostAsync($"/api/pre-examinations/{visit.Id}/complete", null), "vision_empty");

        await Read<PreExamDto>(await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest("شکایت", null, "6/9", null, null, null, null, null, blank.Version)));
        await AssertStatus(await triage.PostAsync($"/api/pre-examinations/{visit.Id}/complete", null), HttpStatusCode.NoContent);
        var ready = (await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!;
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), ready.Status);

        // After Vision the desk may still send a patient back and forth.
        var back = await Read<VisitRow>(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Waiting, ready.Version)));
        Assert.Contains(nameof(VisitStatus.ReadyForDoctor), back.NextStatuses);
        await Read<VisitRow>(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.ReadyForDoctor, back.Version)));
        await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));
    }

    [Fact]
    public async Task A_refraction_finished_before_Vision_does_not_carry_the_patient_to_the_doctor()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (triage, _) = await UserAsync(Roles.Triage);
        var desk = await api.FeeDeskAsync();
        var (patient, visit) = await VisitAsync(department: "triage", vision: false);

        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var refraction = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction").GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, refraction, null))), HttpStatusCode.Created);
        await api.PayFeeAsync(await api.OrderFeeAsync(patient.FileNumber, order.TestName));

        await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", new SaveRefractionRequest("6/18", null, null, null, null, null, null, null, null, [], null)));
        await AssertStatus(await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null), HttpStatusCode.NoContent);

        Assert.Equal(nameof(VisitStatus.Registered), (await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!.Status);
    }
}
