using System.Net;
using System.Net.Http.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class AuthTests(ApiFixture api)
{
    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Anonymous_request_is_rejected()
    {
        var response = await api.NewClient().GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_with_clear_code()
    {
        var name = Name("wrong");
        await api.CreateUserAsync(name, Roles.Receptionist);

        var response = await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, "not-the-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_login", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_without_csrf_token_is_rejected()
    {
        var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("x", "y"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Receptionist_cannot_reach_user_management_directly()
    {
        var name = Name("recep");
        await api.CreateUserAsync(name, Roles.Receptionist);
        var client = await api.LoginAsync(name);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Super_admin_can_manage_users_but_holds_no_clinical_permissions()
    {
        var name = Name("admin");
        await api.CreateUserAsync(name, Roles.SuperAdmin);
        var client = await api.LoginAsync(name);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);

        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/auth/me");
        Assert.DoesNotContain(me!.Permissions, p =>
            p.StartsWith("consultation.") || p.StartsWith("clinicalrecord.") || p.StartsWith("preexam.") || p.StartsWith("prescriptions."));
    }

    [Fact]
    public async Task Deactivated_user_loses_access_immediately_and_cannot_log_in_again()
    {
        var adminName = Name("admin");
        await api.CreateUserAsync(adminName, Roles.SuperAdmin);
        var admin = await api.LoginAsync(adminName);

        var name = Name("cash");
        var id = await api.CreateUserAsync(name, Roles.Cashier);
        var victim = await api.LoginAsync(name);
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/auth/me")).StatusCode);

        var deactivate = await admin.PostAsync($"/api/users/{id}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/auth/me")).StatusCode);

        var relogin = await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);
    }

    [Fact]
    public async Task Role_change_applies_to_an_existing_session()
    {
        var adminName = Name("admin");
        await api.CreateUserAsync(adminName, Roles.SuperAdmin);
        var admin = await api.LoginAsync(adminName);

        var name = Name("mgr");
        var id = await api.CreateUserAsync(name, Roles.Manager);
        var user = await api.LoginAsync(name);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/users")).StatusCode);

        var change = await admin.PutAsJsonAsync($"/api/users/{id}/roles", new SetRolesRequest([Roles.Cashier]));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // The stamp changed, so the old cookie is no longer valid at all.
        Assert.NotEqual(HttpStatusCode.OK, (await user.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Admin_reset_ends_the_old_session_and_only_the_new_password_works()
    {
        var adminName = Name("admin");
        await api.CreateUserAsync(adminName, Roles.SuperAdmin);
        var admin = await api.LoginAsync(adminName);

        var name = Name("staff");
        var id = await api.CreateUserAsync(name, Roles.Cashier);
        var staff = await api.LoginAsync(name);

        const string fresh = "Brand-New-Password-1";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/users/{id}/reset-password", new ResetPasswordRequest(fresh))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword))).StatusCode);
        await api.LoginAsync(name, fresh);

        var cashier = await api.LoginAsync(name, fresh);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/users/{id}/reset-password", new ResetPasswordRequest(fresh))).StatusCode);
    }

    [Fact]
    public async Task User_changes_own_password_keeps_this_session_and_ends_others()
    {
        var name = Name("self");
        await api.CreateUserAsync(name, Roles.Cashier);
        var here = await api.LoginAsync(name);
        var elsewhere = await api.LoginAsync(name);

        var wrong = await here.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("not-the-password", "Another-Password-1"));
        Assert.Contains("wrong_password", await wrong.Content.ReadAsStringAsync());

        var changed = await here.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(ApiFixture.TestPassword, "Another-Password-1"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Audit_log_is_readable_only_with_permission_and_contains_no_password()
    {
        var name = Name("aud");
        await api.CreateUserAsync(name, Roles.Manager);
        var manager = await api.LoginAsync(name);
        await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, "secret-attempt-value"));

        var body = await manager.GetStringAsync("/api/audit?action=LoginFailed");
        Assert.Contains("LoginFailed", body);
        Assert.DoesNotContain("secret-attempt-value", body);

        var receptionName = Name("rec");
        await api.CreateUserAsync(receptionName, Roles.Receptionist);
        var reception = await api.LoginAsync(receptionName);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Successful_login_and_failed_login_are_audited()
    {
        var name = Name("audit");
        var id = await api.CreateUserAsync(name, Roles.Receptionist);
        await api.LoginAsync(name);
        await api.NewClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, "not-the-password"));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AuditLogs.Where(a => a.EntityId == id.ToString()).Select(a => a.Action).ToListAsync();

        Assert.Contains("Login", actions);
        Assert.Contains("LoginFailed", actions);
    }

    [Fact]
    public async Task Numbering_never_issues_a_duplicate_under_concurrency()
    {
        var scope = $"test-{Guid.NewGuid():N}"[..20];
        var numbers = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(async () =>
        {
            using var s = api.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var value = await s.ServiceProvider.GetRequiredService<NumberService>().NextAsync(scope);
            await tx.CommitAsync();
            return value;
        })));

        Assert.Equal(30, numbers.Distinct().Count());
        Assert.Equal(30, numbers.Max());
    }

    [Fact]
    public async Task Stale_version_is_rejected_as_a_conflict()
    {
        using var first = api.Services.CreateScope();
        using var second = api.Services.CreateScope();
        var dbA = first.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = second.ServiceProvider.GetRequiredService<AppDbContext>();

        var key = $"test.{Guid.NewGuid():N}"[..20];
        dbA.Settings.Add(new Setting { Key = key, Value = "a" });
        await dbA.SaveChangesAsync();

        var copyB = await dbB.Settings.SingleAsync(s => s.Key == key);
        var copyA = await dbA.Settings.SingleAsync(s => s.Key == key);

        copyA.Value = "changed by A";
        await dbA.SaveChangesAsync();

        copyB.Value = "changed by B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Every_role_permission_in_the_matrix_is_seeded()
    {
        using var scope = api.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

        foreach (var name in RolePermissions.RoleNames)
        {
            var role = await roles.FindByNameAsync(name);
            Assert.NotNull(role);
            var claims = (await roles.GetClaimsAsync(role!)).Select(c => c.Value).ToHashSet();
            Assert.Superset(RolePermissions.For(name).ToHashSet(), claims);
            Assert.Subset(RolePermissions.For(name).ToHashSet(), claims);
        }
    }
}
