using System.Globalization;

namespace AzureBlobDownloader.Core.Storage;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Human-readable size with binary multiples, e.g. <c>1.5 GB</c>.</summary>
    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.##";
        return $"{value.ToString(format, provider ?? CultureInfo.CurrentCulture)} {Units[unit]}";
    }
}
