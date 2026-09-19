using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Padra.Web.Services;

/// <summary>
/// Culture-aware presentation.
/// <para>
/// Persian uses its own digits and the Jalali calendar, so numbers and dates go
/// through here rather than straight <c>ToString</c>: the culture supplies the
/// grouping and the calendar, and native digits are substituted when the culture
/// declares them.
/// </para>
/// </summary>
public static class CultureFormat
{
    private const long Kib = 1024;

    private static readonly string[] SizeKeys =
        { "unit.byte", "unit.kib", "unit.mib", "unit.gib", "unit.tib" };

    /// <summary>A culture ready for display: Jalali calendar for Persian.</summary>
    public static CultureInfo Build(string name)
    {
        var culture = new CultureInfo(name);
        if (culture.TwoLetterISOLanguageName == "fa")
        {
            var persian = culture.OptionalCalendars.OfType<PersianCalendar>().FirstOrDefault();
            if (persian is not null)
            {
                culture.DateTimeFormat.Calendar = persian;
            }
        }

        return culture;
    }

    public static string Number(long value) => Localise(
        value.ToString("N0", CultureInfo.CurrentUICulture));

    public static string Number(int value) => Number((long)value);

    public static string Bytes(long value, IStringLocalizer<UiStrings> localizer)
    {
        double scaled = value;
        var index = 0;
        while (scaled >= Kib && index < SizeKeys.Length - 1)
        {
            scaled /= Kib;
            index++;
        }

        var text = scaled.ToString(index == 0 ? "N0" : "N1", CultureInfo.CurrentUICulture);
        return localizer[SizeKeys[index], Localise(text)];
    }

    public static string Time(DateTimeOffset value) => Localise(
        value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentUICulture));

    public static string DateTime(DateTimeOffset value) => Localise(
        value.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture));

    /// <summary>Swap ASCII digits for the culture's own, when it declares any.</summary>
    public static string Localise(string text)
    {
        var digits = CultureInfo.CurrentUICulture.NumberFormat.NativeDigits;
        if (digits.Length != 10 || digits[0] == "0")
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is >= '0' and <= '9' ? digits[character - '0'] : character.ToString());
        }

        return builder.ToString();
    }
}
