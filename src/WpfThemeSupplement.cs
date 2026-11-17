using System.Collections;
using System.Windows;
using System.Windows.Markup;

// Retained WPF templates only: no HWND paint hooks or hosted WinForms controls.
internal static class WpfThemeSupplement
{
    internal static void Apply(Window window)
    {
        var resources=(ResourceDictionary)XamlReader.Parse(Templates);
        foreach(DictionaryEntry item in resources)window.Resources[item.Key]=item.Value;
    }
    const string Templates=@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='{x:Type CheckBox}'>
  <Setter Property='Foreground' Value='#E4EDF0'/><Setter Property='Margin' Value='0,7,14,7'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type CheckBox}'>
   <Grid Background='Transparent'><Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
    <Border x:Name='Box' Width='16' Height='16' CornerRadius='2' Background='#17232C' BorderBrush='#617B88' BorderThickness='1' VerticalAlignment='Center'>
     <Path x:Name='Tick' Data='M 2,7 L 6,11 L 12,2' Stroke='#42D1B7' StrokeThickness='2' Visibility='Collapsed' Margin='1'/>
    </Border>
    <ContentPresenter Grid.Column='1' Margin='7,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>
   </Grid>
   <ControlTemplate.Triggers>
    <Trigger Property='IsChecked' Value='True'><Setter TargetName='Tick' Property='Visibility' Value='Visible'/></Trigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#42D1B7'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='#42D1B7'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='{x:Type TabControl}'>
  <Setter Property='Background' Value='#0E161D'/><Setter Property='BorderBrush' Value='#314550'/><Setter Property='Padding' Value='12'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabControl}'>
   <Grid KeyboardNavigation.TabNavigation='Local'><Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/></Grid.RowDefinitions>
    <TabPanel IsItemsHost='True' Margin='0,0,0,0' Panel.ZIndex='1' KeyboardNavigation.TabIndex='1'/>
    <Border Grid.Row='1' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' Padding='{TemplateBinding Padding}'>
     <ContentPresenter x:Name='PART_SelectedContentHost' ContentSource='SelectedContent' KeyboardNavigation.TabNavigation='Local'/>
    </Border>
   </Grid>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='{x:Type TabItem}'>
  <Setter Property='Foreground' Value='#9BB2BB'/><Setter Property='Background' Value='#0E161D'/><Setter Property='Padding' Value='18,10'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabItem}'>
   <Border x:Name='Tab' Background='{TemplateBinding Background}' BorderBrush='#314550' BorderThickness='0' Padding='{TemplateBinding Padding}' Margin='0,0,3,0'>
    <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' HorizontalAlignment='Center' VerticalAlignment='Center'/>
   </Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Tab' Property='Background' Value='#1F2F3A'/></Trigger>
    <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='#42D1B7'/><Setter TargetName='Tab' Property='Background' Value='#17232C'/><Setter TargetName='Tab' Property='BorderThickness' Value='1,1,1,0'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='{x:Type ScrollBar}'>
  <Setter Property='Background' Value='#0E161D'/><Setter Property='Width' Value='14'/><Setter Property='MinWidth' Value='0'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
   <Grid Background='{TemplateBinding Background}'>
    <Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
     <Track.DecreaseRepeatButton><RepeatButton x:Name='Decrease' Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
     <Track.Thumb><Thumb Background='#506B78' Focusable='False'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'><Border Background='{TemplateBinding Background}' CornerRadius='4' Margin='3' MinWidth='6' MinHeight='6'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#42D1B7'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter Property='Background' Value='#42D1B7'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
     <Track.IncreaseRepeatButton><RepeatButton x:Name='Increase' Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
    </Track>
   </Grid>
   <ControlTemplate.Triggers>
    <Trigger Property='Orientation' Value='Horizontal'><Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='14'/><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/><Setter TargetName='Decrease' Property='Command' Value='{x:Static ScrollBar.PageLeftCommand}'/><Setter TargetName='Increase' Property='Command' Value='{x:Static ScrollBar.PageRightCommand}'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='{x:Type ListBox}'>
  <Setter Property='Background' Value='#17232C'/><Setter Property='Foreground' Value='#E4EDF0'/><Setter Property='BorderBrush' Value='#314550'/><Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Auto'/>
 </Style>
 <Style TargetType='{x:Type ListBoxItem}'>
  <Setter Property='Padding' Value='6,4'/><Setter Property='Foreground' Value='#E4EDF0'/><Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ListBoxItem}'><Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#1A4A49'/></Trigger><Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#1F2F3A'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='{x:Type DataGridRow}'>
  <Setter Property='Foreground' Value='#E4EDF0'/><Setter Property='Background' Value='#17232C'/><Setter Property='BorderBrush' Value='#314550'/>
  <Style.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#1F2F3A'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#1A4A49'/></Trigger></Style.Triggers>
 </Style>
</ResourceDictionary>";
}
