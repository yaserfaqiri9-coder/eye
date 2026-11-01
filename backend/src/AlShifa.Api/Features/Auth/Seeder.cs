using System.Security.Claims;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Auth;

public static class Seeder
{
    private static readonly (string Code, string Name)[] Departments =
    [
        ("reception", "پذیرش"),
        ("triage", "معاینه ابتدایی"),
        ("doctor", "داکتر"),
        ("lab", "آزمایشگاه"),
        ("surgery", "عملیات"),
        ("pharmacy", "دواخانه"),
        ("optical", "عینک"),
        ("cashier", "صندوق"),
        ("finance", "حسابداری"),
        ("inventory", "گدام"),
    ];

    private static readonly (string Code, string Name)[] StockLocations =
    [
        ("central", "گدام مرکزی"),
        ("pharmacy", "دواخانه"),
        ("optical", "بخش عینک"),
    ];

    /// <summary>Roles, permissions and departments are reference data and are safe to apply in every environment.</summary>
    public static async Task SeedReferenceDataAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var roles = services.GetRequiredService<RoleManager<AppRole>>();

        foreach (var name in RolePermissions.RoleNames)
        {
            var role = await roles.FindByNameAsync(name);
            if (role is null)
            {
                role = new AppRole(name);
                await roles.CreateAsync(role);
            }

            var existing = (await roles.GetClaimsAsync(role))
                .Where(c => c.Type == RolePermissions.ClaimType)
                .Select(c => c.Value)
                .ToHashSet();

            foreach (var permission in RolePermissions.For(name).Where(p => !existing.Contains(p)))
            {
                await roles.AddClaimAsync(role, new Claim(RolePermissions.ClaimType, permission));
            }
        }

        var codes = await db.Departments.Select(d => d.Code).ToListAsync();
        db.Departments.AddRange(Departments
            .Where(d => !codes.Contains(d.Code))
            .Select(d => new Department { Code = d.Code, Name = d.Name }));

        var accounts = await db.LedgerAccounts.Select(a => a.Code).ToListAsync();
        db.LedgerAccounts.AddRange(Accounts.All
            .Where(a => !accounts.Contains(a.Code))
            .Select(a => new LedgerAccount { Code = a.Code, Name = a.Name }));

        var locations = await db.StockLocations.Select(l => l.Code).ToListAsync();
        db.StockLocations.AddRange(StockLocations
            .Where(l => !locations.Contains(l.Code))
            .Select(l => new StockLocation { Code = l.Code, Name = l.Name }));

        if (!await db.CashRegisters.AnyAsync())
        {
            db.CashRegisters.Add(new CashRegister { Name = "صندوق اصلی" });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Creates or resets a Super Admin. Production passwords come from the operator, never from source.</summary>
    public static async Task CreateAdminAsync(IServiceProvider services, string userName, string password)
    {
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByNameAsync(userName);
        if (user is not null)
        {
            throw new InvalidOperationException($"User '{userName}' already exists.");
        }

        user = new AppUser { UserName = userName, FullName = userName };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(user, Roles.SuperAdmin);
    }
}
