using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZeroFloor.ViewModels;

namespace ZeroFloor.Views;

public partial class MainMenuView : UserControl
{
    public MainMenuView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainMenuViewModel viewModel &&
            viewModel.Assets.ResolveBackgroundImage("main_menu") is { } image)
        {
            RootGrid.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            FallbackTitle.Visibility = Visibility.Collapsed;
        }

        FocusManager.SetFocusedElement(this, NewGameButton);
        NewGameButton.Focus();
    }
}
