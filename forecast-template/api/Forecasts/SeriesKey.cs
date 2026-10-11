using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Forecast.Api.Forecasts;

/// <summary>
/// Канонизация ключа ряда: ML присылает series_key произвольным объектом, C# сопоставляет по канонической строке
/// (поля по алфавиту, числа нормализованы). Имена ключей ML не хардкодим — только здесь, при построении ключа из БД.
/// TODO(хакатон): ключевые колонки series меняются вместе с init.sql и ml/config.yaml: series_keys.
/// </summary>
public static class SeriesKey
{
    public static string FromDb(int storeNbr, string family) =>
        Canonical([new("family", "s:" + family), new("store_nbr", "n:" + storeNbr.ToString(CultureInfo.InvariantCulture))]);

    public static string FromMl(Dictionary<string, JsonElement> key) =>
        Canonical(key.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ValueKind switch
        {
            JsonValueKind.Number => "n:" + kv.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.String => "s:" + kv.Value.GetString(),
            _ => "j:" + kv.Value.GetRawText(),
        })));

    private static string Canonical(IEnumerable<KeyValuePair<string, string>> parts)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v).Append('\u001f');
        return sb.ToString();
    }
}
