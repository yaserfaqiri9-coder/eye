using System.Globalization;
using AlShifa.Api.Infrastructure;

namespace AlShifa.Api.Features.Reports;

/// <summary>How a report value reads on paper and in a workbook: Shamsi dates, Kabul time and grouped numbers.</summary>
public static class ReportText
{
    private static readonly PersianCalendar Calendar = new();

    private static readonly string[] Months = ["حمل", "ثور", "جوزا", "سرطان", "اسد", "سنبله", "میزان", "عقرب", "قوس", "جدی", "دلو", "حوت"];

    /// <summary>Kabul is UTC+4:30 all year.</summary>
    public static DateTime Kabul(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).AddHours(4.5);

    public static string Date(DateOnly day)
    {
        var at = day.ToDateTime(TimeOnly.MinValue);
        return $"{Calendar.GetYear(at):0000}/{Calendar.GetMonth(at):00}/{Calendar.GetDayOfMonth(at):00}";
    }

    public static string Time(DateTime utc)
    {
        var at = Kabul(utc);
        return $"{Date(DateOnly.FromDateTime(at))}  {at:HH:mm}";
    }

    /// <summary>Such as «5 حمل 1405», for the days a report covers.</summary>
    public static string Words(DateOnly day)
    {
        var at = day.ToDateTime(TimeOnly.MinValue);
        return $"{Calendar.GetDayOfMonth(at)} {Months[Calendar.GetMonth(at) - 1]} {Calendar.GetYear(at)}";
    }

    public static string Period(DateOnly? from, DateOnly? to) =>
        from is null || to is null ? $"وضعیت همین حالا ({Words(KabulClock.Today)})"
        : from == to ? Words(from.Value)
        : $"{Words(from.Value)} تا {Words(to.Value)}";

    /// <summary>An ASCII date for a file name, such as 1405-01-05.</summary>
    public static string FileDate(DateOnly day) => Date(day).Replace('/', '-');

    public static string Number(decimal value) =>
        (value == 0 ? 0 : value).ToString(value == decimal.Truncate(value) ? "#,##0" : "#,##0.##", CultureInfo.InvariantCulture);

    public static string Money(decimal value) => $"{Number(value)} افغانی";

    /// <summary>A cell as text for the PDF. Null reads as an empty cell.</summary>
    public static string Cell(object? value, string kind) => value switch
    {
        null => "",
        DateOnly day => Date(day),
        DateTime time => kind == ReportKind.Date ? Date(DateOnly.FromDateTime(Kabul(time))) : Time(time),
        decimal number => Number(number),
        int number => Number(number),
        long number => Number(number),
        double number => Number((decimal)number),
        _ => value.ToString() ?? "",
    };

    /// <summary>A number in a numeric column stays a number; everything else is shown as written.</summary>
    public static decimal? NumberOf(object? value) => value switch
    {
        decimal number => number,
        int number => number,
        long number => number,
        double number => (decimal)number,
        _ => null,
    };
}
