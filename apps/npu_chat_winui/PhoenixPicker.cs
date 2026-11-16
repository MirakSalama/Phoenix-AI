using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NpuChat;

public sealed class PhoenixPicker : ComboBox
{
    public PhoenixPicker()
    {
        DefaultStyleKey = typeof(ComboBox);
        Style = (Style)Application.Current.Resources["PhoenixPickerStyle"];
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // The popup lives in its own visual tree. Apply the shared frame to the native template part.
        if (GetTemplateChild("PopupBorder") is Border border)
        {
            var resources = Application.Current.Resources;
            border.Background = (Brush)resources["PhoenixPopupFill"];
            border.BorderBrush = (Brush)resources["PhoenixPopupOutline"];
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(8);
        }
    }
}
