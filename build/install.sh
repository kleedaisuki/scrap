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

# 在有限时间内尽力停止旧 daemon。Stop the old daemon on a best-effort, bounded-time basis.
stop_installed_daemon() {
    [ -x "$bin_dir/scrap" ] || return 0
    "$bin_dir/scrap" daemon shutdown >/dev/null 2>&1 & daemon_command=$!
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
    if grep -F "$begin_marker" "$profile" >/dev/null 2>&1; then return; fi
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
        echo "安装包不完整，缺少 $program。Package is incomplete: $program is missing." >&2
        exit 1
    fi
done

stop_installed_daemon
mkdir -p "$bin_dir"
chmod 700 "$scrap_root" "$bin_dir"

transaction=$(mktemp -d "$scrap_root/.install.XXXXXX")
staged=$transaction/new
backup=$transaction/old
mkdir -p "$staged" "$backup"
chmod 700 "$transaction" "$staged" "$backup"

# 在全部旧文件成功移走后再提交新文件；失败时恢复旧版本。
# Commit new files only after every old file moved successfully; restore the old version on failure.
rollback() {
    for program in $programs; do
        rm -f "$bin_dir/$program"
        if [ -f "$backup/$program" ]; then mv "$backup/$program" "$bin_dir/$program"; fi
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
        if [ -e "$bin_dir/$program" ]; then mv "$bin_dir/$program" "$backup/$program" || return 1; fi
    done
    for program in $programs; do mv "$staged/$program" "$bin_dir/$program" || return 1; done
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
