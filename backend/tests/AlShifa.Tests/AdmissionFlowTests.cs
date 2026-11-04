using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>The single flow: patient and visit at the desk, payment at the cashier, then a queue number issued by the payment itself.</summary>
[Collection("api")]
public sealed class AdmissionFlowTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task AssertRefused(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.Conflict)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, text);
        Assert.Contains(code, text);
    }

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
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

    private async Task<PatientDto> NewPatientAsync()
    {
        var desk = await api.FeeDeskAsync();
        return await Read<PatientDto>(await desk.SendAsync(ApiFixture.Keyed("/api/patients",
            new SavePatientRequest(Name("بیمار"), null, null, Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
    }

    private async Task<AdmissionRow> AdmitAsync(long? patientId = null)
    {
        var desk = await api.FeeDeskAsync();
        var id = patientId ?? (await NewPatientAsync()).Id;
        return await Read<AdmissionRow>(await desk.SendAsync(ApiFixture.Keyed("/api/admissions",
            new CreateAdmissionRequest(id, await api.DepartmentIdAsync("triage"), null, "تاری دید"))), HttpStatusCode.Created);
    }

    private static HttpRequestMessage PayRequest(AdmissionRow row, string? key = null) =>
        ApiFixture.Keyed("/api/payments", new PaymentRequest(row.InvoiceId, row.Balance, PaymentMethod.Cash, null, row.InvoiceVersion), key);

    /// <summary>The cashier collects the fee; the desk then reads the result the way its screen does.</summary>
    private async Task<AdmissionRow> PayAsync(AdmissionRow row)
    {
        var cashier = await api.FeeCashierAsync();
        await Read<PaymentRow>(await cashier.SendAsync(PayRequest(row)), HttpStatusCode.Created);
        var desk = await api.FeeDeskAsync();
        return (await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!;
    }

    [Fact]
    public async Task Reception_sends_the_visit_fee_to_the_cashier_and_cannot_collect_it_itself()
    {
        var desk = await api.FeeDeskAsync();
        var row = await AdmitAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.SendAsync(PayRequest(row))).StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Payments.CountAsync(p => p.InvoiceId == row.InvoiceId)));

        var cashier = await api.FeeCashierAsync();
        var waiting = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={row.InvoiceNumber}", JsonOptions);
        Assert.Equal(row.Balance, Assert.Single(waiting!.Items).Balance);
    }

    [Fact]
    public async Task The_cashier_gets_the_number_with_the_payment_and_on_the_receipt()
    {
        var cashier = await api.FeeCashierAsync();
        var row = await AdmitAsync();

        var payment = await Read<PaymentRow>(await cashier.SendAsync(PayRequest(row)), HttpStatusCode.Created);
        var issued = (await (await api.FeeDeskAsync()).GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!.QueueNumber;
        Assert.NotNull(issued);
        Assert.Equal(issued, payment.QueueNumber);

        var receipt = await cashier.GetFromJsonAsync<JsonElement>($"/api/print/receipt/{payment.Id}");
        var number = receipt.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("label").GetString() == "شماره نوبت");
        Assert.Equal(issued.ToString(), number.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_visit_stands_in_no_queue_until_its_fee_is_paid_and_the_payment_issues_the_number()
    {
        var desk = await api.FeeDeskAsync();
        var triage = await UserAsync(Roles.Triage);
        var doctor = await UserAsync(Roles.Doctor);
        var row = await AdmitAsync();

        Assert.Null(row.QueueNumber);
        Assert.False(row.Paid);
        Assert.Equal(nameof(VisitStatus.AwaitingPayment), row.Status);
        Assert.Equal(100, row.Balance);
        Assert.Contains((await desk.GetFromJsonAsync<List<AdmissionRow>>("/api/admissions/pending", JsonOptions))!, a => a.VisitId == row.VisitId);

        foreach (var kind in new[] { "triage", "doctor" })
        {
            var queue = await doctor.GetFromJsonAsync<List<VisitRow>>($"/api/clinical/worklist?kind={kind}", JsonOptions);
            Assert.DoesNotContain(queue!, v => v.Id == row.VisitId);
        }

        await AssertRefused(await triage.PutAsJsonAsync($"/api/pre-examinations/{row.VisitId}", new SavePreExamRequest(null, null, "6/9", "6/9", null, null, null, null, null)), "payment_required");
        await AssertRefused(await desk.GetAsync($"/api/print/visit/{row.VisitId}"), "no_queue_number");

        var paid = await PayAsync(row);
        Assert.NotNull(paid.QueueNumber);
        Assert.True(paid.Paid);
        Assert.Equal(0, paid.Balance);
        Assert.NotNull(paid.ReceiptNumber);
        Assert.Equal(nameof(VisitStatus.Waiting), paid.Status);

        // Reception and the exam queue read the same number, payment state and stage.
        var inQueue = (await triage.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=triage", JsonOptions))!.Single(v => v.Id == row.VisitId);
        var atDesk = await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{row.VisitId}", JsonOptions);
        Assert.Equal(paid.QueueNumber, inQueue.QueueNumber);
        Assert.Equal("Paid", inQueue.FeeStatus);
        Assert.Equal(inQueue with { NextStatuses = atDesk!.NextStatuses }, atDesk);
        Assert.DoesNotContain((await desk.GetFromJsonAsync<List<AdmissionRow>>("/api/admissions/pending", JsonOptions))!, a => a.VisitId == row.VisitId);

        var token = await InDbAsync(db => db.VisitTokens.AsNoTracking().SingleAsync(t => t.VisitId == row.VisitId));
        Assert.Equal(paid.QueueNumber, token.QueueNumber);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Double_clicks_and_a_lost_response_charge_once_and_give_one_number()
    {
        var cashier = await api.FeeCashierAsync();
        var row = await AdmitAsync();

        // Different keys, as from two screens: one payment stands and the others are told the invoice changed.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => cashier.SendAsync(PayRequest(row))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await InDbAsync(db => db.Payments.CountAsync(p => p.InvoiceId == row.InvoiceId)));
        Assert.NotNull(await InDbAsync(db => db.Visits.Where(v => v.Id == row.VisitId).Select(v => v.QueueNumber).SingleAsync()));

        // The answer never arrived, so the cashier retries with the same key.
        var second = await AdmitAsync();
        var key = Guid.NewGuid().ToString("N");
        var first = await Read<PaymentRow>(await cashier.SendAsync(PayRequest(second, key)), HttpStatusCode.Created);
        var again = await Read<PaymentRow>(await cashier.SendAsync(PayRequest(second, key)), HttpStatusCode.Created);
        Assert.Equal(first, again);
        Assert.Equal(1, await InDbAsync(db => db.Payments.CountAsync(p => p.InvoiceId == second.InvoiceId)));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Simultaneous_payments_get_unique_consecutive_numbers()
    {
        var cashier = await api.FeeCashierAsync();
        var rows = new List<AdmissionRow>();
        for (var i = 0; i < 8; i++)
        {
            rows.Add(await AdmitAsync());
        }

        var responses = await Task.WhenAll(rows.Select(r => cashier.SendAsync(PayRequest(r))));
        var numbers = (await Task.WhenAll(responses.Select(r => Read<PaymentRow>(r, HttpStatusCode.Created)))).Select(r => r.QueueNumber!.Value).Order().ToList();
        Assert.Equal(Enumerable.Range(numbers[0], 8), numbers);
    }

    [Fact]
    public async Task One_patient_gets_one_visit_and_one_fee_however_many_desks_try()
    {
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync();
        var department = await api.DepartmentIdAsync("triage");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            desk.SendAsync(ApiFixture.Keyed("/api/admissions", new CreateAdmissionRequest(patient.Id, department, null, null)))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await InDbAsync(db => db.Visits.CountAsync(v => v.PatientId == patient.Id)));
        Assert.Equal(1, await InDbAsync(db => db.Invoices.CountAsync(i => i.PatientId == patient.Id)));
    }

    [Fact]
    public async Task A_discount_needs_its_permission_never_waives_the_fee_and_refuses_an_old_amount()
    {
        var desk = await api.FeeDeskAsync();
        var accountant = await UserAsync(Roles.Accountant);
        var row = await AdmitAsync();
        var url = $"/api/invoices/{row.InvoiceId}/discount";

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync(url, new DiscountRequest(20, "دلیل معتبر", row.InvoiceVersion))).StatusCode);
        await AssertRefused(await accountant.PostAsJsonAsync(url, new DiscountRequest(100, "معافیت کامل", row.InvoiceVersion)), "visit_fee_no_exemption");
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync(url, new DiscountRequest(30, "مریض بی‌بضاعت", row.InvoiceVersion))).StatusCode);

        await AssertRefused(await (await api.FeeCashierAsync()).SendAsync(PayRequest(row)), "invoice_changed");
        Assert.Null((await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!.QueueNumber);

        var fresh = (await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!;
        Assert.Equal(70, fresh.Balance);
        var paid = await PayAsync(fresh);
        Assert.NotNull(paid.QueueNumber);
        Assert.Equal(30, paid.Discount);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_fee_settled_at_the_cashier_or_by_a_discount_also_issues_the_number()
    {
        var cashier = await UserAsync(Roles.Cashier);
        var accountant = await UserAsync(Roles.Accountant);
        var opened = await InDbAsync(async db =>
        {
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            return register.Id;
        });
        Assert.True((await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(opened, 0))).IsSuccessStatusCode);

        var row = await AdmitAsync();
        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(row.InvoiceId, 40, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        var desk = await api.FeeDeskAsync();
        var partial = (await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!;
        Assert.Null(partial.QueueNumber);
        Assert.Equal(nameof(VisitStatus.AwaitingPayment), partial.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{row.InvoiceId}/discount", new DiscountRequest(20, "تخفیف مجاز", partial.InvoiceVersion))).StatusCode);
        var stillOwing = (await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!;
        Assert.Null(stillOwing.QueueNumber);

        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(row.InvoiceId, stillOwing.Balance, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        var settled = (await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{row.VisitId}", JsonOptions))!;
        Assert.NotNull(settled.QueueNumber);
        Assert.Equal(nameof(VisitStatus.Waiting), settled.Status);

        var viaDiscount = await AdmitAsync();
        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(viaDiscount.InvoiceId, 60, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        var version = (await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{viaDiscount.VisitId}", JsonOptions))!.InvoiceVersion;
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{viaDiscount.InvoiceId}/discount", new DiscountRequest(40, "تخفیف مجاز", version))).StatusCode);
        Assert.NotNull((await desk.GetFromJsonAsync<AdmissionRow>($"/api/admissions/{viaDiscount.VisitId}", JsonOptions))!.QueueNumber);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_payment_left_without_a_number_is_recovered_without_taking_money_again()
    {
        var desk = await api.FeeDeskAsync();
        var row = await PayAsync(await AdmitAsync());

        // The state a failure between payment and number would leave.
        await InDbAsync(async db =>
        {
            await db.VisitTokens.Where(t => t.VisitId == row.VisitId).ExecuteUpdateAsync(s => s.SetProperty(t => t.QueueNumber, (int?)null));
            return await db.Visits.Where(v => v.Id == row.VisitId).ExecuteUpdateAsync(s => s
                .SetProperty(v => v.QueueNumber, (int?)null)
                .SetProperty(v => v.Status, VisitStatus.AwaitingPayment));
        });

        var listed = (await desk.GetFromJsonAsync<List<AdmissionRow>>("/api/admissions/pending", JsonOptions))!.Single(a => a.VisitId == row.VisitId);
        Assert.True(listed.Paid);
        Assert.Null(listed.QueueNumber);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => desk.PostAsync($"/api/admissions/{row.VisitId}/issue-number", null)));
        var recovered = await Task.WhenAll(responses.Select(r => Read<AdmissionRow>(r)));
        Assert.Single(recovered.Select(r => r.QueueNumber).Distinct());
        Assert.NotNull(recovered[0].QueueNumber);
        Assert.Equal(nameof(VisitStatus.Waiting), recovered[0].Status);
        Assert.Equal(1, await InDbAsync(db => db.Payments.CountAsync(p => p.InvoiceId == row.InvoiceId)));

        // Recovery never stands in for a payment.
        var unpaid = await AdmitAsync();
        await AssertRefused(await desk.PostAsync($"/api/admissions/{unpaid.VisitId}/issue-number", null), "payment_required");
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Cancelling_an_unpaid_visit_closes_its_fee_and_frees_the_patient()
    {
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync();
        var row = await AdmitAsync(patient.Id);
        var visit = await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{row.VisitId}", JsonOptions);
        Assert.Equal([nameof(VisitStatus.Cancelled)], visit!.NextStatuses);

        await Read<VisitRow>(await desk.PostAsJsonAsync($"/api/visits/{row.VisitId}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, visit.Version)));
        Assert.Equal(InvoiceStatus.Void, await InDbAsync(db => db.Invoices.Where(i => i.Id == row.InvoiceId).Select(i => i.Status).SingleAsync()));
        await AssertRefused(await (await api.FeeCashierAsync()).SendAsync(PayRequest(row)), "invoice_void");

        var next = await AdmitAsync(patient.Id);
        Assert.NotEqual(row.VisitId, next.VisitId);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_full_refund_before_any_clinical_work_takes_the_patient_out_of_the_queue()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var triage = await UserAsync(Roles.Triage);
        var patient = await NewPatientAsync();
        var row = await PayAsync(await AdmitAsync(patient.Id));
        var paymentId = await InDbAsync(db => db.Payments.Where(p => p.InvoiceId == row.InvoiceId).Select(p => p.Id).SingleAsync());

        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/refunds", new RefundRequest(paymentId, row.Total, "مریض انصراف داد"))), HttpStatusCode.Created);

        var queue = await triage.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=triage", JsonOptions);
        Assert.DoesNotContain(queue!, v => v.Id == row.VisitId);
        Assert.Equal(VisitStatus.Cancelled, await InDbAsync(db => db.Visits.Where(v => v.Id == row.VisitId).Select(v => v.Status).SingleAsync()));

        var next = await AdmitAsync(patient.Id);
        Assert.NotEqual(row.VisitId, next.VisitId);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Yesterdays_unpaid_visit_cannot_be_paid_into_todays_queue()
    {
        var cashier = await api.FeeCashierAsync();
        var row = await AdmitAsync();
        await InDbAsync(db => db.Visits.Where(v => v.Id == row.VisitId).ExecuteUpdateAsync(s => s.SetProperty(v => v.VisitDate, KabulClock.Today.AddDays(-1))));

        await AssertRefused(await cashier.SendAsync(PayRequest(row)), "visit_expired");
        Assert.Equal(0, await InDbAsync(db => db.Payments.CountAsync(p => p.InvoiceId == row.InvoiceId)));
    }

    [Fact]
    public async Task A_service_is_paid_before_it_starts_and_the_patient_returns_in_the_same_visit_with_no_new_fee_or_number()
    {
        var desk = await api.FeeDeskAsync();
        var lab = await UserAsync(Roles.Lab);
        var doctor = await UserAsync(Roles.Doctor);
        var row = await PayAsync(await AdmitAsync());
        await api.CompleteVisionAsync(row.VisitId);
        await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{row.VisitId}", new SaveConsultationRequest("یافته", null, null, null, null, null)));

        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var imaging = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Imaging").GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(row.PatientId, row.VisitId, imaging, null))), HttpStatusCode.Created);
        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/start", new ChangeOrderStatusRequest(order.Version)), "payment_required");

        await api.PayFeeAsync(await api.OrderFeeAsync(row.PatientFileNumber, order.TestName));

        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/start", new ChangeOrderStatusRequest(order.Version))).StatusCode);
        var started = await lab.GetFromJsonAsync<PagedResult<DiagnosticOrderDto>>($"/api/diagnostic-orders?visitId={row.VisitId}", JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", new SaveResultRequest("نرمال", started!.Items.Single().Version))).StatusCode);

        var back = await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{row.VisitId}", JsonOptions);
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), back!.Status);
        Assert.Equal(row.QueueNumber, back.QueueNumber);
        Assert.Equal(1, await InDbAsync(db => db.VisitTokens.CountAsync(t => t.VisitId == row.VisitId)));
        Assert.Equal(2, await InDbAsync(db => db.Invoices.CountAsync(i => i.VisitId == row.VisitId)));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task An_appointment_is_no_queue_number_and_its_arrival_joins_the_same_payment_step()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var created = await Read<JsonElement>(await admin.PostAsJsonAsync("/api/staff", new AlShifa.Api.Features.Users.SaveStaffRequest("داکتر " + Name("x"), null, true, true, null, null)), HttpStatusCode.Created);
        var doctorId = created.GetProperty("id").GetInt64();
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync();
        var at = DateTime.UtcNow.AddDays(3).Date.AddHours(5);
        await Read<JsonElement>(await desk.SendAsync(ApiFixture.Keyed("/api/appointments", new CreateAppointmentRequest(patient.Id, doctorId, at, null))), HttpStatusCode.Created);
        Assert.False(await InDbAsync(db => db.Visits.AnyAsync(v => v.PatientId == patient.Id)));

        var day = DateOnly.FromDateTime(at.AddHours(4.5));
        var appointment = (await desk.GetFromJsonAsync<List<AppointmentRow>>($"/api/appointments?date={day:yyyy-MM-dd}", JsonOptions))!.Single(a => a.PatientId == patient.Id);
        var arrival = await Read<AdmissionRow>(await desk.SendAsync(ApiFixture.Keyed($"/api/appointments/{appointment.Id}/check-in", new CheckInRequest(await api.DepartmentIdAsync("triage"), appointment.Version))), HttpStatusCode.Created);
        Assert.Null(arrival.QueueNumber);
        Assert.False(arrival.Paid);

        var paid = await PayAsync(arrival);
        Assert.NotNull(paid.QueueNumber);
        Assert.Equal(arrival.VisitId, await InDbAsync(db => db.Appointments.Where(a => a.Id == appointment.Id).Select(a => a.VisitId!.Value).SingleAsync()));
    }
}
