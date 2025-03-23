using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OwnaAvalonia.Views.Converters
{
    public class TimeSpanMsConverter : IValueConverter
    {

        public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            if (value is not TimeSpan ts)
            {
                return null;
            }

            return ts.TotalMilliseconds;
        }

        public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        {
            if (value is not double d)
            {
                return null;
            }

            return TimeSpan.FromMilliseconds(d >= 0 ? d : 0);
        }
    }
}
