using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace AlShifa.Api.Features.Users;

public class AppUser : IdentityUser<long>
{
    [MaxLength(150)]
    public string FullName { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppRole : IdentityRole<long>
{
    public AppRole() { }

    public AppRole(string name) : base(name) { }
}

public class Department
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Code { get; set; } = "";

    [MaxLength(100)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

public class Staff
{
    public long Id { get; set; }

    [MaxLength(150)]
    public string FullName { get; set; } = "";

    [MaxLength(30)]
    public string? Phone { get; set; }

    public bool IsDoctor { get; set; }

    public bool IsActive { get; set; } = true;

    public int? DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? UserId { get; set; }

    public AppUser? User { get; set; }

    public int Version { get; set; }
}
