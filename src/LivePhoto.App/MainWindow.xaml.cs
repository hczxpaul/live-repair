using System.IO;
using System.Windows;

namespace LivePhoto.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        Drop += OnDrop;
        DragEnter += OnDragEnter;
        Loaded += async (_, _) => await OpenFolderAsync(App.StartupFolder).ConfigureAwait(true);
    }

    private async Task OpenFolderAsync(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        _vm.SourceFolder = dir;
        if (string.IsNullOrEmpty(_vm.OutputFolder))
        {
            _vm.OutputFolder = SuggestOutput(dir);
        }

        await _vm.EnumerateAndProbeAsync().ConfigureAwait(true);
    }

    private static void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        string p = paths[0];
        string dir = File.Exists(p) ? Path.GetDirectoryName(p) ?? p : p;

        _vm.OutputFolder = SuggestOutput(dir);
        await OpenFolderAsync(dir).ConfigureAwait(true);
    }

    private static string SuggestOutput(string source)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(source);
        string parent = Path.GetDirectoryName(trimmed) ?? source;
        return Path.Combine(parent, Path.GetFileName(trimmed) + "_已修复");
    }
}
