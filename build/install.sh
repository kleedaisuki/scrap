#!/bin/sh
# 为当前 Unix 用户幂等安装 scrap。Idempotently install scrap for the current Unix user.
set -eu

configure_path=1
if [ "${1-}" = "--no-path" ]; then configure_path=0; shift; fi
if [ "$#" -ne 0 ]; then
    echo "用法 / Usage: ./install.sh [--no-path]" >&2
    exit 2
fi

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
scrap_root=$HOME/.scrap
bin_dir=$scrap_root/bin
payload_dir=$script_dir/payload
programs="scrap scrapd scrap-gui"
begin_marker='# >>> scrap PATH / scrap PATH begin >>>'
end_marker='# <<< scrap PATH / scrap PATH end <<<'

# 非阻塞提示 Linux key provider 的系统依赖。Warn about Linux key-provider prerequisites without blocking installation.
check_linux_key_provider() {
    [ "$(uname -s 2>/dev/null || true)" = "Linux" ] || return 0
    libraries=$(ldconfig -p 2>/dev/null || true)
    secret_found=0
    gio_found=0
    if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists libsecret-1; then secret_found=1; fi
    if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists gio-2.0; then gio_found=1; fi
    if printf '%s\n' "$libraries" | grep -q 'libsecret-1\.so'; then secret_found=1; fi
    if printf '%s\n' "$libraries" | grep -q 'libgio-2\.0\.so'; then gio_found=1; fi
    if [ "$secret_found" -eq 0 ]; then
        echo '警告：未检测到 libsecret-1；请安装 libsecret-1-0（Debian/Ubuntu）或 libsecret（Fedora/Arch）。' >&2
        echo 'Warning: libsecret-1 was not detected; install libsecret-1-0 (Debian/Ubuntu) or libsecret (Fedora/Arch).' >&2
    fi
    if [ "$gio_found" -eq 0 ]; then
        echo '警告：未检测到 GLib/GIO；scrap 的 Linux key provider 需要 GLib。' >&2
        echo 'Warning: GLib/GIO was not detected; the Linux key provider requires GLib.' >&2
    fi
    if [ -z "${DBUS_SESSION_BUS_ADDRESS-}" ]; then
        echo '警告：当前没有用户会话 D-Bus；请在带 Secret Service（如 GNOME Keyring/KWallet）的登录会话中运行 scrap。' >&2
        echo 'Warning: no user-session D-Bus is active; run scrap in a login session with Secret Service (for example GNOME Keyring/KWallet).' >&2
    fi
}

# 在有限时间内尽力停止旧 daemon。Stop the old daemon on a best-effort, bounded-time basis.
stop_installed_daemon() {
    [ -x "$bin_dir/scrap" ] || return 0
    "$bin_dir/scrap" daemon shutdown --if-running >/dev/null 2>&1 & daemon_command=$!
    (sleep 5; kill -KILL "$daemon_command" 2>/dev/null || true) & watchdog=$!
    wait "$daemon_command" 2>/dev/null || true
    kill "$watchdog" 2>/dev/null || true
    wait "$watchdog" 2>/dev/null || true
    sleep 1
}

# 将受管理 PATH 块精确写入一次。Write the managed PATH block exactly once.
add_path_block() {
    profile=$1
    [ -f "$profile" ] || : > "$profile"
    begin_count=$(grep -Fxc "$begin_marker" "$profile" || true)
    end_count=$(grep -Fxc "$end_marker" "$profile" || true)
    if [ "$begin_count" -eq 1 ] && [ "$end_count" -eq 1 ] && awk -v begin="$begin_marker" -v end="$end_marker" '
        $0 == begin { if (saw_begin || saw_end) invalid = 1; saw_begin = 1 }
        $0 == end { if (!saw_begin || saw_end) invalid = 1; saw_end = 1 }
        END { exit !(saw_begin && saw_end && !invalid) }
    ' "$profile"; then
        return 0
    fi
    if [ "$begin_count" -ne 0 ] || [ "$end_count" -ne 0 ]; then
        echo "正在修复 $profile 中残缺的 scrap PATH 标记。Repairing incomplete scrap PATH markers in $profile." >&2
        temp=$(mktemp "${TMPDIR:-/tmp}/scrap-profile-install.XXXXXX")
        backup=$(mktemp "${TMPDIR:-/tmp}/scrap-profile-install-backup.XXXXXX")
        if ! cat "$profile" > "$backup"; then
            rm -f "$temp" "$backup"
            return 1
        fi
        # 删除 begin 后与规范模板匹配的最长前缀；第一个非模板行及其后内容属于用户。
        # Remove the longest canonical template prefix after begin; the first non-template line remains user-owned.
        if ! awk -v begin="$begin_marker" -v end="$end_marker" '
            BEGIN {
                body[1] = "case \":$PATH:\" in"
                body[2] = "  *\":$HOME/.scrap/bin:\"*) ;;"
                body[3] = "  *) export PATH=\"$HOME/.scrap/bin:$PATH\" ;;"
                body[4] = "esac"
            }
            $0 == begin { repairing = 1; matched = 0; next }
            repairing && $0 == end { repairing = 0; matched = 0; next }
            repairing && matched < 4 && $0 == body[matched + 1] { matched++; next }
            repairing { repairing = 0; matched = 0; print; next }
            $0 == end { next }
            { print }
        ' "$profile" > "$temp" || ! cat "$temp" > "$profile"; then
            cat "$backup" > "$profile" 2>/dev/null || true
            rm -f "$temp" "$backup"
            return 1
        fi
        rm -f "$temp" "$backup"
    fi
    {
        printf '\n%s\n' "$begin_marker"
        printf '%s\n' 'case ":$PATH:" in'
        printf '%s\n' '  *":$HOME/.scrap/bin:"*) ;;'
        printf '%s\n' '  *) export PATH="$HOME/.scrap/bin:$PATH" ;;'
        printf '%s\n' 'esac'
        printf '%s\n' "$end_marker"
    } >> "$profile"
}

for program in $programs; do
    if [ ! -f "$payload_dir/$program" ]; then
        echo "安装包不完整，缺少 ${program}。Package is incomplete: $program is missing." >&2
        exit 1
    fi
done

check_linux_key_provider
stop_installed_daemon
mkdir -p "$bin_dir"
chmod 700 "$scrap_root" "$bin_dir"

transaction=$(mktemp -d "$scrap_root/.install.XXXXXX")
staged=$transaction/new
backup=$transaction/old
mkdir -p "$staged" "$backup"
chmod 700 "$transaction" "$staged" "$backup"
backed_up=''
installed_new=''

# 在全部旧文件成功移走后再提交新文件；失败时恢复旧版本。
# Commit new files only after every old file moved successfully; restore the old version on failure.
rollback() {
    for program in $installed_new; do
        rm -f "$bin_dir/$program"
    done
    for program in $backed_up; do
        rm -f "$bin_dir/$program"
        mv "$backup/$program" "$bin_dir/$program"
    done
    rm -rf "$transaction"
}
trap 'rollback' HUP INT TERM

# 将可失败步骤包装为显式事务，避免 set -e 在不同 shell 中产生特殊行为。
# Wrap fallible steps in an explicit transaction to avoid shell-specific set -e behavior.
install_payload() {
    for program in $programs; do
        cp "$payload_dir/$program" "$staged/$program" || return 1
        chmod 700 "$staged/$program" || return 1
    done
    for program in $programs; do
        if [ -e "$bin_dir/$program" ]; then
            mv "$bin_dir/$program" "$backup/$program" || return 1
            backed_up="$backed_up $program"
        fi
    done
    for program in $programs; do
        mv "$staged/$program" "$bin_dir/$program" || return 1
        installed_new="$installed_new $program"
    done
}

if ! install_payload; then
    rollback
    echo '安装失败，已恢复先前版本。Installation failed; the previous version was restored.' >&2
    exit 1
fi
trap - HUP INT TERM
rm -rf "$transaction"

if [ "$configure_path" -eq 1 ]; then
    add_path_block "$HOME/.profile"
    case "${SHELL-}" in
        */bash) add_path_block "$HOME/.bashrc" ;;
        */zsh) add_path_block "$HOME/.zshrc"; add_path_block "$HOME/.zprofile" ;;
    esac
fi

printf 'scrap 已安装到 %s。请打开新终端以使用更新后的 PATH。\n' "$bin_dir"
printf 'scrap was installed to %s. Open a new terminal to use the updated PATH.\n' "$bin_dir"
