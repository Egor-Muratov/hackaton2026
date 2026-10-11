namespace Forecast.Api.Import;

/// <summary>
/// Минимальный разбор CSV-строки (подмножество RFC 4180): разделитель ',', поля в кавычках, экранирование "".
/// Многострочные поля в кавычках не поддерживаются (в данных репетиции их нет).
/// </summary>
public static class Csv
{
    /// <summary>Режет строку на поля; возвращает число полей или -1, если кавычка не закрыта.</summary>
    public static int Split(string line, Range[] fields)
    {
        int count = 0, start = 0;
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                if (count == fields.Length) return count + 1;
                fields[count++] = new Range(start, i);
                start = i + 1;
            }
        }
        if (inQuotes) return -1;
        if (count == fields.Length) return count + 1;
        fields[count++] = new Range(start, line.Length);
        return count;
    }

    /// <summary>Значение поля без внешних кавычек и пробелов по краям.</summary>
    public static ReadOnlySpan<char> Field(string line, Range r)
    {
        var s = line.AsSpan()[r].Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1];
            if (s.Contains("\"\"", StringComparison.Ordinal)) return s.ToString().Replace("\"\"", "\"");
        }
        return s;
    }
}
