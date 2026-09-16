using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Tunnelman.Tunnels;

namespace Tunnelman;

public partial class TransferWindow : Window
{
    private readonly bool _export;
    private readonly List<TransferRow> _rows;
    private readonly string? _sourcePath;
    private readonly Func<IReadOnlyList<TunnelRecord>, string?>? _import;
    private bool _syncingAll;
    private bool _committing;
    private int _lastToggled = -1;

    public TransferWindow()
    {
        InitializeComponent();
        _rows = [];
    }

    internal TransferWindow(
        bool export,
        IReadOnlyList<TransferEntry> entries,
        string? sourcePath,
        Func<IReadOnlyList<TunnelRecord>, string?>? import)
        : this()
    {
        _export = export;
        _sourcePath = sourcePath;
        _import = import;
        _rows = entries.Select(e => new TransferRow(e)).ToList();
        foreach (var row in _rows)
            row.PropertyChanged += OnRowPropertyChanged;

        Title = export ? "Export tunnels" : "Import tunnels";
        HeadingText.Text = Title;
        BodyText.Text = export
            ? "Choose what to write into one file."
            : "Choose what to add. New tunnels stay off until you enable them.";
        PrimaryGlyph.Text = export ? "\uEDE1" : "\uE8B5";
        if (!export && !string.IsNullOrWhiteSpace(sourcePath))
        {
            FileNameText.Text = Path.GetFileName(sourcePath);
            FileNameText.ToolTip = sourcePath;
        }

        EntryList.ItemsSource = _rows;
        SyncChrome();
    }

    public int CompletedCount { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e) => TitleBarDarkMode.Apply(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        void FocusFirst()
        {
            if (EntryList.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                return;
            EntryList.Focus();
            if (EntryList.Items.Count > 0)
            {
                EntryList.SelectedIndex = 0;
                if (EntryList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
                    item.Focus();
            }
        }

        if (EntryList.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            Dispatcher.BeginInvoke(FocusFirst, DispatcherPriority.Loaded);
        else
            EntryList.ItemContainerGenerator.StatusChanged += (_, _) => FocusFirst();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferRow.IsChecked))
            SyncChrome();
    }

    private void OnAllPreview(object sender, MouseButtonEventArgs e)
    {
        if (_syncingAll)
            return;
        e.Handled = true;
        ToggleAll();
    }

    private void OnAllKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Space)
            return;
        e.Handled = true;
        ToggleAll();
    }

    private void ToggleAll()
    {
        var selectable = Selectable().ToList();
        if (selectable.Count == 0)
            return;
        var allOn = selectable.All(r => r.IsChecked);
        foreach (var row in selectable)
            row.IsChecked = !allOn;
        SyncChrome();
    }

    private void OnRowPreview(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.OriginalSource is not DependencyObject source)
            return;
        if (FindAncestor<System.Windows.Controls.CheckBox>(source) is not null)
            return;
        if (EntryList.ContainerFromElement(source) is not ListBoxItem container)
            return;
        if (container.DataContext is not TransferRow row || !row.CanSelect)
            return;

        var index = _rows.IndexOf(row);
        var next = !row.IsChecked;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && _lastToggled >= 0)
        {
            var from = Math.Min(_lastToggled, index);
            var to = Math.Max(_lastToggled, index);
            for (var i = from; i <= to; i++)
            {
                if (_rows[i].CanSelect)
                    _rows[i].IsChecked = next;
            }
        }
        else
        {
            row.IsChecked = next;
        }

        _lastToggled = index;
        SyncChrome();
    }

    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            if (EntryList.SelectedItem is TransferRow row && row.CanSelect)
            {
                row.IsChecked = !row.IsChecked;
                _lastToggled = _rows.IndexOf(row);
                SyncChrome();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            foreach (var row in Selectable())
                row.IsChecked = true;
            SyncChrome();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            foreach (var row in Selectable())
                row.IsChecked = false;
            SyncChrome();
            e.Handled = true;
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_committing)
            return;
        _committing = true;
        var wasDefault = PrimaryButton.IsDefault;
        PrimaryButton.IsDefault = false;
        try
        {
            var selected = _rows.Where(r => r.CanSelect && r.IsChecked).Select(r => r.Entry.Record).ToList();
            if (selected.Count == 0)
                return;
            if (_export)
            {
                if (!TryExport(selected))
                    return;
            }
            else if (_import is not null)
            {
                var error = _import(selected);
                if (error is not null)
                {
                    MessageBox.Show(this, error, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            CompletedCount = selected.Count;
            DialogResult = true;
        }
        finally
        {
            PrimaryButton.IsDefault = wasDefault;
            _committing = false;
        }
    }

    private bool TryExport(IReadOnlyList<TunnelRecord> selected)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export tunnels",
            Filter = TunnelTransfer.FileFilter,
            FileName = "tunnels.tunnelman.json",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
            return false;

        try
        {
            TunnelTransfer.Write(TunnelTransfer.NormalizeExportPath(dialog.FileName), selected);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, TunnelTransfer.CouldNotWrite, "Tunnelman", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void SyncChrome()
    {
        var selectable = Selectable().ToList();
        var checkedCount = selectable.Count(r => r.IsChecked);
        CountText.Text = TunnelTransfer.SelectedLabel(checkedCount);
        PrimaryLabel.Text = TunnelTransfer.PrimaryLabel(_export, checkedCount);
        PrimaryButton.IsEnabled = checkedCount > 0;

        _syncingAll = true;
        try
        {
            if (selectable.Count == 0)
            {
                AllBox.IsEnabled = false;
                AllBox.IsChecked = false;
            }
            else
            {
                AllBox.IsEnabled = true;
                AllBox.IsChecked = checkedCount == 0
                    ? false
                    : checkedCount == selectable.Count ? true : null;
            }
        }
        finally
        {
            _syncingAll = false;
        }
    }

    private IEnumerable<TransferRow> Selectable() => _rows.Where(r => r.CanSelect);

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}
