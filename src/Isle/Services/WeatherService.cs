using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;

namespace Isle.Services
{
    public class HourWeather
    {
        public string Time { get; set; }
        public string Temp { get; set; }
        public string Icon { get; set; }
        public DateTime At { get; set; }
        public double Snowfall { get; set; }
        public int PrecipitationProbability { get; set; }
    }

    public class DayWeather
    {
        public string Name { get; set; }
        public string Icon { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Precip { get; set; }
        public string MinText => Math.Round(Min) + "°";
        public string MaxText => Math.Round(Max) + "°";
        public string PrecipText => Precip >= 0.1 ? Precip.ToString("0.#", CultureInfo.CurrentCulture) + " мм" : "";
        // для полоски диапазона
        public double BarLeft { get; set; }
        public double BarWidth { get; set; }
    }

    /// <summary>Ожидаемое погодное явление: гроза, ливень, снег, туман, сильный ветер, мороз, жара…</summary>
    public sealed record WeatherAlert(string Kind, string Title, DateTime At, string Glyph);

    public class WeatherNow
    {
        public string City = "";
        public double Temp, Feels, Wind, WindDeg, Precip, TodayMin, TodayMax;
        /// <summary>Откуда дует ветер: С, СВ, В, ЮВ, Ю, ЮЗ, З, СЗ.</summary>
        public string WindFrom => new[] { "С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ" }[(int)Math.Round(((WindDeg % 360) + 360) % 360 / 45.0) % 8];
        public int Humidity, Code;
        public bool IsDay = true;
        public DateTime Sunrise, Sunset;
        public DateTime ForecastNow;
        public DateTime? NextSnowAt;
        public string SnowForecast
        {
            get
            {
                if (!NextSnowAt.HasValue) return "Снег в прогнозе не ожидается";
                var day = NextSnowAt.Value.Date - ForecastNow.Date;
                var when = day == TimeSpan.Zero ? "сегодня" : day == TimeSpan.FromDays(1) ? "завтра" : NextSnowAt.Value.ToString("dd.MM");
                return "Снег ожидается " + when + " около " + NextSnowAt.Value.ToString("HH:mm");
            }
        }
        /// <summary>Ближайшее явление в следующие 12 часов, которого нет сейчас (null — спокойно).</summary>
        public WeatherAlert NextAlert;
        public string AlertForecast
        {
            get
            {
                if (NextAlert == null) return SnowForecast;
                var day = NextAlert.At.Date - ForecastNow.Date;
                var when = day == TimeSpan.Zero ? "сегодня" : day == TimeSpan.FromDays(1) ? "завтра" : NextAlert.At.ToString("dd.MM");
                return NextAlert.Glyph + " " + NextAlert.Title + " " + when + " около " + NextAlert.At.ToString("HH:mm");
            }
        }
        public List<HourWeather> Hours = new();
        public List<DayWeather> Days = new();
        public string Description => WeatherService.Describe(Code);
        public string Glyph => WeatherService.Glyph(Code, IsDay);
    }

    /// <summary>Прогноз MET Norway Locationforecast с ограничением частоты запросов по их правилам.</summary>
    public static class WeatherService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly SemaphoreSlim ForecastLock = new(1, 1);
        private static readonly Dictionary<string, ForecastCache> Forecasts = new(StringComparer.Ordinal);
        /// <summary>Сырой прогноз храним целиком: «сейчас», почасовой и предупреждения пересчитываются от текущего времени при каждом обращении.</summary>
        private sealed record ForecastCache(WeatherNow Weather, DateTimeOffset RefreshAfter, DateTimeOffset? LastModified, string Json);

        /// <summary>Прогноз из кэша, но «сейчас» — настоящее (а не время скачивания: оно бывает 3 часа назад).</summary>
        private static WeatherNow Fresh(ForecastCache cached, string city)
        {
            try
            {
                if (string.IsNullOrEmpty(cached.Json)) { cached.Weather.City = city; return cached.Weather; }
                using var doc = JsonDocument.Parse(cached.Json);
                var w = ParseLocationForecast(doc.RootElement, city);
                w.Sunrise = cached.Weather.Sunrise;
                w.Sunset = cached.Weather.Sunset;
                return w;
            }
            catch { cached.Weather.City = city; return cached.Weather; }
        }

        static WeatherService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/0.2 (+https://github.com/Jeredpoi/-)");
        }

        public static async Task<(string name, double lat, double lon)?> GeocodeAsync(string city)
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&name=" + Uri.EscapeDataString(city);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var r = res[0];
            return (r.GetProperty("name").GetString(), r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
        }

        public static async Task<WeatherNow> LoadAsync(double lat, double lon, string city)
        {
            Journal.Info("weather", "запрос " + city + " (" + lat.ToString("0.###", CultureInfo.InvariantCulture) + "," + lon.ToString("0.###", CultureInfo.InvariantCulture) + ")");
            var inv = CultureInfo.InvariantCulture;
            lat = Math.Round(lat, 4);
            lon = Math.Round(lon, 4);
            string key = lat.ToString("0.####", inv) + "," + lon.ToString("0.####", inv);
            await ForecastLock.WaitAsync();
            try
            {
                var now = DateTimeOffset.UtcNow;
                Forecasts.TryGetValue(key, out var cached);
                if (cached != null && now < cached.RefreshAfter) return Fresh(cached, city);

                var url = "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=" + lat.ToString("0.####", inv) + "&lon=" + lon.ToString("0.####", inv);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (cached?.LastModified is { } lastModified) request.Headers.IfModifiedSince = lastModified;
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached != null)
                {
                    var renewed = response.Content.Headers.Expires ?? now + MinimumRefreshInterval(lat, lon);
                    cached = cached with { RefreshAfter = Max(renewed, now + MinimumRefreshInterval(lat, lon)) };
                    Forecasts[key] = cached;
                    return Fresh(cached, city);
                }
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var weather = ParseLocationForecast(doc.RootElement, city);
                await LoadSunAsync(lat, lon, weather);

                var minInterval = MinimumRefreshInterval(lat, lon);
                var expires = response.Content.Headers.Expires ?? now;
                var refreshAfter = Max(expires, now + minInterval);
                var modified = response.Content.Headers.LastModified;
                Forecasts[key] = new ForecastCache(weather, refreshAfter, modified, json);
                return weather;
            }
            finally { ForecastLock.Release(); }
        }

        private sealed record ForecastPoint(DateTime At, double Temp, double Min, double Max, double Humidity, double Wind, double WindDeg, double Precip, int Probability, int Code, bool IsDay);

        internal static WeatherNow ParseLocationForecast(JsonElement root, string city)
        {
            var inv = CultureInfo.InvariantCulture;
            var timeseries = root.GetProperty("properties").GetProperty("timeseries");
            var points = new List<ForecastPoint>(timeseries.GetArrayLength());
            foreach (var item in timeseries.EnumerateArray())
            {
                var at = DateTimeOffset.Parse(item.GetProperty("time").GetString(), inv).ToLocalTime().DateTime;
                var data = item.GetProperty("data");
                var instant = data.GetProperty("instant").GetProperty("details");
                string symbol = GetSymbol(data);
                var code = CodeForSymbol(symbol);
                var period = FindPeriod(data);
                var details = period.ValueKind == JsonValueKind.Object && period.TryGetProperty("details", out var d) ? d : default;
                double precip = NumberOr(details, "precipitation_amount", 0);
                int probability = (int)Math.Round(NumberOr(details, "probability_of_precipitation", 0));
                double temperature = NumberOr(instant, "air_temperature", 0);
                points.Add(new ForecastPoint(at,
                    temperature, NumberOr(details, "air_temperature_min", temperature), NumberOr(details, "air_temperature_max", temperature), NumberOr(instant, "relative_humidity", 0),
                    NumberOr(instant, "wind_speed", 0), NumberOr(instant, "wind_from_direction", 0),
                    precip, probability, code, IsDaySymbol(symbol)));
            }
            if (points.Count == 0) throw new InvalidOperationException("MET Norway returned an empty forecast.");

            var now = DateTime.Now;
            var current = points.OrderBy(p => Math.Abs((p.At - now).TotalMinutes)).First();
            var weather = new WeatherNow
            {
                City = city, Temp = current.Temp, Feels = current.Temp, Humidity = (int)Math.Round(current.Humidity),
                Code = current.Code, Wind = current.Wind, WindDeg = current.WindDeg, Precip = current.Precip,
                IsDay = current.IsDay, ForecastNow = now
            };

            var future = points.Where(p => p.At >= now.AddMinutes(-60)).ToList();
            if (future.Count == 0) future = points.Take(1).ToList();
            for (int i = 0; i < future.Count; i++)
            {
                var point = future[i];
                if (!weather.NextSnowAt.HasValue && point.At > now && IsSnow(point.Code)) weather.NextSnowAt = point.At;
                if (weather.Hours.Count >= 8) continue;
                weather.Hours.Add(new HourWeather
                {
                    Time = weather.Hours.Count == 0 ? "Сейчас" : point.At.ToString("HH:mm"),
                    Temp = Math.Round(point.Temp) + "°", Icon = Glyph(point.Code, point.IsDay), At = point.At,
                    Snowfall = IsSnow(point.Code) ? point.Precip : 0, PrecipitationProbability = point.Probability
                });
            }

            weather.NextAlert = FindAlert(points, current, now);

            var ru = new CultureInfo("ru-RU");
            var days = points.GroupBy(p => p.At.Date).Where(g => g.Key >= now.Date).Take(7).ToList();
            double globalMin = days.Min(g => g.Min(p => p.Min));
            double globalMax = days.Max(g => g.Max(p => p.Max));
            const double barTotal = 160;
            foreach (var group in days)
            {
                var samples = group.ToList();
                var noon = samples.OrderBy(p => Math.Abs((p.At.Hour + p.At.Minute / 60.0) - 13)).First();
                var day = new DayWeather
                {
                    Name = group.Key == now.Date ? "Сегодня" : ru.TextInfo.ToTitleCase(group.Key.ToString("ddd", ru)),
                    Icon = Glyph(noon.Code, noon.IsDay), Min = samples.Min(p => p.Temp), Max = samples.Max(p => p.Temp),
                    Precip = samples.Sum(p => Math.Max(0, p.Precip))
                };
                day.Min = samples.Min(p => p.Min);
                day.Max = samples.Max(p => p.Max);
                day.BarLeft = (day.Min - globalMin) / Math.Max(1, globalMax - globalMin) * barTotal;
                day.BarWidth = Math.Max(6, (day.Max - day.Min) / Math.Max(1, globalMax - globalMin) * barTotal);
                weather.Days.Add(day);
                if (group.Key == now.Date) { weather.TodayMin = day.Min; weather.TodayMax = day.Max; }
            }
            return weather;
        }

        /// <summary>Какое опасное или заметное явление в этой точке прогноза (null — ничего особенного).</summary>
        private static (string Kind, string Title, string Glyph)? Hazard(int code, double temp, double wind)
        {
            if (code is 96 or 99) return ("hail", "Гроза с градом", "⛈");
            if (code == 95) return ("thunder", "Гроза", "⛈");
            if (code == 67) return ("sleet", "Мокрый снег, возможен гололёд", "🌨");
            if (code is 73 or 75 or 85) return ("snow", code == 75 ? "Сильный снег" : "Снег", "❄");
            if (code is 65) return ("rain", "Сильный дождь", "🌧");
            if (code is 61 or 80) return ("rain", code == 80 ? "Ливень" : "Дождь", "🌧");
            if (code == 45) return ("fog", "Туман", "🌫");
            if (wind >= 12) return ("wind", "Сильный ветер " + Math.Round(wind) + " м/с", "💨");
            if (temp <= -15) return ("frost", "Сильный мороз " + Math.Round(temp) + "°", "🥶");
            if (temp >= 30) return ("heat", "Жара " + Math.Round(temp) + "°", "🥵");
            return null;
        }

        /// <summary>Первое явление в ближайшие 12 часов, которого нет прямо сейчас (дождь, который уже идёт, — не новость).</summary>
        private static WeatherAlert FindAlert(List<ForecastPoint> points, ForecastPoint current, DateTime now)
        {
            var nowKind = Hazard(current.Code, current.Temp, current.Wind)?.Kind;
            foreach (var p in points.Where(p => p.At > now && p.At <= now.AddHours(12)).OrderBy(p => p.At))
            {
                var h = Hazard(p.Code, p.Temp, p.Wind);
                if (h == null || h.Value.Kind == nowKind) continue;
                return new WeatherAlert(h.Value.Kind, h.Value.Title, p.At, h.Value.Glyph);
            }
            return null;
        }

        private static JsonElement FindPeriod(JsonElement data)
        {
            foreach (var key in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
                if (data.TryGetProperty(key, out var period)) return period;
            return default;
        }

        private static string GetSymbol(JsonElement data)
        {
            var period = FindPeriod(data);
            return period.ValueKind == JsonValueKind.Object && period.TryGetProperty("summary", out var summary) && summary.TryGetProperty("symbol_code", out var symbol)
                ? symbol.GetString() ?? "" : "";
        }

        private static double NumberOr(JsonElement element, string name, double fallback) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

        private static int CodeForSymbol(string symbol)
        {
            var baseName = symbol.Split('_')[0].ToLowerInvariant();
            if (baseName.Contains("thunder") && baseName.Contains("hail")) return 99;
            if (baseName.Contains("thunder")) return 95;
            if (baseName.Contains("hail")) return 96;
            if (baseName.Contains("snowshowers")) return 85;
            if (baseName.Contains("heavysnow")) return 75;
            if (baseName.Contains("sleet")) return 67;
            if (baseName.Contains("snow")) return 73;
            if (baseName.Contains("rainshowers") || baseName.Contains("showers")) return 80;
            if (baseName.Contains("heavyrain")) return 65;
            if (baseName.Contains("rain") || baseName.Contains("drizzle")) return 61;
            if (baseName.Contains("fog")) return 45;
            if (baseName.Contains("partlycloudy")) return 2;
            if (baseName.Contains("cloudy")) return 3;
            if (baseName.Contains("fair")) return 1;
            if (baseName.Contains("clearsky")) return 0;
            return 3;
        }

        private static bool IsDaySymbol(string symbol) => symbol.Contains("_day", StringComparison.OrdinalIgnoreCase) || symbol.Contains("polartwilight", StringComparison.OrdinalIgnoreCase);

        private static async Task LoadSunAsync(double lat, double lon, WeatherNow weather)
        {
            try
            {
                var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
                string offsetText = (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string url = "https://api.met.no/weatherapi/sunrise/3.0/sun?lat=" + lat.ToString("0.####", CultureInfo.InvariantCulture) +
                    "&lon=" + lon.ToString("0.####", CultureInfo.InvariantCulture) + "&date=" + date + "&offset=" + Uri.EscapeDataString(offsetText);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var properties = doc.RootElement.GetProperty("properties");
                if (properties.TryGetProperty("sunrise", out var rise) && rise.TryGetProperty("time", out var riseTime) && riseTime.ValueKind == JsonValueKind.String)
                    weather.Sunrise = DateTimeOffset.Parse(riseTime.GetString(), CultureInfo.InvariantCulture).LocalDateTime;
                if (properties.TryGetProperty("sunset", out var set) && set.TryGetProperty("time", out var setTime) && setTime.ValueKind == JsonValueKind.String)
                    weather.Sunset = DateTimeOffset.Parse(setTime.GetString(), CultureInfo.InvariantCulture).LocalDateTime;
            }
            catch { }
        }

        private static TimeSpan MinimumRefreshInterval(double lat, double lon)
        {
            bool nordic = (lat >= 54 && lat <= 72 && lon >= 4 && lon <= 32) || (lat >= 63 && lat <= 67 && lon >= -25 && lon <= -13);
            return TimeSpan.FromMinutes(nordic ? 30 : 180);
        }

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

        private static bool IsSnow(int code) => code is 71 or 73 or 75 or 77 or 85 or 86;

        public static string Describe(int c) => c switch
        {
            0 => "Ясно",
            1 => "Преимущественно ясно",
            2 => "Переменная облачность",
            3 => "Пасмурно",
            45 or 48 => "Туман",
            51 or 53 or 55 => "Морось",
            56 or 57 => "Ледяная морось",
            61 or 63 => "Дождь",
            65 => "Сильный дождь",
            66 or 67 => "Ледяной дождь",
            71 or 73 => "Снег",
            75 => "Сильный снег",
            77 => "Снежные зёрна",
            80 or 81 or 82 => "Ливень",
            85 or 86 => "Снегопад",
            95 => "Гроза",
            96 or 99 => "Гроза с градом",
            _ => "—"
        };

        /// <summary>Символ из шрифта Segoe Fluent Icons / MDL2 нет для погоды, поэтому используем простые текстовые глифы.</summary>
        public static string Glyph(int c, bool day) => c switch
        {
            0 => day ? "☀" : "☾",
            1 or 2 => day ? "⛅" : "☁",
            3 => "☁",
            45 or 48 => "≋",
            >= 51 and <= 67 => "☂",
            >= 80 and <= 82 => "☂",
            >= 71 and <= 77 => "❄",
            85 or 86 => "❄",
            >= 95 => "⚡",
            _ => "·"
        };
    }
}
