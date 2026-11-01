namespace AlShifa.Api.Features.Auth;

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Manager = "Manager";
    public const string Receptionist = "Receptionist";
    public const string Triage = "Triage";
    public const string Doctor = "Doctor";
    public const string Lab = "Lab";
    public const string Surgery = "Surgery";
    public const string Pharmacist = "Pharmacist";
    public const string Optical = "Optical";
    public const string Cashier = "Cashier";
    public const string Accountant = "Accountant";
    public const string Inventory = "Inventory";
}

/// <summary>Default role permissions; mirrors the matrix in PERMISSIONS.md.</summary>
public static class RolePermissions
{
    public const string ClaimType = "permission";

    private static readonly Dictionary<char, string> Actions = new()
    {
        ['V'] = "view",
        ['C'] = "create",
        ['E'] = "edit",
        ['A'] = "approve",
        ['X'] = "cancel",
        ['P'] = "print",
        ['O'] = "export",
        ['R'] = "refund",
        ['M'] = "manage",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Matrix = new()
    {
        [Roles.SuperAdmin] = new()
        {
            ["patients"] = "V", ["visits"] = "V", ["users"] = "VCEM", ["settings"] = "VEM", ["audit"] = "V",
        },
        [Roles.Manager] = new()
        {
            ["patients"] = "V", ["visits"] = "V", ["appointments"] = "V", ["diagnostics"] = "V", ["surgery"] = "V",
            ["pharmacy"] = "V", ["optical"] = "V", ["inventory"] = "V", ["invoices"] = "VO", ["payments"] = "VO",
            ["cash"] = "V", ["ledger"] = "V", ["expenses"] = "V", ["reports"] = "VPO", ["users"] = "V",
            ["settings"] = "V", ["audit"] = "V",
        },
        [Roles.Receptionist] = new()
        {
            ["patients"] = "VCEP", ["visits"] = "VCEXP", ["appointments"] = "VCEXP", ["reports"] = "V",
        },
        [Roles.Triage] = new()
        {
            ["patients"] = "V", ["visits"] = "VE", ["preexam"] = "VCE", ["clinicalrecord"] = "V",
        },
        [Roles.Doctor] = new()
        {
            ["patients"] = "V", ["visits"] = "VE", ["appointments"] = "V", ["preexam"] = "V",
            ["consultation"] = "VCEAP", ["clinicalrecord"] = "V", ["prescriptions"] = "VCEXP", ["opticalrx"] = "VCEXP",
            ["diagnostics"] = "VCXP", ["surgery"] = "VCEAXP", ["reports"] = "V",
        },
        [Roles.Lab] = new()
        {
            ["patients"] = "V", ["visits"] = "V", ["diagnostics"] = "VCEAP", ["reports"] = "V",
        },
        [Roles.Surgery] = new()
        {
            ["patients"] = "V", ["visits"] = "V", ["appointments"] = "V", ["preexam"] = "V", ["consultation"] = "V",
            ["clinicalrecord"] = "V", ["diagnostics"] = "V", ["surgery"] = "VEAP", ["reports"] = "V",
        },
        [Roles.Pharmacist] = new()
        {
            ["patients"] = "V", ["prescriptions"] = "VP", ["pharmacy"] = "VCEXP", ["inventory"] = "VC",
            ["invoices"] = "VC", ["reports"] = "V",
        },
        [Roles.Optical] = new()
        {
            ["patients"] = "V", ["visits"] = "V", ["opticalrx"] = "VP", ["optical"] = "VCEXP", ["inventory"] = "VC",
            ["invoices"] = "VC", ["reports"] = "V",
        },
        [Roles.Cashier] = new()
        {
            ["patients"] = "V", ["visits"] = "V", ["pharmacy"] = "V", ["optical"] = "V", ["invoices"] = "VCXP",
            ["payments"] = "VCP", ["cash"] = "VCE", ["reports"] = "V",
        },
        [Roles.Accountant] = new()
        {
            ["pharmacy"] = "V", ["optical"] = "V", ["inventory"] = "V", ["invoices"] = "VAO", ["payments"] = "VAR",
            ["cash"] = "V", ["ledger"] = "VCAO", ["expenses"] = "VCEAX", ["reports"] = "VPOM", ["settings"] = "V",
            ["audit"] = "V",
        },
        [Roles.Inventory] = new()
        {
            ["pharmacy"] = "V", ["inventory"] = "VCEAXPO", ["reports"] = "V",
        },
    };

    public static IReadOnlyCollection<string> RoleNames => Matrix.Keys;

    public static IEnumerable<string> For(string role) =>
        Matrix[role].SelectMany(m => m.Value.Select(a => $"{m.Key}.{Actions[a]}"));
}
