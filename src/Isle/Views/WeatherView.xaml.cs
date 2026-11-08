using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Isle.Services;

namespace Isle.Views
{
    public partial class WeatherView : UserControl, IIslandView
    {
        private readonly Random _weatherRandom = new();
        // Пока панель раскрывается, размер меняется каждый кадр — раньше фон (облака, дождь) пересоздавался
        // на каждом шаге, и анимации дёргались. Теперь перерисовываем, когда размер устоялся, и только если что-то изменилось.
        private readonly System.Windows.Threading.DispatcherTimer _fxRedraw = new() { Interval = TimeSpan.FromMilliseconds(180) };
        private string _fxKey;

        public WeatherView()
        {
            InitializeComponent();
            _fxRedraw.Tick += (_, __) => { _fxRedraw.Stop(); if (IsVisible && AppHost.Weather is { } w) DrawWeatherBackdrop(w); };
            AppHost.WeatherChanged += Render;
            // бесконечные анимации снега/дождя крутятся, даже когда вкладка закрыта, и держат отрисовку окна —
            // убираем их, пока погоду не видно, и рисуем заново при показе
            IsVisibleChanged += (_, __) =>
            {
                if (!IsVisible) { WeatherFx.Children.Clear(); _fxKey = null; _fxRedraw.Stop(); }
                else { _fxRedraw.Stop(); _fxRedraw.Start(); }
            };
            Render();
        }

        public void OnShown() => Render();

        private void Render()
        {
            var w = AppHost.Weather;
            if (w == null)
            {
                City.Text = AppSettings.Current.City;
                Desc.Text = AppHost.Window?.WeatherError is { } err
                    ? "Не удалось загрузить погоду — нажми ↻ обновить (" + (err.Length > 60 ? err.Substring(0, 60) + "…" : err) + ")"
                    : "Загружаю погоду…";
                return;
            }
            City.Text = w.City;
            GlyphAnim.Kind = Helpers.WeatherGlyph.KindFor(w.Code, w.IsDay, w.Temp, w.Wind); // живой значок: солнце, облака, дождь, гроза…
            Temp.Text = Math.Round(w.Temp) + "°";
            Desc.Text = w.Description;
            Snow.Text = w.AlertForecast; // ближайшее явление: гроза, дождь, снег, туман, ветер, мороз, жара
            Feels.Text = "Сегодня " + Math.Round(w.TodayMin) + "°…" + Math.Round(w.TodayMax) + "°";
            Wind.Text = w.Wind.ToString("0.#", CultureInfo.CurrentCulture) + " м/с " + w.WindFrom;
            Hum.Text = w.Humidity + "%";
            Prec.Text = w.Precip.ToString("0.#", CultureInfo.CurrentCulture) + " мм";
            Sun.Text = "Восход " + w.Sunrise.ToString("HH:mm") + " · закат " + w.Sunset.ToString("HH:mm");
            Sun.Visibility = w.Sunrise == default && w.Sunset == default ? Visibility.Collapsed : Visibility.Visible;
            Hours.ItemsSource = w.Hours;
            Days.ItemsSource = w.Days;
            DrawWeatherBackdrop(w);
        }

        private void OnWeatherFxSizeChanged(object sender, SizeChangedEventArgs e)
        {
            WeatherFx.Clip = new RectangleGeometry(new Rect(0, 0, WeatherFx.ActualWidth, WeatherFx.ActualHeight), 17, 17);
            _fxRedraw.Stop(); _fxRedraw.Start(); // дождёмся, пока размер устоится
        }

        private void DrawWeatherBackdrop(WeatherNow weather)
        {
            double width = WeatherFx.ActualWidth;
            double height = WeatherFx.ActualHeight;
            if (width < 30 || height < 60) return;
            // та же погода и почти тот же размер — анимации уже идут, не перезапускаем их (иначе рывок)
            string key = weather.Code + "|" + weather.IsDay + "|" + (weather.Wind >= 8) + "|" + Math.Round(width / 12) + "|" + Math.Round(height / 12);
            if (key == _fxKey && WeatherFx.Children.Count > 0) return;
            _fxKey = key;

            WeatherFx.Children.Clear();
            var (top, bottom) = Palette(weather.Code, weather.IsDay);
            HeroBackground.Background = new LinearGradientBrush(top, bottom, 90);

            bool snow = IsSnow(weather.Code);
            bool rain = IsRain(weather.Code) || IsThunder(weather.Code);
            bool cloudy = IsCloudy(weather.Code) || snow || rain;

            // Open-Meteo uses the same cloud codes for day and night. Only draw
            // the sun when the forecast explicitly says this is daytime.
            if (weather.IsDay && (weather.Code == 0 || weather.Code is 1 or 2))
                AddSun(width, cloudy ? 0.72 : 1.0);
            if (!weather.IsDay && weather.Code is 0 or 1 or 2) AddStars(width, height);
            if (cloudy) AddCloud(width, weather.Code);
            if (weather.Code == 3 || rain || snow || IsThunder(weather.Code)) AddCloud(width - 64, weather.Code, 0.7, 62, 0.4); // пасмурно — второе облако, правее градусов
            if (snow) AddSnow(width, height);
            else if (rain) AddRain(width, height);
            else if (weather.Code is 45 or 48) AddMist(width, height);
            if (IsThunder(weather.Code)) AddLightning(width, height);
            if (weather.Wind >= 8) AddWind(width, height);
        }

        private void AddSun(double width, double opacity)
        {
            var sun = new Ellipse
            {
                Width = 84, Height = 84, Opacity = opacity,
                Fill = new RadialGradientBrush(Color.FromArgb(230, 0xFF, 0xD8, 0x8A), Color.FromArgb(0, 0xFF, 0xD8, 0x8A))
            };
            Canvas.SetLeft(sun, width - 115);
            Canvas.SetTop(sun, 24);
            WeatherFx.Children.Add(sun);
            var pulse = new DoubleAnimation(0.86, 1, TimeSpan.FromSeconds(3.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            sun.BeginAnimation(OpacityProperty, pulse);
        }

        private void AddCloud(double width, int code) => AddCloud(width, code, 1, 18, 0.55);

        /// <summary>Облако, которое медленно плывёт туда-сюда.</summary>
        private void AddCloud(double width, int code, double scale, double top, double opacity)
        {
            var color = code >= 95 ? Color.FromRgb(0x8E, 0x95, 0xAC) : Color.FromRgb(0xB4, 0xC3, 0xD4); // приглушённые, чтобы белые градусы читались
            // One continuous silhouette avoids visible seams between translucent lobes.
            var cloud = new System.Windows.Shapes.Path
            {
                Width = 130 * scale, Height = 64 * scale, Opacity = opacity, RenderTransform = new TranslateTransform(),
                Stretch = Stretch.Fill,
                Fill = new SolidColorBrush(color),
                Data = Geometry.Parse("M 16,54 C 7,54 2,47 2,39 C 2,31 8,25 16,24 C 19,11 30,4 43,4 C 55,4 64,11 68,22 C 72,18 78,16 84,16 C 97,16 107,25 109,37 C 120,37 128,44 128,53 C 128,61 122,64 113,64 L 17,64 C 8,64 3,60 3,55 Z")
            };
            // облака — только в правой части карточки, слева крупные белые градусы
            Canvas.SetLeft(cloud, Math.Max(WeatherFx.ActualWidth * 0.5, width - 142 * scale)); Canvas.SetTop(cloud, top);
            cloud.CacheMode = new BitmapCache(); // облако рисуется один раз, дальше только сдвигается — плавно и без нагрузки
            WeatherFx.Children.Add(cloud);
            var drift = new DoubleAnimation(-14, 10, TimeSpan.FromSeconds(7 + _weatherRandom.NextDouble() * 4))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            ((TranslateTransform)cloud.RenderTransform).BeginAnimation(TranslateTransform.XProperty, drift);
        }

        private void AddSnow(double width, double height)
        {
            int count = (int)Math.Clamp(width / 7, 18, 30);
            for (int i = 0; i < count; i++)
            {
                double size = 2 + _weatherRandom.NextDouble() * 3.2;
                var flake = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Opacity = 0.42 + _weatherRandom.NextDouble() * 0.42, RenderTransform = new TranslateTransform() };
                Canvas.SetLeft(flake, _weatherRandom.NextDouble() * width);
                Canvas.SetTop(flake, -_weatherRandom.NextDouble() * height);
                WeatherFx.Children.Add(flake);
                var fall = new DoubleAnimation(-8, height + 12, TimeSpan.FromSeconds(5 + _weatherRandom.NextDouble() * 6))
                {
                    BeginTime = TimeSpan.FromSeconds(_weatherRandom.NextDouble() * 6), RepeatBehavior = RepeatBehavior.Forever
                };
                var drift = new DoubleAnimation(-9, 9, TimeSpan.FromSeconds(2 + _weatherRandom.NextDouble() * 2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                ((TranslateTransform)flake.RenderTransform).BeginAnimation(TranslateTransform.YProperty, fall);
                ((TranslateTransform)flake.RenderTransform).BeginAnimation(TranslateTransform.XProperty, drift);
            }
        }

        private void AddRain(double width, double height)
        {
            int count = (int)Math.Clamp(width / 6, 20, 32);
            for (int i = 0; i < count; i++)
            {
                var drop = new Border { Width = 1.5, Height = 10 + _weatherRandom.NextDouble() * 7, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Color.FromArgb(205, 0xC8, 0xE8, 0xFF)), Opacity = 0.35 + _weatherRandom.NextDouble() * 0.35, RenderTransform = new TranslateTransform() };
                Canvas.SetLeft(drop, _weatherRandom.NextDouble() * width);
                Canvas.SetTop(drop, -_weatherRandom.NextDouble() * height);
                WeatherFx.Children.Add(drop);
                var fall = new DoubleAnimation(-16, height + 16, TimeSpan.FromSeconds(0.72 + _weatherRandom.NextDouble() * 0.72))
                {
                    BeginTime = TimeSpan.FromSeconds(_weatherRandom.NextDouble() * 1.8), RepeatBehavior = RepeatBehavior.Forever
                };
                var drift = new DoubleAnimation(0, 12, TimeSpan.FromSeconds(0.72 + _weatherRandom.NextDouble() * 0.72)) { RepeatBehavior = RepeatBehavior.Forever };
                ((TranslateTransform)drop.RenderTransform).BeginAnimation(TranslateTransform.YProperty, fall);
                ((TranslateTransform)drop.RenderTransform).BeginAnimation(TranslateTransform.XProperty, drift);
            }
        }

        private void AddMist(double width, double height)
        {
            for (int i = 0; i < 4; i++)
            {
                var haze = new Border { Width = width * 0.72, Height = 12, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(45, 0xE1, 0xE9, 0xF0)), RenderTransform = new TranslateTransform() };
                Canvas.SetLeft(haze, (i % 2 == 0) ? 8 : width * 0.24);
                Canvas.SetTop(haze, height * (0.35 + i * 0.11));
                WeatherFx.Children.Add(haze);
                var drift = new DoubleAnimation(-8, 10, TimeSpan.FromSeconds(5 + i)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                ((TranslateTransform)haze.RenderTransform).BeginAnimation(TranslateTransform.XProperty, drift);
            }
        }

        /// <summary>Ночью в ясную погоду — мерцающие звёзды.</summary>
        private void AddStars(double width, double height)
        {
            for (int i = 0; i < 14; i++)
            {
                double size = 1.2 + _weatherRandom.NextDouble() * 1.6;
                var star = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Opacity = 0.2 };
                Canvas.SetLeft(star, _weatherRandom.NextDouble() * width);
                Canvas.SetTop(star, _weatherRandom.NextDouble() * height * 0.55);
                WeatherFx.Children.Add(star);
                star.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 0.85, TimeSpan.FromSeconds(1.2 + _weatherRandom.NextDouble() * 2))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(_weatherRandom.NextDouble() * 2) });
            }
        }

        /// <summary>Гроза: всё небо коротко вспыхивает, раз в несколько секунд двойной вспышкой.</summary>
        private void AddLightning(double width, double height)
        {
            var flash = new Rectangle { Width = width, Height = height, Fill = Brushes.White, Opacity = 0 };
            WeatherFx.Children.Add(flash);
            var keys = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(5.5), RepeatBehavior = RepeatBehavior.Forever };
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.0))));
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.05, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.08))));
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.16))));
            keys.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))));
            flash.BeginAnimation(OpacityProperty, keys);
        }

        /// <summary>Сильный ветер: по карточке проносятся светлые штрихи.</summary>
        private void AddWind(double width, double height)
        {
            for (int i = 0; i < 5; i++)
            {
                var gust = new Border { Width = 26 + _weatherRandom.NextDouble() * 30, Height = 1.6, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Color.FromArgb(90, 0xFF, 0xFF, 0xFF)), RenderTransform = new TranslateTransform() };
                Canvas.SetLeft(gust, -60);
                Canvas.SetTop(gust, height * (0.2 + _weatherRandom.NextDouble() * 0.6));
                WeatherFx.Children.Add(gust);
                ((TranslateTransform)gust.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, width + 120, TimeSpan.FromSeconds(1.1 + _weatherRandom.NextDouble() * 0.8))
                    { BeginTime = TimeSpan.FromSeconds(_weatherRandom.NextDouble() * 2.5), RepeatBehavior = RepeatBehavior.Forever });
            }
        }

        private static bool IsSnow(int c) => c is >= 71 and <= 77 or 85 or 86;
        private static bool IsRain(int c) => c is >= 51 and <= 67 or >= 80 and <= 82;
        private static bool IsThunder(int c) => c >= 95;
        private static bool IsCloudy(int c) => c is 1 or 2 or 3 or 45 or 48;

        private static (Color top, Color bottom) Palette(int code, bool day) => code switch
        {
            0 when day => (Color.FromRgb(0x24, 0x4B, 0x78), Color.FromRgb(0x40, 0x78, 0x99)),
            0 => (Color.FromRgb(0x16, 0x1D, 0x38), Color.FromRgb(0x34, 0x35, 0x60)),
            >= 71 and <= 77 or 85 or 86 => (Color.FromRgb(0x26, 0x3B, 0x59), Color.FromRgb(0x4C, 0x68, 0x7C)),
            >= 95 => (Color.FromRgb(0x25, 0x25, 0x48), Color.FromRgb(0x40, 0x43, 0x67)),
            >= 51 and <= 67 or >= 80 and <= 82 => (Color.FromRgb(0x1A, 0x2C, 0x45), Color.FromRgb(0x35, 0x50, 0x69)),
            45 or 48 => (Color.FromRgb(0x36, 0x40, 0x4A), Color.FromRgb(0x57, 0x64, 0x6E)),
            1 or 2 or 3 => (Color.FromRgb(0x2C, 0x3D, 0x55), Color.FromRgb(0x4A, 0x5A, 0x68)),
            _ => (Color.FromRgb(0x24, 0x32, 0x46), Color.FromRgb(0x3B, 0x4B, 0x5C))
        };

        private async void OnRefresh(object s, RoutedEventArgs e)
        {
            Desc.Text = "Обновляю…";
            await AppHost.Window.RefreshWeather();
            Render();
        }
    }
}
