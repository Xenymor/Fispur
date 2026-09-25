#!/usr/bin/env bash
#
# Richtet einen Ubuntu/Debian-Rechner als OpenBench-Worker für Fispur ein.
#
# Installiert die Voraussetzungen (.NET SDK, make/g++, Python), holt den OpenBench-
# Client, fragt die Zugangsdaten ab und legt ein Startskript an. Jeder Schritt prüft
# zuerst, ob er nötig ist - das Skript lässt sich also gefahrlos erneut ausführen.
# Gegenstück zu setup-worker.ps1 mit denselben Schaltern.
#
#   bash setup-worker.sh                 Vollständiges Setup (fragt nach, braucht sudo)
#   bash setup-worker.sh --check-only    Nur prüfen und berichten
#   bash setup-worker.sh --configure-only --user Xenymor
#                                        Installationen überspringen, Zugangsdaten und
#                                        Startskript neu anlegen
#   bash setup-worker.sh --smoke-test    Fispur einmal testweise bauen und benchen
#
# Schalter: --server URL  --user NAME  --password PW  --install-root DIR
#           --client-source URL  --engine-filter NAME  --force  -h/--help

set -euo pipefail

SERVER='https://test.xenymor.com'
USER_NAME=''
PASSWORD=''
INSTALL_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/fispur-worker"
CLIENT_SOURCE='https://raw.githubusercontent.com/Xenymor/OpenBench/master/Client/client.py'
ENGINE_FILTER='Fispur'
CHECK_ONLY=0
CONFIGURE_ONLY=0
SMOKE_TEST=0
FORCE=0

usage() {
    sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
    case "$1" in
        --server)         SERVER="$2"; shift 2 ;;
        --user)           USER_NAME="$2"; shift 2 ;;
        --password)       PASSWORD="$2"; shift 2 ;;
        --install-root)   INSTALL_ROOT="$2"; shift 2 ;;
        --client-source)  CLIENT_SOURCE="$2"; shift 2 ;;
        --engine-filter)  ENGINE_FILTER="$2"; shift 2 ;;
        --check-only)     CHECK_ONLY=1; shift ;;
        --configure-only) CONFIGURE_ONLY=1; shift ;;
        --smoke-test)     SMOKE_TEST=1; shift ;;
        --force)          FORCE=1; shift ;;
        -h|--help)        usage; exit 0 ;;
        *) echo "Unbekannter Schalter: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# --------------------------------------------------------------------------- helpers

if [ -t 1 ]; then
    C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'; C_OFF=$'\033[0m'
else
    C_CYAN=''; C_GREEN=''; C_YELLOW=''; C_RED=''; C_OFF=''
fi

step() { printf '\n%s=== %s%s\n' "$C_CYAN" "$*" "$C_OFF"; }
ok()   { printf '  %s[ok]%s   %s\n' "$C_GREEN" "$C_OFF" "$*"; }
info() { printf '  [info] %s\n' "$*"; }
warn() { printf '  %s[warn]%s %s\n' "$C_YELLOW" "$C_OFF" "$*"; }
fail() { printf '  %s[fehl]%s %s\n' "$C_RED" "$C_OFF" "$*"; }
die()  { printf '%s%s%s\n' "$C_RED" "$*" "$C_OFF" >&2; exit 1; }

# sudo nur, wenn wir nicht ohnehin root sind
as_root() {
    if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi
}

version_of() {
    # Erste Versionsnummer aus einer Programmausgabe ("g++ (Ubuntu 13.2.0) 13.2.0" -> 13.2.0)
    grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -n 1
}

# Ein per dotnet-install.sh nach ~/.dotnet installiertes SDK liegt nicht im PATH;
# das Startskript nimmt es trotzdem, also zählt es auch hier.
if [ -x "$HOME/.dotnet/dotnet" ] && ! command -v dotnet >/dev/null 2>&1; then
    export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi

dotnet_major() {
    command -v dotnet >/dev/null 2>&1 || { echo 0; return; }
    local major
    major="$(dotnet --list-sdks 2>/dev/null | grep -oE '^[0-9]+' | sort -n | tail -n 1)"
    echo "${major:-0}"
}

python_ok() {
    command -v python3 >/dev/null 2>&1 || return 1
    python3 - <<'PY'
import sys
raise SystemExit(0 if sys.version_info >= (3, 8) else 1)
PY
}

venv_ok() {
    # python3-venv ist auf Debian/Ubuntu ein eigenes Paket
    python3 -c 'import venv, ensurepip' >/dev/null 2>&1
}

# --------------------------------------------------------------------------- install steps

install_packages() {
    step 'Systempakete (make, g++, Python)'

    local missing=()
    command -v make   >/dev/null 2>&1 || missing+=(make)
    command -v g++    >/dev/null 2>&1 || missing+=(g++)
    command -v curl   >/dev/null 2>&1 || missing+=(curl)
    command -v unzip  >/dev/null 2>&1 || missing+=(unzip)
    python_ok                          || missing+=(python3)
    venv_ok                            || missing+=(python3-venv)

    if [ ${#missing[@]} -eq 0 ] && [ "$FORCE" -eq 0 ]; then
        ok "make $(make -v | version_of), g++ $(g++ --version | version_of), python3 $(python3 --version | version_of)"
        return
    fi

    if [ "$CHECK_ONLY" -eq 1 ]; then
        fail "Fehlt: ${missing[*]}"
        return
    fi

    info "Installiere: build-essential ${missing[*]}"
    as_root apt-get update -qq
    as_root apt-get install -y -qq build-essential make g++ curl unzip python3 python3-venv python3-pip
    ok 'Systempakete installiert'
}

install_dotnet() {
    step '.NET SDK'

    local major
    major="$(dotnet_major)"
    if [ "$major" -ge 10 ] && [ "$FORCE" -eq 0 ]; then
        ok "SDK $major.x gefunden ($(command -v dotnet))"
        return
    fi
    if [ "$CHECK_ONLY" -eq 1 ]; then
        fail 'Kein .NET SDK >= 10 gefunden'
        return
    fi

    # Ubuntu ab 24.04 hat das SDK in den eigenen Paketquellen. Sonst wird Microsofts Repo
    # versucht; hat auch das kein dotnet-sdk-10.0 (z. B. ältere Ubuntus/Debians), wird das SDK
    # per dotnet-install.sh nach ~/.dotnet gelegt - diesen Ort kennt auch start-worker.sh.
    info 'Installiere dotnet-sdk-10.0 ...'
    if ! as_root apt-get install -y -qq dotnet-sdk-10.0 2>/dev/null; then
        info 'Nicht in den Distro-Quellen - versuche packages.microsoft.com ...'
        # shellcheck disable=SC1091
        . /etc/os-release
        local deb
        deb="$(mktemp --suffix=.deb)"
        if curl -fsSL "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" -o "$deb" \
            && as_root dpkg -i "$deb" \
            && as_root apt-get update -qq \
            && as_root apt-get install -y -qq dotnet-sdk-10.0; then
            rm -f "$deb"
        else
            rm -f "$deb"
            info 'Kein Paket verfügbar - installiere per dotnet-install.sh nach ~/.dotnet ...'
            local installer
            installer="$(mktemp --suffix=.sh)"
            curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer" \
                || die '.NET-Installer nicht erreichbar. .NET SDK 10 von Hand installieren, dann --configure-only.'
            bash "$installer" --channel 10.0 --install-dir "$HOME/.dotnet" \
                || die '.NET SDK 10 konnte nicht installiert werden.'
            rm -f "$installer"
            export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
        fi
    fi

    major="$(dotnet_major)"
    [ "$major" -ge 10 ] || die 'dotnet ist nach der Installation nicht auffindbar.'
    ok "SDK $major.x installiert"
}

install_venv() {
    step 'Python-Umgebung für den Client'

    local venv="$INSTALL_ROOT/.venv"
    local py="$venv/bin/python"

    if [ -x "$py" ] && [ "$FORCE" -eq 0 ]; then
        ok "venv vorhanden ($venv)"
    else
        if [ "$CHECK_ONLY" -eq 1 ]; then
            fail "venv fehlt ($venv)"
            return
        fi
        info "Lege venv an: $venv"
        python3 -m venv "$venv"
        [ -x "$py" ] || die "venv konnte nicht angelegt werden ($venv)."
    fi

    if [ "$CHECK_ONLY" -eq 1 ]; then
        if "$py" -c 'import requests, psutil, cpuinfo' 2>/dev/null; then
            ok 'requests, psutil, py-cpuinfo vorhanden'
        else
            fail 'Client-Pakete fehlen im venv'
        fi
        return
    fi

    # charset-normalizer explizit: bei sehr neuen Python-Versionen fehlt es sonst gelegentlich,
    # und requests warnt dann bei jedem Start.
    info 'Installiere requests, psutil, py-cpuinfo ...'
    "$py" -m pip install --quiet --upgrade pip
    "$py" -m pip install --quiet requests charset-normalizer psutil py-cpuinfo
    ok 'Client-Pakete installiert'
}

install_client() {
    step 'OpenBench-Client'

    local target="$INSTALL_ROOT/client.py"
    if [ -f "$target" ] && [ "$FORCE" -eq 0 ]; then
        ok "client.py vorhanden ($target)"
        return
    fi
    if [ "$CHECK_ONLY" -eq 1 ]; then
        fail "client.py fehlt ($target)"
        return
    fi

    info "Lade $CLIENT_SOURCE"
    curl -fsSL "$CLIENT_SOURCE" -o "$target"
    # client.py holt worker.py & Co. beim ersten Start selbst aus dem Repo, das der Server
    # vorgibt - mehr als diese eine Datei braucht der Bootstrap nicht.
    grep -q 'OPENBENCH_USERNAME' "$target" || die 'Die geladene Datei sieht nicht nach client.py aus. Stimmt --client-source?'
    ok "client.py geladen ($target)"
}

# --------------------------------------------------------------------------- configuration

read_config() {
    step 'Zugangsdaten und Maschinendaten'

    # Physische Kerne und Sockel; nproc zählt Threads und dient nur als Rückfall.
    local cores sockets
    cores="$(lscpu 2>/dev/null | awk -F: '/^Core\(s\) per socket/ {c=$2} /^Socket\(s\)/ {s=$2} END {if (c && s) print c*s}' | tr -d ' ')"
    sockets="$(lscpu 2>/dev/null | awk -F: '/^Socket\(s\)/ {print $2}' | tr -d ' ')"
    [ -n "$cores" ]   || cores="$(nproc)"
    [ -n "$sockets" ] || sockets=1

    # Vorhandene Werte als Vorgabe übernehmen
    local prev_user='' prev_server='' prev_threads='' prev_sockets=''
    if [ -f "$INSTALL_ROOT/worker.env" ]; then
        prev_user="$(sed -n 's/^OPENBENCH_USERNAME=//p' "$INSTALL_ROOT/worker.env" | head -n 1)"
        prev_server="$(sed -n 's/^OPENBENCH_SERVER=//p' "$INSTALL_ROOT/worker.env" | head -n 1)"
    fi
    if [ -f "$INSTALL_ROOT/worker-config" ]; then
        prev_threads="$(sed -n 's/^THREADS=//p' "$INSTALL_ROOT/worker-config" | head -n 1)"
        prev_sockets="$(sed -n 's/^SOCKETS=//p' "$INSTALL_ROOT/worker-config" | head -n 1)"
    fi

    local default_user="${USER_NAME:-$prev_user}"
    local default_server="${SERVER:-$prev_server}"

    if [ -z "$PASSWORD" ] && [ ! -t 0 ]; then
        die 'Keine Konsole für Rückfragen - --user, --password und --server angeben.'
    fi

    local name url secret threads socks
    read -r -p "OpenBench-Benutzername [$default_user]: " name || true
    name="${name:-$default_user}"
    [ -n "$name" ] || die 'Ohne Benutzernamen geht es nicht.'

    read -r -p "Server-URL [$default_server]: " url || true
    url="${url:-$default_server}"

    if [ -n "$PASSWORD" ]; then
        secret="$PASSWORD"
        info "Passwort aus --password übernommen (${#secret} Zeichen)"
    else
        secret=''
        local attempt
        for attempt in 1 2 3; do
            read -r -s -p 'OpenBench-Passwort: ' secret; echo
            if [ ${#secret} -ge 4 ]; then
                info "Passwort erfasst (${#secret} Zeichen) - stimmt die Länge?"
                break
            fi
            warn "Nur ${#secret} Zeichen erfasst - vermutlich ist das Einfügen fehlgeschlagen."
            secret=''
        done
        [ -n "$secret" ] || die 'Passwort dreimal zu kurz erfasst. Alternativ mit --password übergeben.'
    fi

    info "Erkannt: $cores physische Kerne auf $sockets Sockel"
    read -r -p "Threads [${prev_threads:-$cores}]: " threads || true
    threads="${threads:-${prev_threads:-$cores}}"
    read -r -p "Sockel [${prev_sockets:-$sockets}]: " socks || true
    socks="${socks:-${prev_sockets:-$sockets}}"

    mkdir -p "$INSTALL_ROOT"

    # Die Zugangsdaten liegen nur für diesen Benutzer lesbar. Der Client liest die drei
    # OPENBENCH_*-Variablen selbst, das Passwort taucht so nie in der Prozessliste auf.
    (
        umask 077
        {
            printf 'OPENBENCH_USERNAME=%q\n' "$name"
            printf 'OPENBENCH_PASSWORD=%q\n' "$secret"
            printf 'OPENBENCH_SERVER=%q\n'   "$url"
        } > "$INSTALL_ROOT/worker.env"
        {
            printf 'THREADS=%q\n'     "$threads"
            printf 'SOCKETS=%q\n'     "$socks"
            printf 'ENGINE_ONLY=%q\n' "$ENGINE_FILTER"
        } > "$INSTALL_ROOT/worker-config"
    )
    chmod 600 "$INSTALL_ROOT/worker.env"
    ok "Zugangsdaten abgelegt ($INSTALL_ROOT/worker.env, nur für dich lesbar)"
    ok "Konfiguration gespeichert ($INSTALL_ROOT/worker-config)"
}

write_start_script() {
    step 'Startskript'

    local start="$INSTALL_ROOT/start-worker.sh"
    if [ "$CHECK_ONLY" -eq 1 ]; then
        if [ -x "$start" ]; then ok 'start-worker.sh vorhanden'; else fail 'start-worker.sh fehlt'; fi
        return
    fi

    cat > "$start" <<'EOS'
#!/usr/bin/env bash
# Startet den OpenBench-Worker. Erzeugt von setup-worker.sh - Änderungen gehen bei
# einem erneuten Setup-Lauf verloren. Läuft im Vordergrund; für den Dauerbetrieb in
# tmux oder screen starten, dann überlebt der Worker das Ende der SSH-Sitzung.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

for f in worker.env worker-config; do
    [ -f "$root/$f" ] || { echo "$root/$f fehlt - Setup mit --configure-only wiederholen." >&2; exit 1; }
done

set -a
# shellcheck disable=SC1091
. "$root/worker.env"
set +a
# shellcheck disable=SC1091
. "$root/worker-config"

# Ein per dotnet-install.sh nach ~/.dotnet installiertes SDK ist sonst nicht im PATH.
if [ -x "$HOME/.dotnet/dotnet" ] && ! command -v dotnet >/dev/null 2>&1; then
    export PATH="$HOME/.dotnet:$PATH"
    export DOTNET_ROOT="$HOME/.dotnet"
fi
command -v dotnet >/dev/null 2>&1 || { echo 'dotnet nicht im PATH - .NET SDK fehlt.' >&2; exit 1; }

args=(client.py -T "$THREADS" -N "$SOCKETS")
[ -n "${ENGINE_ONLY:-}" ] && args+=(--only "$ENGINE_ONLY")

echo "Worker startet: $OPENBENCH_USERNAME @ $OPENBENCH_SERVER mit $THREADS Threads"
cd "$root"
exec "$root/.venv/bin/python" "${args[@]}"
EOS
    chmod +x "$start"
    ok "start-worker.sh geschrieben ($start)"
}

# --------------------------------------------------------------------------- verification

verify() {
    step 'Prüfung'

    local all_ok=1
    row() {  # name status
        printf '  %-14s %s\n' "$1" "$2"
        case "$2" in FEHLT*) all_ok=0 ;; esac
    }

    local major; major="$(dotnet_major)"
    if [ "$major" -ge 10 ]; then row 'dotnet SDK' "OK ($major.x)"; else row 'dotnet SDK' 'FEHLT'; fi

    if command -v make >/dev/null 2>&1; then row 'make' "OK ($(make -v | version_of))"; else row 'make' 'FEHLT'; fi
    if command -v g++  >/dev/null 2>&1; then row 'g++'  "OK ($(g++ --version | version_of))"; else row 'g++' 'FEHLT'; fi

    local py="$INSTALL_ROOT/.venv/bin/python"
    if [ -x "$py" ] && "$py" -c 'import requests, psutil, cpuinfo' 2>/dev/null; then
        row 'Python-Pakete' 'OK'
    else
        row 'Python-Pakete' 'FEHLT'
    fi

    if [ -f "$INSTALL_ROOT/client.py" ]; then row 'client.py' 'OK'; else row 'client.py' 'FEHLT'; fi

    echo
    [ "$all_ok" -eq 1 ]
}

smoke_test() {
    step 'Smoke-Test: Fispur einmal bauen'

    local work; work="$(mktemp -d)"
    trap 'rm -rf "$work"' RETURN

    info 'Lade Fispur (main) ...'
    curl -fsSL 'https://github.com/Xenymor/Fispur/archive/main.zip' -o "$work/fispur.zip"
    unzip -q "$work/fispur.zip" -d "$work"

    local src
    src="$(find "$work" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
    [ -n "$src" ] || die 'Das Fispur-Archiv enthielt kein Verzeichnis.'

    info 'make EXE=fispur-smoke ...'
    ( cd "$src" && make -j EXE=fispur-smoke ) || die 'Der Build ist fehlgeschlagen - siehe Ausgabe oben.'
    [ -f "$src/fispur-smoke" ] || die 'make lief durch, aber fispur-smoke fehlt.'

    info 'bench ...'
    local last
    last="$("$src/fispur-smoke" bench | tail -n 1)"
    echo "  $last"
    [[ "$last" =~ [0-9]+\ +nodes\ +[0-9]+\ +nps ]] || die 'Der Bench hat nicht im erwarteten Format geantwortet.'
    ok 'Build und Bench in Ordnung'
}

# --------------------------------------------------------------------------- main

echo
echo 'Fispur / OpenBench - Worker-Setup (Linux)'
echo "Zielverzeichnis: $INSTALL_ROOT"

case "$(uname -m)" in
    x86_64)
        grep -qw avx2 /proc/cpuinfo \
            || die 'Diese CPU meldet kein AVX2. Fispurs NNUE hat dafür keinen Fallback - der Rechner bekäme keine Workloads.'
        ;;
    aarch64) ;;
    *) die "Nicht unterstützte Architektur: $(uname -m)" ;;
esac

NEEDS_INSTALLS=1
if [ "$CHECK_ONLY" -eq 1 ] || [ "$CONFIGURE_ONLY" -eq 1 ]; then NEEDS_INSTALLS=0; fi

if [ "$NEEDS_INSTALLS" -eq 1 ]; then
    command -v apt-get >/dev/null 2>&1 || die 'Kein apt-get gefunden - das Skript unterstützt nur Ubuntu/Debian.'
    if [ "$(id -u)" -ne 0 ] && ! command -v sudo >/dev/null 2>&1; then
        die 'sudo fehlt und du bist nicht root - Pakete können nicht installiert werden.'
    fi
fi

[ "$CHECK_ONLY" -eq 1 ] || mkdir -p "$INSTALL_ROOT"

if [ "$CONFIGURE_ONLY" -eq 1 ]; then
    info 'configure-only: Installationsschritte werden übersprungen.'
else
    install_packages
    install_dotnet
    install_venv
    install_client
fi

if verify; then
    VERIFY_OK=1
else
    VERIFY_OK=0
fi

if [ "$CHECK_ONLY" -eq 1 ]; then
    if [ "$VERIFY_OK" -eq 1 ]; then ok 'Alle Voraussetzungen erfüllt.'; else warn 'Es fehlt etwas - Skript ohne --check-only ausführen.'; fi
    exit 0
fi

if [ "$VERIFY_OK" -eq 0 ]; then
    if [ "$CONFIGURE_ONLY" -eq 1 ]; then
        # Zugangsdaten dürfen auch dann erneuert werden, wenn noch Werkzeuge fehlen.
        warn 'Es fehlen Voraussetzungen (siehe Tabelle) - der Worker startet erst, wenn sie da sind.'
    else
        die 'Es fehlen Voraussetzungen (siehe Tabelle). Setup abgebrochen.'
    fi
fi

[ "$SMOKE_TEST" -eq 1 ] && smoke_test

read_config
write_start_script

echo
printf '%sFertig.%s\n' "$C_GREEN" "$C_OFF"
echo "  Worker starten:   $INSTALL_ROOT/start-worker.sh"
echo "  Dauerbetrieb:     tmux new -s worker \"$INSTALL_ROOT/start-worker.sh\""
echo
echo "Im Startlog muss '$ENGINE_FILTER | dotnet (10.0.x)' erscheinen; danach taucht der"
echo "Rechner unter $SERVER/machines/ auf."
