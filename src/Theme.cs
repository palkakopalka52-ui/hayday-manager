using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FarmDesk
{
    internal static class Theme
    {
        public static readonly Brush Canvas = Brush("#F1F2EE");
        public static readonly Brush Paper = Brush("#FFFFFF");
        public static readonly Brush Ink = Brush("#171A17");
        public static readonly Brush Soft = Brush("#626861");
        public static readonly Brush Line = Brush("#DADFD7");
        public static readonly Brush Green = Brush("#17613B");
        public static readonly Brush GreenLight = Brush("#E0F2E3");
        public static readonly Brush Red = Brush("#A63138");
        public static readonly Brush RedLight = Brush("#F9E7E8");
        public static readonly Brush Dark = Brush("#1A201A");
        public static readonly Brush Shade = Brush("#E7EAE4");

        private static readonly ControlTemplate RoundedButton = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
            "TargetType='Button'><Border x:Name='surface' CornerRadius='9' BorderThickness='{TemplateBinding BorderThickness}' " +
            "BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='False'/>" +
            "</Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
            "<Setter TargetName='surface' Property='Opacity' Value='0.83'/></Trigger>" +
            "<Trigger Property='IsPressed' Value='True'><Setter TargetName='surface' Property='Opacity' Value='0.64'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='surface' Property='Opacity' Value='0.42'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>");

        public static Brush Brush(string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string value, double size, Brush color, bool bold = false)
        {
            return new TextBlock
            {
                Text = value, FontSize = size, Foreground = color,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static Button Button(string text, Brush background, Brush foreground, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text, Background = background, Foreground = foreground,
                Padding = new Thickness(13, 8, 13, 8), MinHeight = 34,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Template = RoundedButton, Margin = new Thickness(0, 0, 7, 0)
            };
            if (click != null) button.Click += click;
            return button;
        }

        public static Border Card(UIElement content, Brush background, double radius, double padding)
        {
            return new Border
            {
                Background = background, CornerRadius = new CornerRadius(radius),
                Padding = new Thickness(padding), Child = content
            };
        }

        public static UIElement Gap(double height) { return new Border { Height = height }; }

        public static BitmapSource Image(System.Drawing.Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
    }
}
