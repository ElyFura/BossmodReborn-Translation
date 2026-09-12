# BossMod Reborn Translation

Deutsche Übersetzung für [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn) als
**eigenständiges Dalamud-Plugin**. BossMod Reborn selbst wird nicht verändert — weder im Quellcode noch
als Datei. Die Übersetzung tauscht die Strings zur Laufzeit im Speicher und stellt beim Entladen wieder
das englische Original her.

## Status: Milestone 1

Übersetzt wird alles, was BossMod datengetrieben in seine Metadaten legt — ohne IL-Patching, ohne Hooks,
ohne Harmony:

| Bereich | Umfang | Zustand |
| --- | --- | --- |
| Konfigurationsbaum (Abschnittsnamen) | 69 `ConfigDisplay` | ✅ |
| Konfigurationsfelder (Label + Tooltip) | 460 `PropertyDisplay` | ✅ |
| Combobox-Werte, Abschnitte, Gruppen, Presets | `PropertyCombo`, `SectionStart`, `GroupDetails`, `GroupPreset` | ✅ |
| Enum-Anzeigenamen (Combobox-Einträge) | `GeneratedEnumMetadata` | ✅ |
| Autorotation (Modul-, Track- und Options-Namen) | `RotationModuleDefinition`, `StrategyConfig`, `StrategyOption` | ✅ |
| Tab-Leiste des Einstellungsfensters | 5 Tabs | ✅ |
| Kampfhinweise (`hints.Add(...)`) | 543 feste + 238 interpolierte | ⏳ Milestone 2 |
| Übrige ImGui-Literale in der UI | 87 Dateien | ⏳ Milestone 3 |

Die mitgelieferte `de.json` ist ein Startbestand: 109 von 913 übersetzbaren Konfigurations-Strings (11,9 %)
— Einstellungsbaum, Radar- und Boss-Modul-Einstellungen, automatische Bewegung, Tabs. Alles ohne Eintrag
bleibt englisch; es fehlt nie Text, er ist nur noch nicht übersetzt.

## Installation

```
dotnet build -c Release -p:Platform=x64
```

Das Ergebnis in `BmrTranslation/bin/Release/` als Dev-Plugin in Dalamud einbinden
(`/xlsettings` → Experimental → Dev-Plugin-Pfad).

## Befehle

| Befehl | Wirkung |
| --- | --- |
| `/bmrtl` | Status: BossMod-Version, Anzahl Einträge, angewendet / fehlend / veraltet |
| `/bmrtl extract` | Schreibt den vollständigen Übersetzungskatalog der **installierten** BossMod-Version |
| `/bmrtl reload` | Übersetzungsdateien neu laden und erneut anwenden |
| `/bmrtl off` / `on` | Übersetzung abschalten (englische Originale zurück) bzw. wieder einschalten |

## Übersetzen

`/bmrtl extract` legt im Plugin-Konfigurationsordner ab:

* `extract/strings.de.json` — jeder übersetzbare Schlüssel mit englischem Original und aktueller Übersetzung
* `extract/missing.de.json` — nur die noch nicht übersetzten Schlüssel, vorbefüllt mit dem englischen Text

Die zweite Datei ausfüllen und als `de.json` **direkt in den Plugin-Konfigurationsordner** legen: eine
Datei dort gewinnt gegen die eingebaute Ressource, sodass man ohne Neubau übersetzen und mit
`/bmrtl reload` sofort nachsehen kann. Was sich bewährt, wandert nach `BmrTranslation/Resources/de.json`.

Schlüssel leiten sich aus dem **Ort** ab, nicht aus dem Text:

```
cfg.node/BossMod.BossModuleConfig                     Abschnitt im Einstellungsbaum
cfg/BossMod.BossModuleConfig/EnableRadar/label        Feldbezeichnung
cfg/BossMod.BossModuleConfig/EnableRadar/tooltip      Tooltip dazu
cfg/BossMod.AI.AIConfig/DesiredPositional/combo.2     dritter Combobox-Eintrag
enum/BossMod.Positional/Rear/display                  Enum-Anzeigename
rot/<Modultyp>/track/<InternalName>/name              Autorotation-Track
ui.tab/Settings                                       Tab im Einstellungsfenster
```

Jeder Eintrag darf den englischen Text mitschreiben, gegen den übersetzt wurde:

```json
"cfg/BossMod.BossModuleConfig/EnableRadar/label": { "de": "Radar aktivieren", "en": "Enable radar" }
```

Ändert BossMod das Original, meldet `/bmrtl` den Eintrag als *stale* — die Übersetzung greift weiter,
muss aber nachgesehen werden. Kurzform `"key": "Text"` ist erlaubt, verzichtet dann aber auf diese Prüfung.

## Verifikation nach einem BossMod-Update

Das Hauptrisiko dieses Plugins ist, dass BossMod etwas umbaut und die Reflection still nicht mehr trifft.
`tools/ShapeCheck` prüft das **offline** — ohne Spiel, ohne Dalamud-Prozess, nur gegen die installierte
Assembly:

```
dotnet run --project tools/ShapeCheck            # findet DLL, Dalamud und de.json selbst
dotnet run --project tools/ShapeCheck -- --strict # Drift zählt als Fehler
```

Geprüft wird dreierlei:

1. **Strukturen** — jeder Typ, jedes Feld und jedes Backing-Field, das die Patcher anfassen, gruppiert nach
   der Datei, die davon abhängt. Ein Fehlschlag zeigt direkt auf den zu korrigierenden Patcher.
2. **Verwaiste Schlüssel** — ein Eintrag in `de.json`, den BossMod nicht mehr kennt (umbenanntes Feld oder
   Typ). Das ist ein Fehler, Exit-Code 1.
3. **Drift** — der Schlüssel existiert noch, aber BossMod hat die englische Formulierung geändert; die
   Übersetzung greift weiter, muss aber nachgesehen werden. Das ist eine Warnung, mit `--strict` ein Fehler.

Optionen: `--bmr <pfad>`, `--dalamud <verzeichnis>`, `--seed <pfad>`, `--strict`, `--help`.

## Grenzen

* **Keine offizielle Dalamud-API** für Zugriff auf ein fremdes Plugin. Die Anbindung läuft über Reflection
  auf die geladene `BossModReborn`-Assembly und kann bei größeren Umbauten dort brechen. Passt die Struktur
  nicht, wird nicht übersetzt und es wird geloggt — BossMod läuft in jedem Fall normal weiter.
* **Versionsgebunden:** geprüft gegen BossMod Reborn **7.5.6.5**.
* Encounter- und Duty-Namen bleiben englisch: BossMod liest seine Lumina-Sheets fest mit
  `Language.English` (`Framework/Service.cs`), unabhängig von der Client-Sprache.
* Nicht übersetzt (bewusst): `InternalName`-Felder, State-Machine-Zustandsnamen und Chat-Befehle — das ist
  Identität, kein Anzeigetext.

Siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) für die Funktionsweise und die Planung der weiteren
Milestones.
