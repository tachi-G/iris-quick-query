using System.Collections.ObjectModel;
using IrisQuickQuery.Infrastructure.Database;

namespace IrisQuickQuery.App.ViewModels;

public sealed class ConnectionViewModel : ObservableObject
{
    private readonly ApplicationServices _services;
    private string _driverName = "InterSystems IRIS ODBC35";
    private string _dsn = string.Empty;
    private string _host = string.Empty;
    private string _namespace = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _tlsConfigurationName = string.Empty;
    private int _port = 1972;
    private bool _savePassword = true;
    private bool _hasSavedPassword;
    private bool _isPasswordPlaceholderVisible;
    private string _environmentStatus = string.Empty;
    private string _notificationMessage = string.Empty;
    private bool _isNotificationVisible;
    private int _notificationVersion;

    public ObservableCollection<string> AvailableDrivers { get; } = [];
    public ObservableCollection<string> AvailableDsns { get; } = [];
    public string DriverName { get => _driverName; set => Set(ref _driverName, value); }
    public string Dsn { get => _dsn; set => Set(ref _dsn, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string Namespace { get => _namespace; set => Set(ref _namespace, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string TlsConfigurationName { get => _tlsConfigurationName; set => Set(ref _tlsConfigurationName, value); }
    public bool SavePassword { get => _savePassword; set => Set(ref _savePassword, value); }
    public bool HasSavedPassword { get => _hasSavedPassword; set => Set(ref _hasSavedPassword, value); }
    public bool IsPasswordPlaceholderVisible { get => _isPasswordPlaceholderVisible; set => Set(ref _isPasswordPlaceholderVisible, value); }
    public string EnvironmentStatus { get => _environmentStatus; set => Set(ref _environmentStatus, value); }
    public string NotificationMessage { get => _notificationMessage; set => Set(ref _notificationMessage, value); }
    public bool IsNotificationVisible { get => _isNotificationVisible; set => Set(ref _isNotificationVisible, value); }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public RelayCommand RescanCommand { get; }

    public ConnectionViewModel(ApplicationServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        TestCommand = new AsyncRelayCommand(TestAsync);
        RescanCommand = new RelayCommand(ScanEnvironment);
    }

    public async Task LoadAsync()
    {
        var profile = await _services.Profiles.GetAsync();
        DriverName = profile.DriverName; Dsn = profile.Dsn ?? string.Empty; Host = profile.Host; Port = profile.Port;
        Namespace = profile.Namespace; Username = profile.Username; TlsConfigurationName = profile.TlsConfigurationName ?? string.Empty;
        SavePassword = profile.SavePassword;
        Password = await _services.Credentials.GetAsync() ?? string.Empty;
        HasSavedPassword = SavePassword && !string.IsNullOrEmpty(Password);
        IsPasswordPlaceholderVisible = HasSavedPassword;
        ScanEnvironment();
    }

    private void ScanEnvironment()
    {
        var report = OdbcEnvironmentInspector.Inspect();
        AvailableDrivers.Clear(); foreach (var item in report.Drivers.Where(x => x.Is64Bit).Select(x => x.Name)) AvailableDrivers.Add(item);
        AvailableDsns.Clear(); foreach (var item in report.DataSources.Where(x => x.Is64Bit).Select(x => x.Name)) AvailableDsns.Add(item);
        EnvironmentStatus = report.Has64BitIrisDriver
            ? "已检测到 64 位 IRIS ODBC 驱动。TLS 状态只有在驱动可证明协商结果时才会标记为已加密。"
            : "未检测到 64 位 InterSystems IRIS ODBC35。请由医院 IT 安装官方 x64 驱动后重新扫描。";
    }

    private bool TryBuildProfile(out ConnectionProfile profile, out string error)
    {
        var driver = DriverName?.Trim() ?? string.Empty;
        var dsn = string.IsNullOrWhiteSpace(Dsn) ? null : Dsn.Trim();
        var host = Host?.Trim() ?? string.Empty;
        var irisNamespace = Namespace?.Trim() ?? string.Empty;
        var username = Username?.Trim() ?? string.Empty;
        if (dsn is null && string.IsNullOrWhiteSpace(driver)) { profile = new(); error = "请选择 64 位 ODBC 驱动，或填写现有 DSN。"; return false; }
        if (dsn is null && string.IsNullOrWhiteSpace(host)) { profile = new(); error = "使用无 DSN 连接时必须填写服务器地址。"; return false; }
        if (dsn is null && string.IsNullOrWhiteSpace(irisNamespace)) { profile = new(); error = "使用无 DSN 连接时必须填写 Namespace。"; return false; }
        if (Port is < 1 or > 65535) { profile = new(); error = "端口必须在 1–65535 之间。"; return false; }
        if (string.IsNullOrWhiteSpace(username)) { profile = new(); error = "请填写只读用户名。"; return false; }
        profile = new ConnectionProfile
        {
            DriverName = driver, Dsn = dsn, Host = host, Port = Port, Namespace = irisNamespace,
            Username = username,
            TlsConfigurationName = string.IsNullOrWhiteSpace(TlsConfigurationName) ? null : TlsConfigurationName.Trim(),
            SavePassword = SavePassword
        };
        error = string.Empty;
        return true;
    }

    public async Task SaveAsync()
    {
        try
        {
            if (!TryBuildProfile(out var profile, out var error)) { ShowNotification(error); return; }
            await _services.Profiles.SaveAsync(profile);
            await _services.Credentials.SaveAsync(SavePassword ? Password : null);
            HasSavedPassword = SavePassword && !string.IsNullOrEmpty(Password);
            IsPasswordPlaceholderVisible = HasSavedPassword;
            ShowNotification("连接配置已保存；密码仅由当前 Windows 用户的 DPAPI 保护。");
        }
        catch (Exception ex) { ShowNotification("保存连接失败：" + ex.Message); }
    }

    public void BeginPasswordEdit() => IsPasswordPlaceholderVisible = false;

    private async Task TestAsync()
    {
        if (!TryBuildProfile(out var profile, out var validationError)) { ShowNotification(validationError); return; }
        try { await _services.QueryExecutor.TestConnectionAsync(profile, Password); ShowNotification("连接成功。TLS 是否实际协商仍以 IRIS/驱动可验证状态为准。"); }
        catch (Exception ex) { ShowNotification("连接失败：" + ex.Message); }
    }

    private void ShowNotification(string message)
    {
        NotificationMessage = message;
        IsNotificationVisible = true;
        var version = ++_notificationVersion;
        _ = HideNotificationAsync(version);
    }

    private async Task HideNotificationAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (version == _notificationVersion) IsNotificationVisible = false;
    }
}
