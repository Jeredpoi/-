using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Isle.Services;

namespace Isle.Views
{
    public partial class WeatherView : UserControl, IIslandView
    {
        public WeatherView()
        {
            InitializeComponent();
            AppHost.WeatherChanged += Render;
            Render();
        }

        public void OnShown() => Render();

        private void Render()
        {
            var w = AppHost.Weather;
            if (w == null)
            {
                City.Text = AppSettings.Current.City;
                Desc.Text = "Загружаю погоду…";
                return;
            }
            City.Text = w.City;
            Glyph.Text = w.Glyph;
            Temp.Text = Math.Round(w.Temp) + "°";
            Desc.Text = w.Description;
            Feels.Text = "Ощущается как " + Math.Round(w.Feels) + "° · " + Math.Round(w.TodayMin) + "°…" + Math.Round(w.TodayMax) + "°";
            Wind.Text = w.Wind.ToString("0.#", CultureInfo.CurrentCulture) + " м/с " + w.WindFrom;
            Hum.Text = w.Humidity + "%";
            Prec.Text = w.Precip.ToString("0.#", CultureInfo.CurrentCulture) + " мм";
            Sun.Text = "Восход " + w.Sunrise.ToString("HH:mm") + " · закат " + w.Sunset.ToString("HH:mm");
            Hours.ItemsSource = w.Hours;
            Days.ItemsSource = w.Days;
        }

        private async void OnRefresh(object s, RoutedEventArgs e)
        {
            Desc.Text = "Обновляю…";
            await AppHost.Window.RefreshWeather();
            Render();
        }
    }
}
