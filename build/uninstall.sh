#!/bin/sh
# 为当前 Unix 用户卸载 scrap；普通模式保留数据。Uninstall scrap for the current Unix user; normal mode preserves data.
set -eu

purge=0
yes=0
configure_path=1
while [ "$#" -gt 0 ]; do
    case "$1" in
        --purge) purge=1 ;;
        --yes) yes=1 ;;
        --no-path) configure_path=0 ;;
        *) echo "用法 / Usage: ./uninstall.sh [--purge] [--yes] [--no-path]" >&2; exit 2 ;;
    esac
    shift
done

scrap_root=$HOME/.scrap
bin_dir=$scrap_root/bin
programs="scrap scrapd scrap-gui"
begin_marker='# >>> scrap PATH / scrap PATH begin >>>'
end_marker='# <<< scrap PATH / scrap PATH end <<<'

# 在有限时间内尽力停止 daemon。Stop the daemon on a best-effort, bounded-time basis.
stop_installed_daemon() {
    [ -x "$bin_dir/scrap" ] || return 0
    "$bin_dir/scrap" daemon shutdown --if-running >/dev/null 2>&1 & daemon_command=$!
    (sleep 5; kill -KILL "$daemon_command" 2>/dev/null || true) & watchdog=$!
    wait "$daemon_command" 2>/dev/null || true
    kill "$watchdog" 2>/dev/null || true
    wait "$watchdog" 2>/dev/null || true
    sleep 1
}

# 只移除本安装器拥有的完整标记块。Remove only the complete block owned by this installer.
remove_path_block() {
    profile=$1
    [ -f "$profile" ] || return 0

    # 无标记时必须零写入；残缺或重复标记留给用户检查，绝不吞掉后续配置。
    # A marker-free file receives zero writes; partial or duplicate markers are left for user inspection.
    begin_count=$(grep -Fxc "$begin_marker" "$profile" || true)
    end_count=$(grep -Fxc "$end_marker" "$profile" || true)
    if [ "$begin_count" -eq 0 ] && [ "$end_count" -eq 0 ]; then return 0; fi
    if [ "$begin_count" -ne 1 ] || [ "$end_count" -ne 1 ] || ! awk -v begin="$begin_marker" -v end="$end_marker" '
        $0 == begin { if (saw_begin || saw_end) invalid = 1; saw_begin = 1 }
        $0 == end { if (!saw_begin || saw_end) invalid = 1; saw_end = 1 }
        END { exit !(saw_begin && saw_end && !invalid) }
    ' "$profile"; then
        echo "PATH 标记残缺或顺序无效，已保留 ${profile}。PATH markers are incomplete or invalid; preserved $profile." >&2
        return 0
    fi

    temp=$(mktemp "${TMPDIR:-/tmp}/scrap-profile.XXXXXX")
    backup=$(mktemp "${TMPDIR:-/tmp}/scrap-profile-backup.XXXXXX")
    if ! cat "$profile" > "$backup"; then
        rm -f "$temp" "$backup"
        return 1
    fi
    if ! awk -v begin="$begin_marker" -v end="$end_marker" '
        $0 == begin { dropping = 1; next }
        dropping && $0 == end { dropping = 0; next }
        !dropping { print }
    ' "$profile" > "$temp"; then
        rm -f "$temp" "$backup"
        return 1
    fi
    # 通过现有路径原地写入会跟随 symlink，并保留目标 inode、权限和 xattr。
    # Writing through the existing path follows symlinks and preserves target inode, mode, and xattrs.
    if ! cat "$temp" > "$profile"; then
        cat "$backup" > "$profile" 2>/dev/null || true
        rm -f "$temp" "$backup"
        echo "无法更新 ${profile}；已尝试恢复原内容。Could not update $profile; restoration was attempted." >&2
        return 1
    fi
    rm -f "$temp" "$backup"
}

if [ "$purge" -eq 1 ] && [ "$yes" -ne 1 ]; then
    printf '将永久删除 %s 中的数据。输入 PURGE 继续 / Data will be deleted permanently. Type PURGE to continue: ' "$scrap_root"
    IFS= read -r answer
    if [ "$answer" != "PURGE" ]; then echo '已取消。Cancelled.'; exit 0; fi
fi

# 尽力停止 daemon；卸载操作本身仍保持幂等。Stop the daemon best-effort; uninstall remains idempotent.
stop_installed_daemon

if [ "$configure_path" -eq 1 ]; then
    remove_path_block "$HOME/.profile"
    remove_path_block "$HOME/.bashrc"
    remove_path_block "$HOME/.zshrc"
    remove_path_block "$HOME/.zprofile"
fi

if [ "$purge" -eq 1 ]; then
    rm -rf "$scrap_root"
    echo 'scrap 程序与本地数据已删除。OS 密钥存储或备份可能仍保留历史副本。'
    echo 'scrap programs and local data were removed. OS key stores or backups may retain historical copies.'
    exit 0
fi

for program in $programs; do rm -f "$bin_dir/$program"; done
rmdir "$bin_dir" 2>/dev/null || true
echo "scrap 程序已卸载；data 与配置已保留在 ${scrap_root}。"
echo "scrap programs were removed; data and configuration remain in $scrap_root."
