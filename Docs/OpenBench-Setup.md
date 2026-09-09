# OpenBench für Fispur — Setup-Runbook

Anleitung für eine eigene [OpenBench](https://github.com/AndyGrant/OpenBench)-Instanz, die Fispur
verteilt testet: Django-Server auf einem Linux-VPS (Domain + HTTPS), Worker auf Windows-Rechnern.

Aufteilung: der **Server** verwaltet Tests, Ergebnisse, Netze und PGNs. Die **Worker** holen sich
Workloads, bauen die zu testende Branch selbst aus dem GitHub-Repo, prüfen den Bench-Wert und spielen
die Partien mit fastchess. Der Server baut nie eine Engine.

---

## 0. Was im Fispur-Repo dafür schon vorbereitet ist

| Datei | Zweck |
|---|---|
| [`Makefile`](../Makefile) | Der Build-Einstieg, den OpenBench aufruft: `make -j EXE=<name> CC=dotnet`. Publiziert `Fispur-cli-(exp)` self-contained als **eine** Datei mit dem Namen `<name>.exe`. |
| [`Fispur-cli-(exp)/Bench.cs`](../Fispur-cli-(exp)/Bench.cs) | `bench`-Kommando, gibt `<nodes> nodes <nps> nps` aus — das Format, das OpenBench parst. |
| [`Fispur-cli-(exp)/Program.cs`](../Fispur-cli-(exp)/Program.cs) | UCI-Schnittstelle inkl. `Hash`- und (Dummy-)`Threads`-Option, wie fastchess sie setzt. |

Wichtige Randbedingungen, die daraus folgen:

- **Self-contained Single-File ist Pflicht.** Der Worker kopiert nach dem Build genau eine Datei aus dem
  Build-Verzeichnis weg (`shutil.move` in `Client/utils.py`). Eine framework-abhängige Ausgabe würde ohne
  ihre DLLs nicht starten.
- **AVX2 ist Pflicht.** `Fispur/src/Fispur-New/NNUE.cs` nutzt AVX2 ohne Fallback. Maschinen ohne AVX2
  dürfen keine Fispur-Workloads bekommen → `cpuflags` in der Engine-Config.
- **Der Bench ist maschinenunabhängig reproduzierbar**, weil die NNUE-Inferenz reine Integer-Arithmetik
  ist. Weichen zwei Rechner ab, stimmt etwas anderes nicht (anderer Commit, anderes Netz).

Denselben Build lokal nachstellen (braucht `make`, z. B. aus MSYS2):

```
make EXE=fispur-ob
./fispur-ob.exe bench
```

Im Repo-Root darf danach **genau eine** neue Datei liegen: `fispur-ob.exe`. Der `bench` endet mit einer
Zeile der Form `1599904 nodes 1737137 nps` und beendet sich selbst.

---

## 1. OpenBench forken (nicht klonen)

Fork von `AndyGrant/OpenBench` anlegen und ausschließlich damit arbeiten. Gründe:

- Du sammelst lokale Änderungen (`Config/config.json`, `OpenSite/settings.py`).
- Die Worker laden ihren Client-Code aus `client_repo_url` / `client_repo_ref`. Zeigt das auf Andys
  `master`, ziehen deine Worker eine neuere `client_version` als dein Server erwartet — und werden von
  deinem eigenen Server abgelehnt.

---

## 2. Grundinstallation (erst lokal bzw. direkt auf dem VPS testen)

Python ≥ 3.9 auf dem Server (3.10 empfohlen), Python ≥ 3.8 auf den Workern.

```bash
sudo apt update && sudo apt install -y git python3-venv python3-dev build-essential

git clone https://github.com/<dein-fork>/OpenBench && cd OpenBench
python3 -m venv .venv && source .venv/bin/activate
pip3 install -r requirements.txt

python3 manage.py migrate
python3 manage.py createsuperuser
python3 manage.py runserver
```

Erreichbar unter `http://localhost:8000/`.

> `makemigrations` läufst du als Instanzbetreiber **nie** — die Migrationen liegen im Repo. Ein eigener
> Migrationsstand macht spätere Upstream-Merges kaputt.

---

## 3. Arbeitsaccount freischalten

Der Superuser aus `createsuperuser` ist nur fürs Admin-Panel. Der Account zum Testen entsteht über
`/register/`.

1. `http://localhost:8000/register/` → Account anlegen (z. B. `Xenymor`).
2. `http://localhost:8000/admin/` → bei diesem User `Staff status` und `Superuser status` setzen.
3. Profil freischalten:

```bash
python3 manage.py shell
>>> from OpenBench.models import Profile
>>> p = Profile.objects.get(user__username='Xenymor')
>>> p.enabled = p.approver = True
>>> p.save()
```

`enabled` erlaubt überhaupt sinnvolle Interaktion, `approver` gibt Testfreigabe und Netzverwaltung.

---

## 4. Instanz-Konfiguration (`Config/config.json`)

Diese Datei enthält **nur** Instanz-Einstellungen. Engines und Bücher stehen dort nicht mehr (siehe
Abschnitt 5) — ältere Anleitungen im Netz sind an der Stelle veraltet.

```json
{
    "client_version"     : 50,
    "client_repo_url"    : "https://github.com/<dein-fork>/OpenBench",
    "client_repo_ref"    : "master",

    "fastchess_min_version" : "1.8.1",
    "fastchess_repo_url"    : "https://github.com/AndyGrant/fastchess",
    "fastchess_repo_ref"    : "master",

    "use_cross_approval"          : false,
    "require_login_to_view"       : false,
    "require_manual_registration" : true,
    "balance_engine_throughputs"  : false,

    "use_x_accel_redirect"  : false,
    "x_accel_redirect_root" : "/x-accel-media/"
}
```

- `client_repo_url` / `client_repo_ref` → **dein Fork**.
- `use_cross_approval: false` — sonst braucht jeder Test die Freigabe eines *anderen* Users.
- `require_manual_registration: true` — auf einem öffentlich erreichbaren VPS sinnvoll.
- `client_version` nicht anfassen; sie muss zu dem Client-Code passen, den dein Fork ausliefert.

Nach jeder Änderung Server neu starten — OpenBench validiert die Datei beim Start und bricht sonst
mit Fehlermeldung ab.

---

## 5. Engine und Buch eintragen

Beides liegt in der Datenbank und wird unter `/manage/engines/` bzw. `/manage/books/` in der Weboberfläche
gepflegt. Für den Erstaufsatz ist der Import aus JSON-Dateien bequemer:

```bash
python3 manage.py import_engines Engines/Fispur.json
python3 manage.py import_books  Books/UHO_4060_v2.epd.json
```

Der Name der Engine bzw. des Buchs ergibt sich aus dem **Dateinamen** (`Fispur.json` → Engine `Fispur`).

### 5.1 `Engines/Fispur.json`

```json
{
    "private" : false,
    "nps"     : 1700000,
    "source"  : "https://github.com/Xenymor/Fispur",

    "build" : {
        "path"      : "",
        "compilers" : ["dotnet>=8.0"],
        "cpuflags"  : ["AVX2", "POPCNT"],
        "systems"   : ["Windows"]
    },

    "test_presets" : {
        "default" : {
            "both_options"      : "Threads=1 Hash=8",
            "both_time_control" : "10.0+0.10",
            "book_name"         : "UHO_4060_v2.epd",
            "test_bounds"       : "[0.00, 3.00]",
            "test_confidence"   : "[0.05, 0.05]",
            "base_branch"       : "main",
            "win_adj"           : "movecount=3 score=400",
            "draw_adj"          : "movenumber=40 movecount=8 score=10",
            "upload_pgns"       : "FALSE"
        }
    },

    "tune_presets"    : { "default" : {} },
    "datagen_presets" : { "default" : {} }
}
```

- `path: ""` → das `Makefile` im Repo-Root.
- `compilers: ["dotnet>=8.0"]` → der Worker ruft `dotnet --version` auf und vergleicht die Zahl.
- `cpuflags` filtert Maschinen: wer AVX2 nicht meldet, bekommt keine Fispur-Workloads.
- `systems: ["Windows"]` → später `["Windows", "Linux"]`, wenn Linux-Worker dazukommen
  (dann im Makefile `RID=linux-x64` setzen bzw. per OS-Weiche).
- **`nps`** ist die Referenzgeschwindigkeit, mit der Zeitkontrollen zwischen unterschiedlich schnellen
  Maschinen skaliert werden. Der Wert oben stammt von einem Lauf auf dem Entwicklungsrechner
  (Fispur 0.13.2: `1599904 nodes 1737137 nps`, Tiefe 9, 24 Stellungen). Auf deiner Referenzmaschine
  einmal `fispur-ob.exe bench` laufen lassen und den dort gemeldeten NPS-Wert eintragen.

### 5.2 `Books/UHO_4060_v2.epd.json`

Der Worker lädt ein **ZIP mit genau einer Datei** und prüft den SHA256 des *entpackten* Inhalts.

```json
{
    "sha"    : "36f2ec751ab78def6be1307430cbe2cd2ba65ade8d2aaae8f10e3df7d0ea83e1",
    "source" : "https://github.com/Xenymor/Fispur/releases/download/books/UHO_4060_v2.epd.zip"
}
```

Der SHA oben gehört zu `Openings/UHO_4060_v2.epd` **mit LF-Zeilenenden**. Das ist kein Detail: der Client
liest die Datei im Text-Modus, CRLF wird beim Lesen zu LF normalisiert — der SHA muss also immer über die
LF-Variante gebildet werden. Erzeugen lässt sich das Paket so:

```bash
python3 - <<'PY'
import hashlib, zipfile
data = open('Openings/UHO_4060_v2.epd','rb').read().replace(b'\r\n', b'\n')
open('UHO_4060_v2.epd','wb').write(data)
print(hashlib.sha256(data).hexdigest())
with zipfile.ZipFile('UHO_4060_v2.epd.zip','w',zipfile.ZIP_DEFLATED) as z:
    z.write('UHO_4060_v2.epd', 'UHO_4060_v2.epd')
PY
```

Das ZIP als **Release-Asset** von `Xenymor/Fispur` hochladen (nicht ins Repo committen) und die URL
in `source` eintragen.

---

## 6. Produktionsbetrieb auf dem VPS

Django-Runserver und SQLite reichen nicht — SQLite kann keine parallelen Schreibzugriffe.

### 6.1 MySQL

```bash
sudo apt install -y mysql-server default-libmysqlclient-dev
sudo mysql_secure_installation
sudo mysql -e "CREATE DATABASE openbench CHARACTER SET utf8mb4;"
sudo mysql -e "CREATE USER 'openbench'@'localhost' IDENTIFIED BY '<passwort>';"
sudo mysql -e "GRANT ALL ON openbench.* TO 'openbench'@'localhost'; FLUSH PRIVILEGES;"

pip3 install mysqlclient
```

In `OpenSite/settings.py`:

```python
DATABASES = {
    'default': {
        'ENGINE'  : 'django.db.backends.mysql',
        'NAME'    : 'openbench',
        'USER'    : 'openbench',
        'PASSWORD': '<passwort>',
        'HOST'    : '127.0.0.1',
        'PORT'    : '3306',
    }
}
```

```bash
python3 manage.py migrate
python3 manage.py migrate --run-syncdb
```

### 6.2 `OpenSite/settings.py` härten

Diese Änderungen **nicht in den Fork pushen** (Zugangsdaten, Secret Key):

```python
SECRET_KEY = '<neu generiert, z.B. python3 -c "import secrets;print(secrets.token_urlsafe(64))">'
DEBUG = False
ALLOWED_HOSTS = ['deine-domain.de']
CSRF_TRUSTED_ORIGINS = ['https://deine-domain.de']
SECURE_PROXY_SSL_HEADER = ('HTTP_X_FORWARDED_PROTO', 'https')

STATIC_ROOT = os.path.join(BASE_DIR, 'staticfiles')
```

Mit `DEBUG = False` liefert Django keine statischen Dateien mehr aus — deshalb `STATIC_ROOT` setzen,
`python3 manage.py collectstatic` laufen lassen und nginx darauf zeigen lassen.

### 6.3 nginx

`/etc/nginx/sites-available/openbench`:

```nginx
server {
    listen 80;
    server_name deine-domain.de;

    # Netz-Uploads sind groß
    client_max_body_size 250M;

    location /static/ {
        alias /home/ubuntu/OpenBench/staticfiles/;
    }

    # Nur nötig, wenn use_x_accel_redirect: true gesetzt ist
    location /x-accel-media/ {
        internal;
        alias /home/ubuntu/OpenBench/Media/;
    }

    location / {
        proxy_pass http://127.0.0.1:8000;
        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/openbench /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d deine-domain.de
sudo ufw allow 80 && sudo ufw allow 443     # Port 8000 bleibt zu
```

Wenn du `use_x_accel_redirect: true` setzt, braucht `www-data` Leserechte auf `Media/`:

```bash
sudo apt install acl
sudo setfacl -m u:www-data:--x /home/ubuntu
sudo setfacl -R -d -m u:www-data:rX /home/ubuntu/OpenBench/Media
```

### 6.4 Gunicorn als systemd-Unit

OpenBench fährt bei SIGTERM/SIGINT sauber herunter und räumt Lockfiles auf; ein SIGKILL kann das
PGN-Archiv beschädigen. Deshalb explizit SIGTERM und ein großzügiges Stop-Timeout:

`/etc/systemd/system/openbench.service`:

```ini
[Unit]
Description=OpenBench (gunicorn)
After=network.target mysql.service

[Service]
User=ubuntu
WorkingDirectory=/home/ubuntu/OpenBench
ExecStart=/home/ubuntu/OpenBench/.venv/bin/gunicorn OpenSite.wsgi:application \
          --bind 127.0.0.1:8000 --workers 3
KillSignal=SIGTERM
TimeoutStopSec=300
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```bash
pip3 install gunicorn
sudo systemctl daemon-reload
sudo systemctl enable --now openbench
```

Manuell beenden: `pkill -TERM gunicorn` und das Ende der Prozesse abwarten — **nie** `-KILL`.

---

## 7. Windows-Worker einrichten

Pro Rechner einmalig:

1. **.NET SDK 8 (oder neuer)** installieren. Prüfen: `dotnet --version` muss in der Shell antworten,
   in der später der Worker läuft — genau so ermittelt OpenBench die Compiler-Version.
2. **make + g++** in den PATH. Der Worker verlangt beides, weil er sich fastchess selbst baut.
   Bequemster Weg ist MSYS2:
   ```
   pacman -S make mingw-w64-x86_64-gcc
   ```
   Danach `C:\msys64\usr\bin` und `C:\msys64\mingw64\bin` in die PATH-Variable aufnehmen.
3. **Python 3.8+** und die Client-Abhängigkeiten:
   ```
   pip install requests psutil py-cpuinfo
   ```

Worker starten (er lädt `worker.py` selbstständig aus deinem Fork nach):

```
python Client/client.py -U <user> -P <passwort> -S https://deine-domain.de -T 8 -N 1
```

- `-T` = Threads insgesamt (i. d. R. physische Kerne), `-N` = Anzahl CPU-Sockets.
- `--only Fispur` beschränkt den Worker auf deine Engine, `--fleet` für unbeaufsichtigte Rechner.

Im Startlog muss stehen:

```
Fispur           | dotnet   (8.0.x)
```

Steht dort `Missing`, findet der Worker `dotnet` nicht (PATH) oder der `compilers`-Eintrag passt nicht.

---

## 8. Betrieb

- **Backups:** `python3 manage.py dumpdata > backup.json` (per Cron), zusätzlich `mysqldump`.
- **Upstream-Updates:** Fork mergen → `Config/config.json` von Hand nachziehen → Server stoppen →
  `manage.py migrate` → Server starten. Ist `client_version` gestiegen, aktualisieren sich die Worker
  beim nächsten Lauf selbst aus deinem Fork.
- **Tests anlegen:** Branch im Fispur-Repo pushen, im Web-UI Test erstellen, Dev-/Base-Bench eintragen.
  Den Bench-Wert bekommst du lokal mit `fispur-ob.exe bench`; Konvention ist, ihn zusätzlich in die
  Commit-Message zu schreiben (`Bench: 1234567`).

---

## 9. Troubleshooting

| Symptom | Ursache |
|---|---|
| `Wrong Bench` | Dev-/Base-Bench im Test falsch eingetragen, oder getestete Branch ≠ Branch, auf der du gebencht hast. |
| Engine im Worker-Log als `Missing` | `dotnet` nicht im PATH der Worker-Shell, oder `compilers` in der Engine-Config passt nicht. |
| Build schlägt fehl, Log zeigt NuGet-Fehler | Worker ohne Internetzugang (erster Build lädt Pakete) oder kein .NET SDK. |
| Maschine bekommt keine Workloads | `cpuflags` (AVX2) fehlt auf der Maschine, oder `systems` passt nicht zum Worker-OS. |
| Buch wird immer neu geladen / `Invalid sha` | SHA nicht über die LF-normalisierte EPD gebildet, oder ZIP enthält mehr als eine Datei. |
| 400 / CSRF-Fehler nach HTTPS-Umzug | `ALLOWED_HOSTS` / `CSRF_TRUSTED_ORIGINS` in `settings.py` nicht gesetzt. |
| Seite ohne CSS nach `DEBUG = False` | `collectstatic` vergessen oder `location /static/` fehlt in nginx. |
