#!/usr/bin/env bash
# Updates the FSD server, the voice server and the site from GitHub and restarts them. Run as root:
#   bash ~/Skynetwork-site/deploy/update.sh
set -euo pipefail

FSD_SRC=~/Skynetwork-fsd
VOICE_SRC=~/Skynetwork-voice
SITE_SRC=~/Skynetwork-site

# Brings a clone to the latest main, whatever branch it was left on (for example a branch checked
# out to try a change before it was merged).
update_repo() {
  git -C "$1" fetch -q origin main
  git -C "$1" checkout -q -B main origin/main
  echo "   $(basename "$1"): $(git -C "$1" log --oneline -1)"
}

# The deploy files of this repository are used below: take the latest first.
update_repo "$SITE_SRC"

echo "== FSD-сервер"
update_repo "$FSD_SRC"
cmake -S "$FSD_SRC" -B "$FSD_SRC/build" >/dev/null
cmake --build "$FSD_SRC/build" -j
install -D -t /opt/skynetwork/fsd "$FSD_SRC/build/skynet-fsd" "$FSD_SRC/build/skynet-admin"

echo "== Голосовой сервер"
if [ ! -d "$VOICE_SRC" ]; then
  git clone https://github.com/chatgpt82628163-pixel/Skynetwork-voice.git "$VOICE_SRC"
fi
update_repo "$VOICE_SRC"
cmake -S "$VOICE_SRC" -B "$VOICE_SRC/build" >/dev/null
cmake --build "$VOICE_SRC/build" -j
install -D -t /opt/skynetwork/voice "$VOICE_SRC/build/skynet-voice"
# First run: install the service and open the UDP port.
if [ ! -f /etc/systemd/system/skynet-voice.service ]; then
  cp "$SITE_SRC/deploy/skynet-voice.service" /etc/systemd/system/
  systemctl daemon-reload
  systemctl enable skynet-voice
fi
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then ufw allow 3782/udp >/dev/null; fi

echo "== Сайт"
rm -rf ~/site-build
dotnet publish "$SITE_SRC/src/SkyNetwork.Site" -c Release -o ~/site-build --nologo -v q
mkdir -p /opt/skynetwork/site
# Stopped first: overwriting the DLLs of a running site crashes it.
systemctl stop skynetwork-site
cp -r ~/site-build/. /opt/skynetwork/site/

# Older installs set ASPNETCORE_URLS, which appsettings.json overrides (the site then listened on 0.0.0.0).
sed -i 's#^Environment=ASPNETCORE_URLS=#Environment=Urls=#' /etc/systemd/system/skynetwork-site.service

echo "== Перезапуск"
systemctl daemon-reload
systemctl restart skynet-fsd skynet-voice skynetwork-site
sleep 5

echo "== Проверка"
systemctl is-active skynet-fsd skynet-voice skynetwork-site
curl -s -o /dev/null -w "сайт: HTTP %{http_code}\n" http://127.0.0.1:8000/
curl -s -o /dev/null -w "карта: HTTP %{http_code}\n" http://127.0.0.1:8000/tiles/light/3/4/2.png
ss -ltn | grep -E ':(6809|8000) ' || true
ss -lun | grep -E ':3782 ' || true
echo "Готово."
