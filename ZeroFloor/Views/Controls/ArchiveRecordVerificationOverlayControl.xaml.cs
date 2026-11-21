using System.Windows;
using System.Windows.Controls;

namespace ZeroFloor.Views.Controls;

public partial class ArchiveRecordVerificationOverlayControl : UserControl
{
    public ArchiveRecordVerificationOverlayControl() => InitializeComponent();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Focus();
    }
}
