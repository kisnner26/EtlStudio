using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace EtlStudio.UI
{
    /// <summary>Sistema visual inspirado en Apple: fondo gris claro, tarjetas blancas redondeadas, azul de acento, tipografia limpia.</summary>
    public static class Theme
    {
        public static readonly Color Bg = Color.FromRgb(0xF5, 0xF5, 0xF7);
        public static readonly Color Ink = Color.FromRgb(0x1D, 0x1D, 0x1F);
        public static readonly Color Muted = Color.FromRgb(0x6E, 0x6E, 0x73);
        public static readonly Color Line = Color.FromRgb(0xD2, 0xD2, 0xD7);
        public static readonly Color Blue = Color.FromRgb(0x00, 0x71, 0xE3);
        public static readonly Color Green = Color.FromRgb(0x1D, 0x9B, 0x4F);
        public static readonly Color Red = Color.FromRgb(0xD7, 0x00, 0x15);

        public static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily FontDisplay = new FontFamily("Segoe UI Variable Display, Segoe UI Semibold, Segoe UI");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static Border Card(UIElement content, double pad = 20)
        {
            return new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(pad), Margin = new Thickness(0, 0, 0, 14), Child = content,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 2, Direction = 270, Opacity = 0.07, Color = Colors.Black }
            };
        }

        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='Button'>
    <Setter Property='Foreground' Value='White'/><Setter Property='Background' Value='#0071E3'/>
    <Setter Property='FontSize' Value='13'/><Setter Property='FontWeight' Value='SemiBold'/><Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Padding' Value='16,8'/><Setter Property='Margin' Value='0,0,8,0'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='{TemplateBinding Background}' CornerRadius='10' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.88'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.7'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='Secondary' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='#E8E8ED'/><Setter Property='Foreground' Value='#1D1D1F'/>
  </Style>
  <Style x:Key='Big' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='FontSize' Value='15'/><Setter Property='Padding' Value='26,12'/>
  </Style>
  <Style TargetType='TextBox'>
    <Setter Property='Padding' Value='10,8'/><Setter Property='FontSize' Value='13'/><Setter Property='Foreground' Value='#1D1D1F'/>
    <Setter Property='Background' Value='White'/><Setter Property='BorderBrush' Value='#D2D2D7'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='TextBox'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='10'>
          <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='#0071E3'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='PasswordBox'>
    <Setter Property='Padding' Value='10,8'/><Setter Property='FontSize' Value='13'/><Setter Property='BorderBrush' Value='#D2D2D7'/>
  </Style>
  <Style TargetType='ComboBox'><Setter Property='Padding' Value='8,6'/><Setter Property='FontSize' Value='13'/><Setter Property='MinHeight' Value='34'/></Style>
  <Style TargetType='CheckBox'><Setter Property='FontSize' Value='13'/><Setter Property='Margin' Value='0,4,16,4'/><Setter Property='VerticalContentAlignment' Value='Center'/></Style>
  <Style TargetType='DataGridColumnHeader'>
    <Setter Property='Background' Value='#F5F5F7'/><Setter Property='Foreground' Value='#6E6E73'/><Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Padding' Value='10,7'/><Setter Property='BorderThickness' Value='0'/>
  </Style>
  <Style TargetType='DataGrid'>
    <Setter Property='Background' Value='White'/><Setter Property='BorderThickness' Value='0'/><Setter Property='GridLinesVisibility' Value='Horizontal'/>
    <Setter Property='HorizontalGridLinesBrush' Value='#ECECF0'/><Setter Property='RowHeight' Value='30'/><Setter Property='HeadersVisibility' Value='Column'/>
    <Setter Property='AutoGenerateColumns' Value='True'/><Setter Property='IsReadOnly' Value='True'/><Setter Property='CanUserAddRows' Value='False'/>
  </Style>
  <Style x:Key='Nav' TargetType='RadioButton'>
    <Setter Property='FontSize' Value='13.5'/><Setter Property='Foreground' Value='#1D1D1F'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Margin' Value='0,2'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RadioButton'>
        <Border x:Name='b' Background='Transparent' CornerRadius='9' Padding='12,9'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#E4E4EA'/></Trigger>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='Background' Value='#0071E3'/><Setter Property='Foreground' Value='White'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</ResourceDictionary>";

        public static void Apply(Window w)
        {
            w.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml));
            w.Background = B(Bg); w.FontFamily = Font; w.Foreground = B(Ink);
        }

        public static Style Style(Window w, string key) { return (Style)w.Resources[key]; }
    }
}
