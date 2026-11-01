#!/usr/bin/env bash
# Restarts the panel backend on hosts without systemd.
if [ -d /run/systemd/system ]; then systemctl restart teamligma-panel; exit; fi
panel_pids() {
    for p in $(pgrep -x dotnet); do
        tr '\0' ' ' < /proc/$p/cmdline 2>/dev/null | grep -q 'TeamLigma\.Panel\.Server\.dll *$' && echo "$p"
    done
}
pids=$(panel_pids); [ -n "$pids" ] && kill $pids 2>/dev/null
for i in $(seq 1 50); do [ -z "$(panel_pids)" ] && break; sleep 0.2; done
pids=$(panel_pids); [ -n "$pids" ] && kill -9 $pids 2>/dev/null && sleep 0.5
cd /roc/claude/panel/server && nohup dotnet TeamLigma.Panel.Server.dll > /roc/claude/panel/server.log 2>&1 &
