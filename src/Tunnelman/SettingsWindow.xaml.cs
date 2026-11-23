using System.Windows;
using System.Windows.Input;

namespace Tunnelman;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        foreach (var choice in UiSettings.AutoLockChoices)
            IdleBox.Items.Add(choice.Label);
        var seconds = UiSettings.NormalizeAutoLock(UiSettings.Current.AutoLockSeconds);
        var index = Array.FindIndex(UiSettings.AutoLockChoices, choice => choice.Seconds == seconds);
        IdleBox.SelectedIndex = index < 0 ? 0 : index;
        TrayLockBox.IsChecked = UiSettings.Current.LockWhenHidden;
        StopDialBox.IsChecked = UiSettings.Current.StopDialingWhenLocked;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void OnTrayLabel(object sender, MouseButtonEventArgs e) =>
        TrayLockBox.IsChecked = TrayLockBox.IsChecked != true;

    private void OnStopDialLabel(object sender, MouseButtonEventArgs e) =>
        StopDialBox.IsChecked = StopDialBox.IsChecked != true;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var index = IdleBox.SelectedIndex < 0 ? 0 : IdleBox.SelectedIndex;
        UiSettings.Current.ApplyLockSettings(
            UiSettings.AutoLockChoices[index].Seconds,
            TrayLockBox.IsChecked == true,
            StopDialBox.IsChecked == true);
        DialogResult = true;
    }
}
