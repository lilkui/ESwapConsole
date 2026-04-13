using System.Globalization;
using System.Text;
using CsvHelper;

namespace ESwapConsole.Services;

public static class CsvHelper
{
    public static T[] ReadCsv<T>(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(fs);
        using CsvReader csv = new(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<T>().ToArray();
    }

    public static void WriteCsv<T>(IEnumerable<T> records, string path)
    {
        using StreamWriter writer = new(path, false, Encoding.UTF8);
        using CsvWriter csv = new(writer, CultureInfo.InvariantCulture);
        csv.WriteRecords(records);
    }
}
