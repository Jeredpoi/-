using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

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
        public string PrecipText => Precip >= 0.1 ? Precip.ToString("0.#", CultureInfo.InvariantCulture) + " мм" : "";
        // для полоски диапазона
        public double BarLeft { get; set; }
        public double BarWidth { get; set; }
    }

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
        public List<HourWeather> Hours = new();
        public List<DayWeather> Days = new();
        public string Description => WeatherService.Describe(Code);
        public string Glyph => WeatherService.Glyph(Code, IsDay);
    }

    /// <summary>Почасовой прогноз Open-Meteo — без ключа, включая снег и вероятность осадков.</summary>
    public static class WeatherService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

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
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(inv) + "&longitude=" + lon.ToString(inv) +
                      "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,precipitation,is_day" +
                      "&hourly=temperature_2m,weather_code,is_day,snowfall,precipitation_probability&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum,sunrise,sunset" +
                      "&wind_speed_unit=ms&timezone=auto&forecast_days=7";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var w = new WeatherNow
            {
                City = city,
                Temp = cur.GetProperty("temperature_2m").GetDouble(),
                Feels = cur.GetProperty("apparent_temperature").GetDouble(),
                Humidity = (int)cur.GetProperty("relative_humidity_2m").GetDouble(),
                Code = cur.GetProperty("weather_code").GetInt32(),
                Wind = cur.GetProperty("wind_speed_10m").GetDouble(),
                WindDeg = cur.TryGetProperty("wind_direction_10m", out var wd) && wd.ValueKind == JsonValueKind.Number ? wd.GetDouble() : 0,
                Precip = cur.GetProperty("precipitation").GetDouble(),
                IsDay = cur.GetProperty("is_day").GetInt32() == 1,
                ForecastNow = DateTime.Parse(cur.GetProperty("time").GetString(), inv),
            };

            var hourly = root.GetProperty("hourly");
            var ht = hourly.GetProperty("time");
            var htemp = hourly.GetProperty("temperature_2m");
            var hcode = hourly.GetProperty("weather_code");
            var hday = hourly.GetProperty("is_day");
            var hsnow = hourly.GetProperty("snowfall");
            var hprob = hourly.GetProperty("precipitation_probability");
            var nowHour = w.ForecastNow;
            int added = 0;
            for (int i = 0; i < ht.GetArrayLength(); i++)
            {
                var t = DateTime.Parse(ht[i].GetString(), inv);
                if (t.AddHours(1) <= nowHour) continue;
                var snow = hsnow[i].GetDouble();
                var code = hcode[i].GetInt32();
                var probability = hprob[i].ValueKind == JsonValueKind.Number ? hprob[i].GetInt32() : 0;
                if (!w.NextSnowAt.HasValue && t > nowHour && (snow > 0 || IsSnow(code))) w.NextSnowAt = t;
                if (added >= 8) continue;
                w.Hours.Add(new HourWeather
                {
                    Time = added == 0 ? "Сейчас" : t.ToString("HH:mm"),
                    Temp = Math.Round(htemp[i].GetDouble()) + "°",
                    Icon = Glyph(code, hday[i].GetInt32() == 1),
                    At = t,
                    Snowfall = snow,
                    PrecipitationProbability = probability
                });
                added++;
            }

            var daily = root.GetProperty("daily");
            var dt = daily.GetProperty("time");
            double gMin = double.MaxValue, gMax = double.MinValue;
            var ru = new CultureInfo("ru-RU");
            for (int i = 0; i < dt.GetArrayLength(); i++)
            {
                var d = DateTime.Parse(dt[i].GetString(), inv);
                var day = new DayWeather
                {
                    Name = i == 0 ? "Сегодня" : ru.TextInfo.ToTitleCase(d.ToString("ddd", ru)),
                    Icon = Glyph(daily.GetProperty("weather_code")[i].GetInt32(), true),
                    Min = daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                    Max = daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                    Precip = daily.GetProperty("precipitation_sum")[i].GetDouble(),
                };
                gMin = Math.Min(gMin, day.Min);
                gMax = Math.Max(gMax, day.Max);
                w.Days.Add(day);
                if (i == 0)
                {
                    w.TodayMin = day.Min; w.TodayMax = day.Max;
                    w.Sunrise = DateTime.Parse(daily.GetProperty("sunrise")[i].GetString(), inv);
                    w.Sunset = DateTime.Parse(daily.GetProperty("sunset")[i].GetString(), inv);
                }
            }
            double span = Math.Max(1, gMax - gMin);
            const double barTotal = 160;
            foreach (var d in w.Days)
            {
                d.BarLeft = (d.Min - gMin) / span * barTotal;
                d.BarWidth = Math.Max(6, (d.Max - d.Min) / span * barTotal);
            }
            return w;
        }

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
