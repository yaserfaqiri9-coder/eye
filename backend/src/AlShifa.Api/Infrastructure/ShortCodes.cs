using System.Globalization;

namespace AlShifa.Api.Infrastructure;

/// <summary>
/// Codes for records people create (products, services, departments) are given by the system, never typed:
/// the next number of the record type, at most four digits. A number someone already used by hand is skipped.
/// </summary>
public static class ShortCodes
{
    public const int MaxLength = 4;

    private const long Max = 9999;

    public static async Task<string> NextAsync(NumberService numbers, string scope, Func<string, Task<bool>> taken, CancellationToken ct)
    {
        while (true)
        {
            var next = await numbers.NextAsync($"code:{scope}", "", ct);
            if (next > Max)
            {
                throw new AppException(StatusCodes.Status409Conflict, "code_limit", "کدهای چهاررقمی این بخش تمام شده است. با پشتیبانی تماس بگیرید.");
            }

            var code = next.ToString(CultureInfo.InvariantCulture);
            if (!await taken(code))
            {
                return code;
            }
        }
    }
}
