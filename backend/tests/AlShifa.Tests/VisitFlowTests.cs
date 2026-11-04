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

/// <summary>The confirmed Al-Shifa flow: paid token, free Vision, paid refraction and imaging, all inside one visit.</summary>
[Collection("api")]
public sealed class VisitFlowTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

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
            new SavePatientRequest(Name("احمد"), null, null, Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
    }

    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync()
    {
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync();
        var token = await api.PaidTokenAsync();
        var visit = await Read<VisitRow>(await desk.SendAsync(ApiFixture.Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync("triage"), null, null, null, token))), HttpStatusCode.Created);
        return (patient, visit);
    }

    private async Task<VisitRow> ReadVisitAsync(HttpClient client, long id) =>
        (await client.GetFromJsonAsync<VisitRow>($"/api/visits/{id}", JsonOptions))!;

    private async Task<int> TestIdAsync(HttpClient doctor, string kind)
    {
        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        return tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == kind).GetProperty("id").GetInt32();
    }

    private async Task<DiagnosticOrderDto> RequestAsync(HttpClient doctor, PatientDto patient, VisitRow visit, string kind, bool returnToDoctor = true) =>
        await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders",
            new CreateDiagnosticOrderRequest(patient.Id, visit.Id, await TestIdAsync(doctor, kind), null, returnToDoctor))), HttpStatusCode.Created);

    private Task<OrderFee> FeeOfAsync(PatientDto patient, string description) => api.OrderFeeAsync(patient.FileNumber, description);

    private Task<JsonElement> PayAsync(OrderFee fee) => api.PayFeeAsync(fee);

    /// <summary>Visit in consultation, with findings and a diagnosis on file.</summary>
    private async Task<(HttpClient Doctor, PatientDto Patient, VisitRow Visit, ConsultationDto Consult)> InConsultationAsync()
    {
        var (patient, visit) = await NewVisitAsync();
        await api.CompleteVisionAsync(visit.Id);
        var doctor = await UserAsync(Roles.Doctor);
        var consult = await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}",
            new SaveConsultationRequest("یافته راست", "یافته چپ", "پلان", null, [new DiagnosisInput(EyeSide.OD, "تشخیص اول")], null)));
        return (doctor, patient, visit, consult);
    }

    [Fact]
    public async Task Token_is_paid_before_details_and_becomes_one_visit_without_a_placeholder_patient()
    {
        var desk = await api.FeeDeskAsync();
        var patientsBefore = await InDbAsync(db => db.Patients.CountAsync());
        var token = await api.NewTokenAsync();
        Assert.False(token.Paid);
        Assert.Equal(100, token.Balance);
        Assert.Equal(patientsBefore, await InDbAsync(db => db.Patients.CountAsync()));
        Assert.Null(await InDbAsync(db => db.Invoices.Where(i => i.Id == token.InvoiceId).Select(i => i.PatientId).SingleAsync()));

        var patient = await NewPatientAsync();
        var department = await api.DepartmentIdAsync("triage");
        CreateVisitRequest Visit(long? tokenId, long? patientId = null) => new(patientId ?? patient.Id, department, null, null, null, tokenId);

        await AssertRefused(await desk.SendAsync(ApiFixture.Keyed("/api/visits", Visit(null))), "visit_fee_required");
        await AssertRefused(await desk.SendAsync(ApiFixture.Keyed("/api/visits", Visit(token.Id))), "visit_fee_required");

        var feeCashier = await api.FeeCashierAsync();
        await Read<JsonElement>(await feeCashier.SendAsync(ApiFixture.Keyed("/api/payments",
            new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null, token.InvoiceVersion))), HttpStatusCode.Created);

        // The number comes from the payment, never before it.
        Assert.Null(token.QueueNumber);
        var numbered = await desk.GetFromJsonAsync<VisitTokenRow>($"/api/visit-tokens/{token.Id}", JsonOptions);
        Assert.NotNull(numbered!.QueueNumber);

        var visit = await Read<VisitRow>(await desk.SendAsync(ApiFixture.Keyed("/api/visits", Visit(token.Id))), HttpStatusCode.Created);
        Assert.Equal(numbered.QueueNumber, visit.QueueNumber);

        var invoice = await InDbAsync(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == token.InvoiceId));
        Assert.Equal(patient.Id, invoice.PatientId);
        Assert.Equal(visit.Id, invoice.VisitId);

        // The same token cannot pay for anyone else, and the same patient owes no second visit while this one is open.
        var other = await NewPatientAsync();
        await AssertRefused(await desk.SendAsync(ApiFixture.Keyed("/api/visits", Visit(token.Id, other.Id))), "token_used");
        await AssertRefused(await desk.SendAsync(ApiFixture.Keyed("/api/visits", Visit(await api.PaidTokenAsync()))), "duplicate_visit");

        var waiting = await desk.GetFromJsonAsync<List<VisitTokenRow>>("/api/visit-tokens", JsonOptions);
        Assert.DoesNotContain(waiting!, t => t.Id == token.Id);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Reception_takes_no_money_and_every_fee_is_collected_at_the_cashier()
    {
        var desk = await api.FeeDeskAsync();
        var token = await api.NewTokenAsync();

        // Reception can neither pay an invoice, nor list invoices, nor open a register.
        await AssertRefused(await desk.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null))), "", HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync("/api/invoices")).StatusCode);
        var register = await InDbAsync(db => db.CashRegisters.Where(r => r.IsActive).Select(r => r.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(register, 0))).StatusCode);

        // Refraction and imaging reach the cashier's list like every other fee, and paying there opens the service.
        var (doctor, patient, visit, _) = await InConsultationAsync();
        foreach (var kind in new[] { "Imaging", "Refraction" })
        {
            var order = await RequestAsync(doctor, patient, visit, kind);
            var fee = await FeeOfAsync(patient, order.TestName);
            var cashier = await api.FeeCashierAsync();
            var waiting = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={patient.FileNumber}", JsonOptions);
            var row = waiting!.Items.Single(i => i.Id == fee.InvoiceId);
            Assert.Equal(order.TestName, row.Description);
            Assert.Equal(1, row.ItemCount);

            await PayAsync(fee);
            Assert.True((await doctor.GetFromJsonAsync<PagedResult<DiagnosticOrderDto>>($"/api/diagnostic-orders?visitId={visit.Id}", JsonOptions))!.Items.Single(o => o.Id == order.Id).Paid);
        }

        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Discount_needs_the_right_permission_and_a_reason_never_waives_the_visit_fee_and_keeps_its_history()
    {
        var desk = await api.FeeDeskAsync();
        var accountant = await UserAsync(Roles.Accountant);
        var token = await api.NewTokenAsync();
        var url = $"/api/invoices/{token.InvoiceId}/discount";

        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync(url, new DiscountRequest(20, "دلیل معتبر", token.InvoiceVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.PostAsJsonAsync(url, new DiscountRequest(20, "  ", token.InvoiceVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.PostAsJsonAsync(url, new DiscountRequest(0, "دلیل معتبر", token.InvoiceVersion))).StatusCode);
        await AssertRefused(await accountant.PostAsJsonAsync(url, new DiscountRequest(100, "معافیت کامل", token.InvoiceVersion)), "visit_fee_no_exemption");

        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync(url, new DiscountRequest(20, "مریض بی‌بضاعت", token.InvoiceVersion))).StatusCode);

        var detail = await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{token.InvoiceId}", JsonOptions);
        var history = Assert.Single(detail!.Discounts);
        Assert.Contains("مریض بی‌بضاعت", history.Details);
        Assert.NotNull(history.ByUser);
        Assert.Equal(80, detail.Invoice.Balance);

        // An amount decided before the discount is refused rather than overcharging.
        var cashier = await api.FeeCashierAsync();
        await AssertRefused(await cashier.SendAsync(ApiFixture.Keyed("/api/payments",
            new PaymentRequest(token.InvoiceId, 100, PaymentMethod.Cash, null, token.InvoiceVersion))), "invoice_changed");

        var fresh = await desk.GetFromJsonAsync<VisitTokenRow>($"/api/visit-tokens/{token.Id}", JsonOptions);
        Assert.Equal(80, fresh!.Balance);
        await Read<JsonElement>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments",
            new PaymentRequest(token.InvoiceId, fresh.Balance, PaymentMethod.Cash, null, fresh.InvoiceVersion))), HttpStatusCode.Created);

        var paid = await desk.GetFromJsonAsync<VisitTokenRow>($"/api/visit-tokens/{token.Id}", JsonOptions);
        Assert.True(paid!.Paid);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Vision_is_free_and_moves_the_patient_to_the_doctor()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var doctor = await UserAsync(Roles.Doctor);

        var before = await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions);
        Assert.DoesNotContain(before!, v => v.Id == visit.Id);

        await Read<PreExamDto>(await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest(null, null, "6/9", "6/12", "15", "16", null, null, null)));
        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/pre-examinations/{visit.Id}/complete", null)).StatusCode);

        Assert.Equal(1, await InDbAsync(db => db.Invoices.CountAsync(i => i.VisitId == visit.Id)));
        var after = await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions);
        Assert.Contains(after!, v => v.Id == visit.Id && v.Status == nameof(VisitStatus.ReadyForDoctor));
    }

    [Fact]
    public async Task Refraction_starts_only_when_fully_paid_returns_to_the_doctor_and_stays_in_the_same_visit()
    {
        var (doctor, patient, visit, consult) = await InConsultationAsync();
        var triage = await UserAsync(Roles.Triage);
        var refraction = new SaveRefractionRequest("6/18", null, null, null, null, null, null, null, null, [], null);

        // Without a request there is nothing to start.
        await AssertRefused(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", refraction), "refraction_not_requested");

        var order = await RequestAsync(doctor, patient, visit, "Refraction");
        Assert.False(order.Paid);
        Assert.Equal(nameof(VisitStatus.AwaitingDiagnostic), (await ReadVisitAsync(doctor, visit.Id)).Status);
        await AssertRefused(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", refraction), "payment_required");

        // A partial payment at the cashier does not open it: the whole amount after discount must be paid.
        var fee = await FeeOfAsync(patient, order.TestName);
        Assert.Equal(50, fee.Balance);
        var name = Name("c");
        await api.CreateUserAsync(name, Roles.Cashier);
        var cashier = await api.LoginAsync(name);
        var register = await InDbAsync(async db =>
        {
            var row = new CashRegister { Name = name };
            db.CashRegisters.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
        Assert.Equal(HttpStatusCode.Created, (await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(register, 0))).StatusCode);
        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(fee.InvoiceId, 20, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        await AssertRefused(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", refraction), "payment_required");

        var accountant = await UserAsync(Roles.Accountant);
        fee = await FeeOfAsync(patient, order.TestName);
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{fee.InvoiceId}/discount", new DiscountRequest(10, "تخفیف مجاز", fee.InvoiceVersion))).StatusCode);
        await PayAsync(await FeeOfAsync(patient, order.TestName));

        await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", refraction));
        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null)).StatusCode);

        var queue = await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions);
        Assert.Contains(queue!, v => v.Id == visit.Id && v.Status == nameof(VisitStatus.ReadyForDoctor));

        // Same visit, no second visit fee, and the earlier exam is intact.
        Assert.Equal(1, await InDbAsync(db => db.Visits.CountAsync(v => v.PatientId == patient.Id)));
        Assert.Equal(2, await InDbAsync(db => db.Invoices.CountAsync(i => i.VisitId == visit.Id)));
        var chart = await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        Assert.Equal("یافته راست", chart!.Consultation!.FindingsOd);
        Assert.Equal("تشخیص اول", Assert.Single(chart.Consultation.Diagnoses).Text);

        var continued = await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}",
            new SaveConsultationRequest("یافته راست", "یافته چپ", "پلان تازه", null, [new DiagnosisInput(EyeSide.OD, "تشخیص اول"), new DiagnosisInput(EyeSide.OS, "تشخیص دوم")], consult.Version)));
        Assert.Equal(2, continued.Diagnoses.Count);
        Assert.Equal(nameof(VisitStatus.InConsultation), (await ReadVisitAsync(doctor, visit.Id)).Status);

        Assert.Equal(HttpStatusCode.OK, (await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(continued.Version, true))).StatusCode);
        Assert.Equal(nameof(VisitStatus.Completed), (await ReadVisitAsync(doctor, visit.Id)).Status);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task A_repeated_request_charges_once_even_when_sent_together()
    {
        var (doctor, patient, visit, _) = await InConsultationAsync();
        var testId = await TestIdAsync(doctor, "Refraction");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null)))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(2, await InDbAsync(db => db.Invoices.CountAsync(i => i.VisitId == visit.Id)));
    }

    [Fact]
    public async Task A_refund_closes_the_gate_again_and_the_doctor_can_then_cancel_the_request()
    {
        var (doctor, patient, visit, consult) = await InConsultationAsync();
        var triage = await UserAsync(Roles.Triage);
        var order = await RequestAsync(doctor, patient, visit, "Refraction");
        var paid = await PayAsync(await FeeOfAsync(patient, order.TestName));

        await AssertRefused(await doctor.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/cancel", new ChangeOrderStatusRequest(order.Version)), "line_already_paid");

        var accountant = await UserAsync(Roles.Accountant);
        var refund = new RefundRequest(paid.GetProperty("id").GetInt64(), paid.GetProperty("amount").GetDecimal(), "مریض منصرف شد");
        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/refunds", refund)), HttpStatusCode.Created);

        var attempt = new SaveRefractionRequest(null, null, null, null, null, null, null, null, null, [], null);
        await AssertRefused(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", attempt), "payment_required");

        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/cancel", new ChangeOrderStatusRequest(order.Version))).StatusCode);
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), (await ReadVisitAsync(doctor, visit.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(consult.Version, true))).StatusCode);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Imaging_has_its_own_tariff_is_paid_first_and_returns_as_the_doctor_decided()
    {
        var (doctor, patient, visit, consult) = await InConsultationAsync();
        var lab = await UserAsync(Roles.Lab);

        var returning = await RequestAsync(doctor, patient, visit, "Imaging", returnToDoctor: true);
        Assert.Equal(nameof(VisitStatus.AwaitingDiagnostic), (await ReadVisitAsync(doctor, visit.Id)).Status);

        var fee = await FeeOfAsync(patient, returning.TestName);
        Assert.Equal(70, fee.Balance);

        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{returning.Id}/start", new ChangeOrderStatusRequest(returning.Version)), "payment_required");
        await AssertRefused(await lab.PostAsJsonAsync($"/api/diagnostic-orders/{returning.Id}/result", new SaveResultRequest("نتیجه", returning.Version)), "payment_required");

        await PayAsync(fee);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{returning.Id}/start", new ChangeOrderStatusRequest(returning.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{returning.Id}/result", new SaveResultRequest("نتیجه", returning.Version + 1))).StatusCode);
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), (await ReadVisitAsync(doctor, visit.Id)).Status);

        // The doctor continues, then sends another image without asking the patient back: nothing waits for it.
        var continued = await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}",
            new SaveConsultationRequest("یافته راست", "یافته چپ", "پلان", null, [new DiagnosisInput(EyeSide.OD, "تشخیص اول")], consult.Version)));
        var oneWay = await RequestAsync(doctor, patient, visit, "Imaging", returnToDoctor: false);
        Assert.False(oneWay.ReturnToDoctor);
        Assert.Equal(nameof(VisitStatus.InConsultation), (await ReadVisitAsync(doctor, visit.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(continued.Version, true))).StatusCode);

        await PayAsync(await FeeOfAsync(patient, oneWay.TestName));
        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{oneWay.Id}/result", new SaveResultRequest("نتیجه دوم", oneWay.Version))).StatusCode);
        Assert.Equal(nameof(VisitStatus.Completed), (await ReadVisitAsync(doctor, visit.Id)).Status);
    }

    [Fact]
    public async Task Early_finalizing_cannot_skip_a_pending_request_and_a_finalized_exam_can_continue_without_losing_anything()
    {
        var (doctor, patient, visit, consult) = await InConsultationAsync();
        var desk = await api.FeeDeskAsync();
        var order = await RequestAsync(doctor, patient, visit, "Refraction");

        await AssertRefused(await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(consult.Version, true)), "pending_requests");
        await AssertRefused(await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(consult.Version, false)), "pending_requests");

        var current = await ReadVisitAsync(desk, visit.Id);
        await AssertRefused(await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Completed, current.Version)), "pending_requests");

        // An unpaid request can simply be withdrawn; its invoice leaves the cashier's list and the patient is back.
        var fee = await FeeOfAsync(patient, order.TestName);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/cancel", new ChangeOrderStatusRequest(order.Version))).StatusCode);
        Assert.Equal(InvoiceStatus.Void, await InDbAsync(db => db.Invoices.Where(i => i.Id == fee.InvoiceId).Select(i => i.Status).SingleAsync()));

        var finalized = await Read<ConsultationDto>(await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(consult.Version, false)));
        Assert.NotNull(finalized.FinalizedAt);

        await AssertRefused(await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/reopen", new ReopenConsultationRequest("  ", finalized.Version)), "", HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/reopen", new ReopenConsultationRequest("بازگشت مریض", finalized.Version))).StatusCode);

        var chart = await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        Assert.Null(chart!.Consultation!.FinalizedAt);
        Assert.Equal("یافته راست", chart.Consultation.FindingsOd);
        Assert.Equal("تشخیص اول", Assert.Single(chart.Consultation.Diagnoses).Text);
        Assert.Contains(chart.Amendments, a => a.Reason == "بازگشت مریض");

        var closed = await Read<ConsultationDto>(await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(chart.Consultation.Version, true)));
        await AssertRefused(await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/reopen", new ReopenConsultationRequest("بعد از تکمیل", closed.Version)), "visit_closed");
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Unpaid_token_can_be_cancelled_and_cannot_then_buy_a_visit()
    {
        var desk = await api.FeeDeskAsync();
        var token = await api.NewTokenAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await desk.PostAsJsonAsync($"/api/visit-tokens/{token.Id}/cancel", new CancelTokenRequest(token.Version))).StatusCode);

        var patient = await NewPatientAsync();
        await AssertRefused(await desk.SendAsync(ApiFixture.Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync("triage"), null, null, null, token.Id))), "visit_fee_required");

        var paid = await api.PaidTokenAsync();
        var row = await desk.GetFromJsonAsync<VisitTokenRow>($"/api/visit-tokens/{paid}", JsonOptions);
        await AssertRefused(await desk.PostAsJsonAsync($"/api/visit-tokens/{paid}/cancel", new CancelTokenRequest(row!.Version)), "token_in_use");
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Reception_sees_only_its_own_cash_session()
    {
        var desk = await api.FeeDeskAsync();
        var sessions = await desk.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions", JsonOptions);
        var mine = await desk.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions?mine=true", JsonOptions);
        Assert.Equal(mine!.Select(s => s.Id), sessions!.Select(s => s.Id));
    }
}
