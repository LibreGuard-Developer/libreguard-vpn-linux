namespace Libreguard.Vpn.Linux.Tests;

public sealed class PackagingSecurityTests
{
    [Fact]
    public void NetworkManagerDispatcher_IsFixedFunctionAndStrictlyScoped()
    {
        var root = FindRepositoryRoot();
        var dispatcher = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "linux",
            "dispatcher",
            "90-libreguard-vpn-lifecycle"));

        Assert.Contains("CONNECTION_ID_VALUE=${CONNECTION_ID:-}", dispatcher);
        Assert.Contains("ACTION=${2:-}", dispatcher);
        Assert.Contains("libreguard-openvpn-*|libreguard-ikev2-*", dispatcher);
        Assert.Contains("libreguard-ikev2-*", dispatcher);
        Assert.Contains("vpn-pre-up)", dispatcher);
        Assert.Contains("vpn-up)", dispatcher);
        Assert.Contains("vpn-down)", dispatcher);
        Assert.Contains("\"$IPV6_HELPER\" install", dispatcher);
        Assert.Contains("exec \"$IKEV2_ROUTE_REPAIR_HELPER\"", dispatcher);
        Assert.Contains("exec \"$IPV6_HELPER\" remove", dispatcher);
        Assert.DoesNotContain("pkexec", dispatcher);
        Assert.DoesNotContain("eval ", dispatcher);
        Assert.DoesNotContain("$1", dispatcher);

        var preUpAction = dispatcher.IndexOf("vpn-pre-up)", StringComparison.Ordinal);
        var preUpLeakGuard = dispatcher.IndexOf("\"$IPV6_HELPER\" install", preUpAction, StringComparison.Ordinal);
        var preUpRouteRepair = dispatcher.IndexOf("\"$IKEV2_ROUTE_REPAIR_HELPER\"", preUpAction, StringComparison.Ordinal);
        var vpnUpAction = dispatcher.IndexOf("vpn-up)", StringComparison.Ordinal);
        Assert.True(preUpAction >= 0 && preUpLeakGuard > preUpAction && preUpRouteRepair > preUpLeakGuard && preUpRouteRepair < vpnUpAction);

        var routeRepairHelper = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "linux",
            "helpers",
            "libreguard-ikev2-route-repair"));
        Assert.Contains("while has_unconditional_table_220_rule", routeRepairHelper);
        Assert.Contains("rule del pref 220 from all lookup 220", routeRepairHelper);
        Assert.DoesNotContain("eval ", routeRepairHelper);
    }

    [Fact]
    public void Ipv6LeakProtectionHelper_IsFixedFunctionIdempotentAndFailClosed()
    {
        var root = FindRepositoryRoot();
        var helper = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "linux",
            "helpers",
            "libreguard-ipv6-leak-protection"));

        Assert.Contains("TABLE=42653", helper);
        Assert.Contains("PRIORITY=1", helper);
        Assert.Contains("LEGACY_PRIORITY=10000", helper);
        Assert.Contains("if [ \"$#\" -ne 1 ]", helper);
        Assert.Contains("case \"$1\" in", helper);
        Assert.Contains("has_foreign_priority_rule", helper);
        Assert.Contains("has_foreign_table_route", helper);
        Assert.Contains("([[:space:]]+dev[[:space:]]+lo)?", helper);
        Assert.Contains("remove_legacy_owned_rules", helper);
        Assert.Contains("has_active_libreguard_vpn", helper);
        Assert.Contains("could not prove that all VPN tunnels are down", helper);
        Assert.Contains("remove_current_owned_rules || true", helper);
        Assert.Contains("remove_owned_routes || true", helper);
        Assert.Contains("-6 route get \"$PROBE_ADDRESS\"", helper);
        Assert.Contains("DOH_CANARY_HOST=use-application-dns.net", helper);
        Assert.Contains("0.0.0.0 use-application-dns.net # LibreGuard VPN DoH canary", helper);
        Assert.Contains("has_conflicting_doh_canary", helper);
        Assert.Contains("install_doh_canary", helper);
        Assert.Contains("remove_doh_canary", helper);
        Assert.Contains("BROWSER_DOH_POLICY_NAME=libreguard-dns-over-https.json", helper);
        Assert.Contains("{\"DnsOverHttpsMode\":\"off\"}", helper);
        Assert.Contains("install_browser_doh_policies", helper);
        Assert.Contains("verify_browser_doh_policies", helper);
        Assert.Contains("remove_browser_doh_policies", helper);
        Assert.Contains("refusing to overwrite administrator configuration", helper);
        Assert.Contains("/etc/opt/chrome/policies/managed", helper);
        Assert.Contains("/etc/chromium/policies/managed", helper);
        Assert.Contains("/etc/brave/policies/managed", helper);
        Assert.Contains("/etc/opt/edge/policies/managed", helper);
        Assert.Contains("cp --preserve=all", helper);
        Assert.Contains("mktemp \"${HOSTS_FILE}.libreguard.XXXXXX\"", helper);
        Assert.DoesNotContain("eval ", helper);

        var installMethod = helper.IndexOf("install_protection()", StringComparison.Ordinal);
        var installRoute = helper.IndexOf(
            "-6 route replace prohibit default table \"$TABLE\" metric \"$METRIC\"",
            installMethod,
            StringComparison.Ordinal);
        var installRule = helper.IndexOf(
            "-6 rule add pref \"$PRIORITY\" from all lookup \"$TABLE\"",
            installMethod,
            StringComparison.Ordinal);
        Assert.True(installMethod >= 0 && installRoute > installMethod && installRule > installRoute);

        var removeMethod = helper.IndexOf("remove_protection()", StringComparison.Ordinal);
        var removeRules = helper.IndexOf("remove_owned_rules", removeMethod, StringComparison.Ordinal);
        var removeRoutes = helper.IndexOf("remove_owned_routes", removeMethod, StringComparison.Ordinal);
        Assert.True(removeMethod >= 0 && removeRules > removeMethod && removeRoutes > removeRules);
    }

    [Fact]
    public void BootRecovery_IsStrictlyScopedToOwnedVpnProfiles()
    {
        var root = FindRepositoryRoot();
        var helper = File.ReadAllText(Path.Combine(root, "packaging", "linux", "helpers", "libreguard-vpn-recovery"));
        var unit = File.ReadAllText(Path.Combine(root, "packaging", "linux", "systemd", "libreguard-vpn-recovery.service"));

        Assert.Contains("libreguard-openvpn-*|libreguard-ikev2-*", helper);
        Assert.Contains("-t -f NAME,TYPE connection show --active", helper);
        Assert.Contains("connection down id", helper);
        Assert.Contains("connection delete id", helper);
        Assert.Contains("MAX_NETWORKMANAGER_ATTEMPTS=20", helper);
        Assert.Contains("LEAK_PROTECTION_HELPER", helper);
        Assert.Contains("refusing DNS cleanup", helper);
        Assert.DoesNotContain("resolv.conf", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolvectl revert", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eval ", helper);

        Assert.Contains("After=NetworkManager.service dbus.service", unit);
        Assert.Contains("Before=network-online.target", unit);
        Assert.Contains("Type=oneshot", unit);
        Assert.Contains("libreguard-vpn-recovery", unit);
        Assert.Contains("WantedBy=multi-user.target", unit);
    }

    [Fact]
    public void LinuxTerminalSignals_RunTheSameBoundedVpnCleanupAsProcessExit()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "App.axaml.cs"));

        Assert.Contains("PosixSignal.SIGINT", app);
        Assert.Contains("TryCleanupVpnState(\"signal-int\")", app);
        Assert.Contains("PosixSignal.SIGHUP", app);
        Assert.Contains("TryCleanupVpnState(\"signal-hup\")", app);
        Assert.Contains("TryCleanupVpnState(\"signal-term\")", app);
        Assert.Contains("TryCleanupVpnState(\"process-exit\")", app);
        Assert.Contains("CancellationTokenSource(TimeSpan.FromSeconds(5))", app);
        Assert.Contains("Interlocked.Exchange(ref _vpnExitCleanupStarted, 1)", app);
    }

    [Fact]
    public void DebianPackage_WiresDispatcherLifecycleAndSafeRemoval()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "libreguard-vpn-linux.csproj"));
        var networkManagerClient = File.ReadAllText(Path.Combine(root, "Services", "NetworkManagerClient.cs"));
        var serviceRegistry = File.ReadAllText(Path.Combine(root, "Services", "ServiceRegistry.cs"));
        var buildScript = File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-packages.sh"));
        var control = File.ReadAllText(Path.Combine(root, "packaging", "linux", "deb", "control"));
        var installer = File.ReadAllText(Path.Combine(root, "install-linux-privileges.sh"));
        var postInstall = File.ReadAllText(Path.Combine(root, "packaging", "linux", "deb", "postinst"));
        var preRemove = File.ReadAllText(Path.Combine(root, "packaging", "linux", "deb", "prerm"));
        var gitAttributes = File.ReadAllText(Path.Combine(root, ".gitattributes"));

        Assert.Contains("libreguard-ipv6-leak-protection", project);
        Assert.Contains("libreguard-vpn-recovery", project);
        Assert.Contains("90-libreguard-vpn-lifecycle", project);
        Assert.Contains("net.libreguard.vpn.linux.repair-ikev2-routing.policy", project);
        Assert.Contains("$ROOT_DIR/packaging/linux/helpers/libreguard-ipv6-leak-protection", buildScript);
        Assert.Contains("$ROOT_DIR/packaging/linux/helpers/libreguard-vpn-recovery", buildScript);
        Assert.Contains("$ROOT_DIR/packaging/linux/systemd/libreguard-vpn-recovery.service", buildScript);
        Assert.Contains("/usr/lib/NetworkManager/dispatcher.d/90-libreguard-vpn-lifecycle", buildScript);
        Assert.Contains("/usr/lib/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle", buildScript);
        Assert.Contains("/etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle", buildScript);
        Assert.Contains("$ROOT_DIR/packaging/linux/deb/prerm", buildScript);
        Assert.Contains("iproute2", control);
        Assert.Contains("policykit-1", control);
        Assert.Contains("pkexec", control);
        Assert.Contains("gnome-keyring", control);
        Assert.Contains("libpam-gnome-keyring", control);
        Assert.Contains("\"pkexec\"", networkManagerClient);
        Assert.True(File.Exists(Path.Combine(root, "packaging", "linux", "polkit", "net.libreguard.vpn.linux.repair-ikev2-routing.policy")));
        Assert.False(File.Exists(Path.Combine(root, "packaging", "linux", "polkit", "net.libreguard.vpn.linux.manage-ipv6-leak-protection.policy")));
        Assert.Contains("IPV6_HELPER_TARGET", installer);
        Assert.Contains("VPN_RECOVERY_HELPER_TARGET", installer);
        Assert.Contains("VPN_RECOVERY_SERVICE_TARGET", installer);
        Assert.Contains("DISPATCHER_TARGET", installer);
        Assert.Contains("PRE_UP_DISPATCHER_TARGET", installer);
        Assert.Contains("SYSTEM_PRE_UP_DISPATCHER_TARGET", installer);
        Assert.Contains("POLICY_TARGET", installer);
        Assert.Contains("has_active_libreguard_vpn", preRemove);
        Assert.Contains("has_ipv6_protection", preRemove);
        Assert.Contains("has_doh_canary", preRemove);
        Assert.Contains("has_browser_doh_policy", preRemove);
        Assert.Contains("libreguard-dns-over-https.json", preRemove);
        Assert.Contains("VerifyBrowserDohProtection", networkManagerClient);
        Assert.Contains("DNS-over-HTTPS canary signal", networkManagerClient);
        Assert.Contains("verifyBrowserDohProtection: true", serviceRegistry);
        Assert.Contains("ip -N -6 rule show", preRemove);
        Assert.Contains("\"$HELPER\" remove", preRemove);
        Assert.Contains("Package removal was stopped", preRemove);
        Assert.Contains("exit 1", preRemove);
        Assert.Contains("cleanup_stale_leak_protection", postInstall);
        Assert.Contains("\"$LEAK_PROTECTION_HELPER\" remove", postInstall);
        Assert.Contains("systemctl enable libreguard-vpn-recovery.service", postInstall);
        Assert.Contains("systemctl disable libreguard-vpn-recovery.service", preRemove);
        Assert.Contains("packaging/linux/helpers/* text eol=lf", gitAttributes);
        Assert.Contains("packaging/linux/dispatcher/* text eol=lf", gitAttributes);
        Assert.Contains("packaging/linux/systemd/* text eol=lf", gitAttributes);
        Assert.Contains("packaging/linux/deb/prerm text eol=lf", gitAttributes);
    }

    [Fact]
    public void UnifiedBuild_PreservesDebianCompatibilityAndNormalizesPayload()
    {
        var root = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-packages.sh"));
        var debWrapper = File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-deb.sh"));
        var rpmWrapper = File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-rpm.sh"));
        var gitAttributes = File.ReadAllText(Path.Combine(root, ".gitattributes"));

        Assert.Contains("PACKAGE_FORMAT=\"all\"", buildScript);
        Assert.Contains("--format <deb|rpm|all>", buildScript);
        Assert.Contains("--install requires --format deb or --format rpm", buildScript);
        Assert.Contains("libreguard-vpn-linux_${PACKAGE_VERSION}_amd64.deb", buildScript);
        Assert.Contains("libreguard-vpn-linux-${PACKAGE_VERSION}-${RPM_PACKAGE_RELEASE}.x86_64.rpm", buildScript);
        Assert.Contains("--root-owner-group", buildScript);
        Assert.Contains("dpkg-deb --root-owner-group --build", buildScript);
        Assert.Contains("dpkg_deb_supports_root_owner_group", buildScript);
        Assert.Contains("has_dotnet_10_sdk", buildScript);
        Assert.DoesNotContain("dpkg-deb --help 2>&1 | grep", buildScript);
        Assert.DoesNotContain("dotnet --list-sdks 2>/dev/null | grep", buildScript);
        Assert.DoesNotContain("printf '%s\\n' \"$package_contents\" | grep", buildScript);
        Assert.Contains("rm -f -- \"$DEB_PATH\"", buildScript);
        Assert.Contains("rm -rf -- \"$PUBLISH_DIR\"", buildScript);
        Assert.Contains("--artifacts-path \"$BUILD_ROOT\"", buildScript);
        Assert.Contains("--output \"$PUBLISH_DIR\"", buildScript);
        Assert.Contains("/p:AppVersion=Linux/$PACKAGE_VERSION", buildScript);
        Assert.Contains("validate_published_app_version", buildScript);
        Assert.Contains("--force", buildScript);
        Assert.Contains("--disable-build-servers", buildScript);
        Assert.DoesNotContain("dotnet clean", buildScript);
        Assert.DoesNotContain("--no-incremental", buildScript);
        Assert.Contains("exec bash \"$SCRIPT_DIR/build-packages.sh\" \"$@\" --format deb", debWrapper);
        Assert.Contains("exec bash \"$SCRIPT_DIR/build-packages.sh\" \"$@\" --format rpm", rpmWrapper);
        Assert.Contains("*.sh text eol=lf", gitAttributes);
        Assert.Contains("packaging/linux/deb/postinst text eol=lf", gitAttributes);
        Assert.Contains("packaging/linux/deb/postrm text eol=lf", gitAttributes);

        var debPostinst = File.ReadAllText(Path.Combine(root, "packaging", "linux", "deb", "postinst"));
        var privilegeInstaller = File.ReadAllText(Path.Combine(root, "install-linux-privileges.sh"));
        Assert.Contains("/etc/netplan/01-network-manager-all.yaml", debPostinst);
        Assert.Contains("chown root:root", debPostinst);
        Assert.Contains("chmod 0600", debPostinst);
        Assert.DoesNotContain("netplan apply", debPostinst);
        Assert.DoesNotContain("cat > /etc/netplan", debPostinst);
        Assert.Contains("/etc/netplan/01-network-manager-all.yaml", privilegeInstaller);
        Assert.Contains("chown root:root", privilegeInstaller);
        Assert.Contains("chmod 0600", privilegeInstaller);
        Assert.DoesNotContain("netplan apply", privilegeInstaller);
        Assert.DoesNotContain("cat > /etc/netplan", privilegeInstaller);
    }

    [Fact]
    public void CardCheckout_PackageSupportsWpeAndGtkWebViewRuntimesAndHasNoManualRefreshButton()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "libreguard-vpn-linux.csproj"));
        var control = File.ReadAllText(Path.Combine(root, "packaging", "linux", "deb", "control"));
        var checkoutWindow = File.ReadAllText(Path.Combine(root, "Views", "CardCheckoutWindow.axaml"));
        var checkoutWindowCodeBehind = File.ReadAllText(Path.Combine(root, "Views", "CardCheckoutWindow.axaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.axaml"));
        var app = File.ReadAllText(Path.Combine(root, "App.axaml"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var linuxWebViewEnvironment = File.ReadAllText(Path.Combine(root, "Services", "LinuxWebViewEnvironment.cs"));

        Assert.Contains("Avalonia.Controls.WebView", project);
        Assert.DoesNotContain("WebView.Avalonia", project);
        Assert.Contains("Depends:", control);
        Assert.Contains("libwebkit2gtk-4.1-0", control);
        Assert.Contains("libxtst6", control);
        Assert.Contains("ca-certificates", control);
        Assert.Contains("fonts-noto-color-emoji", control);
        Assert.Contains("Recommends: libwpewebkit-2.0-1", control);
        Assert.Contains("xdg-utils", control);
        Assert.Contains("libwebkit2gtk-4.1-0", dockerfile);
        Assert.Contains("fonts-noto-color-emoji", dockerfile);
        Assert.Contains("fonts-noto-color-emoji", File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-packages.sh")));
        Assert.Contains("libWPEWebKit-2.0.so.1", linuxWebViewEnvironment);
        Assert.Contains("libWPEBackend-fdo-1.0.so.1", linuxWebViewEnvironment);
        Assert.Contains("libwpe-1.0.so.1", linuxWebViewEnvironment);
        Assert.Contains("PreferWebKitGtkInstead = true", linuxWebViewEnvironment);
        Assert.Contains("gtk.ExperimentalOffscreen = true", linuxWebViewEnvironment);
        Assert.Contains("gtk.ExperimentalOffscreen = false", linuxWebViewEnvironment);
        Assert.Contains("WEBKIT_DISABLE_DMABUF_RENDERER", linuxWebViewEnvironment);
        Assert.DoesNotContain("Refresh Account", checkoutWindow);
        Assert.DoesNotContain("Checkout URL", checkoutWindow);
        Assert.DoesNotContain("CheckoutUrlText", checkoutWindow);
        Assert.Contains("ActivateNextProfile(\"window-opened\")", checkoutWindowCodeBehind);
        Assert.DoesNotContain("ActivateNextProfile(\"initial\")", checkoutWindowCodeBehind);
        Assert.Contains("WindowStartupLocation=\"CenterOwner\"", checkoutWindow);
        Assert.Contains("ShowInTaskbar=\"True\"", checkoutWindow);
        Assert.Contains("Width=\"1100\"", checkoutWindow);
        Assert.Contains("Height=\"860\"", checkoutWindow);
        Assert.DoesNotContain("Refresh Account", mainWindow);
        Assert.DoesNotContain("Complete secure card checkout, then refresh", mainWindow);
        Assert.Contains("IsCardCheckoutLinkVisible", mainWindow);
        Assert.Contains("<FontFamily x:Key=\"IconFontFamily\">", app);
        Assert.Contains("Noto Color Emoji", app);
        Assert.Contains("FontFamily=\"{StaticResource IconFontFamily}\"", mainWindow);
        Assert.DoesNotContain("FontFamily=\"Segoe UI Symbol, Segoe UI, Sans\"", mainWindow);
        Assert.Contains("&#x1F6E1;", mainWindow);
        Assert.Contains("&#x1F5A5;", mainWindow);
        Assert.Contains("&#x1F4C8;", mainWindow);
        Assert.Contains("&#x1F4BB;", mainWindow);
        Assert.Contains("&#x1F4DC;", mainWindow);
        Assert.Contains("Text=\"🛡\"", mainWindow);

        var cardSectionStart = mainWindow.IndexOf("<StackPanel IsVisible=\"{Binding IsCardSelected}\">", StringComparison.Ordinal);
        var moneroSectionStart = mainWindow.IndexOf("<StackPanel IsVisible=\"{Binding IsMoneroSelected}\">", StringComparison.Ordinal);
        Assert.True(cardSectionStart >= 0 && moneroSectionStart > cardSectionStart);
        var cardSection = mainWindow[cardSectionStart..moneroSectionStart];
        Assert.Equal(1, cardSection.Split("Content=\"Change Payment Method\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void UnifiedBuild_GeneratesAndVerifiesBuildIdentityAndRecoversDependencies()
    {
        var root = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-packages.sh"));
        var debControl = File.ReadAllText(Path.Combine(root, "packaging", "linux", "deb", "control"));
        var rpmSpec = File.ReadAllText(Path.Combine(root, "packaging", "linux", "rpm", "libreguard-vpn-linux.spec"));

        Assert.Contains("write_build_identity", buildScript);
        Assert.Contains("validate_build_identity", buildScript);
        Assert.Contains("require_clean_source", buildScript);
        Assert.Contains("--allow-dirty-source", buildScript);
        Assert.Contains("status --porcelain --untracked-files=normal", buildScript);
        Assert.Contains("installed smoke-test packages must report dirty=false", buildScript);
        Assert.Contains("'\"dirty\": false'", buildScript);
        Assert.Contains("'\"dirty\": false'", File.ReadAllText(Path.Combine(root, "packaging", "linux", "smoke-deb.sh")));
        Assert.Contains("'\"dirty\": false'", File.ReadAllText(Path.Combine(root, "packaging", "linux", "smoke-rpm.sh")));
        Assert.Contains("build-info.json", buildScript);
        Assert.Contains("Version: 1.1.17", debControl);
        Assert.Contains("package_version 1.1.17", rpmSpec);
        Assert.Contains("dpkg -i", buildScript);
        Assert.Contains("install_debian_maintainer_script", buildScript);
        Assert.Contains("sed 's/\\r$//'", buildScript);
        Assert.Contains("--fix-broken \"$DEB_PATH\"", buildScript);
        Assert.Contains("dpkg-query -W -f='${Version}'", buildScript);
        Assert.Contains("sha256sum", buildScript);
        Assert.Contains("LibreGuard is running", buildScript);
        Assert.Contains("grep -q \"./opt/libreguard-vpn-linux/build-info.json\" <<< \"$package_contents\"", buildScript);
        Assert.DoesNotContain("echo \"$package_contents\" | grep", buildScript);
    }

    [Fact]
    public void RpmBuild_PreservesPublishedPayloadAndDeclaresFedoraDependencies()
    {
        var root = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "packaging", "linux", "build-packages.sh"));
        var spec = File.ReadAllText(Path.Combine(root, "packaging", "linux", "rpm", "libreguard-vpn-linux.spec"));
        var smokeScript = File.ReadAllText(Path.Combine(root, "packaging", "linux", "smoke-rpm.sh"));
        var fedoraIkeV2Policy = File.ReadAllText(Path.Combine(root, "packaging", "linux", "selinux", "libreguard_ikev2_fedora.cil"));
        var processRunner = File.ReadAllText(Path.Combine(root, "Services", "ProcessRunner.cs"));

        Assert.Contains("rpmbuild -bb", buildScript);
        Assert.Contains("rpm2cpio", buildScript);
        Assert.Contains("compare_payload \"$extracted_root\"", buildScript);
        Assert.Contains("FILEUSERNAME", buildScript);
        Assert.Contains("FILEGROUPNAME", buildScript);
        Assert.Contains("FILEMODES:perms", buildScript);
        Assert.Contains("sha256sum", buildScript);

        Assert.Contains("BuildArch: x86_64", spec);
        Assert.Contains("AutoReqProv: no", spec);
        Assert.Contains("%global __os_install_post %{nil}", spec);
        Assert.Contains("%global debug_package %{nil}", spec);
        Assert.Contains("%defattr(-,root,root,-)", spec);
        Assert.Contains("/opt/libreguard-vpn-linux", spec);
        Assert.Contains("/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair", spec);
        Assert.Contains("%attr(0755,root,root) /usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection", spec);
        Assert.Contains("%attr(0755,root,root) /usr/libexec/libreguard-vpn-linux/libreguard-vpn-recovery", spec);
        Assert.Contains("%attr(0644,root,root) /usr/lib/systemd/system/libreguard-vpn-recovery.service", spec);
        Assert.Contains("%attr(0755,root,root) /usr/lib/NetworkManager/dispatcher.d/90-libreguard-vpn-lifecycle", spec);
        Assert.Contains("%attr(0755,root,root) /usr/lib/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle", spec);
        Assert.Contains("%attr(0755,root,root) /etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle", spec);
        Assert.Contains("%attr(0644,root,root) /usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil", spec);
        Assert.Contains("%post", spec);
        Assert.Contains("semodule -i /usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil", spec);
        Assert.Contains("\"$leak_protection_helper\" remove >/dev/null 2>&1 || true", spec);
        Assert.Contains("systemctl enable libreguard-vpn-recovery.service", spec);
        Assert.Contains("/etc/netplan/01-network-manager-all.yaml", spec);
        Assert.Contains("chown root:root", spec);
        Assert.Contains("chmod 0600", spec);
        Assert.DoesNotContain("netplan apply", spec);
        Assert.DoesNotContain("cat > /etc/netplan", spec);
        Assert.Contains("%preun", spec);
        Assert.Contains("semodule -r libreguard_ikev2_fedora", spec);
        Assert.Contains("leak_protection_helper=\"/usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection\"", spec);
        Assert.Contains("\"$leak_protection_helper\" remove", spec);
        Assert.Contains("systemctl disable libreguard-vpn-recovery.service", spec);
        Assert.Contains("browser DNS leak-protection state", spec);
        Assert.Contains("X-LibreGuard-ManagedShortcut=true", spec);

        Assert.Contains("(allow ipsec_t user_home_dir_t (dir (getattr search)))", fedoraIkeV2Policy);
        Assert.Contains("(allow ipsec_t home_cert_t (dir (getattr search)))", fedoraIkeV2Policy);
        Assert.Contains("(allow ipsec_t home_cert_t (file (getattr open read)))", fedoraIkeV2Policy);
        Assert.DoesNotContain("NetworkManager_var_run_t", fedoraIkeV2Policy);
        Assert.DoesNotContain("(write", fedoraIkeV2Policy);

        foreach (var dependency in new[]
                 {
                     "NetworkManager",
                     "NetworkManager-openvpn",
                     "NetworkManager-strongswan",
                     "openssl",
                     "openssl-libs",
                     "glibc",
                     "libgcc",
                     "libstdc++",
                     "libicu",
                     "krb5-libs",
                     "ca-certificates",
                     "tzdata",
                     "polkit",
                     "iproute",
                     "webkit2gtk4.1",
                     "xorg-x11-server-Xwayland",
                     "libsecret",
                     "gnome-keyring",
                     "gnome-keyring-pam",
                     "xdg-utils",
                     "google-noto-color-emoji-fonts",
                     "policycoreutils",
                     "libselinux-utils",
                     "acl",
                     "libX11",
                     "libXtst",
                     "libICE",
                     "libSM",
                     "fontconfig",
                     "libxkbcommon",
                     "libxcb"
                 })
        {
            Assert.Contains($"Requires: {dependency}", spec);
        }

        Assert.Contains("dnf install -y \"$RPM_PATH\"", smokeScript);
        Assert.Contains("--webview-smoke", smokeScript);
        Assert.Contains("dnf remove -y libreguard-vpn-linux", smokeScript);
        Assert.Contains("libreguard_ikev2_fedora.cil", smokeScript);
        Assert.Contains("rpm -q acl", smokeScript);
        Assert.Contains("\"getfacl\" => \"/usr/bin/getfacl\"", processRunner);
        Assert.Contains("\"getenforce\" => \"/usr/bin/getenforce\"", processRunner);
        Assert.Contains("\"matchpathcon\" => \"/usr/bin/matchpathcon\"", processRunner);
        Assert.Contains("\"resolvectl\" => \"/usr/bin/resolvectl\"", processRunner);
        Assert.Contains("\"restorecon\" => \"/usr/bin/restorecon\"", processRunner);
        Assert.Contains("\"setfacl\" => \"/usr/bin/setfacl\"", processRunner);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "libreguard-vpn-linux.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
