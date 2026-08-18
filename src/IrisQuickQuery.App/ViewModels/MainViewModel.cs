using System.IO;

namespace IrisQuickQuery.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private object? _currentPage;
    private string _currentTitle = "快捷查询";
    private string _currentSection = "query";
    public QueryViewModel Query { get; }
    public DraftConfigurationViewModel Configuration { get; }
    public ConnectionViewModel Connection { get; }
    public AboutViewModel About { get; }
    public object? CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
    public string CurrentTitle { get => _currentTitle; private set => Set(ref _currentTitle, value); }
    public string CurrentSection { get => _currentSection; private set => Set(ref _currentSection, value); }
    public RelayCommand NavigateCommand { get; }

    public MainViewModel(ApplicationServices services)
    {
        Query = new QueryViewModel(services);
        Configuration = new DraftConfigurationViewModel(services);
        Connection = new ConnectionViewModel(services);
        About = new AboutViewModel(services.Paths.RootDirectory, services.Paths.LogDirectory);
        NavigateCommand = new RelayCommand(async parameter => await NavigateAsync(parameter?.ToString() ?? "query"));
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await Configuration.LoadCurrentAsync(); await Connection.LoadAsync(); await Query.LoadAsync();
        CurrentPage = Query;
    }

    private async Task NavigateAsync(string page)
    {
        switch (page)
        {
            case "query": await Query.LoadAsync(); CurrentPage = Query; CurrentTitle = "快捷查询"; CurrentSection = page; break;
            case "elements": CurrentPage = new ElementConfigurationPageViewModel(Configuration); CurrentTitle = "元素配置"; CurrentSection = page; break;
            case "rules": CurrentPage = new RuleConfigurationPageViewModel(Configuration); CurrentTitle = "SQL 规则"; CurrentSection = page; break;
            case "connection": await Connection.LoadAsync(); CurrentPage = Connection; CurrentTitle = "连接设置"; CurrentSection = page; break;
            case "about": CurrentPage = About; CurrentTitle = "关于"; CurrentSection = page; break;
        }
    }
}

public sealed record ElementConfigurationPageViewModel(DraftConfigurationViewModel Session);
public sealed record RuleConfigurationPageViewModel(DraftConfigurationViewModel Session);

public sealed class AboutViewModel
{
    public string DataPath { get; }
    public string LogPath { get; }
    public string Version => typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public RelayCommand OpenLogFolderCommand { get; }

    public AboutViewModel(string dataPath, string logPath)
    {
        DataPath = dataPath;
        LogPath = logPath;
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LogPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{LogPath}\"") { UseShellExecute = true });
    }
}
