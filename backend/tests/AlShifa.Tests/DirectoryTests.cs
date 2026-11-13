using System.Net;
using System.Net.Http.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class DirectoryTests(ApiFixture api)
{
    private async Task<HttpClient> User(string role)
    {
        var name = "directory_" + Guid.NewGuid().ToString("N")[..12];
        await api.CreateUserAsync(name, role);
        return await api.LoginAsync(name);
    }

    [Fact]
    public async Task Departments_are_selectable_when_active_and_protected_from_stale_updates()
    {
        var admin = await User(Roles.SuperAdmin);
        var desk = await User(Roles.Receptionist);
        var response = await admin.PostAsJsonAsync("/api/departments", new SaveDepartmentRequest("کلینیک چشم"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = (await response.Content.ReadFromJsonAsync<Department>())!;
        Assert.Matches("^[0-9]{1,4}$", row.Code); // the system gives a short code; nobody types it
        Assert.Contains((await desk.GetFromJsonAsync<LookupItem[]>("/api/lookups/departments"))!, d => d.Id == row.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PostAsJsonAsync("/api/departments", new SaveDepartmentRequest("ممنوع"))).StatusCode);
        var edit = new SaveDepartmentRequest("چشم", false, row.Version);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/departments/{row.Id}", edit)).StatusCode);
        Assert.DoesNotContain((await desk.GetFromJsonAsync<LookupItem[]>("/api/lookups/departments"))!, d => d.Id == row.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/departments/{row.Id}", edit)).StatusCode);
        var second = (await (await admin.PostAsJsonAsync("/api/departments", new SaveDepartmentRequest("کلینیک دوم"))).Content.ReadFromJsonAsync<Department>())!;
        Assert.NotEqual(row.Code, second.Code);
    }

    [Fact]
    public async Task Purchase_resolves_supplier_and_keeps_historical_name_after_rename()
    {
        var inventory = await User(Roles.Inventory);
        var name = "شرکت " + Guid.NewGuid().ToString("N");
        var response = await inventory.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest(name, "0700000000", "کابل"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var supplier = (await response.Content.ReadFromJsonAsync<Supplier>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await inventory.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest(name, null, null))).StatusCode);
        int productId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var product = new Product { Code = Guid.NewGuid().ToString("N"), Name = "جنس آزمایشی", Unit = "عدد" };
            db.Products.Add(product); await db.SaveChangesAsync(); productId = product.Id;
        }
        var request = new CreatePurchaseRequest("نام دستکاری‌شده", "central", [new PurchaseLineInput(productId, null, null, 2, 5)], supplier.Id);
        var key = Guid.NewGuid().ToString("N");
        var purchase = await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", request, key));
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        var body = await purchase.Content.ReadAsStringAsync();
        Assert.Equal(body, await (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", request, key))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PutAsJsonAsync($"/api/suppliers/{supplier.Id}", new SaveSupplierRequest(name + " جدید", null, null, false, supplier.Version))).StatusCode);
        Assert.DoesNotContain((await inventory.GetFromJsonAsync<Supplier[]>("/api/suppliers?activeOnly=true"))!, s => s.Id == supplier.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", request))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", request with { SupplierId = int.MaxValue }))).StatusCode);
        using var check = api.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>().Purchases.SingleAsync(p => p.SupplierId == supplier.Id);
        Assert.Equal(name, saved.SupplierName); Assert.Equal(10, saved.Total);
    }

    [Fact]
    public async Task Doctor_directory_filters_staff_and_validates_profile_links()
    {
        var admin = await User(Roles.SuperAdmin);
        var name = "داکتر " + Guid.NewGuid().ToString("N");
        var request = new SaveStaffRequest(name, null, true, true, null, null, Specialty: "چشم", Biography: "متخصص چشم", Website: "https://example.com", Avatar: 18);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/staff", request with { Website = "javascript:alert(1)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/staff", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/staff", request with { FullName = name + " کارمند", IsDoctor = false })).StatusCode);
        var rows = (await admin.GetFromJsonAsync<PagedResult<StaffRow>>("/api/staff?isDoctor=true&q=" + Uri.EscapeDataString(name)))!;
        var doctor = Assert.Single(rows.Items);
        Assert.True(doctor.IsDoctor); Assert.Equal("چشم", doctor.Specialty); Assert.Equal(18, doctor.Avatar);
    }
}
