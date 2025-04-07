using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

using OwnaAvalonia.Models;

namespace OwnaAvalonia.Views.Converters
{
    public class LogColorConverter : IValueConverter
    {
    #nullable disable
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Log log)
            {
                return null;
            }

            return log.Type switch
            {
                Log.LogType.Error => Brush.Parse("#fc5b5b"),
                Log.LogType.Warning => Brush.Parse("#f5d664"),
                _ => Brush.Parse("#ffffff")
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    #nullable restore
    }
}
