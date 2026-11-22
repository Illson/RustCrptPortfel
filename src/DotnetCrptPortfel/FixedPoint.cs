using System.Globalization;

namespace DotnetCrptPortfel;

internal static class FixedPoint
{
    internal const int QuantityDigits = 18;
    internal const int PriceDigits = 8;
    internal const int ValueDigits = QuantityDigits + PriceDigits;

    internal static UInt128 Parse(ReadOnlySpan<char> input, int digits)
    {
        int decimalPoint = input.IndexOf('.');
        ReadOnlySpan<char> whole = decimalPoint < 0 ? input : input[..decimalPoint];
        ReadOnlySpan<char> fraction = decimalPoint < 0 ? [] : input[(decimalPoint + 1)..];
        if (decimalPoint >= 0 && fraction.IsEmpty)
        {
            throw new FormatException("expected digits after the decimal point");
        }

        if (whole.IsEmpty || whole.ContainsAnyExceptInRange('0', '9') || fraction.ContainsAnyExceptInRange('0', '9'))
        {
            throw new FormatException("expected an unsigned decimal number");
        }

        if (fraction.Length > digits)
        {
            throw new FormatException("too many fractional digits");
        }

        var culture = CultureInfo.InvariantCulture;
        if (!UInt128.TryParse(whole, NumberStyles.None, culture, out UInt128 wholeUnits))
        {
            throw new FormatException("decimal value is too large");
        }

        try
        {
            UInt128 units = checked(wholeUnits * PowerOfTen(digits));
            if (!fraction.IsEmpty)
            {
                UInt128 fractionUnits = UInt128.Parse(fraction, NumberStyles.None, culture);
                units = checked(units + fractionUnits * PowerOfTen(digits - fraction.Length));
            }

            return units;
        }
        catch (OverflowException exception)
        {
            throw new FormatException("decimal value is too large", exception);
        }
    }

    internal static string Format(UInt128 units, int digits)
    {
        UInt128 scale = PowerOfTen(digits);
        var culture = CultureInfo.InvariantCulture;
        string whole = (units / scale).ToString(culture);
        UInt128 fraction = units % scale;
        if (fraction == 0)
        {
            return whole;
        }

        string fractionText = fraction.ToString(culture).PadLeft(digits, '0').TrimEnd('0');
        return $"{whole}.{fractionText}";
    }

    private static UInt128 PowerOfTen(int digits)
    {
        UInt128 power = 1;
        for (int index = 0; index < digits; index++)
        {
            power = checked(power * 10);
        }

        return power;
    }
}
