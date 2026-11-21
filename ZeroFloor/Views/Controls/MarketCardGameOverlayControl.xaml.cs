using System.Windows;
using System.Windows.Controls;

namespace ZeroFloor.Views.Controls;

public partial class MarketCardGameOverlayControl : UserControl
{
    public MarketCardGameOverlayControl() => InitializeComponent();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Focus();
    }
}
