// Author: Ilgaz Mehmetoğlu
using System.Globalization;
using Avalonia.Data.Converters;

namespace Koware.Browser;

/// <summary>
/// Static converters for XAML bindings.
/// </summary>
public static class Converters
{
    /// <summary>
    /// Converts browse mode boolean to search watermark text.
    /// </summary>
    public static readonly IValueConverter ModeToWatermark = new FuncValueConverter<bool, string>(
        isManga => isManga ? "Search manga titles..." : "Search anime titles...");

    /// <summary>
    /// Returns true if the value is zero.
    /// </summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(
        value => value == 0);

    /// <summary>
    /// Returns true if the value is not zero.
    /// </summary>
    public static readonly IValueConverter IsNotZero = new FuncValueConverter<int, bool>(
        value => value != 0);
}
