using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MoveEpicGamesGames.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
