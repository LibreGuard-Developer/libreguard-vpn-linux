%{!?package_version:%global package_version 1.1.17}
%{!?package_release:%global package_release 1}
%{!?payload_root:%global payload_root /nonexistent/libreguard-vpn-linux-payload}
%{!?selinux_policy_source:%global selinux_policy_source /nonexistent/libreguard_ikev2_fedora.cil}

# The payload is a pre-published self-contained .NET application. RPM build-root
# post-processing must not strip or otherwise rewrite those verified binaries.
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

Name: libreguard-vpn-linux
Version: %{package_version}
Release: %{package_release}
Summary: LibreGuard VPN client for Linux
License: GPL-2.0-or-later
URL: https://libreguard.net
BuildArch: x86_64
AutoReqProv: no

Requires: NetworkManager
Requires: NetworkManager-openvpn
Requires: NetworkManager-strongswan
Requires: openssl
Requires: openssl-libs
Requires: glibc
Requires: libgcc
Requires: libstdc++
Requires: libicu
Requires: krb5-libs
Requires: ca-certificates
Requires: tzdata
Requires: polkit
Requires: iproute
Requires: webkit2gtk4.1
Requires: libsecret
Requires: gnome-keyring
Requires: gnome-keyring-pam
Requires: xdg-utils
Requires: xorg-x11-server-Xwayland
Requires: google-noto-color-emoji-fonts
Requires: policycoreutils
Requires: libselinux-utils
Requires: acl
Requires: libX11
Requires: libXtst
Requires: libICE
Requires: libSM
Requires: fontconfig
Requires: libxkbcommon
Requires: libxcb
Requires: desktop-file-utils
Requires: gtk-update-icon-cache
Requires: hicolor-icon-theme
Requires: xdg-user-dirs

%description
Native LibreGuard VPN desktop client supporting IKEv2/IPSec and OpenVPN through NetworkManager.

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a %{payload_root}/. %{buildroot}/
install -D -m 0644 %{selinux_policy_source} %{buildroot}/usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil

%files
%defattr(-,root,root,-)
/opt/libreguard-vpn-linux
%dir /usr/libexec/libreguard-vpn-linux
%attr(0755,root,root) /usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair
%attr(0755,root,root) /usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection
%attr(0755,root,root) /usr/lib/NetworkManager/dispatcher.d/90-libreguard-vpn-lifecycle
%attr(0755,root,root) /usr/lib/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle
%attr(0755,root,root) /etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle
%attr(0644,root,root) /usr/share/applications/libreguard-vpn-linux.desktop
%attr(0644,root,root) /usr/share/icons/hicolor/256x256/apps/libreguard-vpn-linux.png
%attr(0644,root,root) /usr/share/polkit-1/actions/net.libreguard.vpn.linux.repair-ikev2-routing.policy
%attr(0644,root,root) /usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil

%post
if [ -r /etc/os-release ] \
    && grep -Eq '^ID=(fedora|"fedora")$' /etc/os-release; then
    if ! semodule -i /usr/share/selinux/packages/libreguard/libreguard_ikev2_fedora.cil; then
        echo "error: could not install the Fedora IKEv2 SELinux policy" >&2
        exit 1
    fi
fi

leak_protection_helper="/usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection"
if [ -x "$leak_protection_helper" ]; then
    # Safe on upgrades: the helper refuses cleanup while a LibreGuard VPN is active.
    "$leak_protection_helper" remove >/dev/null 2>&1 || true
fi

netplan_file="/etc/netplan/01-network-manager-all.yaml"
if [ -e "$netplan_file" ]; then
    if ! chown root:root "$netplan_file" || ! chmod 0600 "$netplan_file"; then
        echo "error: could not secure $netplan_file (expected root:root, mode 0600)" >&2
        exit 1
    fi
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor || true
fi

desktop_entry_source="/usr/share/applications/libreguard-vpn-linux.desktop"
managed_marker="X-LibreGuard-ManagedShortcut=true"
if [ -f "$desktop_entry_source" ]; then
    while IFS=: read -r user_name _ uid _ _ home_dir shell_path; do
        [ "$uid" -ge 1000 ] 2>/dev/null || continue
        [ "$uid" -ne 65534 ] 2>/dev/null || continue
        [ -d "$home_dir" ] || continue
        case "$shell_path" in
            */nologin|*/false) continue ;;
        esac

        desktop_dir=""
        if command -v runuser >/dev/null 2>&1 && command -v xdg-user-dir >/dev/null 2>&1; then
            desktop_dir="$(runuser -u "$user_name" -- xdg-user-dir DESKTOP 2>/dev/null || true)"
        fi
        [ -n "$desktop_dir" ] || desktop_dir="$home_dir/Desktop"
        [ -d "$desktop_dir" ] || continue

        shortcut_path="$desktop_dir/LibreGuard VPN.desktop"
        if [ -f "$shortcut_path" ]; then
            if ! grep -q "^$managed_marker$" "$shortcut_path" 2>/dev/null \
                && ! { grep -q '^Name=LibreGuard VPN$' "$shortcut_path" 2>/dev/null \
                    && grep -q '^Exec=/opt/libreguard-vpn-linux/libreguard-vpn-linux$' "$shortcut_path" 2>/dev/null; }; then
                continue
            fi
        fi

        temporary_path="$(mktemp)"
        cp "$desktop_entry_source" "$temporary_path"
        echo >> "$temporary_path"
        echo "$managed_marker" >> "$temporary_path"
        install -o "$user_name" -g "$user_name" -m 0755 "$temporary_path" "$shortcut_path"
        rm -f "$temporary_path"
    done < /etc/passwd
fi

%preun
if [ "$1" -eq 0 ]; then
    if command -v semodule >/dev/null 2>&1 \
        && semodule -l 2>/dev/null | grep -q '^libreguard_ikev2_fedora\([[:space:]]\|$\)'; then
        if ! semodule -r libreguard_ikev2_fedora; then
            echo "LibreGuard could not remove its Fedora IKEv2 SELinux policy; package removal was stopped." >&2
            exit 1
        fi
    fi

    leak_protection_helper="/usr/libexec/libreguard-vpn-linux/libreguard-ipv6-leak-protection"
    if [ -x "$leak_protection_helper" ] && ! "$leak_protection_helper" remove; then
        echo "LibreGuard could not remove its transient IPv6 and browser DNS leak-protection state; package removal was stopped." >&2
        exit 1
    fi

    managed_marker="X-LibreGuard-ManagedShortcut=true"
    while IFS=: read -r user_name _ uid _ _ home_dir shell_path; do
        [ "$uid" -ge 1000 ] 2>/dev/null || continue
        [ "$uid" -ne 65534 ] 2>/dev/null || continue
        [ -d "$home_dir" ] || continue
        case "$shell_path" in
            */nologin|*/false) continue ;;
        esac

        desktop_dir=""
        if command -v runuser >/dev/null 2>&1 && command -v xdg-user-dir >/dev/null 2>&1; then
            desktop_dir="$(runuser -u "$user_name" -- xdg-user-dir DESKTOP 2>/dev/null || true)"
        fi
        [ -n "$desktop_dir" ] || desktop_dir="$home_dir/Desktop"
        shortcut_path="$desktop_dir/LibreGuard VPN.desktop"
        if [ -f "$shortcut_path" ] \
            && { grep -q "^$managed_marker$" "$shortcut_path" 2>/dev/null \
                || { grep -q '^Name=LibreGuard VPN$' "$shortcut_path" 2>/dev/null \
                    && grep -q '^Exec=/opt/libreguard-vpn-linux/libreguard-vpn-linux$' "$shortcut_path" 2>/dev/null; }; }; then
            rm -f "$shortcut_path"
        fi
    done < /etc/passwd
fi

%postun
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q /usr/share/icons/hicolor || true
fi
