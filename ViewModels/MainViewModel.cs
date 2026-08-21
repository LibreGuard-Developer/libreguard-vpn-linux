using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private enum PreAuthDeviceRemovalMode
    {
        None,
        Password,
        OAuthCode
    }

    private const string FavoriteServersKey = "favorite-server-ids";
    private const string RecentServersKey = "recent-server-ids";
    private const string ServerSearchKey = "server-search";
    private const string ServerSortKey = "server-sort";
    private const string NotificationsEnabledKey = "notifications-enabled";
    private const long FreePlanBytesLimit = 5L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ServerLoadRetryDelay = TimeSpan.FromMilliseconds(500);
    private readonly IAuthSessionService _authSession;
    private readonly IBackendApiClient _backend;
    private readonly IDeviceIdentityService _deviceIdentity;
    private readonly IVpnConnectionService _vpn;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalStatisticsStore _localStatisticsStore;
    private readonly IThemePreferenceService _themePreferenceService;
    private readonly ICardCheckoutWindowService _cardCheckoutWindow;
    private readonly IGoogleOAuthService _googleOAuth;
    private readonly ILinuxPreflightService _preflightService;
    private readonly IServerLatencyService _serverLatencyService;
    private readonly ITunnelTrafficMonitor _tunnelTrafficMonitor;
    private readonly IFileSavePickerService _fileSavePicker;
    private readonly IClipboardService _clipboard;
    private readonly IDesktopNotificationService _desktopNotifications;
    private readonly DispatcherTimer _dashboardMetricsTimer;
    private readonly DispatcherTimer _moneroPaymentTimer;
    private readonly object _accountStateRefreshLock = new();
    private readonly string _appVersion;

    private bool _isAuthenticated;
    private bool _isInitializing = true;
    private string _authView = "Login";
    private string _currentSection = "Dashboard";
    private string _email = string.Empty;
    private string _accountEmail = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _registeredUserId = string.Empty;
    private string _resetToken = string.Empty;
    private string _newPassword = string.Empty;
    private string _oauthToken = string.Empty;
    private string _twoFactorCode = string.Empty;
    private string _recoveryCode = string.Empty;
    private string _twoFactorManagementCode = string.Empty;
    private string _twoFactorSharedKey = string.Empty;
    private string _twoFactorAuthenticatorUri = string.Empty;
    private string _recoveryCodesText = string.Empty;
    private string _statusMessage = "Welcome to LibreGuard.";
    private string _connectionMessage = "Disconnected";
    private string _selectedProtocol = "IKEv2";
    private string _billingCycle = "monthly";
    private AppThemePreference _themePreference = AppThemePreference.System;
    private string _checkoutUrl = string.Empty;
    private bool _isEmbeddedCheckoutOpen;
    private string _timeRemaining = string.Empty;
    private string _preflightSummary = "Linux VPN dependencies have not been checked yet.";
    private string _pendingLoginToken = string.Empty;
    private PreAuthDeviceRemovalMode _preAuthDeviceRemovalMode;
    private string _serverSearchText = string.Empty;
    private string _serverSortMode = "Ping";
    private string _statisticsPeriod = "Week";
    private string _statisticsTotalDataText = "0 B";
    private string _statisticsConnectionsText = "0";
    private string _statisticsAverageSessionText = "0m";
    private string _statisticsAverageDownloadText = "0 B";
    private string _statisticsTotalDownloadText = "0 B";
    private string _statisticsTotalUploadText = "0 B";
    private string _connectionDurationText = "00:00:00";
    private string _originalPublicIpText = "—";
    private string _vpnIpText = "—";
    private string _liveDownloadSpeedText = "0 B/s";
    private string _liveUploadSpeedText = "0 B/s";
    private string _sessionDataTotalText = "0 B";
    private bool _isCompactLayout;
    private bool _isRefreshingTunnelTraffic;
    private VpnConnectionState _connectionState = VpnConnectionState.Disconnected;
    private VpnServer? _selectedServer;
    private VpnServer? _connectedServer;
    private UserDevice? _selectedDevice;
    private UserCertificate? _selectedCertificate;
    private UsageQuota? _quota;
    private SubscriptionStatus? _subscription;
    private bool _twoFactorEnabled;
    private bool _twoFactorToggleEnabled;
    private bool _hasAuthenticator;
    private bool _autoConnect;
    private bool _killSwitch = true;
    private DnsPreferenceResponse? _dnsPreference;
    private bool _adBlockingEnabled;
    private bool _isApplyingAdBlockingState;
    private bool _isUpdatingAdBlocking;
    private bool _isAdBlockingProRequired;
    private bool _isRecoveringAdBlockingEntitlement;
    private CancellationTokenSource? _adBlockingUpdateCts;
    private long _accountEntitlementRevision;
    private long _dnsPreferenceRevision;
    private bool _suppressAccountRefreshOnSectionTransition;
    private bool _notificationsEnabled = true;
    private bool _showPassword;
    private bool _showConfirmPassword;
    private bool _showNewPassword;
    private bool _showRecoveryCodeEntry;
    private bool _isDeviceLimitModalVisible;
    private bool _isLoadingPayment;
    private bool _isMoneroSelected;
    private bool _isCardSelected;
    private bool _isPaymentComplete;
    private bool _isUpdatingTwoFactorToggleState;
    private decimal _shortfall;
    private Task? _accountStateRefreshTask;
    private CancellationTokenSource? _accountStateRefreshCts;
    private int _accountStateRefreshGeneration;
    private CancellationTokenSource? _emailPollingCts;
    private bool _serversLoadedFromBackend;
    private int _latencyRefreshGeneration;
    private readonly HashSet<int> _favoriteServerIds = [];
    private readonly List<int> _recentServerIds = [];
    private TwoFactorSetup? _pendingTwoFactorSetup;
    private DateTimeOffset? _connectedAt;
    private int _lastMoneroStatusRefreshMinute = -1;
    private DateTimeOffset _lastLiveStatisticsPresentationUpdate = DateTimeOffset.MinValue;
    private bool _hasRecordedLiveStatisticsSnapshot;
    private LocalStatisticsProfile _statisticsProfile = new();
    private MoneroPriceResponse? _moneroPrice;
    private MoneroInvoiceResponse? _moneroInvoice;
    private MoneroStatusResponse? _moneroStatus;
    private string _cardTransactionId = string.Empty;
    private CancellationTokenSource? _cardCheckoutCts;
    private int _cardCheckoutSessionGeneration;
    private VpnConnectionState? _lastNotificationState;

    public MainViewModel(
        IAuthSessionService authSession,
        IBackendApiClient backend,
        IDeviceIdentityService deviceIdentity,
        IVpnConnectionService vpn,
        ISettingsStore settingsStore,
        ILocalStatisticsStore localStatisticsStore,
        IThemePreferenceService themePreferenceService,
        ICardCheckoutWindowService cardCheckoutWindow,
        IGoogleOAuthService googleOAuth,
        ILinuxPreflightService preflightService,
        IServerLatencyService serverLatencyService,
        ITunnelTrafficMonitor tunnelTrafficMonitor,
        IFileSavePickerService fileSavePicker,
        IClipboardService clipboard,
        IDesktopNotificationService desktopNotifications,
        string? appVersion = null)
    {
        _authSession = authSession;
        _backend = backend;
        _deviceIdentity = deviceIdentity;
        _vpn = vpn;
        _settingsStore = settingsStore;
        _localStatisticsStore = localStatisticsStore;
        _themePreferenceService = themePreferenceService;
        _cardCheckoutWindow = cardCheckoutWindow;
        _googleOAuth = googleOAuth;
        _preflightService = preflightService;
        _serverLatencyService = serverLatencyService;
        _tunnelTrafficMonitor = tunnelTrafficMonitor;
        _fileSavePicker = fileSavePicker;
        _clipboard = clipboard;
        _desktopNotifications = desktopNotifications;
        _appVersion = appVersion ?? AppSettings.Load().AppVersion;
        _dashboardMetricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dashboardMetricsTimer.Tick += DashboardMetricsTimerOnTick;
        _moneroPaymentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _moneroPaymentTimer.Tick += MoneroPaymentTimerOnTick;

        LoginCommand = new AsyncCommand(LoginAsync);
        OAuthLoginCommand = new AsyncCommand(OAuthLoginAsync);
        RegisterCommand = new AsyncCommand(RegisterAsync);
        CheckEmailConfirmationCommand = new AsyncCommand(CheckEmailConfirmationAsync);
        ResendConfirmationCommand = new AsyncCommand(ResendConfirmationAsync);
        VerifyTwoFactorCommand = new AsyncCommand(VerifyTwoFactorAsync);
        VerifyRecoveryCodeCommand = new AsyncCommand(VerifyRecoveryCodeAsync);
        ForgotPasswordCommand = new AsyncCommand(ForgotPasswordAsync);
        ResetPasswordCommand = new AsyncCommand(ResetPasswordAsync);
        LogoutCommand = new AsyncCommand(LogoutAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ConnectCommand = new AsyncCommand(ConnectAsync, CanStartConnection);
        QuickConnectCommand = new AsyncCommand(QuickConnectAsync, CanStartConnection);
        DisconnectCommand = new AsyncCommand(DisconnectAsync);
        CancelConnectionAttemptCommand = new RelayCommand(_ => CancelConnectionAttempt());
        SetupTwoFactorCommand = new AsyncCommand(SetupTwoFactorAsync);
        EnableTwoFactorCommand = new AsyncCommand(EnableTwoFactorAsync);
        DisableTwoFactorCommand = new AsyncCommand(DisableTwoFactorAsync);
        ResetTwoFactorCommand = new AsyncCommand(ResetTwoFactorAsync);
        GenerateRecoveryCodesCommand = new AsyncCommand(GenerateRecoveryCodesAsync);
        RemoveSelectedDeviceCommand = new AsyncCommand(RemoveSelectedDeviceAsync, () => SelectedDevice is { IsActive: true, IsCurrent: false });
        DeleteSelectedDeviceCommand = new AsyncCommand(DeleteSelectedDeviceAsync, () => SelectedDevice is not null && !SelectedDevice.IsActive);
        RemoveOtherDevicesCommand = new AsyncCommand(RemoveOtherDevicesAsync);
        RemoveInactiveDevicesCommand = new AsyncCommand(RemoveInactiveDevicesAsync);
        OpenUpgradeCommand = new AsyncCommand(OpenUpgradeAsync);
        GoBackToSettingsCommand = new RelayCommand(_ => GoBackToSettings());
        SelectCardCommand = new AsyncCommand(SelectCardAsync);
        OpenCardCheckoutInBrowserCommand = new AsyncCommand(OpenCardCheckoutInBrowserAsync);
        SelectMoneroCommand = new AsyncCommand(SelectMoneroAsync);
        SwitchPaymentMethodCommand = new RelayCommand(_ => SwitchPaymentMethod());
        CheckPaymentStatusCommand = new AsyncCommand(CheckPaymentStatusAsync);
        CopyAddressCommand = new AsyncCommand(CopyAddressAsync);
        CopyAmountCommand = new AsyncCommand(CopyAmountAsync);
        RunPreflightCommand = new AsyncCommand(RunPreflightAsync);
        DownloadSelectedOpenVpnConfigCommand = new AsyncCommand(DownloadSelectedOpenVpnConfigAsync, () => SelectedServer is not null);
        DownloadSelectedConfigCommand = new AsyncParameterCommand(DownloadCertificateConfigAsync, parameter => parameter is UserCertificate && CanAccessCertificates);
        DownloadSelectedCertificateCommand = new AsyncParameterCommand(DownloadCertificateFileAsync, parameter => parameter is UserCertificate && CanAccessCertificates);
        TogglePasswordVisibilityCommand = new RelayCommand(_ => ShowPassword = !ShowPassword);
        ToggleConfirmPasswordVisibilityCommand = new RelayCommand(_ => ShowConfirmPassword = !ShowConfirmPassword);
        ToggleNewPasswordVisibilityCommand = new RelayCommand(_ => ShowNewPassword = !ShowNewPassword);
        ToggleRecoveryCodeEntryCommand = new RelayCommand(_ => ShowRecoveryCodeEntry = !ShowRecoveryCodeEntry);
        CancelLoginCommand = new RelayCommand(_ => (LoginCommand as AsyncCommand)?.Cancel());
        CancelGoogleLoginCommand = new RelayCommand(_ => (OAuthLoginCommand as AsyncCommand)?.Cancel());
        CancelRegisterCommand = new RelayCommand(_ => (RegisterCommand as AsyncCommand)?.Cancel());
        CancelForgotPasswordCommand = new RelayCommand(_ => (ForgotPasswordCommand as AsyncCommand)?.Cancel());
        CancelResetPasswordCommand = new RelayCommand(_ => (ResetPasswordCommand as AsyncCommand)?.Cancel());
        DismissDeviceLimitCommand = new RelayCommand(_ => DismissDeviceLimit());
        SelectBillingCycleCommand = new RelayCommand(parameter => BillingCycle = parameter?.ToString() ?? "monthly");
        SelectSectionCommand = new RelayCommand(parameter => CurrentSection = parameter?.ToString() ?? "Dashboard");
        SelectThemeCommand = new AsyncParameterCommand(SelectThemeAsync);
        SelectAuthViewCommand = new RelayCommand(parameter =>
        {
            var view = parameter?.ToString() ?? "Login";
            if (!view.Equals("TwoFactor", StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingLoginChallenge();
            }

            AuthView = view;
        });
        SelectProtocolCommand = new RelayCommand(parameter =>
        {
            var protocol = parameter?.ToString() ?? "IKEv2";
            if (protocol.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase) && Subscription?.IsPro != true)
            {
                NavigateToUpgradeSettings();
                OnPropertyChanged(nameof(IsIkev2Protocol));
                OnPropertyChanged(nameof(IsOpenVpnProtocol));
                return;
            }

            SelectedProtocol = protocol;

            // Re-raise the computed checked properties even when the selected
            // protocol did not change, so a rejected or repeated selection is
            // reflected immediately by both radio controls.
            OnPropertyChanged(nameof(IsIkev2Protocol));
            OnPropertyChanged(nameof(IsOpenVpnProtocol));
        });
        ToggleFavoriteServerCommand = new RelayCommand(parameter =>
        {
            if (parameter is VpnServer server)
            {
                ToggleFavoriteServer(server);
            }
        });
        ClearServerSearchCommand = new RelayCommand(_ => ServerSearchText = string.Empty);
        SelectStatisticsPeriodCommand = new RelayCommand(parameter => SelectedStatisticsPeriod = parameter?.ToString() ?? "Week");
        ConnectToServerCommand = new AsyncParameterCommand(ConnectToServerAsync, parameter => parameter is VpnServer server && CanUseTrayServer(server));
        SelectServerCommand = new RelayCommand(parameter =>
        {
            if (parameter is VpnServer server)
            {
                if (!CanUseServer(server))
                {
                    NavigateToUpgradeSettings("Upgrade to Pro to connect to Pro servers.");
                    return;
                }

                SelectedServer = server;
                CurrentSection = "Dashboard";
            }
        });
        DiscardSelectedServerCommand = new RelayCommand(_ => DiscardSelectedServer());

        TrackCommandState(LoginCommand, nameof(IsLoginRunning), nameof(IsLoginIdle));
        TrackCommandState(OAuthLoginCommand, nameof(IsGoogleLoginRunning), nameof(IsGoogleLoginIdle));
        TrackCommandState(RegisterCommand, nameof(IsRegisterRunning), nameof(IsRegisterIdle));
        TrackCommandState(ForgotPasswordCommand, nameof(IsForgotPasswordRunning), nameof(IsForgotPasswordIdle));
        TrackCommandState(ResetPasswordCommand, nameof(IsResetPasswordRunning), nameof(IsResetPasswordIdle));
        TrackCommandState(RefreshCommand, nameof(IsRefreshRunning));
        TrackCommandState(ConnectCommand, nameof(IsConnectRunning), nameof(IsExitConfirmationRequired), nameof(IsConnectionAttemptActive), nameof(ConnectionActionCommand), nameof(ConnectionActionText), nameof(ShouldStrikeOriginalIp), nameof(ShouldShowOriginalIpPlain), nameof(CanUseTrayServers));
        TrackCommandState(QuickConnectCommand, nameof(IsQuickConnectRunning), nameof(IsExitConfirmationRequired), nameof(IsConnectionAttemptActive), nameof(ConnectionActionCommand), nameof(ConnectionActionText), nameof(ShouldStrikeOriginalIp), nameof(ShouldShowOriginalIpPlain), nameof(CanUseTrayServers));

        _vpn.StatusChanged += (_, status) =>
        {
            if (status.State is VpnConnectionState.Disconnected or VpnConnectionState.Error)
            {
                ConnectedServer = null;
            }

            ConnectionState = status.State;
            ConnectionMessage = status.Message ?? status.State.ToString();
            _ = ShowVpnStatusNotificationAsync(status);
            _ = HandleVpnStatusChangedAsync(status);
        };
    }

    public ObservableCollection<VpnServer> Servers { get; } = [];
    public ObservableCollection<VpnServer> VisibleServers { get; } = [];
    public ObservableCollection<VpnServer> FavoriteServers { get; } = [];
    public ObservableCollection<VpnServer> RecentServers { get; } = [];
    public ObservableCollection<ServerGroupViewModel> ServerGroups { get; } = [];
    public ObservableCollection<ChartBarViewModel> UsageChartBars { get; } = [];
    public ObservableCollection<ChartBarViewModel> ConnectionChartBars { get; } = [];
    public ObservableCollection<ChartBarViewModel> ServerLoadChartBars { get; } = [];
    public ObservableCollection<ChartBarViewModel> AppVersionBars { get; } = [];
    public ObservableCollection<TrafficUsageRowViewModel> DailyTrafficRows { get; } = [];
    public ObservableCollection<SessionDurationRowViewModel> ConnectionDurationRows { get; } = [];
    public ObservableCollection<UserDevice> Devices { get; } = [];
    public ObservableCollection<UserCertificate> Certificates { get; } = [];
    public ObservableCollection<string> Protocols { get; } = ["IKEv2", "OpenVPN"];
    public ObservableCollection<string> BillingCycles { get; } = ["monthly", "yearly"];
    public ObservableCollection<string> ServerSortModes { get; } = ["Ping", "Load", "Name"];
    public ObservableCollection<string> StatisticsPeriods { get; } = ["Week", "Month", "Year"];

    public string AppVersion => _appVersion;

    public event EventHandler? TwoFactorSetupDialogRequested;
    public event EventHandler? TwoFactorDisableDialogRequested;

    public ICommand LoginCommand { get; }
    public ICommand OAuthLoginCommand { get; }
    public ICommand RegisterCommand { get; }
    public ICommand CheckEmailConfirmationCommand { get; }
    public ICommand ResendConfirmationCommand { get; }
    public ICommand VerifyTwoFactorCommand { get; }
    public ICommand VerifyRecoveryCodeCommand { get; }
    public ICommand ForgotPasswordCommand { get; }
    public ICommand ResetPasswordCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand QuickConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand CancelConnectionAttemptCommand { get; }
    public ICommand SetupTwoFactorCommand { get; }
    public ICommand EnableTwoFactorCommand { get; }
    public ICommand DisableTwoFactorCommand { get; }
    public ICommand ResetTwoFactorCommand { get; }
    public ICommand GenerateRecoveryCodesCommand { get; }
    public ICommand RemoveSelectedDeviceCommand { get; }
    public ICommand DeleteSelectedDeviceCommand { get; }
    public ICommand RemoveOtherDevicesCommand { get; }
    public ICommand RemoveInactiveDevicesCommand { get; }
    public ICommand OpenUpgradeCommand { get; }
    public ICommand GoBackToSettingsCommand { get; }
    public ICommand SelectCardCommand { get; }
    public ICommand OpenCardCheckoutInBrowserCommand { get; }
    public ICommand SelectMoneroCommand { get; }
    public ICommand SwitchPaymentMethodCommand { get; }
    public ICommand CheckPaymentStatusCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand CopyAmountCommand { get; }
    public ICommand RunPreflightCommand { get; }
    public ICommand DownloadSelectedOpenVpnConfigCommand { get; }
    public ICommand DownloadSelectedConfigCommand { get; }
    public ICommand DownloadSelectedCertificateCommand { get; }
    public ICommand TogglePasswordVisibilityCommand { get; }
    public ICommand ToggleConfirmPasswordVisibilityCommand { get; }
    public ICommand ToggleNewPasswordVisibilityCommand { get; }
    public ICommand ToggleRecoveryCodeEntryCommand { get; }
    public ICommand CancelLoginCommand { get; }
    public ICommand CancelGoogleLoginCommand { get; }
    public ICommand CancelRegisterCommand { get; }
    public ICommand CancelForgotPasswordCommand { get; }
    public ICommand CancelResetPasswordCommand { get; }
    public ICommand DismissDeviceLimitCommand { get; }
    public ICommand SelectBillingCycleCommand { get; }
    public ICommand SelectSectionCommand { get; }
    public ICommand SelectThemeCommand { get; }
    public ICommand SelectAuthViewCommand { get; }
    public ICommand SelectServerCommand { get; }
    public ICommand ConnectToServerCommand { get; }
    public ICommand DiscardSelectedServerCommand { get; }
    public ICommand ToggleFavoriteServerCommand { get; }
    public ICommand ClearServerSearchCommand { get; }
    public ICommand SelectStatisticsPeriodCommand { get; }
    public ICommand SelectProtocolCommand { get; }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (SetProperty(ref _isAuthenticated, value))
            {
                OnPropertyChanged(nameof(IsUnauthenticated));
            }
        }
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (SetProperty(ref _isInitializing, value))
            {
                OnPropertyChanged(nameof(IsUnauthenticated));
            }
        }
    }

    public bool IsUnauthenticated => !IsAuthenticated && !IsInitializing;

    public string AuthView
    {
        get => _authView;
        set
        {
            if (SetProperty(ref _authView, value))
            {
                RaiseViewFlags();
            }
        }
    }

    public string CurrentSection
    {
        get => _currentSection;
        set
        {
            var previousSection = _currentSection;
            if (SetProperty(ref _currentSection, value))
            {
                RaiseViewFlags();
                if (IsStatistics)
                {
                    UpdateStatisticsPresentation();
                }

                if (IsAuthenticated &&
                    !_suppressAccountRefreshOnSectionTransition &&
                    ShouldRefreshAccountStateForSectionTransition(previousSection, _currentSection))
                {
                    _ = RefreshAccountStateAsync(CancellationToken.None);
                }
            }
        }
    }

    private static bool ShouldRefreshAccountStateForSectionTransition(string previousSection, string currentSection)
    {
        if (currentSection.Equals("Settings", StringComparison.OrdinalIgnoreCase))
        {
            return !previousSection.Equals("Settings", StringComparison.OrdinalIgnoreCase);
        }

        if (IsAccountStateSection(previousSection) || !IsAccountStateSection(currentSection))
        {
            return false;
        }

        return true;
    }

    private static bool IsAccountStateSection(string section)
        => section.Equals("Devices", StringComparison.OrdinalIgnoreCase)
           || section.Equals("Certificates", StringComparison.OrdinalIgnoreCase);

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string AccountEmail
    {
        get => _accountEmail;
        private set => SetProperty(ref _accountEmail, value);
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                RaisePasswordStateChanged();
            }
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (SetProperty(ref _confirmPassword, value))
            {
                OnPropertyChanged(nameof(IsPasswordMatch));
                OnPropertyChanged(nameof(ShouldShowPasswordMismatch));
            }
        }
    }

    public string RegisteredUserId
    {
        get => _registeredUserId;
        set => SetProperty(ref _registeredUserId, value);
    }

    public string ResetToken
    {
        get => _resetToken;
        set => SetProperty(ref _resetToken, value);
    }

    public string NewPassword
    {
        get => _newPassword;
        set
        {
            if (SetProperty(ref _newPassword, value))
            {
                RaiseNewPasswordStateChanged();
            }
        }
    }

    public string OAuthToken
    {
        get => _oauthToken;
        set => SetProperty(ref _oauthToken, value);
    }

    public string TwoFactorCode
    {
        get => _twoFactorCode;
        set => SetProperty(ref _twoFactorCode, value);
    }

    public string RecoveryCode
    {
        get => _recoveryCode;
        set => SetProperty(ref _recoveryCode, value);
    }

    public string TwoFactorManagementCode
    {
        get => _twoFactorManagementCode;
        set => SetProperty(ref _twoFactorManagementCode, value);
    }

    public string TwoFactorSharedKey
    {
        get => _twoFactorSharedKey;
        set => SetProperty(ref _twoFactorSharedKey, value);
    }

    public string TwoFactorAuthenticatorUri
    {
        get => _twoFactorAuthenticatorUri;
        set => SetProperty(ref _twoFactorAuthenticatorUri, value);
    }

    public string RecoveryCodesText
    {
        get => _recoveryCodesText;
        set => SetProperty(ref _recoveryCodesText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ConnectionMessage
    {
        get => _connectionMessage;
        set => SetProperty(ref _connectionMessage, value);
    }

    public string SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            if (SetProperty(ref _selectedProtocol, value))
            {
                OnPropertyChanged(nameof(IsIkev2Protocol));
                OnPropertyChanged(nameof(IsOpenVpnProtocol));
            }
        }
    }

    public string BillingCycle
    {
        get => _billingCycle;
        set
        {
            var normalized = NormalizeBillingCycle(value);
            if (SetProperty(ref _billingCycle, normalized))
            {
                OnPropertyChanged(nameof(IsMonthlyBilling));
                OnPropertyChanged(nameof(IsYearlyBilling));
                OnPropertyChanged(nameof(SelectedBillingCycle));
                if (IsUpgrade)
                {
                    _ = UpdateMoneroPriceAsync(CancellationToken.None);
                }
            }
        }
    }

    public AppThemePreference ThemePreference
    {
        get => _themePreference;
        private set
        {
            if (SetProperty(ref _themePreference, value))
            {
                OnPropertyChanged(nameof(IsSystemTheme));
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
            }
        }
    }

    public bool IsSystemTheme => ThemePreference == AppThemePreference.System;
    public bool IsLightTheme => ThemePreference == AppThemePreference.Light;
    public bool IsDarkTheme => ThemePreference == AppThemePreference.Dark;

    public string CheckoutUrl
    {
        get => _checkoutUrl;
        set
        {
            if (SetProperty(ref _checkoutUrl, value))
            {
                OnPropertyChanged(nameof(HasCheckoutUrl));
                OnPropertyChanged(nameof(IsCardCheckoutLinkVisible));
            }
        }
    }

    public bool IsEmbeddedCheckoutOpen
    {
        get => _isEmbeddedCheckoutOpen;
        private set
        {
            if (SetProperty(ref _isEmbeddedCheckoutOpen, value))
            {
                OnPropertyChanged(nameof(IsCardCheckoutLinkVisible));
            }
        }
    }

    public string TimeRemaining
    {
        get => _timeRemaining;
        set => SetProperty(ref _timeRemaining, value);
    }

    public string PreflightSummary
    {
        get => _preflightSummary;
        set => SetProperty(ref _preflightSummary, value);
    }

    public string ServerSearchText
    {
        get => _serverSearchText;
        set
        {
            if (SetProperty(ref _serverSearchText, value))
            {
                _ = _settingsStore.SetAsync(ServerSearchKey, value, CancellationToken.None);
                UpdateServerPresentation();
            }
        }
    }

    public string ServerSortMode
    {
        get => _serverSortMode;
        set
        {
            if (SetProperty(ref _serverSortMode, value))
            {
                _ = _settingsStore.SetAsync(ServerSortKey, value, CancellationToken.None);
                UpdateServerPresentation();
            }
        }
    }

    public string SelectedStatisticsPeriod
    {
        get => _statisticsPeriod;
        set
        {
            if (SetProperty(ref _statisticsPeriod, value))
            {
                _ = _localStatisticsStore.SetStatisticsPeriodAsync(_authSession.CurrentSession, value, CancellationToken.None);
                OnPropertyChanged(nameof(IsWeekStatisticsPeriod));
                OnPropertyChanged(nameof(IsMonthStatisticsPeriod));
                OnPropertyChanged(nameof(IsYearStatisticsPeriod));
                UpdateStatisticsPresentation();
            }
        }
    }

    public string StatisticsTotalDataText
    {
        get => _statisticsTotalDataText;
        private set => SetProperty(ref _statisticsTotalDataText, value);
    }

    public string StatisticsConnectionsText
    {
        get => _statisticsConnectionsText;
        private set => SetProperty(ref _statisticsConnectionsText, value);
    }

    public string StatisticsAverageSessionText
    {
        get => _statisticsAverageSessionText;
        private set => SetProperty(ref _statisticsAverageSessionText, value);
    }

    public string StatisticsAverageDownloadText
    {
        get => _statisticsAverageDownloadText;
        private set => SetProperty(ref _statisticsAverageDownloadText, value);
    }

    public string StatisticsTotalDownloadText
    {
        get => _statisticsTotalDownloadText;
        private set => SetProperty(ref _statisticsTotalDownloadText, value);
    }

    public string StatisticsTotalUploadText
    {
        get => _statisticsTotalUploadText;
        private set => SetProperty(ref _statisticsTotalUploadText, value);
    }

    public string ConnectionDurationText
    {
        get => _connectionDurationText;
        private set => SetProperty(ref _connectionDurationText, value);
    }

    public string OriginalPublicIpText
    {
        get => _originalPublicIpText;
        private set => SetProperty(ref _originalPublicIpText, value);
    }

    public string VpnIpText
    {
        get => _vpnIpText;
        private set
        {
            if (SetProperty(ref _vpnIpText, value))
            {
                OnPropertyChanged(nameof(TrayToolTipText));
            }
        }
    }

    public string LiveDownloadSpeedText
    {
        get => _liveDownloadSpeedText;
        private set => SetProperty(ref _liveDownloadSpeedText, value);
    }

    public string LiveUploadSpeedText
    {
        get => _liveUploadSpeedText;
        private set => SetProperty(ref _liveUploadSpeedText, value);
    }

    public string SessionDataTotalText
    {
        get => _sessionDataTotalText;
        private set
        {
            if (SetProperty(ref _sessionDataTotalText, value))
            {
                OnPropertyChanged(nameof(TrayToolTipText));
            }
        }
    }

    public VpnConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsNotConnected));
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(IsConnectionIdle));
                OnPropertyChanged(nameof(IsConnectionConnecting));
                OnPropertyChanged(nameof(IsConnectionConnected));
                OnPropertyChanged(nameof(IsConnectionDisconnecting));
                OnPropertyChanged(nameof(IsExitConfirmationRequired));
                OnPropertyChanged(nameof(IsConnectionAttemptActive));
                OnPropertyChanged(nameof(ConnectionShieldBrush));
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(ConnectionStateText));
                OnPropertyChanged(nameof(ConnectionActionCommand));
                OnPropertyChanged(nameof(ConnectionActionText));
                OnPropertyChanged(nameof(SidebarServerDisplayText));
                OnPropertyChanged(nameof(SidebarServerDetailsText));
                OnPropertyChanged(nameof(IsSelectedServerCardVisible));
                OnPropertyChanged(nameof(IsQuickConnectCardVisible));
                OnPropertyChanged(nameof(ShouldStrikeOriginalIp));
                OnPropertyChanged(nameof(ShouldShowOriginalIpPlain));
                OnPropertyChanged(nameof(CanUseTrayServers));
                OnPropertyChanged(nameof(TrayTopActionText));
                OnPropertyChanged(nameof(TrayToolTipText));
                RefreshConnectionCommandState();
            }
        }
    }

    public VpnServer? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value))
            {
                if (ConnectCommand is AsyncCommand asyncCommand)
                {
                    asyncCommand.RaiseCanExecuteChanged();
                }

                if (QuickConnectCommand is AsyncCommand quickConnectCommand)
                {
                    quickConnectCommand.RaiseCanExecuteChanged();
                }

                if (DownloadSelectedOpenVpnConfigCommand is AsyncCommand downloadCommand)
                {
                    downloadCommand.RaiseCanExecuteChanged();
                }

                if (_selectedServer is not null)
                {
                    TouchRecentServer(_selectedServer.Id);
                    UpdateServerPresentation();
                }

                OnPropertyChanged(nameof(IsServerSelected));
                OnPropertyChanged(nameof(HasSelectedServer));
                OnPropertyChanged(nameof(SidebarServerDisplayText));
                OnPropertyChanged(nameof(SidebarServerDetailsText));
                OnPropertyChanged(nameof(IsSelectedServerCardVisible));
                OnPropertyChanged(nameof(IsQuickConnectCardVisible));
            }
        }
    }

    public VpnServer? ConnectedServer
    {
        get => _connectedServer;
        private set
        {
            if (SetProperty(ref _connectedServer, value))
            {
                OnPropertyChanged(nameof(ConnectedServerDisplayText));
                OnPropertyChanged(nameof(ConnectedServerDetailsText));
                OnPropertyChanged(nameof(ConnectedServerFlag));
                OnPropertyChanged(nameof(ConnectedServerLinkSpeedText));
                OnPropertyChanged(nameof(FormattedConnectedLocationText));
                OnPropertyChanged(nameof(SidebarServerDisplayText));
                OnPropertyChanged(nameof(SidebarServerDetailsText));
                OnPropertyChanged(nameof(TrayToolTipText));
            }
        }
    }

    public bool IsServerSelected => SelectedServer is not null;
    public bool HasSelectedServer => SelectedServer is not null;
    public bool IsDisconnected => ConnectionState is VpnConnectionState.Disconnected or VpnConnectionState.Error;
    public bool IsSelectedServerCardVisible => IsDisconnected && SelectedServer is not null;
    public bool IsQuickConnectCardVisible => IsDisconnected && SelectedServer is null;
    public bool IsConnectionIdle => IsDisconnected;
    public bool IsConnectionConnecting => ConnectionState is VpnConnectionState.Preparing or VpnConnectionState.Connecting;
    public bool IsConnectionConnected => ConnectionState == VpnConnectionState.Connected;
    public bool IsConnectionDisconnecting => ConnectionState == VpnConnectionState.Disconnecting;
    public bool IsExitConfirmationRequired => ConnectionState is VpnConnectionState.Preparing or VpnConnectionState.Connecting or VpnConnectionState.Connected or VpnConnectionState.Disconnecting
        || IsConnectRunning
        || IsQuickConnectRunning;
    public bool IsConnectionAttemptActive => ConnectionState is VpnConnectionState.Preparing or VpnConnectionState.Connecting
        || ((IsConnectRunning || IsQuickConnectRunning) && ConnectionState is not VpnConnectionState.Connected and not VpnConnectionState.Disconnecting);
    public bool IsQuickConnectRunning => QuickConnectCommand is AsyncCommand { IsRunning: true };
    public bool ShouldStrikeOriginalIp => IsConnectionAttemptActive || ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Disconnecting;
    public bool ShouldShowOriginalIpPlain => !ShouldStrikeOriginalIp;
    public IBrush ConnectionShieldBrush => ConnectionState switch
    {
        VpnConnectionState.Connected => new SolidColorBrush(Color.Parse("#10B981")),
        VpnConnectionState.Preparing or VpnConnectionState.Connecting or VpnConnectionState.Disconnecting => new SolidColorBrush(Color.Parse("#F59E0B")),
        _ => new SolidColorBrush(Color.Parse("#94A3B8"))
    };
    public string SidebarServerDisplayText => ConnectedServer?.DisplayName ?? SelectedServer?.DisplayName ?? "Quick Connect";
    public string SidebarServerDetailsText => ConnectedServer?.ServerHostname ?? SelectedServer?.ServerHostname ?? "Automatically choose the best available server";
    public string ConnectedServerDisplayText => ConnectedServer?.DisplayName ?? "Quick Connect";
    public string ConnectedServerDetailsText => ConnectedServer?.ServerHostname ?? ConnectedServer?.ServerIp ?? string.Empty;
    public string ConnectedServerFlag => ConnectedServer?.CountryFlag ?? "⚡";
    public string ConnectedServerLinkSpeedText => ConnectedServer?.LinkSpeedText ?? string.Empty;
    public bool CanUseTrayServers => IsDisconnected && !IsConnectRunning && !IsQuickConnectRunning;
    public bool CanUseTrayServer(VpnServer server) => CanUseTrayServers && CanUseServer(server);
    public string TrayTopActionText => IsConnected ? "Disconnect" : "Quick Connect";
    public string TrayMonthlyUsageText => IsFreePlan && ShowMonthlyUsageProgress
        ? $"Monthly usage: {MonthlyUsageDisplayText}"
        : string.Empty;
    public string TrayToolTipText
    {
        get
        {
            var text = ConnectionState switch
            {
                VpnConnectionState.Connected => $"LibreGuard VPN - {GetConnectedTrayLocation()} - {VpnIpText} - Session data: {SessionDataTotalText}",
                VpnConnectionState.Preparing or VpnConnectionState.Connecting => "LibreGuard VPN - Connecting",
                _ => "LibreGuard VPN - Not Connected"
            };

            return string.IsNullOrWhiteSpace(TrayMonthlyUsageText)
                ? text
                : $"{text} - {TrayMonthlyUsageText}";
        }
    }

    public UserDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                if (RemoveSelectedDeviceCommand is AsyncCommand removeCommand)
                {
                    removeCommand.RaiseCanExecuteChanged();
                }

                if (DeleteSelectedDeviceCommand is AsyncCommand deleteCommand)
                {
                    deleteCommand.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public UserCertificate? SelectedCertificate
    {
        get => _selectedCertificate;
        set => SetProperty(ref _selectedCertificate, value);
    }

    public UsageQuota? Quota
    {
        get => _quota;
        set
        {
            if (SetProperty(ref _quota, value))
            {
                OnPropertyChanged(nameof(QuotaText));
                OnPropertyChanged(nameof(MonthlyUsageDisplayText));
                OnPropertyChanged(nameof(MonthlyUsageLimitText));
                OnPropertyChanged(nameof(IsMonthlyUsageUnlimited));
                OnPropertyChanged(nameof(ShowMonthlyUsageProgress));
                OnPropertyChanged(nameof(MonthlyUsagePercentage));
                OnPropertyChanged(nameof(TrayMonthlyUsageText));
                OnPropertyChanged(nameof(TrayToolTipText));
            }
        }
    }

    public SubscriptionStatus? Subscription
    {
        get => _subscription;
        set
        {
            if (SetProperty(ref _subscription, value))
            {
                OnPropertyChanged(nameof(PlanText));
                OnPropertyChanged(nameof(IsProPlan));
                OnPropertyChanged(nameof(IsFreePlan));
                OnPropertyChanged(nameof(CanUseTrayServers));
                OnPropertyChanged(nameof(ShowUpgradeSettingsCard));
                OnPropertyChanged(nameof(CanAccessCertificates));
                OnPropertyChanged(nameof(ShowCertificatesUpgradePrompt));
                OnPropertyChanged(nameof(IsDeviceLimitReached));
                OnPropertyChanged(nameof(DeviceLimitMessage));
                OnPropertyChanged(nameof(ActiveDevicesText));
                OnPropertyChanged(nameof(MonthlyUsageDisplayText));
                OnPropertyChanged(nameof(MonthlyUsageLimitText));
                OnPropertyChanged(nameof(IsMonthlyUsageUnlimited));
                OnPropertyChanged(nameof(ShowMonthlyUsageProgress));
                OnPropertyChanged(nameof(MonthlyUsagePercentage));
                OnPropertyChanged(nameof(TrayMonthlyUsageText));
                OnPropertyChanged(nameof(TrayToolTipText));
                NotifyAdBlockingStateChanged();

                if (DownloadSelectedConfigCommand is AsyncParameterCommand configCommand)
                {
                    configCommand.RaiseCanExecuteChanged();
                }

                if (DownloadSelectedCertificateCommand is AsyncParameterCommand certificateCommand)
                {
                    certificateCommand.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public bool AutoConnect
    {
        get => _autoConnect;
        set
        {
            if (SetProperty(ref _autoConnect, value))
            {
                _ = _settingsStore.SetAsync(nameof(AutoConnect), value, CancellationToken.None);
            }
        }
    }

    public bool TwoFactorEnabled
    {
        get => _twoFactorEnabled;
        set
        {
            if (SetProperty(ref _twoFactorEnabled, value))
            {
                OnPropertyChanged(nameof(TwoFactorStatusText));
            }
        }
    }

    public bool TwoFactorToggleEnabled
    {
        get => _twoFactorToggleEnabled;
        set
        {
            if (!SetProperty(ref _twoFactorToggleEnabled, value) || _isUpdatingTwoFactorToggleState)
            {
                return;
            }

            _ = HandleTwoFactorToggleChangedAsync(value);
        }
    }

    public bool HasAuthenticator
    {
        get => _hasAuthenticator;
        set => SetProperty(ref _hasAuthenticator, value);
    }

    public bool KillSwitch
    {
        get => _killSwitch;
        set
        {
            if (SetProperty(ref _killSwitch, value))
            {
                _ = _settingsStore.SetAsync(nameof(KillSwitch), value, CancellationToken.None);
            }
        }
    }

    public bool AdBlockingEnabled
    {
        get => _adBlockingEnabled;
        set
        {
            if (!SetProperty(ref _adBlockingEnabled, value) || _isApplyingAdBlockingState)
            {
                return;
            }

            OnPropertyChanged(nameof(AdBlockingStatusText));
            OnPropertyChanged(nameof(IsAdBlockingPaused));

            if (!CanToggleAdBlocking || _dnsPreference is null)
            {
                SetAdBlockingToggleState(_dnsPreference?.RequestedEnabled ?? false);
                return;
            }

            _ = UpdateAdBlockingAsync(value, _dnsPreference, CancellationToken.None);
        }
    }

    public bool IsAdBlockingSettingsAvailable => _dnsPreference is not null;
    public bool IsUpdatingAdBlocking
    {
        get => _isUpdatingAdBlocking;
        private set
        {
            if (SetProperty(ref _isUpdatingAdBlocking, value))
            {
                OnPropertyChanged(nameof(CanToggleAdBlocking));
                OnPropertyChanged(nameof(AdBlockingStatusText));
            }
        }
    }

    public bool CanToggleAdBlocking =>
        IsAuthenticated &&
        IsAdBlockingSettingsAvailable &&
        !IsUpdatingAdBlocking &&
        !_isAdBlockingProRequired &&
        Subscription?.IsPro == true &&
        _dnsPreference?.CanUseAdBlocking == true;
    public bool ShowAdBlockingProBadge =>
        Subscription is not null &&
        (_isAdBlockingProRequired || Subscription.IsPro == false || _dnsPreference?.CanUseAdBlocking == false);
    public bool ShowAdBlockingUpgradeAction => ShowAdBlockingProBadge;
    public bool IsAdBlockingEffectivelyEnabled => _dnsPreference?.EffectiveEnabled == true;
    public bool IsAdBlockingPaused =>
        AdBlockingEnabled &&
        IsAdBlockingSettingsAvailable &&
        (_isAdBlockingProRequired || Subscription?.IsPro != true || _dnsPreference?.CanUseAdBlocking != true);
    public bool ShowAdBlockingPropagation =>
        !_isAdBlockingProRequired &&
        Subscription?.IsPro == true &&
        _dnsPreference is { CanUseAdBlocking: true, PropagationSeconds: > 0 };
    public string AdBlockingPropagationText => ShowAdBlockingPropagation
        ? $"Changes can take up to {_dnsPreference!.PropagationSeconds} seconds to reach VPN servers."
        : string.Empty;
    public string AdBlockingStatusText
    {
        get
        {
            if (!IsAdBlockingSettingsAvailable)
            {
                return "Status unavailable. Refresh your account to try again.";
            }

            if (IsAdBlockingPaused)
            {
                return "Paused—Pro required.";
            }

            if (_isAdBlockingProRequired || Subscription?.IsPro != true || _dnsPreference!.CanUseAdBlocking != true)
            {
                return "Available with Pro—upgrade to enable ad blocking.";
            }

            var effectiveMode = string.IsNullOrWhiteSpace(_dnsPreference!.EffectiveMode)
                ? "standard private DNS"
                : _dnsPreference.EffectiveMode;
            if (IsUpdatingAdBlocking)
            {
                return $"Updating request. Effective mode: {effectiveMode}.";
            }

            if (_dnsPreference.EffectiveEnabled)
            {
                return $"Active. Effective mode: {effectiveMode}.";
            }

            if (AdBlockingEnabled)
            {
                return $"Requested on. Effective mode: {effectiveMode}.";
            }

            return $"Off. Effective mode: {effectiveMode}.";
        }
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (SetProperty(ref _notificationsEnabled, value))
            {
                _ = _settingsStore.SetAsync(NotificationsEnabledKey, value, CancellationToken.None);
            }
        }
    }

    public bool ShowPassword
    {
        get => _showPassword;
        set
        {
            if (SetProperty(ref _showPassword, value))
            {
                OnPropertyChanged(nameof(HidePassword));
            }
        }
    }

    public bool ShowConfirmPassword
    {
        get => _showConfirmPassword;
        set
        {
            if (SetProperty(ref _showConfirmPassword, value))
            {
                OnPropertyChanged(nameof(HideConfirmPassword));
            }
        }
    }

    public bool ShowNewPassword
    {
        get => _showNewPassword;
        set
        {
            if (SetProperty(ref _showNewPassword, value))
            {
                OnPropertyChanged(nameof(HideNewPassword));
            }
        }
    }

    public bool ShowRecoveryCodeEntry
    {
        get => _showRecoveryCodeEntry;
        set
        {
            if (SetProperty(ref _showRecoveryCodeEntry, value))
            {
                OnPropertyChanged(nameof(HideRecoveryCodeEntry));
            }
        }
    }

    public bool IsDeviceLimitModalVisible
    {
        get => _isDeviceLimitModalVisible;
        set => SetProperty(ref _isDeviceLimitModalVisible, value);
    }

    public bool IsLoadingPayment
    {
        get => _isLoadingPayment;
        set => SetProperty(ref _isLoadingPayment, value);
    }

    public bool IsMoneroSelected
    {
        get => _isMoneroSelected;
        set
        {
            if (SetProperty(ref _isMoneroSelected, value))
            {
                OnPropertyChanged(nameof(IsPaymentMethodSelectionVisible));
            }
        }
    }

    public bool IsCardSelected
    {
        get => _isCardSelected;
        set
        {
            if (SetProperty(ref _isCardSelected, value))
            {
                OnPropertyChanged(nameof(IsPaymentMethodSelectionVisible));
            }
        }
    }

    public bool IsPaymentComplete
    {
        get => _isPaymentComplete;
        set => SetProperty(ref _isPaymentComplete, value);
    }

    public decimal Shortfall
    {
        get => _shortfall;
        set => SetProperty(ref _shortfall, value);
    }

    public MoneroPriceResponse? MoneroPrice
    {
        get => _moneroPrice;
        set => SetProperty(ref _moneroPrice, value);
    }

    public MoneroInvoiceResponse? MoneroInvoice
    {
        get => _moneroInvoice;
        set => SetProperty(ref _moneroInvoice, value);
    }

    public MoneroStatusResponse? MoneroStatus
    {
        get => _moneroStatus;
        set
        {
            if (SetProperty(ref _moneroStatus, value))
            {
                OnPropertyChanged(nameof(MoneroConfirmationsText));
            }
        }
    }

    public bool HidePassword => !ShowPassword;
    public bool HideConfirmPassword => !ShowConfirmPassword;
    public bool HideNewPassword => !ShowNewPassword;
    public bool HideRecoveryCodeEntry => !ShowRecoveryCodeEntry;

    public bool IsLoginView => AuthView == "Login";
    public bool IsRegisterView => AuthView == "Register";
    public bool IsForgotView => AuthView == "Forgot";
    public bool IsResetView => AuthView == "Reset";
    public bool IsEmailConfirmationView => AuthView == "EmailConfirmation";
    public bool IsTwoFactorView => AuthView == "TwoFactor";
    public bool IsDashboard => CurrentSection == "Dashboard";
    public bool IsServers => CurrentSection == "Servers";
    public bool IsStatistics => CurrentSection == "Statistics";
    public bool IsSettings => CurrentSection == "Settings";
    public bool IsUpgrade => CurrentSection == "Upgrade";
    public bool IsDevices => CurrentSection == "Devices";
    public bool IsCertificates => CurrentSection == "Certificates";
    public bool IsConnected => ConnectionState == VpnConnectionState.Connected;
    public bool IsNotConnected => !IsConnected;
    public ICommand ConnectionActionCommand => ConnectionState == VpnConnectionState.Disconnecting
        ? DisconnectCommand
        : IsConnectionAttemptActive
            ? CancelConnectionAttemptCommand
            : IsConnected
                ? DisconnectCommand
                : ConnectCommand;
    public string ConnectionActionText => ConnectionState == VpnConnectionState.Disconnecting
        ? "Disconnecting..."
        : IsConnectionAttemptActive
            ? "Cancel"
            : IsConnected
                ? "Disconnect"
                : "Connect";
    public bool IsIkev2Protocol => SelectedProtocol.Equals("IKEv2", StringComparison.OrdinalIgnoreCase);
    public bool IsOpenVpnProtocol => SelectedProtocol.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase);
    public bool IsMonthlyBilling => BillingCycle.Equals("monthly", StringComparison.OrdinalIgnoreCase);
    public bool IsYearlyBilling => BillingCycle.Equals("yearly", StringComparison.OrdinalIgnoreCase);
    public Libreguard.Vpn.Linux.Models.BillingCycle SelectedBillingCycle => IsYearlyBilling
        ? Libreguard.Vpn.Linux.Models.BillingCycle.Yearly
        : Libreguard.Vpn.Linux.Models.BillingCycle.Monthly;
    public bool IsPaymentMethodSelectionVisible => !IsMoneroSelected && !IsCardSelected;
    public bool HasCheckoutUrl => !string.IsNullOrWhiteSpace(CheckoutUrl);
    public bool IsCardCheckoutLinkVisible => HasCheckoutUrl && !IsEmbeddedCheckoutOpen;
    public bool IsProPlan => Subscription is { } subscription
        ? subscription.IsPro
        : string.Equals(PlanText, "Pro", StringComparison.OrdinalIgnoreCase);
    public bool IsFreePlan => !IsProPlan;
    public bool ShowUpgradeSettingsCard => !IsProPlan;
    public bool CanAccessCertificates => Subscription?.IsPro == true;
    public bool ShowCertificatesUpgradePrompt => Subscription is not null && !Subscription.IsPro;
    public bool IsDeviceLimitReached => Subscription is { CanAddDevice: false };
    public string ConnectionStatusText => IsConnectionDisconnecting
        ? "Disconnecting"
        : IsConnectionConnecting
            ? ConnectionState.ToString()
            : IsConnectionConnected
                ? "Connected"
                : "Ready to connect";
    public string ConnectionStateText => ConnectionState.ToString();
    public string TwoFactorStatusText => TwoFactorEnabled ? "Enabled" : "Disabled";
    public string PlanText => Subscription is { } subscription
        ? GetPlanText(subscription)
        : _authSession.CurrentSession?.PlanType ?? "Free";
    public string FormattedConnectedLocationText => ConnectedServer?.DisplayName ?? "—";
    public bool IsMonthlyUsageUnlimited => Quota?.IsUnlimited == true || Subscription?.IsPro == true;
    public bool ShowMonthlyUsageProgress => !IsMonthlyUsageUnlimited;
    public string MonthlyUsageLimitText => IsMonthlyUsageUnlimited ? "∞" : FormatBytes(GetMonthlyUsageLimitBytes());
    public string MonthlyUsageDisplayText => $"{FormatBytes(Quota?.BytesUsed ?? 0)} / {MonthlyUsageLimitText}";
    public double MonthlyUsagePercentage => IsMonthlyUsageUnlimited
        ? 0
        : Math.Clamp((double)(Quota?.BytesUsed ?? 0) / Math.Max(1L, GetMonthlyUsageLimitBytes()) * 100.0, 0, 100);
    public string PasswordStrengthLabel => PasswordStrengthScore >= 100 ? "Strong" : "Weak";
    public string NewPasswordStrengthLabel => NewPasswordStrengthScore >= 100 ? "Strong" : "Weak";
    public int PasswordStrengthScore => CalculatePasswordStrength(Password);
    public int NewPasswordStrengthScore => CalculatePasswordStrength(NewPassword);
    public bool IsPasswordEmpty => string.IsNullOrEmpty(Password);
    public bool IsNewPasswordEmpty => string.IsNullOrEmpty(NewPassword);
    public bool IsPasswordStrong => PasswordStrengthScore >= 100;
    public bool IsNewPasswordStrong => NewPasswordStrengthScore >= 100;
    public bool ShouldShowPasswordStrength => !IsPasswordEmpty;
    public bool ShouldShowNewPasswordStrength => !IsNewPasswordEmpty;
    public bool IsPasswordWeak => ShouldShowPasswordStrength && !IsPasswordStrong;
    public bool IsNewPasswordWeak => ShouldShowNewPasswordStrength && !IsNewPasswordStrong;
    public bool IsPasswordMatch => string.Equals(PasswordForMatch, ConfirmPassword, StringComparison.Ordinal);
    public bool ShouldShowPasswordMismatch => !string.IsNullOrEmpty(ConfirmPassword) && !IsPasswordMatch;
    public bool IsLoginRunning => LoginCommand is AsyncCommand { IsRunning: true };
    public bool IsLoginIdle => !IsLoginRunning;
    public bool IsGoogleLoginRunning => OAuthLoginCommand is AsyncCommand { IsRunning: true };
    public bool IsGoogleLoginIdle => !IsGoogleLoginRunning;
    public bool IsRegisterRunning => RegisterCommand is AsyncCommand { IsRunning: true };
    public bool IsRegisterIdle => !IsRegisterRunning;
    public bool IsForgotPasswordRunning => ForgotPasswordCommand is AsyncCommand { IsRunning: true };
    public bool IsForgotPasswordIdle => !IsForgotPasswordRunning;
    public bool IsResetPasswordRunning => ResetPasswordCommand is AsyncCommand { IsRunning: true };
    public bool IsResetPasswordIdle => !IsResetPasswordRunning;
    public bool IsRefreshRunning => RefreshCommand is AsyncCommand { IsRunning: true };
    public bool IsConnectRunning => ConnectCommand is AsyncCommand { IsRunning: true };
    public string PasswordPolicyText => "Password must be at least 8 characters and include a number and a special character.";
    public string DeviceLimitMessage => Subscription?.Message
        ?? "You have reached the maximum number of active devices for your plan. Select a device to remove before continuing.";
    public string ServerSearchHint => string.IsNullOrWhiteSpace(ServerSearchText) ? "Search cities, countries, or hostnames" : ServerSearchText;
    public string QuotaText => Quota is null
        ? "Usage not loaded"
        : Quota.BytesLimit is null
            ? $"{FormatBytes(Quota.BytesUsed)} used"
            : $"{FormatBytes(Quota.BytesUsed)} of {FormatBytes(Quota.BytesLimit.Value)}";
    public string ActiveDevicesText => Subscription is null
        ? Devices.Count.ToString()
        : $"{Subscription.ActiveDevices}/{Subscription.MaxDevices}";
    public string MoneroConfirmationsText => $"{MoneroStatus?.Confirmations ?? 0}/10 Confirmations";
    public bool HasFavorites => FavoriteServers.Count > 0;
    public bool HasRecentServers => RecentServers.Count > 0;
    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (SetProperty(ref _isCompactLayout, value))
            {
                OnPropertyChanged(nameof(IsWideLayout));
            }
        }
    }
    public bool IsWideLayout => !IsCompactLayout;
    public bool IsWeekStatisticsPeriod => SelectedStatisticsPeriod.Equals("Week", StringComparison.OrdinalIgnoreCase);
    public bool IsMonthStatisticsPeriod => SelectedStatisticsPeriod.Equals("Month", StringComparison.OrdinalIgnoreCase);
    public bool IsYearStatisticsPeriod => SelectedStatisticsPeriod.Equals("Year", StringComparison.OrdinalIgnoreCase);

    private string PasswordForMatch => IsResetView ? NewPassword : Password;

    public async Task InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        finally
        {
            IsInitializing = false;
        }
    }

    private async Task InitializeCoreAsync()
    {
        AutoConnect = await _settingsStore.GetAsync<bool?>(nameof(AutoConnect), CancellationToken.None) ?? false;
        KillSwitch = await _settingsStore.GetAsync<bool?>(nameof(KillSwitch), CancellationToken.None) ?? true;
        NotificationsEnabled = await _settingsStore.GetAsync<bool?>(NotificationsEnabledKey, CancellationToken.None) ?? true;
        ServerSearchText = await _settingsStore.GetAsync<string>(ServerSearchKey, CancellationToken.None) ?? string.Empty;
        ServerSortMode = await _settingsStore.GetAsync<string>(ServerSortKey, CancellationToken.None) ?? "Ping";
        ThemePreference = _themePreferenceService.CurrentPreference;

        var favorites = await _settingsStore.GetAsync<List<int>>(FavoriteServersKey, CancellationToken.None) ?? [];
        _favoriteServerIds.Clear();
        foreach (var serverId in favorites)
        {
            _favoriteServerIds.Add(serverId);
        }

        var recent = await _settingsStore.GetAsync<List<int>>(RecentServersKey, CancellationToken.None) ?? [];
        _recentServerIds.Clear();
        _recentServerIds.AddRange(recent.Take(10));
        try
        {
            if (await _authSession.TryRestoreSessionAsync(CancellationToken.None))
            {
                IsAuthenticated = true;
                var restoredEmail = _authSession.CurrentSession?.Email ?? string.Empty;
                Email = restoredEmail;
                AccountEmail = restoredEmail;
                await LoadLocalStatisticsAsync(closeStaleActiveSession: true, CancellationToken.None);
                InvalidateAccountRefreshState();
                NotifyPlanStateChanged();
                await RefreshAccountStateAsync(CancellationToken.None);
                return;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stored session could not be restored: {ex.Message}";
        }

        Servers.Clear();
        UpdateServerPresentation();
        _statisticsProfile = new LocalStatisticsProfile();
        UpdateStatisticsPresentation();
    }

    public void UpdateLayoutMode(double width)
    {
        IsCompactLayout = width < 1060;
    }

    private async Task SelectThemeAsync(object? parameter, CancellationToken cancellationToken)
    {
        var nextPreference = ParseThemePreference(parameter);
        if (ThemePreference == nextPreference)
        {
            return;
        }

        var previousPreference = ThemePreference;
        ThemePreference = nextPreference;
        try
        {
            await _themePreferenceService.SetPreferenceAsync(nextPreference, cancellationToken);
        }
        catch (Exception ex)
        {
            ThemePreference = previousPreference;
            StatusMessage = $"Unable to save theme preference: {ex.Message}";
        }
    }

    public async Task<bool> PrepareForExitAsync(CancellationToken cancellationToken)
    {
        if (!IsExitConfirmationRequired)
        {
            return true;
        }

        (ConnectCommand as AsyncCommand)?.Cancel();
        (QuickConnectCommand as AsyncCommand)?.Cancel();
        ConnectionMessage = "Disconnecting before exit...";
        StatusMessage = ConnectionMessage;

        try
        {
            await _vpn.ShutdownAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ConnectionMessage = ex.Message;
            return false;
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusMessage = "Signing in...";
            ClearPendingLoginChallenge();
            var device = await _deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
            var response = await _backend.LoginAsync(Email, Password, device, cancellationToken);
            if (response.RequiresTwoFactor)
            {
                HandleTwoFactorChallenge(response, response.Message ?? "Enter your 2FA code to continue.", Email);
                return;
            }

            await CompleteLoginAsync(response, cancellationToken);
        }
        catch (BackendApiException ex) when (TryShowPreAuthDeviceLimit(ex, PreAuthDeviceRemovalMode.Password))
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static AppThemePreference ParseThemePreference(object? parameter)
    {
        if (parameter is AppThemePreference preference)
        {
            return preference;
        }

        if (Enum.TryParse<AppThemePreference>(parameter?.ToString(), ignoreCase: true, out var parsedPreference))
        {
            return parsedPreference;
        }

        return AppThemePreference.System;
    }

    private async Task VerifyTwoFactorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var device = await _deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(_pendingLoginToken))
            {
                StatusMessage = "A valid pending login token is required before 2FA verification.";
                return;
            }

            var response = await _backend.VerifyTwoFactorAsync(Email, TwoFactorCode, _pendingLoginToken, device, cancellationToken);
            await CompleteLoginAsync(response, cancellationToken);
        }
        catch (BackendApiException ex) when (TryShowPreAuthDeviceLimit(ex, PreAuthDeviceRemovalMode.Password))
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task VerifyRecoveryCodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusMessage = "Signing in with recovery code...";
            var device = await _deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(_pendingLoginToken))
            {
                StatusMessage = "A valid pending login token is required before recovery-code verification.";
                return;
            }

            var response = await _backend.VerifyRecoveryCodeAsync(Email, RecoveryCode, _pendingLoginToken, device, cancellationToken);
            await CompleteLoginAsync(response, cancellationToken);
        }
        catch (BackendApiException ex) when (TryShowPreAuthDeviceLimit(ex, PreAuthDeviceRemovalMode.Password))
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task CompleteLoginAsync(LoginResponse response, CancellationToken cancellationToken)
    {
        if (!response.Success || string.IsNullOrWhiteSpace(response.Token) || string.IsNullOrWhiteSpace(response.RefreshToken))
        {
            if (response.RequiresTwoFactor)
            {
                HandleTwoFactorChallenge(response, response.Message ?? "Two-factor authentication required.", response.Email ?? Email);
                return;
            }

            StatusMessage = response.Message ?? "Login failed.";
            return;
        }

        ClearPendingLoginChallenge();
        var session = new AuthSession(
            response.Token,
            response.RefreshToken,
            response.Email ?? Email,
            response.UserId ?? string.Empty,
            response.DeviceId ?? string.Empty,
            response.ActiveDevices,
            response.MaxDevices,
            response.PlanType ?? "Free");

        ResetAdBlockingState();
        await _authSession.SetSessionAsync(session, cancellationToken);
        await LoadLocalStatisticsAsync(closeStaleActiveSession: true, cancellationToken);
        InvalidateAccountRefreshState();
        IsAuthenticated = true;
        Email = session.Email;
        AccountEmail = session.Email;
        NotifyPlanStateChanged();
        StatusMessage = "Signed in.";
        await RefreshAccountStateAsync(cancellationToken);
    }

    private void HandleTwoFactorChallenge(LoginResponse response, string defaultMessage, string fallbackEmail)
    {
        _pendingLoginToken = response.PendingLoginToken ?? string.Empty;
        Email = response.Email ?? fallbackEmail;
        TwoFactorCode = string.Empty;
        RecoveryCode = string.Empty;
        AuthView = "TwoFactor";
        StatusMessage = defaultMessage;
    }

    private void ClearPendingLoginChallenge()
    {
        _pendingLoginToken = string.Empty;
        _preAuthDeviceRemovalMode = PreAuthDeviceRemovalMode.None;
        TwoFactorCode = string.Empty;
        RecoveryCode = string.Empty;
    }

    private void DismissDeviceLimit()
    {
        IsDeviceLimitModalVisible = false;
        if (!IsAuthenticated)
        {
            Devices.Clear();
            SelectedDevice = null;
            _preAuthDeviceRemovalMode = PreAuthDeviceRemovalMode.None;
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusMessage = "Creating account...";
            var device = await _deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
            var response = await _backend.RegisterAsync(
                new RegisterRequest(Email, Password, ConfirmPassword, device.AppVersion),
                cancellationToken);
            RegisteredUserId = response.UserId ?? RegisteredUserId;
            Email = string.IsNullOrWhiteSpace(response.Email) ? Email : response.Email;
            StatusMessage = response.Message ?? "Registration started. Confirm your email, then sign in.";
            AuthView = string.IsNullOrWhiteSpace(RegisteredUserId) ? "Login" : "EmailConfirmation";
            if (AuthView == "EmailConfirmation")
            {
                StartEmailConfirmationPolling();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task OAuthLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusMessage = "Opening Google sign-in in your browser...";
            ClearPendingLoginChallenge();
            var device = await _deviceIdentity.GetRegistrationPayloadAsync(cancellationToken);
            var authorizationCode = await _googleOAuth.AuthenticateAsync(cancellationToken);
            StatusMessage = "Signing in with Google...";
            var response = await _backend.LoginWithGoogleCodeAsync(authorizationCode, device, cancellationToken);
            if (response.RequiresTwoFactor)
            {
                HandleTwoFactorChallenge(response, response.Message ?? "Enter your 2FA code to continue.", response.Email ?? Email);
                return;
            }

            await CompleteLoginAsync(response, cancellationToken);
        }
        catch (BackendApiException ex) when (TryShowPreAuthDeviceLimit(ex, PreAuthDeviceRemovalMode.OAuthCode))
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private bool TryShowPreAuthDeviceLimit(BackendApiException ex, PreAuthDeviceRemovalMode mode)
    {
        if (ex.StatusCode != HttpStatusCode.Conflict || string.IsNullOrWhiteSpace(ex.ResponseBody))
        {
            return false;
        }

        DeviceLimitExceededResponse? limit;
        try
        {
            limit = JsonSerializer.Deserialize<DeviceLimitExceededResponse>(ex.ResponseBody, JsonOptions.Default);
        }
        catch (JsonException)
        {
            return false;
        }

        if (limit is null || !string.Equals(limit.ErrorCode, "DEVICE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _preAuthDeviceRemovalMode = mode;
        Devices.Clear();
        foreach (var device in limit.Devices ?? [])
        {
            Devices.Add(device with { IsActive = true, IsCurrent = false });
        }

        SelectedDevice = Devices.FirstOrDefault();
        IsDeviceLimitModalVisible = Devices.Count > 0;
        StatusMessage = limit.Message ?? ex.Message;
        if (!IsDeviceLimitModalVisible)
        {
            StatusMessage = "Device limit reached, but no removable active devices were returned.";
        }

        return true;
    }

    private async Task CheckEmailConfirmationAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(RegisteredUserId))
            {
                StatusMessage = "Registration user id is missing. Please register again or sign in after confirming your email.";
                return;
            }

            var status = await _backend.CheckConfirmationAsync(RegisteredUserId, cancellationToken);
            StatusMessage = status.Message ?? "Email confirmation status checked.";
            if (status.EmailConfirmed)
            {
                _emailPollingCts?.Cancel();
                AuthView = "Login";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task ResendConfirmationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _backend.ResendConfirmationAsync(Email, cancellationToken);
            StatusMessage = response.Message ?? "Confirmation email sent.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void StartEmailConfirmationPolling()
    {
        _emailPollingCts?.Cancel();
        _emailPollingCts = new CancellationTokenSource();
        var token = _emailPollingCts.Token;

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 45 && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), token);
                    if (string.IsNullOrWhiteSpace(RegisteredUserId))
                    {
                        continue;
                    }

                    var status = await _backend.CheckConfirmationAsync(RegisteredUserId, token);
                    if (!status.EmailConfirmed)
                    {
                        continue;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = status.Message ?? "Email confirmed. You can now sign in.";
                        AuthView = "Login";
                    });
                    _emailPollingCts?.Cancel();
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => StatusMessage = ex.Message);
                }
            }
        }, token);
    }

    private async Task ForgotPasswordAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _backend.ForgotPasswordAsync(new ForgotPasswordRequest(Email), cancellationToken);
            StatusMessage = response.Message ?? "Password reset email sent.";
            AuthView = "Reset";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task ResetPasswordAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
            {
                StatusMessage = "New password and confirmation do not match.";
                return;
            }

            var response = await _backend.ResetPasswordAsync(new ResetPasswordRequest(Email, ResetToken, NewPassword), cancellationToken);
            StatusMessage = response.Message ?? "Password reset complete.";
            AuthView = "Login";
            Password = string.Empty;
            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            ResetToken = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task LogoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            var refreshToken = await _authSession.GetRefreshTokenAsync(cancellationToken);
            await _backend.LogoutAsync(refreshToken, cancellationToken);
        }
        catch (Exception)
        {
            StatusMessage = "Backend logout could not be completed. The local session will still be cleared.";
        }

        Exception? cleanupFailure = null;
        try
        {
            await FinalizeLocalStatisticsSessionAsync("LoggedOut", cancellationToken);
        }
        catch (Exception)
        {
            cleanupFailure = new InvalidOperationException("local statistics cleanup failed");
        }

        try
        {
            await _authSession.ClearSessionAsync(cancellationToken);
        }
        catch (Exception)
        {
            cleanupFailure ??= new InvalidOperationException("secure session cleanup failed");
        }
        finally
        {
            ResetSignedOutState();
        }

        if (cleanupFailure is not null)
        {
            StatusMessage = "Signed out locally, but secure credential cleanup needs attention.";
        }
    }

    private Task RefreshAsync(CancellationToken cancellationToken)
        => RefreshAccountStateAsync(cancellationToken);

    private async Task LoadLocalStatisticsAsync(bool closeStaleActiveSession, CancellationToken cancellationToken)
    {
        _statisticsProfile = await _localStatisticsStore.LoadProfileAsync(_authSession.CurrentSession, closeStaleActiveSession, cancellationToken);
        var period = await _localStatisticsStore.GetStatisticsPeriodAsync(_authSession.CurrentSession, cancellationToken) ?? "Week";
        if (SetProperty(ref _statisticsPeriod, period))
        {
            OnPropertyChanged(nameof(IsWeekStatisticsPeriod));
            OnPropertyChanged(nameof(IsMonthStatisticsPeriod));
            OnPropertyChanged(nameof(IsYearStatisticsPeriod));
        }

        UpdateStatisticsPresentation();
    }

    private Task RefreshAccountStateAsync(CancellationToken cancellationToken)
    {
        var session = _authSession.CurrentSession;
        if (session is null)
        {
            return Task.CompletedTask;
        }

        lock (_accountStateRefreshLock)
        {
            if (_accountStateRefreshTask is { IsCompleted: false })
            {
                return _accountStateRefreshTask;
            }

            var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _accountStateRefreshCts = refreshCts;
            var refreshGeneration = Volatile.Read(ref _accountStateRefreshGeneration);
            var sessionIdentity = GetSessionIdentity(session);
            _accountStateRefreshTask = RefreshAccountStateCoreAsync(refreshGeneration, sessionIdentity, refreshCts);
            return _accountStateRefreshTask;
        }
    }

    private async Task RefreshAccountStateCoreAsync(int refreshGeneration, string sessionIdentity, CancellationTokenSource refreshCts)
    {
        var cancellationToken = refreshCts.Token;
        var entitlementRevision = Volatile.Read(ref _accountEntitlementRevision);
        try
        {
            var snapshot = await ExecuteAuthenticatedAsync(async token =>
            {
                var serversLoaded = await LoadServersCoreAsync(token);
                var quota = await _backend.GetUsageQuotaAsync(token);
                var subscription = await _backend.GetSubscriptionStatusAsync(token);
                var devices = await _backend.GetDevicesAsync(token);
                var twoFactor = await _backend.GetTwoFactorStatusAsync(token);
                var certificates = subscription.IsPro
                    ? await _backend.GetCertificatesAsync(token)
                    : [];
                var dnsPreference = await LoadDnsPreferenceForRefreshAsync(token);
                return new RefreshSnapshot(
                    serversLoaded,
                    quota,
                    subscription,
                    devices,
                    twoFactor,
                    certificates,
                    dnsPreference,
                    entitlementRevision);
            }, cancellationToken);

            if (!ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, cancellationToken))
            {
                return;
            }

            if (_isRecoveringAdBlockingEntitlement ||
                snapshot.EntitlementRevision != Volatile.Read(ref _accountEntitlementRevision))
            {
                return;
            }

            Quota = snapshot.Quota;
            Subscription = snapshot.Subscription;
            var selectedCertificateId = SelectedCertificate?.Id;
            Devices.Clear();
            foreach (var device in snapshot.Devices)
            {
                Devices.Add(device);
            }

            Certificates.Clear();
            foreach (var certificate in snapshot.Certificates)
            {
                Certificates.Add(certificate);
            }

            if (!snapshot.Subscription.IsPro)
            {
                SelectedCertificate = null;
            }
            else
            {
                SelectedCertificate = selectedCertificateId.HasValue
                    ? Certificates.FirstOrDefault(certificate => certificate.Id == selectedCertificateId.Value) ?? Certificates.FirstOrDefault()
                    : Certificates.FirstOrDefault();
            }

            UpdateTwoFactorState(snapshot.TwoFactor.Is2faEnabled, snapshot.TwoFactor.HasAuthenticator);
            if (!IsUpdatingAdBlocking &&
                snapshot.DnsPreference.Revision == Volatile.Read(ref _dnsPreferenceRevision))
            {
                if (snapshot.DnsPreference.Response is { } dnsPreference)
                {
                    ApplyDnsPreference(dnsPreference);
                }
                else
                {
                    SetDnsPreferenceUnavailable();
                }
            }

            UpdateServerPresentation();
            UpdateStatisticsPresentation();
            if (snapshot.ServersLoaded)
            {
                StatusMessage = "Account data refreshed.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            lock (_accountStateRefreshLock)
            {
                if (ReferenceEquals(_accountStateRefreshCts, refreshCts))
                {
                    _accountStateRefreshTask = null;
                    _accountStateRefreshCts?.Dispose();
                    _accountStateRefreshCts = null;
                }
            }
        }
    }

    private async Task<bool> LoadServersAsync(CancellationToken cancellationToken)
        => await ExecuteAuthenticatedAsync(LoadServersCoreAsync, cancellationToken);

    private async Task<bool> LoadServersCoreAsync(CancellationToken cancellationToken)
    {
        var serverLoadAttempts = 0;
        try
        {
            InvalidateLatencyRefreshState();
            var selectedServerId = SelectedServer?.Id;
            var connectedServerId = ConnectedServer?.Id;
            IReadOnlyList<VpnServer> servers;
            while (true)
            {
                serverLoadAttempts++;
                StartupDiagnostics.Log($"vpn-servers-load-attempt attempt={serverLoadAttempts}");
                try
                {
                    servers = await _backend.GetServersAsync(cancellationToken);
                    break;
                }
                catch (Exception ex) when (serverLoadAttempts == 1 && IsTransientServerLoadFailure(ex, cancellationToken))
                {
                    StartupDiagnostics.Log($"vpn-servers-load-retry type={ex.GetType().Name} delay_ms={ServerLoadRetryDelay.TotalMilliseconds:F0}");
                    await Task.Delay(ServerLoadRetryDelay, cancellationToken);
                }
            }

            Servers.Clear();
            foreach (var server in servers)
            {
                server.PingMs = 0;
                Servers.Add(server);
            }

            SelectedServer = selectedServerId is null
                ? null
                : Servers.FirstOrDefault(server => server.Id == selectedServerId);
            ConnectedServer = connectedServerId is null
                ? null
                : Servers.FirstOrDefault(server => server.Id == connectedServerId);
            _serversLoadedFromBackend = true;
            UpdateServerPresentation();
            StartLatencyRefresh();
            UpdateStatisticsPresentation();
            return true;
        }
        catch (BackendApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Let ExecuteAuthenticatedAsync refresh the session and retry the authorized operation.
            throw;
        }
        catch (Exception ex)
        {
            if (!_serversLoadedFromBackend)
            {
                Servers.Clear();
                UpdateServerPresentation();
                UpdateStatisticsPresentation();
            }

            var retrySuffix = serverLoadAttempts > 1 ? " after retry" : string.Empty;
            StatusMessage = $"Unable to load VPN servers{retrySuffix}: {ex.Message}";
            StartupDiagnostics.Log($"vpn-servers-load-failed attempts={serverLoadAttempts} type={ex.GetType().Name}");
            return false;
        }
    }

    private async Task<DnsPreferenceLoadResult> LoadDnsPreferenceForRefreshAsync(CancellationToken cancellationToken)
    {
        var revision = Volatile.Read(ref _dnsPreferenceRevision);
        try
        {
            return new DnsPreferenceLoadResult(
                await _backend.GetDnsPreferenceAsync(cancellationToken),
                revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BackendApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"dns-preference-load-failed type={ex.GetType().Name}");
            return new DnsPreferenceLoadResult(null, revision);
        }
    }

    private async Task UpdateAdBlockingAsync(
        bool requestedEnabled,
        DnsPreferenceResponse previousPreference,
        CancellationToken cancellationToken)
    {
        var refreshGeneration = Volatile.Read(ref _accountStateRefreshGeneration);
        var sessionIdentity = GetSessionIdentity(_authSession.CurrentSession);
        var updateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousUpdateCts = _adBlockingUpdateCts;
        _adBlockingUpdateCts = updateCts;
        previousUpdateCts?.Cancel();
        var updateToken = updateCts.Token;
        Interlocked.Increment(ref _dnsPreferenceRevision);
        IsUpdatingAdBlocking = true;
        try
        {
            var preference = await ExecuteAuthenticatedAsync(
                async token =>
                {
                    if (!ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, token))
                    {
                        throw new OperationCanceledException(token);
                    }

                    return await _backend.UpdateDnsPreferenceAsync(requestedEnabled, token);
                },
                updateToken);
            if (!ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, updateToken))
            {
                return;
            }

            ApplyDnsPreference(preference);
            StatusMessage = preference.PropagationSeconds > 0
                ? $"Ad Blocking request saved. Changes can take up to {preference.PropagationSeconds} seconds."
                : "Ad Blocking request saved.";
        }
        catch (BackendApiException ex) when (IsProRequiredError(ex))
        {
            if (!ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, CancellationToken.None))
            {
                return;
            }

            _isRecoveringAdBlockingEntitlement = true;
            Interlocked.Increment(ref _accountEntitlementRevision);
            try
            {
                await RecoverFromProRequiredAsync(
                    previousPreference,
                    refreshGeneration,
                    sessionIdentity,
                    updateToken);
            }
            catch (OperationCanceledException) when (updateToken.IsCancellationRequested)
            {
                if (ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, CancellationToken.None))
                {
                    ApplyDnsPreference(previousPreference);
                }
            }
            finally
            {
                if (ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, CancellationToken.None))
                {
                    Interlocked.Increment(ref _accountEntitlementRevision);
                    _isRecoveringAdBlockingEntitlement = false;
                }
            }
        }
        catch (OperationCanceledException) when (updateToken.IsCancellationRequested)
        {
            if (ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, CancellationToken.None))
            {
                ApplyDnsPreference(previousPreference);
            }
        }
        catch (Exception ex)
        {
            if (ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, CancellationToken.None))
            {
                ApplyDnsPreference(previousPreference);
                StatusMessage = "Ad Blocking could not be updated. Your previous setting was restored.";
                StartupDiagnostics.Log($"dns-preference-update-failed type={ex.GetType().Name}");
            }
        }
        finally
        {
            if (ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, CancellationToken.None))
            {
                Interlocked.Increment(ref _dnsPreferenceRevision);
                IsUpdatingAdBlocking = false;
            }

            if (ReferenceEquals(_adBlockingUpdateCts, updateCts))
            {
                _adBlockingUpdateCts = null;
            }

            updateCts.Dispose();
        }
    }

    private async Task RecoverFromProRequiredAsync(
        DnsPreferenceResponse previousPreference,
        int refreshGeneration,
        string sessionIdentity,
        CancellationToken cancellationToken)
    {
        DnsPreferenceResponse? authoritativePreference = null;
        SubscriptionStatus? authoritativeSubscription = null;
        try
        {
            authoritativePreference = await ExecuteAuthenticatedAsync(
                _backend.GetDnsPreferenceAsync,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            StartupDiagnostics.Log($"dns-preference-pro-recovery-load-failed type={ex.GetType().Name}");
        }

        try
        {
            authoritativeSubscription = await ExecuteAuthenticatedAsync(
                _backend.GetSubscriptionStatusAsync,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            StartupDiagnostics.Log($"dns-preference-pro-recovery-subscription-failed type={ex.GetType().Name}");
        }

        if (!ShouldApplyAccountRefresh(refreshGeneration, sessionIdentity, cancellationToken))
        {
            return;
        }

        if (authoritativeSubscription is not null)
        {
            Subscription = authoritativeSubscription;
        }

        if (authoritativePreference is not null)
        {
            ApplyDnsPreference(authoritativePreference);
        }
        else
        {
            SetAdBlockingToggleState(previousPreference.RequestedEnabled);
            SetDnsPreferenceUnavailable();
        }

        _isAdBlockingProRequired = true;
        NotifyAdBlockingStateChanged();

        NavigateToUpgradeSettings("Ad Blocking is available with a Pro subscription.");
    }

    private static bool IsProRequiredError(BackendApiException exception)
    {
        if (exception.StatusCode != HttpStatusCode.Forbidden || string.IsNullOrWhiteSpace(exception.ResponseBody))
        {
            return false;
        }

        try
        {
            var error = JsonSerializer.Deserialize<BackendErrorCode>(exception.ResponseBody, JsonOptions.Default);
            return string.Equals(error?.ErrorCode, "PRO_REQUIRED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(error?.Code, "PRO_REQUIRED", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ApplyDnsPreference(DnsPreferenceResponse preference)
    {
        _isAdBlockingProRequired = false;
        _dnsPreference = preference;
        SetAdBlockingToggleState(preference.RequestedEnabled);
        NotifyAdBlockingStateChanged();
    }

    private void SetDnsPreferenceUnavailable()
    {
        _dnsPreference = null;
        NotifyAdBlockingStateChanged();
    }

    private void ResetAdBlockingState()
    {
        _adBlockingUpdateCts?.Cancel();
        Interlocked.Increment(ref _accountEntitlementRevision);
        Interlocked.Increment(ref _dnsPreferenceRevision);
        _isRecoveringAdBlockingEntitlement = false;
        _isAdBlockingProRequired = false;
        _dnsPreference = null;
        IsUpdatingAdBlocking = false;
        SetAdBlockingToggleState(false);
        NotifyAdBlockingStateChanged();
    }

    private void SetAdBlockingToggleState(bool value)
    {
        _isApplyingAdBlockingState = true;
        try
        {
            AdBlockingEnabled = value;
        }
        finally
        {
            _isApplyingAdBlockingState = false;
        }
    }

    private void NotifyAdBlockingStateChanged()
    {
        OnPropertyChanged(nameof(IsAdBlockingSettingsAvailable));
        OnPropertyChanged(nameof(CanToggleAdBlocking));
        OnPropertyChanged(nameof(ShowAdBlockingProBadge));
        OnPropertyChanged(nameof(ShowAdBlockingUpgradeAction));
        OnPropertyChanged(nameof(IsAdBlockingEffectivelyEnabled));
        OnPropertyChanged(nameof(IsAdBlockingPaused));
        OnPropertyChanged(nameof(ShowAdBlockingPropagation));
        OnPropertyChanged(nameof(AdBlockingPropagationText));
        OnPropertyChanged(nameof(AdBlockingStatusText));
    }

    private void SetCurrentSectionWithoutAccountRefresh(string section)
    {
        _suppressAccountRefreshOnSectionTransition = true;
        try
        {
            CurrentSection = section;
        }
        finally
        {
            _suppressAccountRefreshOnSectionTransition = false;
        }
    }

    private static bool IsTransientServerLoadFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is BackendApiException backendException)
        {
            return backendException.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                   (int)backendException.StatusCode >= 500;
        }

        return exception is HttpRequestException or TimeoutException;
    }

    public Task RefreshCurrentAccountStateAsync()
        => RefreshAccountStateAsync(CancellationToken.None);

    public TwoFactorSetupDialogViewModel CreateTwoFactorSetupDialogViewModel()
    {
        if (_pendingTwoFactorSetup is null)
        {
            throw new InvalidOperationException("Two-factor setup has not been initialized.");
        }

        return new TwoFactorSetupDialogViewModel(_authSession, _backend, _pendingTwoFactorSetup);
    }

    public void CancelTwoFactorSetupFlow()
    {
        _pendingTwoFactorSetup = null;
        TwoFactorManagementCode = string.Empty;
        TwoFactorSharedKey = string.Empty;
        TwoFactorAuthenticatorUri = string.Empty;
        SetTwoFactorToggleState(false);
    }

    public void CompleteTwoFactorSetupFlow()
    {
        _pendingTwoFactorSetup = null;
        TwoFactorManagementCode = string.Empty;
        TwoFactorSharedKey = string.Empty;
        TwoFactorAuthenticatorUri = string.Empty;
    }

    public void CancelTwoFactorDisableFlow()
        => SetTwoFactorToggleState(true);

    public async Task<bool> ConfirmTwoFactorDisableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAuthenticatedAsync(_backend.DisableTwoFactorAsync, cancellationToken);
            StatusMessage = response.Message ?? "2FA disabled.";
            await RefreshAccountStateAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            SetTwoFactorToggleState(true);
            return false;
        }
    }

    private async Task HandleTwoFactorToggleChangedAsync(bool enabled)
    {
        if (enabled)
        {
            await BeginTwoFactorSetupFlowAsync(CancellationToken.None);
            return;
        }

        TwoFactorDisableDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTwoFactorState(bool isEnabled, bool hasAuthenticator)
    {
        TwoFactorEnabled = isEnabled;
        HasAuthenticator = hasAuthenticator;
        SetTwoFactorToggleState(isEnabled);
    }

    private void SetTwoFactorToggleState(bool value)
    {
        _isUpdatingTwoFactorToggleState = true;
        try
        {
            TwoFactorToggleEnabled = value;
        }
        finally
        {
            _isUpdatingTwoFactorToggleState = false;
        }
    }

    private async Task BeginTwoFactorSetupFlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var setup = await ExecuteAuthenticatedAsync(_backend.SetupTwoFactorAsync, cancellationToken);
            _pendingTwoFactorSetup = setup;
            TwoFactorSharedKey = setup.SharedKey ?? setup.ManualEntryKey ?? string.Empty;
            TwoFactorAuthenticatorUri = setup.AuthenticatorUri ?? string.Empty;
            TwoFactorManagementCode = string.Empty;
            StatusMessage = setup.Message ?? "Authenticator setup created.";
            TwoFactorSetupDialogRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _pendingTwoFactorSetup = null;
            StatusMessage = ex.Message;
            SetTwoFactorToggleState(false);
        }
    }

    private async Task SetupTwoFactorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var setup = await ExecuteAuthenticatedAsync(_backend.SetupTwoFactorAsync, cancellationToken);
            TwoFactorSharedKey = setup.SharedKey ?? setup.ManualEntryKey ?? string.Empty;
            TwoFactorAuthenticatorUri = setup.AuthenticatorUri ?? string.Empty;
            StatusMessage = setup.Message ?? "Authenticator setup created.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task EnableTwoFactorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var code = string.IsNullOrWhiteSpace(TwoFactorManagementCode) ? TwoFactorCode : TwoFactorManagementCode;
            var response = await ExecuteAuthenticatedAsync(token => _backend.EnableTwoFactorAsync(code, token), cancellationToken);
            StatusMessage = response.Message ?? "2FA enabled.";
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task DisableTwoFactorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAuthenticatedAsync(_backend.DisableTwoFactorAsync, cancellationToken);
            StatusMessage = response.Message ?? "2FA disabled.";
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task ResetTwoFactorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAuthenticatedAsync(_backend.ResetTwoFactorAsync, cancellationToken);
            TwoFactorSharedKey = string.Empty;
            TwoFactorAuthenticatorUri = string.Empty;
            RecoveryCodesText = string.Empty;
            StatusMessage = response.Message ?? "2FA reset.";
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task GenerateRecoveryCodesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAuthenticatedAsync(_backend.GenerateRecoveryCodesAsync, cancellationToken);
            RecoveryCodesText = string.Join("  ", response.RecoveryCodes ?? []);
            StatusMessage = response.Message ?? "Recovery codes generated.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RemoveSelectedDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SelectedDevice is null)
            {
                StatusMessage = "Choose a device first.";
                return;
            }

            if (!SelectedDevice.IsActive)
            {
                StatusMessage = "Choose an active device to log out.";
                return;
            }

            if (SelectedDevice.IsCurrent)
            {
                StatusMessage = "Use sign out to log out this device.";
                return;
            }

            if (!IsAuthenticated && _preAuthDeviceRemovalMode == PreAuthDeviceRemovalMode.Password)
            {
                var removalResponse = await _backend.RemovePreAuthDeviceAsync(Email, Password, SelectedDevice.Id, cancellationToken);
                StatusMessage = removalResponse.Message ?? "Device removed. Sign in again to continue.";
                DismissDeviceLimit();
                return;
            }

            if (!IsAuthenticated && _preAuthDeviceRemovalMode == PreAuthDeviceRemovalMode.OAuthCode)
            {
                StatusMessage = "Confirming Google account before removing device...";
                var authorizationCode = await _googleOAuth.AuthenticateAsync(cancellationToken);
                var removalResponse = await _backend.RemovePreAuthOAuthDeviceWithCodeAsync("Google", authorizationCode, SelectedDevice.Id, cancellationToken);
                StatusMessage = removalResponse.Message ?? "Device removed. Sign in with Google again to continue.";
                DismissDeviceLimit();
                return;
            }

            var response = await ExecuteAuthenticatedAsync(token => _backend.RemoveDeviceAsync(SelectedDevice.Id, token), cancellationToken);
            StatusMessage = response.Message ?? "Device logged out.";
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task DeleteSelectedDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SelectedDevice is null)
            {
                StatusMessage = "Choose an inactive device first.";
                return;
            }

            var response = await ExecuteAuthenticatedAsync(token => _backend.DeleteDeviceAsync(SelectedDevice.Id, token), cancellationToken);
            StatusMessage = response.Message ?? "Inactive device deleted.";
            SelectedDevice = null;
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RemoveOtherDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAuthenticatedAsync(_backend.RemoveAllOtherDevicesAsync, cancellationToken);
            StatusMessage = response.Message ?? "Other devices logged out.";
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RemoveInactiveDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAuthenticatedAsync(_backend.RemoveAllInactiveDevicesAsync, cancellationToken);
            StatusMessage = response.Message ?? "Inactive devices cleaned up.";
            await RefreshAccountStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task OpenUpgradeAsync(CancellationToken cancellationToken)
    {
        CurrentSection = "Upgrade";
        await RestoreLatestMoneroInvoiceAsync(cancellationToken);
    }

    private void GoBackToSettings()
    {
        StopMoneroPaymentTimer();
        CurrentSection = "Settings";
    }

    private async Task SelectCardAsync(CancellationToken cancellationToken)
    {
        StartupDiagnostics.Log("checkout-create-command-started");
        CancelCardCheckoutSession();
        var sessionGeneration = ++_cardCheckoutSessionGeneration;
        using var checkoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cardCheckoutCts = checkoutCts;

        IsCardSelected = true;
        IsMoneroSelected = false;
        IsLoadingPayment = true;
        IsPaymentComplete = false;
        CheckoutUrl = string.Empty;
        _cardTransactionId = string.Empty;
        StopMoneroPaymentTimer();

        try
        {
            var checkout = await ExecuteAuthenticatedAsync(
                token => _backend.CreateCardCheckoutAsync(SelectedBillingCycle, token),
                checkoutCts.Token);
            StartupDiagnostics.Log("checkout-create-api-completed");
            if (!IsCurrentCardCheckoutSession(sessionGeneration, checkoutCts))
            {
                return;
            }

            CheckoutUrl = checkout.CheckoutUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(CheckoutUrl))
            {
                StartupDiagnostics.Log("checkout-create-invalid reason=missing-url");
                StatusMessage = "Checkout URL was not returned by the backend.";
                return;
            }

            _cardTransactionId = checkout.TransactionId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_cardTransactionId))
            {
                StartupDiagnostics.Log("checkout-create-invalid reason=missing-transaction-id");
                StatusMessage = "Checkout transaction ID was not returned by the backend.";
                return;
            }

            StatusMessage = "Opening card checkout in the app.";
            IsLoadingPayment = false;
            IsEmbeddedCheckoutOpen = true;
            var result = await _cardCheckoutWindow.ShowCheckoutAsync(
                new CardCheckoutWindowRequest(CheckoutUrl, _cardTransactionId, checkout.BillingCycle, checkout.AmountUsd, checkout.Currency),
                checkoutCts.Token);
            if (IsCurrentCardCheckoutSession(sessionGeneration, checkoutCts))
            {
                await HandleCardCheckoutWindowResultAsync(result, checkoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (checkoutCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            StartupDiagnostics.Log("checkout-create-command-canceled");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"checkout-create-command-error type={ex.GetType().Name}");
            if (IsCurrentCardCheckoutSession(sessionGeneration, checkoutCts))
            {
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            if (IsCurrentCardCheckoutSession(sessionGeneration, checkoutCts))
            {
                _cardCheckoutCts = null;
                IsLoadingPayment = false;
                IsEmbeddedCheckoutOpen = false;
            }
        }
    }

    private async Task HandleCardCheckoutWindowResultAsync(CardCheckoutWindowResult result, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case CardCheckoutWindowResult.Paid:
                IsPaymentComplete = true;
                await RefreshAccountStateAsync(cancellationToken);
                SetCurrentSectionWithoutAccountRefresh("Settings");
                StatusMessage = "Card payment confirmed. Your Pro subscription is active.";
                break;
            case CardCheckoutWindowResult.Unavailable:
                StatusMessage = "In-app checkout is unavailable. Use the browser fallback below to continue.";
                break;
            case CardCheckoutWindowResult.Failed:
                StatusMessage = "Card payment failed. Your account was not upgraded.";
                break;
            case CardCheckoutWindowResult.Canceled:
                StatusMessage = "Card checkout was canceled. Your account was not upgraded.";
                break;
            case CardCheckoutWindowResult.Refunded:
                StatusMessage = "The card payment was refunded. Your account was not upgraded.";
                break;
            case CardCheckoutWindowResult.TimedOut:
                StatusMessage = "Payment confirmation is taking longer than expected. Your checkout URL remains available.";
                break;
            default:
                StatusMessage = "Card checkout closed. Use Continue in Browser to keep monitoring this checkout.";
                break;
        }
    }

    private async Task OpenCardCheckoutInBrowserAsync(CancellationToken cancellationToken)
    {
        StartupDiagnostics.Log("checkout-browser-command-started");
        if (string.IsNullOrWhiteSpace(CheckoutUrl))
        {
            StartupDiagnostics.Log("checkout-browser-command-rejected reason=missing-url");
            StatusMessage = "Checkout URL is not available yet.";
            return;
        }

        var open = await _cardCheckoutWindow.OpenInBrowserAsync(CheckoutUrl, cancellationToken);
        if (!open.Success)
        {
            StartupDiagnostics.Log("checkout-browser-command-failed");
            StatusMessage = "Browser checkout could not be opened automatically. Try again or use another browser.";
            return;
        }

        StartupDiagnostics.Log("checkout-browser-command-succeeded");

        if (string.IsNullOrWhiteSpace(_cardTransactionId))
        {
            StatusMessage = "Card checkout opened in your browser, but automatic confirmation is unavailable for this session.";
            return;
        }

        StatusMessage = "Card checkout opened in your browser. LibreGuard is monitoring payment automatically.";
        if (_cardCheckoutWindow.IsCheckoutActive)
        {
            return;
        }

        CancelCardCheckoutSession();
        var sessionGeneration = ++_cardCheckoutSessionGeneration;
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cardCheckoutCts = monitorCts;
        try
        {
            var result = await _cardCheckoutWindow.MonitorCheckoutAsync(_cardTransactionId, monitorCts.Token);
            if (IsCurrentCardCheckoutSession(sessionGeneration, monitorCts))
            {
                await HandleCardCheckoutWindowResultAsync(result, monitorCts.Token);
            }
        }
        catch (OperationCanceledException) when (monitorCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (IsCurrentCardCheckoutSession(sessionGeneration, monitorCts))
            {
                _cardCheckoutCts = null;
            }
        }
    }

    private async Task SelectMoneroAsync(CancellationToken cancellationToken)
    {
        IsMoneroSelected = true;
        IsCardSelected = false;
        IsLoadingPayment = true;
        IsPaymentComplete = false;
        CheckoutUrl = string.Empty;
        _cardTransactionId = string.Empty;

        try
        {
            MoneroPrice = await ExecuteAuthenticatedAsync(
                token => _backend.GetMoneroPriceAsync(SelectedBillingCycle, token),
                cancellationToken);
            MoneroInvoice = await ExecuteAuthenticatedAsync(
                token => _backend.CreateMoneroInvoiceAsync(SelectedBillingCycle, token),
                cancellationToken);

            if (MoneroInvoice is not null)
            {
                await CheckPaymentStatusAsync(cancellationToken);
                StartMoneroPaymentTimer();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoadingPayment = false;
        }
    }

    private void SwitchPaymentMethod()
    {
        CancelCardCheckoutSession();
        IsMoneroSelected = false;
        IsCardSelected = false;
        IsLoadingPayment = false;
        IsPaymentComplete = false;
        CheckoutUrl = string.Empty;
        _cardTransactionId = string.Empty;
        MoneroInvoice = null;
        MoneroStatus = null;
        Shortfall = 0;
        TimeRemaining = string.Empty;
        StopMoneroPaymentTimer();
    }

    private bool IsCurrentCardCheckoutSession(int generation, CancellationTokenSource cancellation)
        => generation == _cardCheckoutSessionGeneration && ReferenceEquals(_cardCheckoutCts, cancellation);

    private void CancelCardCheckoutSession()
    {
        _cardCheckoutSessionGeneration++;
        IsEmbeddedCheckoutOpen = false;
        var cancellation = _cardCheckoutCts;
        _cardCheckoutCts = null;
        cancellation?.Cancel();
        _cardCheckoutWindow.CancelCheckout();
    }

    private async Task CheckPaymentStatusAsync(CancellationToken cancellationToken)
    {
        if (MoneroInvoice is null || string.IsNullOrWhiteSpace(MoneroInvoice.InvoiceId))
        {
            return;
        }

        IsLoadingPayment = true;
        try
        {
            MoneroStatus = await ExecuteAuthenticatedAsync(
                token => _backend.GetMoneroPaymentStatusAsync(MoneroInvoice.InvoiceId, token),
                cancellationToken);
            Shortfall = Math.Max(0, MoneroStatus.AmountRequired - MoneroStatus.AmountReceived);
            if (MoneroStatus.Confirmations >= MoneroStatus.RequiredConfirmations)
            {
                IsPaymentComplete = true;
                await RefreshAccountStateAsync(cancellationToken);
                StopMoneroPaymentTimer();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoadingPayment = false;
        }
    }

    private async Task CopyAddressAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(MoneroInvoice?.PaymentAddress))
        {
            return;
        }

        await _clipboard.SetTextAsync(MoneroInvoice.PaymentAddress, cancellationToken);
        StatusMessage = "Monero address copied.";
    }

    private async Task CopyAmountAsync(CancellationToken cancellationToken)
    {
        if (MoneroPrice is null)
        {
            return;
        }

        await _clipboard.SetTextAsync(MoneroPrice.XmrAmount.ToString(CultureInfo.InvariantCulture), cancellationToken);
        StatusMessage = "Monero amount copied.";
    }

    private async Task RestoreLatestMoneroInvoiceAsync(CancellationToken cancellationToken)
    {
        if (_authSession.CurrentSession is null)
        {
            return;
        }

        try
        {
            var latest = await ExecuteAuthenticatedAsync(_backend.GetLatestMoneroInvoiceAsync, cancellationToken);
            if (latest.CreatedAt.AddHours(24) <= DateTimeOffset.UtcNow)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(latest.BillingCycle))
            {
                BillingCycle = latest.BillingCycle;
            }

            MoneroInvoice = latest;
            IsMoneroSelected = true;
            IsCardSelected = false;
            await UpdateMoneroPriceAsync(cancellationToken);
            await CheckPaymentStatusAsync(cancellationToken);
            StartMoneroPaymentTimer();
        }
        catch
        {
        }
    }

    private async Task UpdateMoneroPriceAsync(CancellationToken cancellationToken)
    {
        try
        {
            MoneroPrice = await ExecuteAuthenticatedAsync(
                token => _backend.GetMoneroPriceAsync(SelectedBillingCycle, token),
                cancellationToken);
        }
        catch
        {
        }
    }

    private async Task DownloadCertificateConfigAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not UserCertificate certificate)
        {
            StatusMessage = "Choose a certificate first.";
            return;
        }

        try
        {
            await DownloadCertificateArtifactAsync(
                token => _backend.DownloadCertificateConfigAsync(certificate.Id, token),
                $"{certificate.Name}-config{ConfigExtensionFor(certificate.VpnType)}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task DownloadSelectedOpenVpnConfigAsync(CancellationToken cancellationToken)
    {
        if (SelectedServer is null)
        {
            StatusMessage = "Choose a server first.";
            return;
        }

        try
        {
            await DownloadCertificateArtifactAsync(
                token => _backend.DownloadOpenVpnConfigAsync(SelectedServer.Id, token),
                $"{SelectedServer.ServerName}-openvpn.ovpn",
                cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task DownloadCertificateFileAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not UserCertificate certificate)
        {
            StatusMessage = "Choose a certificate first.";
            return;
        }

        try
        {
            await DownloadCertificateArtifactAsync(
                token => _backend.DownloadCertificateAsync(certificate.Id, token),
                $"{certificate.Name}.crt",
                cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task DownloadCertificateArtifactAsync(Func<CancellationToken, Task<Stream>> sourceFactory, string fileName, CancellationToken cancellationToken)
    {
        var safeName = SanitizeFileName(fileName);
        await using var target = await _fileSavePicker.PickSaveFileAsync(safeName, cancellationToken);
        if (target is null)
        {
            StatusMessage = "Download cancelled.";
            return;
        }

        await using var source = await ExecuteAuthenticatedAsync(sourceFactory, cancellationToken);
        await source.CopyToAsync(target.Stream, cancellationToken);
        StatusMessage = $"Saved {safeName} to {target.DisplayPath}.";
    }

    private async Task RunPreflightAsync(CancellationToken cancellationToken)
    {
        try
        {
            var protocol = SelectedProtocol.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase)
                ? VpnProtocol.OpenVpn
                : VpnProtocol.Ikev2;
            var preflight = await _preflightService.CheckAsync(protocol, cancellationToken);
            PreflightSummary = preflight.Summary;
            StatusMessage = preflight.IsReady ? "Linux VPN dependencies are ready." : preflight.Summary;
        }
        catch (Exception ex)
        {
            PreflightSummary = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectionState is VpnConnectionState.Connecting or VpnConnectionState.Preparing or VpnConnectionState.Disconnecting)
        {
            return;
        }

        if (ConnectionState == VpnConnectionState.Connected)
        {
            StatusMessage = "Disconnect first.";
            return;
        }

        try
        {
            await ConnectCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ConnectedServer = null;
            StopLiveDashboardSession();
            ConnectionState = VpnConnectionState.Disconnected;
            ConnectionMessage = "Connection cancelled.";
            StatusMessage = "Connection cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            ConnectedServer = null;
            StopLiveDashboardSession();
            ConnectionState = VpnConnectionState.Error;
            ConnectionMessage = ex.Message;
            StatusMessage = ex.Message;
            _ = ShowVpnStatusNotificationAsync(new VpnStatus(VpnConnectionState.Error, null, ex.Message));
        }
    }

    private async Task QuickConnectAsync(CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
    }

    private async Task ConnectToServerAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (parameter is not VpnServer server || !CanUseTrayServer(server))
        {
            return;
        }

        SelectedServer = server;
        CurrentSection = "Dashboard";
        await ConnectAsync(cancellationToken);
    }

    private void DiscardSelectedServer()
    {
        SelectedServer = null;
        CurrentSection = "Dashboard";
        UpdateServerPresentation();
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var protocol = SelectedProtocol.Equals("OpenVPN", StringComparison.OrdinalIgnoreCase)
            ? VpnProtocol.OpenVpn
            : VpnProtocol.Ikev2;

        var preflight = await _preflightService.CheckAsync(protocol, cancellationToken);
        PreflightSummary = preflight.Summary;
        if (!preflight.IsReady)
        {
            StatusMessage = preflight.Summary;
            return;
        }

        var target = SelectedServer;
        if (target is null)
        {
            if (Servers.Count == 0 && !await LoadServersAsync(cancellationToken))
            {
                return;
            }

            if (Servers.Count == 0)
            {
                StatusMessage = "No VPN servers are available right now.";
                return;
            }

            var effectivePlan = await DetermineEffectivePlanAsync(cancellationToken);
            var eligibleServers = GetEligibleServers(effectivePlan);
            if (eligibleServers.Count == 0)
            {
                StatusMessage = "No VPN servers are available right now.";
                return;
            }

            var latencies = _serverLatencyService.GetCachedLatencies();
            if (!HasUsableLatencySnapshot(eligibleServers, latencies))
            {
                latencies = await _serverLatencyService.MeasureLatenciesAsync(Servers.ToList(), cancellationToken);
            }

            target = ServerSelectionHelper.SelectBestServer(eligibleServers, latencies, effectivePlan);
            target ??= eligibleServers.FirstOrDefault();
        }

        if (target is null)
        {
            StatusMessage = "No VPN servers are available right now.";
            return;
        }

        ConnectedServer = target;
        cancellationToken.ThrowIfCancellationRequested();
        await _vpn.ConnectAsync(target, protocol, cancellationToken);
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _vpn.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task HandleVpnStatusChangedAsync(VpnStatus status)
    {
        if (status.State is VpnConnectionState.Preparing or VpnConnectionState.Connecting or VpnConnectionState.Connected or VpnConnectionState.Disconnecting)
        {
            if (!string.IsNullOrWhiteSpace(status.ClientPublicIp))
            {
                OriginalPublicIpText = status.ClientPublicIp;
            }

            if (!string.IsNullOrWhiteSpace(status.ServerIp))
            {
                VpnIpText = status.ServerIp;
            }
            else if (status.State is VpnConnectionState.Preparing or VpnConnectionState.Connecting)
            {
                VpnIpText = ConnectedServer?.ServerIp ?? "—";
            }
            else if (status.State == VpnConnectionState.Disconnecting)
            {
                VpnIpText = ConnectedServer?.ServerIp ?? VpnIpText;
            }

            if (status.State == VpnConnectionState.Connected)
            {
                _connectedAt = status.ConnectedAt ?? DateTimeOffset.UtcNow;
                UpdateConnectionDurationText();
                await StartLocalStatisticsSessionAsync(status);

                if (!_dashboardMetricsTimer.IsEnabled)
                {
                    _dashboardMetricsTimer.Start();
                }

                if (!string.IsNullOrWhiteSpace(status.ActiveProfile))
                {
                    try
                    {
                        var snapshot = await _tunnelTrafficMonitor.StartSessionAsync(status.ActiveProfile, CancellationToken.None);
                        ApplyTunnelTrafficSnapshot(snapshot);
                    }
                    catch
                    {
                        ApplyTunnelTrafficSnapshot(new TunnelTrafficSnapshot(null, 0, 0, 0, 0, false));
                    }
                }
                else
                {
                    ApplyTunnelTrafficSnapshot(new TunnelTrafficSnapshot(null, 0, 0, 0, 0, false));
                }

                return;
            }

            ResetLiveMetrics();
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.ClientPublicIp))
        {
            OriginalPublicIpText = status.ClientPublicIp;
        }

        await FinalizeLocalStatisticsSessionAsync(status.State.ToString(), CancellationToken.None);
        StopLiveDashboardSession();
    }

    private async Task ShowVpnStatusNotificationAsync(VpnStatus status)
    {
        if (!NotificationsEnabled)
        {
            return;
        }

        var notificationState = status.State switch
        {
            VpnConnectionState.Preparing or VpnConnectionState.Connecting => VpnConnectionState.Connecting,
            VpnConnectionState.Connected => VpnConnectionState.Connected,
            VpnConnectionState.Disconnected => VpnConnectionState.Disconnected,
            VpnConnectionState.Error => VpnConnectionState.Error,
            _ => (VpnConnectionState?)null
        };

        if (notificationState is null || notificationState == _lastNotificationState)
        {
            return;
        }

        _lastNotificationState = notificationState;
        var title = notificationState.Value switch
        {
            VpnConnectionState.Connecting => "Connecting",
            VpnConnectionState.Connected => "Connected",
            VpnConnectionState.Error => "Connection error",
            _ => "Disconnected"
        };
        var body = notificationState.Value switch
        {
            VpnConnectionState.Connecting => status.Message ?? "Connecting to LibreGuard VPN...",
            VpnConnectionState.Connected => ConnectedServer is null
                ? "LibreGuard VPN is connected."
                : $"Connected to {ConnectedServer.DisplayName}.",
            VpnConnectionState.Error => status.Message ?? "LibreGuard VPN could not connect.",
            _ => "LibreGuard VPN is disconnected."
        };

        await _desktopNotifications.ShowAsync($"LibreGuard VPN - {title}", body, CancellationToken.None);
    }

    private void DashboardMetricsTimerOnTick(object? sender, EventArgs e)
    {
        UpdateConnectionDurationText();
        _ = RefreshTunnelTrafficAsync();
    }

    private async Task StartLocalStatisticsSessionAsync(VpnStatus status)
    {
        if (_statisticsProfile.ActiveSession is not null)
        {
            return;
        }

        var connectedAt = status.ConnectedAt ?? DateTimeOffset.UtcNow;
        var server = ConnectedServer;
        var activeSession = new LocalVpnSession
        {
            StartedAt = connectedAt,
            LastObservedAt = connectedAt,
            ServerId = server?.Id,
            ServerName = server?.ServerName,
            Country = server?.Country,
            City = server?.City,
            Protocol = SelectedProtocol,
            ProfileName = status.ActiveProfile
        };

        _statisticsProfile = await _localStatisticsStore.StartSessionAsync(_authSession.CurrentSession, activeSession, CancellationToken.None);
        _hasRecordedLiveStatisticsSnapshot = false;
        UpdateStatisticsPresentation();
    }

    private async Task RecordLocalStatisticsSnapshotAsync(TunnelTrafficSnapshot snapshot, CancellationToken cancellationToken)
    {
        _statisticsProfile = await _localStatisticsStore.RecordSnapshotAsync(
            _authSession.CurrentSession,
            snapshot,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (_hasRecordedLiveStatisticsSnapshot)
        {
            UpdateLiveStatisticsPresentation();
        }
        else
        {
            _hasRecordedLiveStatisticsSnapshot = true;
            UpdateStatisticsPresentation();
        }
    }

    private async Task FinalizeLocalStatisticsSessionAsync(string finalStatus, CancellationToken cancellationToken)
    {
        _statisticsProfile = await _localStatisticsStore.FinalizeActiveSessionAsync(
            _authSession.CurrentSession,
            DateTimeOffset.UtcNow,
            finalStatus,
            cancellationToken);
        _hasRecordedLiveStatisticsSnapshot = false;
        UpdateStatisticsPresentation();
    }

    private async Task RefreshTunnelTrafficAsync()
    {
        if (_isRefreshingTunnelTraffic || !IsConnected)
        {
            return;
        }

        _isRefreshingTunnelTraffic = true;
        try
        {
            var snapshot = await _tunnelTrafficMonitor.RefreshAsync(CancellationToken.None);
            ApplyTunnelTrafficSnapshot(snapshot);
        }
        catch
        {
            ApplyTunnelTrafficSnapshot(new TunnelTrafficSnapshot(null, 0, 0, 0, 0, false));
        }
        finally
        {
            _isRefreshingTunnelTraffic = false;
        }
    }

    private void UpdateConnectionDurationText()
    {
        var duration = _connectedAt.HasValue
            ? DateTimeOffset.UtcNow - _connectedAt.Value
            : TimeSpan.Zero;
        if (duration < TimeSpan.Zero)
        {
            ConnectionDurationText = "00:00:00";
            return;
        }

        ConnectionDurationText = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void ApplyTunnelTrafficSnapshot(TunnelTrafficSnapshot snapshot)
    {
        LiveDownloadSpeedText = $"{FormatBytes(snapshot.DownloadBytesPerSecond)}/s";
        LiveUploadSpeedText = $"{FormatBytes(snapshot.UploadBytesPerSecond)}/s";
        SessionDataTotalText = FormatBytes(snapshot.SessionTotalBytes);
        if (snapshot.IsAvailable && IsConnected)
        {
            _ = RecordLocalStatisticsSnapshotAsync(snapshot, CancellationToken.None);
        }
    }

    private void StopLiveDashboardSession()
    {
        ResetLiveMetrics();
        VpnIpText = "—";
    }

    private void ResetLiveMetrics()
    {
        _dashboardMetricsTimer.Stop();
        _tunnelTrafficMonitor.Stop();
        _connectedAt = null;
        _isRefreshingTunnelTraffic = false;
        ConnectionDurationText = "00:00:00";
        LiveDownloadSpeedText = "0 B/s";
        LiveUploadSpeedText = "0 B/s";
        SessionDataTotalText = "0 B";
    }

    private async Task<bool> DetermineEffectivePlanAsync(CancellationToken cancellationToken)
    {
        if (Subscription?.IsPro == true)
        {
            return true;
        }

        try
        {
            var subscription = await ExecuteAuthenticatedAsync(_backend.GetSubscriptionStatusAsync, cancellationToken);
            if (subscription is not null)
            {
                Subscription = subscription;
                return subscription.IsPro;
            }
        }
        catch
        {
        }

        return false;
    }

    private async Task<T> ExecuteAuthenticatedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _authSession.ExecuteAuthorizedAsync(operation, cancellationToken);
        }
        catch (SessionExpiredException ex)
        {
            await HandleSessionExpiredAsync(ex.Message, cancellationToken);
            throw;
        }
    }

    private async Task HandleSessionExpiredAsync(string message, CancellationToken cancellationToken)
    {
        await FinalizeLocalStatisticsSessionAsync("SessionExpired", cancellationToken);
        await _authSession.ClearSessionAsync(cancellationToken);
        ResetSignedOutState();
        StatusMessage = message;
    }

    private void StartMoneroPaymentTimer()
    {
        StopMoneroPaymentTimer();
        _lastMoneroStatusRefreshMinute = -1;
        _moneroPaymentTimer.Start();
        UpdateMoneroTimerDisplay();
    }

    private void StopMoneroPaymentTimer()
    {
        _moneroPaymentTimer.Stop();
        _lastMoneroStatusRefreshMinute = -1;
    }

    private async void MoneroPaymentTimerOnTick(object? sender, EventArgs e)
    {
        UpdateMoneroTimerDisplay();
        if (!_moneroPaymentTimer.IsEnabled || MoneroInvoice is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now.Second == 0 && _lastMoneroStatusRefreshMinute != now.Minute)
        {
            _lastMoneroStatusRefreshMinute = now.Minute;
            await CheckPaymentStatusAsync(CancellationToken.None);
        }
    }

    private void UpdateMoneroTimerDisplay()
    {
        if (MoneroInvoice is null)
        {
            TimeRemaining = string.Empty;
            return;
        }

        var remaining = MoneroInvoice.CreatedAt.AddHours(24) - DateTimeOffset.UtcNow;
        if (remaining.TotalSeconds <= 0)
        {
            TimeRemaining = "Expired";
            StopMoneroPaymentTimer();
            return;
        }

        TimeRemaining = $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }

    private static string NormalizeBillingCycle(string? value)
        => string.Equals(value, "yearly", StringComparison.OrdinalIgnoreCase)
            ? "yearly"
            : "monthly";

    private void ResetSignedOutState()
    {
        InvalidateAccountRefreshState();
        StopLiveDashboardSession();
        StopMoneroPaymentTimer();
        OriginalPublicIpText = "—";
        VpnIpText = "—";
        ClearPendingLoginChallenge();
        ClearSensitiveAuthState();
        ResetAdBlockingState();
        Quota = null;
        Subscription = null;
        _statisticsProfile = new LocalStatisticsProfile();
        Devices.Clear();
        Certificates.Clear();
        SelectedCertificate = null;
        SelectedDevice = null;
        SelectedServer = null;
        ConnectedServer = null;
        CheckoutUrl = string.Empty;
        SwitchPaymentMethod();
        TwoFactorEnabled = false;
        SetTwoFactorToggleState(false);
        HasAuthenticator = false;
        IsAuthenticated = false;
        CurrentSection = "Dashboard";
        AuthView = "Login";
        Servers.Clear();
        _serversLoadedFromBackend = false;
        InvalidateLatencyRefreshState();
        UpdateServerPresentation();
        UpdateStatisticsPresentation();
        NotifyPlanStateChanged();
    }

    private void ClearSensitiveAuthState()
    {
        Email = string.Empty;
        AccountEmail = string.Empty;
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        ResetToken = string.Empty;
        NewPassword = string.Empty;
        OAuthToken = string.Empty;
        TwoFactorManagementCode = string.Empty;
        TwoFactorSharedKey = string.Empty;
        TwoFactorAuthenticatorUri = string.Empty;
        RecoveryCodesText = string.Empty;
        RegisteredUserId = string.Empty;
    }

    private void NotifyPlanStateChanged()
    {
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(IsProPlan));
        OnPropertyChanged(nameof(IsFreePlan));
        OnPropertyChanged(nameof(ShowUpgradeSettingsCard));
        NotifyAdBlockingStateChanged();
    }

    private void InvalidateAccountRefreshState()
    {
        lock (_accountStateRefreshLock)
        {
            Interlocked.Increment(ref _accountStateRefreshGeneration);
            _accountStateRefreshCts?.Cancel();
            _accountStateRefreshCts?.Dispose();
            _accountStateRefreshCts = null;
            _accountStateRefreshTask = null;
        }
    }

    private bool ShouldApplyAccountRefresh(int refreshGeneration, string sessionIdentity, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && refreshGeneration == Volatile.Read(ref _accountStateRefreshGeneration)
            && string.Equals(sessionIdentity, GetSessionIdentity(_authSession.CurrentSession), StringComparison.Ordinal);

    private static string GetSessionIdentity(AuthSession? session)
    {
        if (session is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(session.UserId))
        {
            return session.UserId;
        }

        if (!string.IsNullOrWhiteSpace(session.Email))
        {
            return session.Email;
        }

        return session.Token;
    }

    private static string GetPlanText(SubscriptionStatus subscription)
        => subscription.IsPro
            ? subscription.PlanType
            : "Free";

    private string GetConnectedTrayLocation()
    {
        if (ConnectedServer is not { } server)
        {
            return "Connected";
        }

        return string.IsNullOrWhiteSpace(server.City)
            ? server.Country
            : $"{server.Country}, {server.City}";
    }

    private void StartLatencyRefresh()
    {
        if (Servers.Count == 0)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _latencyRefreshGeneration);
        var snapshot = Servers.ToList();
        _ = RefreshServerLatenciesAsync(generation, snapshot);
    }

    private async Task RefreshServerLatenciesAsync(int generation, IReadOnlyList<VpnServer> snapshot)
    {
        try
        {
            var latencies = await _serverLatencyService.MeasureLatenciesAsync(snapshot, CancellationToken.None);
            if (generation != Volatile.Read(ref _latencyRefreshGeneration))
            {
                return;
            }

            void ApplyLatencyResults()
            {
                if (generation != Volatile.Read(ref _latencyRefreshGeneration))
                {
                    return;
                }

                foreach (var server in Servers)
                {
                    server.PingMs = !string.IsNullOrWhiteSpace(server.ServerHostname) &&
                        latencies.TryGetValue(server.ServerHostname, out var latency)
                        ? latency
                        : -1;
                }

                UpdateServerPresentation();
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
            {
                Dispatcher.UIThread.Post(ApplyLatencyResults);
            }
            else
            {
                ApplyLatencyResults();
            }
        }
        catch
        {
        }
    }

    private void InvalidateLatencyRefreshState()
        => Interlocked.Increment(ref _latencyRefreshGeneration);

    private List<VpnServer> GetEligibleServers(bool isPro)
        => Servers.Where(server => isPro || !server.IsPremium).ToList();

    private static bool HasUsableLatencySnapshot(IReadOnlyList<VpnServer> servers, IReadOnlyDictionary<string, int> latencies)
        => servers.All(server =>
            !string.IsNullOrWhiteSpace(server.ServerHostname) &&
            server.PingMs > 0 &&
            latencies.TryGetValue(server.ServerHostname, out var latency) &&
            latency > 0);

    private void UpdateServerPresentation()
    {
        var servers = ApplyServerFilters();

        VisibleServers.Clear();
        foreach (var server in servers)
        {
            VisibleServers.Add(server);
        }

        FavoriteServers.Clear();
        foreach (var server in servers.Where(server => _favoriteServerIds.Contains(server.Id)))
        {
            FavoriteServers.Add(server);
        }

        RecentServers.Clear();
        foreach (var server in _recentServerIds.Select(id => Servers.FirstOrDefault(item => item.Id == id)).Where(server => server is not null).Cast<VpnServer>())
        {
            if (servers.Any(item => item.Id == server.Id))
            {
                RecentServers.Add(server);
            }
        }

        ServerGroups.Clear();
        foreach (var group in servers.GroupBy(server => server.Country).OrderBy(group => group.Key))
        {
            ServerGroups.Add(new ServerGroupViewModel(group.Key, group.Count(), group.ToList()));
        }

        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecentServers));
        OnPropertyChanged(nameof(ServerSearchHint));
        OnPropertyChanged(nameof(CanUseTrayServers));
    }

    private void UpdateStatisticsPresentation()
    {
        _lastLiveStatisticsPresentationUpdate = DateTimeOffset.UtcNow;
        var now = DateTimeOffset.UtcNow;
        var (periodStart, periodEnd) = GetStatisticsPeriodBounds(SelectedStatisticsPeriod, now);
        var sessions = GetStatisticsSessions(periodStart, periodEnd, now).ToList();
        var trafficBuckets = GetStatisticsDailyBuckets(periodStart, periodEnd).ToList();

        var downloadBytes = trafficBuckets.Sum(bucket => bucket.DownloadBytes);
        var uploadBytes = trafficBuckets.Sum(bucket => bucket.UploadBytes);
        var totalDataBytes = downloadBytes + uploadBytes;
        var connections = sessions.Count;
        var averageSession = CalculateAverageLocalSessionDuration(sessions, periodStart, periodEnd, now);

        StatisticsTotalDataText = FormatBytes(totalDataBytes);
        StatisticsConnectionsText = connections.ToString();
        StatisticsAverageSessionText = FormatDuration(averageSession);
        StatisticsAverageDownloadText = FormatBytes(connections > 0 ? downloadBytes / connections : downloadBytes);
        StatisticsTotalDownloadText = FormatBytes(downloadBytes);
        StatisticsTotalUploadText = FormatBytes(uploadBytes);

        var trafficRows = BuildLocalTrafficUsageRows(SelectedStatisticsPeriod, now).ToList();
        var durationRows = BuildLocalSessionDurationRows(SelectedStatisticsPeriod, now).ToList();
        var locationBars = BuildLocalLocationBars(sessions).ToList();

        RebuildRows(DailyTrafficRows, trafficRows);
        RebuildRows(ConnectionDurationRows, durationRows);
        RebuildChart(ServerLoadChartBars, locationBars);

        // Keep the legacy chart collections populated for any older bindings.
        RebuildChart(UsageChartBars, trafficRows.Select((row, index) => new ChartBarViewModel(
            row.Label,
            Math.Max(row.DownloadPercentage, row.UploadPercentage),
            row.TotalText,
            index % 2 == 0 ? "#1570EF" : "#10B981")));
        RebuildChart(ConnectionChartBars, durationRows.Select((row, index) => new ChartBarViewModel(
            row.Label,
            row.Percentage,
            row.DurationText,
            index % 2 == 0 ? "#10B981" : "#1570EF")));
        OnPropertyChanged(nameof(QuotaText));
        OnPropertyChanged(nameof(MonthlyUsageDisplayText));
        OnPropertyChanged(nameof(MonthlyUsageLimitText));
        OnPropertyChanged(nameof(IsMonthlyUsageUnlimited));
        OnPropertyChanged(nameof(ShowMonthlyUsageProgress));
        OnPropertyChanged(nameof(MonthlyUsagePercentage));
        OnPropertyChanged(nameof(TrayMonthlyUsageText));
        OnPropertyChanged(nameof(TrayToolTipText));
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(ActiveDevicesText));
    }

    private void UpdateLiveStatisticsPresentation()
    {
        if (!IsStatistics)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastLiveStatisticsPresentationUpdate < TimeSpan.FromSeconds(5))
        {
            return;
        }

        UpdateStatisticsPresentation();
    }

    private IEnumerable<LocalVpnSession> GetStatisticsSessions(DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        foreach (var session in _statisticsProfile.CompletedSessions)
        {
            if (SessionOverlapsPeriod(session, periodStart, periodEnd, now))
            {
                yield return session;
            }
        }

        if (_statisticsProfile.ActiveSession is { } activeSession
            && SessionOverlapsPeriod(activeSession, periodStart, periodEnd, now))
        {
            yield return activeSession;
        }
    }

    private IEnumerable<LocalDailyTraffic> GetStatisticsDailyBuckets(DateTimeOffset periodStart, DateTimeOffset periodEnd)
        => _statisticsProfile.DailyTraffic.Where(bucket =>
            TryParseLocalStatisticsDate(bucket.Date, out var date)
            && date >= periodStart.UtcDateTime.Date
            && date < periodEnd.UtcDateTime.Date);

    private IEnumerable<TrafficUsageRowViewModel> BuildLocalTrafficUsageRows(string period, DateTimeOffset now)
    {
        var labels = GetPeriodLabels(period);
        var buckets = labels.ToDictionary(label => label, _ => (Download: 0L, Upload: 0L), StringComparer.Ordinal);
        foreach (var item in _statisticsProfile.DailyTraffic)
        {
            if (!TryParseLocalStatisticsDate(item.Date, out var date) || !TryGetPeriodBucketLabel(period, now, date, out var label))
            {
                continue;
            }

            var current = buckets[label];
            buckets[label] = (current.Download + item.DownloadBytes, current.Upload + item.UploadBytes);
        }

        var maxBytes = Math.Max(1L, buckets.Values.SelectMany(value => new[] { value.Download, value.Upload }).DefaultIfEmpty(0).Max());
        foreach (var label in labels)
        {
            var value = buckets[label];
            yield return new TrafficUsageRowViewModel(
                label,
                FormatBytes(value.Download),
                FormatBytes(value.Upload),
                value.Download * 100.0 / maxBytes,
                value.Upload * 100.0 / maxBytes,
                FormatBytes(value.Download + value.Upload));
        }
    }

    private IEnumerable<SessionDurationRowViewModel> BuildLocalSessionDurationRows(string period, DateTimeOffset now)
    {
        var labels = GetPeriodLabels(period);
        var buckets = labels.ToDictionary(label => label, _ => 0L, StringComparer.Ordinal);
        foreach (var item in _statisticsProfile.DailyTraffic)
        {
            if (!TryParseLocalStatisticsDate(item.Date, out var date) || !TryGetPeriodBucketLabel(period, now, date, out var label))
            {
                continue;
            }

            buckets[label] += item.ConnectedSeconds;
        }

        var maxSeconds = Math.Max(1L, buckets.Values.DefaultIfEmpty(0).Max());
        foreach (var label in labels)
        {
            var seconds = buckets[label];
            yield return new SessionDurationRowViewModel(
                label,
                FormatDuration(TimeSpan.FromSeconds(seconds)),
                seconds * 100.0 / maxSeconds);
        }
    }

    private static IEnumerable<ChartBarViewModel> BuildLocalLocationBars(IReadOnlyList<LocalVpnSession> sessions)
    {
        var groups = sessions
            .Select(session => string.IsNullOrWhiteSpace(session.City)
                ? session.Country ?? session.ServerName ?? "Unknown"
                : session.City)
            .GroupBy(label => label)
            .Select(group => new { Label = group.Key, Count = group.Count() })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Label)
            .Take(6)
            .ToList();

        var max = Math.Max(1, groups.Select(group => group.Count).DefaultIfEmpty(0).Max());
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            yield return new ChartBarViewModel(
                group.Label,
                group.Count * 100.0 / max,
                $"{group.Count} connection{(group.Count == 1 ? string.Empty : "s")}",
                index % 2 == 0 ? "#1570EF" : "#10B981");
        }
    }

    private static TimeSpan CalculateAverageLocalSessionDuration(
        IReadOnlyList<LocalVpnSession> sessions,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset now)
    {
        var durations = sessions
            .Select(session => GetClippedSessionDuration(session, periodStart, periodEnd, now))
            .Where(duration => duration > TimeSpan.Zero)
            .ToList();

        return durations.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)durations.Average(duration => duration.Ticks));
    }

    private static bool SessionOverlapsPeriod(LocalVpnSession session, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        var end = session.EndedAt ?? now;
        return session.StartedAt < periodEnd && end > periodStart;
    }

    private static TimeSpan GetClippedSessionDuration(LocalVpnSession session, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        var start = session.StartedAt > periodStart ? session.StartedAt : periodStart;
        var end = (session.EndedAt ?? now) < periodEnd ? session.EndedAt ?? now : periodEnd;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) GetStatisticsPeriodBounds(string period, DateTimeOffset now)
    {
        var today = now.UtcDateTime.Date;
        if (period.Equals("Year", StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (new DateTimeOffset(start), new DateTimeOffset(start.AddYears(1)));
        }

        if (period.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (new DateTimeOffset(start), new DateTimeOffset(start.AddMonths(1)));
        }

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday);
        return (new DateTimeOffset(weekStart, TimeSpan.Zero), new DateTimeOffset(weekStart.AddDays(7), TimeSpan.Zero));
    }

    private static bool TryGetPeriodBucketLabel(string period, DateTimeOffset now, DateTime date, out string label)
    {
        var (start, end) = GetStatisticsPeriodBounds(period, now);
        if (date < start.UtcDateTime.Date || date >= end.UtcDateTime.Date)
        {
            label = string.Empty;
            return false;
        }

        if (period.Equals("Year", StringComparison.OrdinalIgnoreCase))
        {
            label = GetPeriodLabels(period)[date.Month - 1];
            return true;
        }

        if (period.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            label = $"W{Math.Min(6, Math.Max(1, ((date.Day - 1) / 7) + 1))}";
            return true;
        }

        label = date.DayOfWeek.ToString()[..3];
        return true;
    }

    private static bool TryParseLocalStatisticsDate(string value, out DateTime date)
        => DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);

    private static void RebuildRows<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string[] GetPeriodLabels(string period)
    {
        if (period.Equals("Year", StringComparison.OrdinalIgnoreCase))
        {
            return ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        }

        if (period.Equals("Month", StringComparison.OrdinalIgnoreCase))
        {
            return ["W1", "W2", "W3", "W4", "W5", "W6"];
        }

        return ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    }

    private long GetMonthlyUsageLimitBytes()
        => Quota?.BytesLimit ?? FreePlanBytesLimit;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (duration.TotalHours >= 24)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
    }

    private IEnumerable<VpnServer> ApplyServerFilters()
    {
        IEnumerable<VpnServer> query = Servers;

        if (!string.IsNullOrWhiteSpace(ServerSearchText))
        {
            var filter = ServerSearchText.Trim();
            query = query.Where(server =>
                server.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                server.ServerName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                server.Country.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (server.City?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        query = ServerSortMode.Equals("Load", StringComparison.OrdinalIgnoreCase)
            ? query.OrderBy(server => server.LoadPercent).ThenBy(server => server.DisplayName)
            : ServerSortMode.Equals("Name", StringComparison.OrdinalIgnoreCase)
                ? query.OrderBy(server => server.Country).ThenBy(server => server.DisplayName)
                : query.OrderBy(server => server.PingMs > 0 ? server.PingMs : int.MaxValue).ThenBy(server => server.DisplayName);

        return query.ToList();
    }

    private static void RebuildChart(ObservableCollection<ChartBarViewModel> target, IEnumerable<ChartBarViewModel> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void ToggleFavoriteServer(VpnServer server)
    {
        if (_favoriteServerIds.Contains(server.Id))
        {
            _favoriteServerIds.Remove(server.Id);
        }
        else
        {
            _favoriteServerIds.Add(server.Id);
        }

        _ = _settingsStore.SetAsync(FavoriteServersKey, _favoriteServerIds.OrderBy(id => id).ToList(), CancellationToken.None);
        UpdateServerPresentation();
    }

    private void TouchRecentServer(int serverId)
    {
        _recentServerIds.Remove(serverId);
        _recentServerIds.Insert(0, serverId);

        if (_recentServerIds.Count > 8)
        {
            _recentServerIds.RemoveRange(8, _recentServerIds.Count - 8);
        }

        _ = _settingsStore.SetAsync(RecentServersKey, _recentServerIds, CancellationToken.None);
    }

    private sealed record RefreshSnapshot(
        bool ServersLoaded,
        UsageQuota Quota,
        SubscriptionStatus Subscription,
        IReadOnlyList<UserDevice> Devices,
        TwoFactorStatus TwoFactor,
        IReadOnlyList<UserCertificate> Certificates,
        DnsPreferenceLoadResult DnsPreference,
        long EntitlementRevision);

    private sealed record DnsPreferenceLoadResult(DnsPreferenceResponse? Response, long Revision);

    private sealed record BackendErrorCode(string? ErrorCode, string? Code);

    private void RaiseViewFlags()
    {
        OnPropertyChanged(nameof(IsLoginView));
        OnPropertyChanged(nameof(IsRegisterView));
        OnPropertyChanged(nameof(IsForgotView));
        OnPropertyChanged(nameof(IsResetView));
        OnPropertyChanged(nameof(IsEmailConfirmationView));
        OnPropertyChanged(nameof(IsTwoFactorView));
        OnPropertyChanged(nameof(IsDashboard));
        OnPropertyChanged(nameof(IsServers));
        OnPropertyChanged(nameof(IsStatistics));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsUpgrade));
        OnPropertyChanged(nameof(IsDevices));
        OnPropertyChanged(nameof(IsCertificates));
        OnPropertyChanged(nameof(IsPasswordMatch));
        OnPropertyChanged(nameof(ShouldShowPasswordMismatch));
    }

    private void NavigateToUpgradeSettings(string message = "Upgrade to Pro to use OpenVPN.")
    {
        StatusMessage = message;
        CurrentSection = "Upgrade";
    }

    private void RaisePasswordStateChanged()
    {
        OnPropertyChanged(nameof(PasswordStrengthScore));
        OnPropertyChanged(nameof(PasswordStrengthLabel));
        OnPropertyChanged(nameof(IsPasswordEmpty));
        OnPropertyChanged(nameof(IsPasswordStrong));
        OnPropertyChanged(nameof(ShouldShowPasswordStrength));
        OnPropertyChanged(nameof(IsPasswordWeak));
        OnPropertyChanged(nameof(IsPasswordMatch));
        OnPropertyChanged(nameof(ShouldShowPasswordMismatch));
    }

    private void RaiseNewPasswordStateChanged()
    {
        OnPropertyChanged(nameof(NewPasswordStrengthScore));
        OnPropertyChanged(nameof(NewPasswordStrengthLabel));
        OnPropertyChanged(nameof(IsNewPasswordEmpty));
        OnPropertyChanged(nameof(IsNewPasswordStrong));
        OnPropertyChanged(nameof(ShouldShowNewPasswordStrength));
        OnPropertyChanged(nameof(IsNewPasswordWeak));
        OnPropertyChanged(nameof(IsPasswordMatch));
        OnPropertyChanged(nameof(ShouldShowPasswordMismatch));
    }

    private void TrackCommandState(ICommand command, params string[] propertyNames)
    {
        if (command is not INotifyPropertyChanged notify)
        {
            return;
        }

        notify.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(AsyncCommand.IsRunning))
            {
                return;
            }

            foreach (var propertyName in propertyNames)
            {
                OnPropertyChanged(propertyName);
            }

            if (ReferenceEquals(command, ConnectCommand) || ReferenceEquals(command, QuickConnectCommand))
            {
                RefreshConnectionCommandState();
            }
        };
    }

    private bool CanStartConnection()
        => (ConnectionState is VpnConnectionState.Disconnected or VpnConnectionState.Error) && !IsConnectRunning && !IsQuickConnectRunning;

    private bool CanUseServer(VpnServer server)
        => IsProPlan || !server.IsPremium;

    private void CancelConnectionAttempt()
    {
        (ConnectCommand as AsyncCommand)?.Cancel();
        (QuickConnectCommand as AsyncCommand)?.Cancel();

        if (ConnectionState is VpnConnectionState.Preparing or VpnConnectionState.Connecting or VpnConnectionState.Connected)
        {
            ConnectionMessage = "Cancelling connection attempt...";
            StatusMessage = ConnectionMessage;
            _ = DisconnectAsync(CancellationToken.None);
        }
    }

    private void RefreshConnectionCommandState()
    {
        if (ConnectCommand is AsyncCommand connectCommand)
        {
            connectCommand.RaiseCanExecuteChanged();
        }

        if (QuickConnectCommand is AsyncCommand quickConnectCommand)
        {
            quickConnectCommand.RaiseCanExecuteChanged();
        }

        if (ConnectToServerCommand is AsyncParameterCommand connectToServerCommand)
        {
            connectToServerCommand.RaiseCanExecuteChanged();
        }
    }

    private static int CalculatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return 0;
        }

        var score = 0;
        if (password.Length >= 8)
        {
            score += 40;
        }

        if (password.Any(char.IsDigit))
        {
            score += 30;
        }

        if (password.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            score += 30;
        }

        return Math.Clamp(score, 0, 100);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string ConfigExtensionFor(string vpnType)
        => vpnType.Contains("OPENVPN", StringComparison.OrdinalIgnoreCase)
            ? ".ovpn"
            : ".sswan";

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        return new string(chars);
    }
}
