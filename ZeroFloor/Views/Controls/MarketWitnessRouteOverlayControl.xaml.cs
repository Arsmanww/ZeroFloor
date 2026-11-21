using System.Windows;
using System.Windows.Controls;

namespace ZeroFloor.Views.Controls;

public partial class MarketWitnessRouteOverlayControl : UserControl
{
    public MarketWitnessRouteOverlayControl() => InitializeComponent();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Focus();
    }
}
