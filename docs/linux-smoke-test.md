# Linux Smoke Test

Run this on a disposable Ubuntu 24.04 or newer VM and on Fedora Workstation 43 or 44. It verifies package installation, desktop integration, embedded card checkout, backend flows, and both NetworkManager VPN profiles. Fedora validation assumes a conventional DNF-managed system, not an Atomic/rpm-ostree image.

## Package

```bash
sudo apt-get update
sudo apt-get install -y git curl desktop-file-utils
mkdir -p ~/src
git clone /media/sf_libreguard-vpn-linux ~/src/libreguard-vpn-linux-hotfix
cd ~/src/libreguard-vpn-linux-hotfix
git switch codex/restore-standard-vpn-connectivity
test -z "$(git status --porcelain --untracked-files=normal)"
bash ./packaging/linux/build-packages.sh --format all --version 1.1.17 --rpm-release 1
bash ./packaging/linux/smoke-deb.sh ./artifacts/libreguard-vpn-linux_1.1.17_amd64.deb
```

On Fedora:

```bash
sudo dnf install -y git desktop-file-utils xauth xorg-x11-server-Xvfb mesa-dri-drivers
git clone <repo-url> ~/src/libreguard-vpn-linux-hotfix
cd ~/src/libreguard-vpn-linux-hotfix
git switch codex/restore-standard-vpn-connectivity
bash ./packaging/linux/build-rpm.sh --version 1.1.17 --rpm-release 1
bash ./packaging/linux/smoke-rpm.sh ./artifacts/libreguard-vpn-linux-1.1.17-1.x86_64.rpm
```

The package script installs missing build prerequisites on the smoke-test VM. Runtime dependencies are declared in the `.deb` and `.rpm` and are installed automatically by `apt` or DNF during the package install smoke.
Do not build directly from a VirtualBox `/media/sf_*` working tree. Clone it into `~/src` (or clone the remote repository there), check out the exact hotfix commit, and require an empty `git status`. The package builder fails closed on tracked or untracked changes; `--allow-dirty-source` exists only for development packages and cannot be used with `--install`.

After installation, verify the package came from the intended clean commit:

```bash
cat /opt/libreguard-vpn-linux/build-info.json
grep -F '"dirty": false' /opt/libreguard-vpn-linux/build-info.json
```

### Compatibility baseline checkpoint

Before installing the hotfix, create two clean VM-native worktrees and build the known-good and merged baselines independently:

```bash
cd ~/src/libreguard-vpn-linux-hotfix
git worktree add ~/src/libreguard-baseline-ead2854 ead2854dc5e01436cd1e8e04e993dd85ba57cc61
git worktree add ~/src/libreguard-baseline-56d50b7 56d50b7484214348f68bbe6fb85efe92900414e2

cd ~/src/libreguard-baseline-ead2854
test -z "$(git status --porcelain --untracked-files=normal)"
bash ./packaging/linux/build-deb.sh --version '1.1.17~ead2854' --allow-missing-google-oauth-client-id

cd ~/src/libreguard-baseline-56d50b7
test -z "$(git status --porcelain --untracked-files=normal)"
bash ./packaging/linux/build-deb.sh --version '1.1.17~56d50b7' --allow-missing-google-oauth-client-id
```

Install the `ead2854` package first and try one ordinary IKEv2 server with a current backend-issued client certificate. If that clean baseline also produces `AUTH_FAILED`, stop the rollback comparison and investigate backend certificate issuance, client-certificate validity/key matching, and server-side authorization. Only continue to compare `56d50b7` and the hotfix when the clean `ead2854` package connects.

### Standard VPN hotfix gate

Install the clean `1.1.17` hotfix package. Root YE/YR servers are excluded from this gate. Connect every ordinary IKEv2 server, then connect, disconnect, and reconnect one ordinary OpenVPN server. During IKEv2 import, confirm NetworkManager retained the backend address and IPv4-only selector:

```bash
nmcli -g vpn.data connection show <libreguard-ikev2-profile>
sudo journalctl -u NetworkManager --since '10 minutes ago' | grep -iE 'AUTH_FAILED|login-failed|connect-failed'
```

The `vpn.data` readback must include the backend `address`, the backend client certificate/key, the ordinary gateway CA (for example `ThunderGradVPN-Root-CA`), and `remote-ts = 0.0.0.0/0`; it must not include `::/0`. There must be one activation attempt for an ordinary CA and no alternate pinned-root sweep. Any `AUTH_FAILED` or `login-failed` entry for these ordinary servers fails the gate.

## Runtime Dependencies

```bash
sudo apt-get install -y network-manager network-manager-openvpn network-manager-strongswan strongswan openssl polkitd pkexec libsecret-tools gnome-keyring libpam-gnome-keyring xdg-utils libwebkit2gtk-4.1-0 libxtst6 xvfb
sudo dnf install -y NetworkManager NetworkManager-openvpn NetworkManager-strongswan openssl openssl-libs glibc libgcc libstdc++ libicu krb5-libs ca-certificates tzdata polkit iproute webkit2gtk4.1 libsecret gnome-keyring gnome-keyring-pam xdg-utils xorg-x11-server-Xwayland google-noto-color-emoji-fonts policycoreutils libselinux-utils acl xorg-x11-server-Xvfb
```

Run the first command on Debian/Ubuntu or the second on Fedora. `gnome-keyring` supplies the Secret Service backend. LibreGuard persistently selects its private file-backed store after a Secret Service failure so a broken or locked keyring is not prompted on every launch.

Before the VPN smoke, check the host NetworkManager version and Netplan permissions:

```bash
nmcli --version
stat -c '%U %a %n' /etc/netplan/01-network-manager-all.yaml
```

When the file exists, the Debian/RPM installers and `install-linux-privileges.sh` automatically enforce `root:root` ownership and mode `600`; they do not create or rewrite Netplan configuration.

NetworkManager 1.52 and newer uses `ipv4.routed-dns=yes`. Ubuntu 24.04's older NetworkManager releases use the equivalent explicit `10.254.0.53/32` route through the VPN; neither path permits a public DNS fallback.

If you are testing from a copied publish directory instead of an installed package, run the one-time privilege setup before the first IKEv2 connection:

```bash
sudo ./install-linux-privileges.sh
```

The installed `/etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle` hook performs normal IKEv2 route repair synchronously as root. A healthy package/publish-folder setup must not display a route-repair authorization prompt. If that hook is missing or fails, the GUI retains a Polkit-authorized recovery path and may prompt according to local policy.

## Manual App Flow

1. Launch LibreGuard from the desktop menu or run `/opt/libreguard-vpn-linux/libreguard-vpn-linux`.
   Confirm `~/Desktop/LibreGuard VPN.desktop` exists after installation when the desktop folder is present.
2. Sign in with a verified LibreGuard account.
3. Open Settings and run the dependency check.
4. Force the corrected software-rendered path under Xvfb/llvmpipe. The smoke window sends real X11 mouse/keyboard events, checks the input value through JavaScript, and samples a rendered blue swatch:

   ```bash
   LIBREGUARD_WEBVIEW_MODE=gtk-offscreen LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe \
     xvfb-run -a /opt/libreguard-vpn-linux/libreguard-vpn-linux --webview-smoke
   ```

   Repeat with `LIBREGUARD_WEBVIEW_MODE=gtk-native`, `wpe`, and `browser` on machines that provide those backends. The diagnostic override accepts `auto|gtk-native|wpe|gtk-offscreen|browser`.
5. In a VirtualBox/Linux Mint VM with 3D acceleration disabled, start a Creem test-mode card checkout and verify mouse focus, ordinary digits without Shift, letters, Tab, Backspace, card number/expiry/CVC entry, scrolling/select controls, and correct blue colors. Also confirm Continue in Browser and automatic successful-payment refresh.
6. Open Servers, select an IKEv2 server, and connect.
7. Confirm the connection completes without an admin credential prompt after the one-time installer has been run.
8. Confirm NetworkManager created and activated a `libreguard-ikev2-*` profile:

```bash
nmcli connection show --active
```

9. Confirm the broken unconditional route is gone:

```bash
ip rule show | grep "lookup 220"
```

Expected:

- `220: from all lookup 220` is not present.

10. Disconnect from the app and confirm the profile is inactive.
11. Repeat with OpenVPN on a Pro account/server.
12. Open Certificates, download a config and certificate, and confirm files appear under `$XDG_STATE_HOME/libreguard/downloads`.

Operational VPN credentials are separate from downloads. Confirm OpenVPN files remain under the last-known-good `~/.local/state/libreguard/configs` location while IKEv2 files are under Fedora's certificate-labelled `~/.cert/libreguard` location. Both directories must be `0700` and their files `0600`:

```bash
stat -c '%a %n' ~/.local/state/libreguard/configs ~/.local/state/libreguard/configs/* ~/.cert/libreguard ~/.cert/libreguard/*
```

On Fedora with SELinux enforcing, confirm the default certificate contexts and check that the flow produced no relevant denial:

```bash
getenforce
matchpathcon -V ~/.cert/libreguard ~/.cert/libreguard/*
sudo ausearch -m AVC,USER_AVC -ts recent | grep -iE 'libreguard|openvpn|strongswan|charon'
```

If IKEv2 context validation fails, install `policycoreutils`, run `restorecon -RF ~/.cert/libreguard`, and retry the connection. LibreGuard intentionally refuses VPN preparation when SELinux is enforcing and the correct labels cannot be established.

On Fedora with NetworkManager 1.56, also confirm the activation log contains `vpn-ikev2-fedora-credential-helper-workaround` with `credential_acl=uid0-temporary state=enabled` followed by `credential_acl=removed state=restored-private`. After both successful and failed attempts, `getfacl -p ~/.cert/libreguard ~/.cert/libreguard/*` must show no added `user:root`/`user:0` entry, and `semodule -l` must list `libreguard_ikev2_fedora`. No workaround event or ACL mutation should occur on Debian/Ubuntu or other NetworkManager versions.

## Fedora Workstation Release Gate

At least one Fedora Workstation VM release smoke must run with SELinux enforcing. Complete these checks:

1. Install the RPM with DNF, launch from the desktop menu, and complete embedded card checkout.
2. Verify Secret Service storage, then repeat with Secret Service unavailable. After the first fallback, restart twice and confirm no further keyring prompt/error appears and the session/device identity still restore.
3. Connect and disconnect OpenVPN and IKEv2; verify profile creation, private DNS, credential modes/contexts, and no relevant AVC denial.
4. Confirm repeated IKEv2 connects and reboots repair table 220 through the pre-up dispatcher without prompts. Then temporarily disable the pre-up hook in a disposable VM and exercise the Polkit recovery prompt once.
5. Upgrade in place with `sudo dnf upgrade ./artifacts/libreguard-vpn-linux-<version>-<release>.x86_64.rpm`; confirm the managed desktop shortcut is refreshed and VPN credentials are not treated as package-owned user data.
6. Remove with `sudo dnf remove libreguard-vpn-linux`; confirm package files and the managed shortcut are removed, while unrelated user files are untouched.

## Private DNS and Ad Blocking

Run these checks only after every selectable VPN server has the LibreGuard regular and filtered resolvers, DNS interception, and policy reconciliation enabled.

1. Connect with IKEv2 and inspect the active LibreGuard profile, replacing the placeholder with the profile name shown by `nmcli connection show --active`:

```bash
nmcli -f ipv4.dns,ipv4.dns-search,ipv4.routed-dns,ipv4.ignore-auto-dns,ipv4.dns-priority,ipv4.never-default,ipv4.ignore-auto-routes,ipv6.dns,ipv6.dns-search,ipv6.ignore-auto-dns,ipv6.dns-priority,ipv6.never-default,ipv6.ignore-auto-routes connection show <libreguard-ikev2-profile>
```

Expected on NetworkManager 1.52 or newer:

- the only configured DNS server is `10.254.0.53`;
- the IPv4 routing domain is `~.`;
- `ipv4.routed-dns` is `yes`;
- both `ignore-auto-dns` values are `yes`;
- both DNS priorities are `-2147483648`;
- `ipv4.never-default` and `ipv4.ignore-auto-routes` are `no`;
- `ipv6.never-default` and `ipv6.ignore-auto-routes` are `yes` for IKEv2;
- no IPv6 DNS server or search domain is configured.

On older NetworkManager releases, `ipv4.routed-dns` may be unavailable. Confirm instead that the profile contains `10.254.0.53/32` in `ipv4.routes` and that no public resolver is configured.

2. Confirm NetworkManager's active DNS state and, when `systemd-resolved` is available, the effective resolver state:

```bash
nmcli -g IP4.DNS,IP6.DNS connection show --active id <libreguard-ikev2-profile>
resolvectl status
resolvectl dns <vpn-device>
resolvectl domain <vpn-device>
```

The active profile and VPN link must expose only `10.254.0.53`, and the VPN link must own `~.`. A physical DHCP resolver may remain visible on its ordinary link, but it must not appear as a global resolver or on another link that owns `~.`; either condition is a DNS leak and must prevent `Connected`. Hosts without a working `resolvectl` are validated through NetworkManager's active DNS state.

While connected, verify browser DoH containment as well:

```bash
grep -Fqx '0.0.0.0 use-application-dns.net # LibreGuard VPN DoH canary' /etc/hosts
sudo find /etc/opt/chrome/policies/managed \
  /etc/chromium/policies/managed \
  /etc/chromium-browser/policies/managed \
  /etc/brave/policies/managed \
  /etc/opt/edge/policies/managed \
  -maxdepth 1 -name libreguard-dns-over-https.json -exec grep -H '"DnsOverHttpsMode":"off"' {} \; 2>/dev/null
```

Only policy directories for installed browsers are expected. After the last LibreGuard VPN disconnects, the hosts marker and every `libreguard-dns-over-https.json` file must be gone. Existing administrator browser-policy files are never modified. Firefox's canary applies to automatic/default DoH selection; a user-forced or enterprise-locked Firefox DoH configuration remains outside this guarantee.

3. Before testing the VPN, record the physical connection's IPv6 setting and the literal IPv4 route to the VPN server. The outer server route is the one permitted physical route while the tunnel is being established:

```bash
nmcli -g GENERAL.CONNECTION device show <physical-device>
nmcli -g ipv6.never-default connection show <physical-connection>
ip -4 route get <literal-server-ip>
```

The server address must be a literal IPv4 address and its route must use the physical device before connection.

4. Confirm the private resolver and representative IPv4/IPv6 traffic use the VPN device:

```bash
ip -4 route get 10.254.0.53
ip -4 route get 1.1.1.1
ip -6 route get 2606:4700:4700::1111
```

The two IPv4 results must contain `dev` followed by the active `tun*` or `lgvpn*` device. The IPv6 result must either use that VPN device or fail with no usable route (for an IPv4-only VPN session). It must never name the physical interface.

For a session on a host that had a physical IPv6 default route, confirm LibreGuard temporarily applies containment through NetworkManager (this remains required even when the VPN also supplies IPv6):

```bash
nmcli -g ipv6.never-default connection show <physical-connection>
```

The value must be `yes` while the VPN is connected and return to the recorded pre-VPN value after disconnect. If LibreGuard cannot apply or verify that guard, it must fail before reporting `Connected`.

5. Install `dnsutils` on the disposable test VM and verify ordinary IPv4 and IPv6 records resolve over the private resolver:

```bash
dig @10.254.0.53 example.com A
dig @10.254.0.53 example.com AAAA
```

6. Disconnect and confirm the LibreGuard profile no longer contributes DNS, the physical IPv6 `never-default` setting is restored, and the host's pre-VPN resolver returns.
7. Repeat steps 1-6 with OpenVPN.
8. With a Free account, confirm the Ad Blocking card is visible but locked with a Pro upgrade action, and a domain from the active blocklist still resolves through regular DNS.
9. With a Pro account, leave Ad Blocking off and confirm the same regular result. Enable it while the VPN remains connected, wait the propagation interval shown by the app, and confirm the domain changes to the active blocklist policy's blocked response. Disable it and confirm regular resolution returns without reconnecting.
10. On a coordinated staging node, query a public resolver address over TCP and UDP port 53 and confirm the VPN server intercepts the traffic. Stop the regular resolver and confirm DNS fails instead of falling back to a public resolver.

## Root YE Regression

Repeat the IKEv2 flow with one known server that presents a Let's Encrypt ECDSA chain rooted at Root YE.

After connecting, confirm NetworkManager stored the generated gateway CA bundle:

```bash
nmcli -g vpn.data connection show <libreguard-ikev2-profile>
```

Expected:

- `vpn.data` includes `certificate=` and points at a generated single-certificate `*.gateway-ca-bundle.crt` file; alternate roots are stored as sibling `*.gateway-ca-<n>.crt` files for issuer-specific retry.
- The connection activates successfully.
- `journalctl` does not show `no issuer certificate found for "C=US, O=Let's Encrypt, CN=YE2"`.
- `journalctl` does not show `no trusted ECDSA public key found`.

If you need to inspect the live attempt, use:

```bash
journalctl -u NetworkManager --since "5 minutes ago" | grep -E "charon-nm|YE2|ECDSA|issuer certificate"
```

## Expected NetworkManager Profiles

- OpenVPN profiles are imported through `nmcli connection import type openvpn file <profile.ovpn>`.
- IKEv2/IPSec profiles are created as `vpn-type strongswan` connections.
- The GUI must not be run with root privileges; NetworkManager and Polkit own privileged networking work.

## Silent Startup Triage

If the app appears to hang at launch with no visible window, collect evidence in this order.

1. Launch from a terminal and capture stderr. LibreGuard now writes timestamped startup markers to stderr and to `$XDG_STATE_HOME/libreguard/startup.log`.
2. Check for unresolved native dependencies:

```bash
ldd ./libreguard-vpn-linux | grep "not found"
```

3. Retry with software rendering toggles one at a time:

```bash
LIBGL_ALWAYS_SOFTWARE=1 ./libreguard-vpn-linux
MESA_LOADER_DRIVER_OVERRIDE=llvmpipe ./libreguard-vpn-linux
```

4. Check whether the process is running but hidden:

```bash
ps -ef | grep libreguard
wmctrl -lp | grep -i libreguard
```

5. If it still stalls, capture a syscall trace:

```bash
strace -f -tt -o startup.strace ./libreguard-vpn-linux
```

6. Record the desktop session context:

```bash
echo "$DISPLAY"
echo "$XDG_SESSION_TYPE"
echo "$XDG_CURRENT_DESKTOP"
loginctl show-session "$XDG_SESSION_ID"
```

7. Check recent journal output:

```bash
journalctl --user --since "5 minutes ago"
journalctl -b --since "5 minutes ago" | grep -Ei "libreguard|avalonia|tray|x11|dbus"
```

8. Compare direct publish-folder launch with the packaged install path under `/opt/libreguard-vpn-linux`.
9. If the desktop shortcut does not open the app on Mint or another desktop environment, launch `/opt/libreguard-vpn-linux/libreguard-vpn-linux` from a terminal and inspect `$XDG_STATE_HOME/libreguard/startup.log` or `~/.local/state/libreguard/startup.log`.
