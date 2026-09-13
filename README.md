# BossMod Reborn Translation

Deutsche Übersetzung für [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn) als
**eigenständiges Dalamud-Plugin**. BossMod Reborn selbst wird nicht verändert — weder im Quellcode noch
als Datei. Die Übersetzung tauscht die Strings zur Laufzeit im Speicher und stellt beim Entladen wieder
das englische Original her.

## Status: Milestone 1 - 3

Die Konfigurationsebene kommt ohne IL-Patching aus — BossMod legt sie datengetrieben in Metadaten ab.
Kampfhinweise und Fenstertexte nicht: das sind String-Literale im Code und brauchen Harmony.

| Bereich | Umfang | Zustand |
| --- | --- | --- |
| Konfigurationsbaum (Abschnittsnamen) | 69 `ConfigDisplay` | ✅ |
| Konfigurationsfelder (Label + Tooltip) | 460 `PropertyDisplay` | ✅ |
| Combobox-Werte, Abschnitte, Gruppen, Presets | `PropertyCombo`, `SectionStart`, `GroupDetails`, `GroupPreset` | ✅ |
| Enum-Anzeigenamen (Combobox-Einträge) | `GeneratedEnumMetadata` | ✅ |
| Autorotation (Modul-, Track- und Options-Namen) | `RotationModuleDefinition`, `StrategyConfig`, `StrategyOption` | ✅ |
| Tab-Leiste des Einstellungsfensters | 5 Tabs | ✅ |
| Statusfenster (Abdeckung, Drift, Export) | `/bmrtl` bzw. Zahnrad in der Plugin-Liste | ✅ |
| Konfigurations-Übersetzung | 794 Schlüssel, alle entschieden | ✅ 447 deutsch, 347 bewusst englisch |
| Enum-Schlüssel (Combobox-Einträge) | 338 Schlüssel, alle entschieden | ✅ 208 deutsch, 130 bewusst englisch |
| Autorotation-Texte (83 Module, 158 Tracks, 228 Optionen) | 2996 Schlüssel | ✅ 2539 deutsch, 457 Identität |
| Kampfhinweise (`hints.Add(...)`) | 636 feste Literale | ✅ alle übersetzt (Harmony) |
| Interpolierte Kampfhinweise | 220 Aufrufe | ✅ 59 Regex-Muster |
| Übrige UI-Literale (Fenster, Buttons, Tooltips) | 380 nutzerseitige | ✅ 259 deutsch, 121 Identität |
| Debug- und Replay-UI | 1282 Literale | ⛔ bewusst ausgelassen |

**Alle Ebenen sind abgearbeitet:** 5149 Schlüssel, davon 4094 übersetzt und 1055 bewusst englisch, 0
offen. Das umfasst die allgemeinen Einstellungen ebenso wie jeden Encounter, FRU und TOP und DSW
eingeschlossen, und seit diesem Stand die vollständige Autorotation.

Bei den Enums ist der Zuschnitt die eigentliche Arbeit. BossMod registriert **jedes** seiner Enums — 3607
Stück mit zusammen 40543 Membern —, aber der weit überwiegende Teil sind Konventionen: `AID`, `SID`,
`OID`, `IconID`. Deren Membernamen sind Identität, kein Anzeigetext. Übersetzt wird nur, was ein Spieler
lesen kann: der Enum-Typ eines Konfigurationsfelds (also der Inhalt einer Combobox) und jedes Enum, dessen
Member ein `PropertyDisplay` tragen. Das sind 42 Enums mit 338 Schlüsseln statt 40543.

Abgeleitet wird das **offline** aus der Assembly, nicht aus einer Spielsitzung. BossMod baut seine
Enum-Tabellen erst bei Bedarf auf, deshalb sieht `/bmrtl extract` immer nur die Comboboxen, die in dieser
Sitzung jemand geöffnet hat — der Rest fehlt stillschweigend. `tools/ShapeCheck` leitet stattdessen alle
ab und prüft sie damit auch auf Drift.

### Was bewusst englisch bleibt

Zwei Kategorien, beide aus demselben Grund: deutsche Spieler kennen sie ausschließlich englisch, weil
Guides, Partyfinder und Community-Sprache englisch sind. Eine Übersetzung würde den Abgleich mit einem
Guide erschweren, nicht erleichtern.

1. **Raid-Notation, Strategie- und Fähigkeitsnamen** (1055 Schlüssel) — `MT/R1 N, OT/R2 S`,
   `LPDU (global): M1>M2>MT>OT>R1>R2>H1>H2`, Clockspots, `CW`/`CCW`, `Hector (NA)`, `Banana Codex`.
2. **Fähigkeits- und Mechaniknamen** innerhalb übersetzter Sätze — `'Elusive Jump'`, `Cyclonic Break`,
   `Sanctity of the Ward`. Der Satz drumherum ist deutsch, der Name bleibt zitierfähig.

Kategorie 1 ist im Sprachfile als Entscheidung markiert, nicht als Lücke:

```json
"cfg/.../P1BoundOfFaithAssignment/preset.0": { "de": "", "en": "Supports N, DD S" }
```

Ein leeres `"de"` heißt „entschieden, bleibt englisch"; ein fehlender Eintrag heißt „noch nicht bearbeitet".
Ohne diese Unterscheidung würde die Zahl offener Schlüssel nie auf null gehen und damit nichts mehr
aussagen. `/bmrtl` und `tools/ShapeCheck` zählen beide Kategorien getrennt.

Wer es anders haben will, ändert `de.json` — keine Codeänderung nötig.

## Kampfhinweise (Milestone 2)

636 feste Hinweise sind übersetzt — von „Raus aus der AoE!" bis zu den langen Blue-Mage-Empfehlungen der
Variant-Dungeons. Das ist die einzige Ebene, die IL-Patching braucht: Hinweise sind Literale in über 800
Aufrufstellen und werden jeden Frame neu berechnet, es gibt also keine Daten zum Umschreiben.

Gepatcht werden die **zusammenführenden** Methoden, nicht die einzelnen `Add`-Aufrufe:

| Ziel | deckt ab |
| --- | --- |
| `BossModule.CalculateHintsForRaidMember` | alle Spielerhinweise aller Komponenten |
| `BossModule.CalculateGlobalHints` | alle raidweiten Hinweise |
| `ZoneModule.CalculateGlobalHints` | Duty-Automatisierung (virtuell: jeder Override) |
| `BossModule.PrePullHints` | Pre-Pull-Notizen (virtuell: alle 59 Overrides) |

### Interpolierte Hinweise

Rund ein Viertel der Hinweise ist interpoliert — `hints.Add($"Stack with {name}")`. Für die kann ein exakter
Treffer nie funktionieren, deshalb gibt es geordnete Regex-Regeln in `$hintPatterns`:

```json
{ "en": "^Target (.+)!$", "de": "$1 anvisieren!" }
```

Ein exakter Eintrag gewinnt immer gegen ein Muster, eine engere Regel steht über einer breiteren. Die 59
mitgelieferten Muster deckeln die häufigen Formen (`Order: …`, `Target …!`, `… counters physical damage!`).

**Interpolierte Wortlaute lassen sich nicht vorab auflisten** — sie existieren in der Assembly nicht als
einzelnes Literal. Deshalb protokolliert das Plugin jeden Hinweis, der durchläuft, und `/bmrtl extract`
gibt die unübersetzten zurück. Wer spielt, sammelt sie also ein; das Statusfenster zeigt den Zähler.

## Installation

### Als Nutzer: eigenes Plugin-Repository

`/xlsettings` → Experimental → „Custom Plugin Repositories", diese URL eintragen:

```
https://raw.githubusercontent.com/ElyFura/BossmodReborn-Translation/main/repo.json
```

Danach taucht *BossMod Reborn Translation* im Plugin-Installer auf.

### Als Entwickler

```
dotnet build -c Release -p:Platform=x64
```

Das Ergebnis liegt in `BmrTranslation/bin/x64/Release/` — dieser Pfad ist im Projekt fest verdrahtet
(`BaseOutputPath`), damit er sich nicht je nach Build-Aufruf verschiebt und der in Dalamud eingetragene
Dev-Plugin-Pfad gültig bleibt. Dort als Dev-Plugin einbinden: `/xlsettings` → Experimental →
Dev-Plugin-Pfad. Der Release-Build legt zusätzlich `BmrTranslation/latest.zip` an — das ist das Artefakt,
das als Release-Asset hochgeladen wird.

### Release

`repo.json` wird **nicht von Hand gepflegt**. Die `AssemblyVersion` darin ist das Einzige, woran Dalamud
ein Update erkennt; driftet sie vom Projekt ab, bietet das Plugin still kein Update mehr an. Sie wird
deshalb aus `BmrTranslation.csproj` und `BmrTranslation.json` erzeugt:

```
python tools/build-repo-json.py           # schreiben
python tools/build-repo-json.py --check   # meldet Exit-Code 1, wenn repo.json veraltet ist
```

Ablauf: Version in der `.csproj` erhöhen → `build-repo-json.py` → `repo.json` committen → Tag `vX.Y.Z.W`
pushen. Den Rest erledigt der Release-Workflow.

Alle übrigen Metadaten — auch `IconUrl` (`res/bmrg.png`) — stehen **nur** in `BmrTranslation.json` und
werden von dort nach `repo.json` durchgereicht. Zeigt `IconUrl` auf eine Datei in diesem Repository, prüft
der Generator, dass sie existiert und eingecheckt ist: ein toter Raw-Link erscheint im Installer als
kaputte Kachel, und das fällt sonst erst nach der Veröffentlichung auf.

### GitHub Actions

| Workflow | Auslöser | Tut |
| --- | --- | --- |
| `ci.yml` | Push auf `main`, Pull Request | Bauen, AlcProbe (positiv **und** negativ), `repo.json --check`, Sprachdatei prüfen, Paketinhalt prüfen, ZIP als Artefakt |
| `release.yml` | Tag `v*` | Bauen, AlcProbe, Tag/Projektversion/`repo.json` abgleichen, `latest.zip` ans Release hängen |

Beide laden Dalamud über `DALAMUD_HOME` aus der offiziellen Distribution — ein Runner hat keine
XIVLauncher-Installation. Deshalb gewinnt `DALAMUD_HOME` inzwischen auf **jedem** System, nicht mehr nur
unter Linux.

Zwei Dinge, die die CI bewusst *nicht* tut:

* **`ShapeCheck` läuft nicht.** Es prüft gegen die *installierte* `BossModReborn.dll`, und die kann sich
  ein Runner nicht beschaffen. Die Drift-Prüfung nach einem BossMod-Update bleibt ein lokaler Lauf.
* **`repo.json` wird nicht automatisch geschrieben.** Der Release-Workflow *weigert sich* zu
  veröffentlichen, wenn Tag, Projektversion und `repo.json` auseinanderlaufen. Ein stillschweigender
  Auto-Commit auf `main` würde den Fehler verstecken, statt ihn zu zeigen — und sein Symptom ist ohnehin
  kein Fehler, sondern ein Update, das nie angeboten wird.

Der negative AlcProbe-Lauf (`--as-shipped-before`) steht bewusst als eigener CI-Schritt: schlägt er eines
Tages *nicht* mehr fehl, ist der Harmony-Bootstrap überflüssig geworden und gehört überprüft, statt
für immer mitgeschleppt zu werden.

## Befehle

| Befehl | Wirkung |
| --- | --- |
| `/bmrtl` | Öffnet das Statusfenster (auch über Zahnrad/Symbol in der Plugin-Liste) |
| `/bmrtl status` | Status in den Chat: BossMod-Version, Einträge, angewendet / fehlend / veraltet |
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

### Stapelweise übersetzen, ohne das Spiel zu starten

Für größere Batches braucht man das Spiel nicht — die Konfigurations-Strings lassen sich direkt aus der
installierten Assembly ziehen:

```
dotnet run --project tools/ShapeCheck -- --missing > missing.json   # nur unentschiedene Schlüssel
dotnet run --project tools/ShapeCheck -- --all > all.json           # alle, um Bestehendes zu überarbeiten
```

Dann eine Batch-Datei mit einer Zeile pro Eintrag schreiben (`\n` im Text erzeugt einen Zeilenumbruch,
`=` markiert „bleibt bewusst englisch"):

```
cfg/BossMod.ColorConfig/ArenaEnemy/label ||| Arena: Gegner
cfg/BossMod.Dawntrail.Savage.M10STheXtremes.Strategy/Hector/label ||| =
```

und einmischen:

```
python tools/merge-translations.py missing.json batch.txt
```

Das Skript nimmt den **englischen Text niemals aus der Batch-Datei**, sondern immer aus dem Dump — so passt
der gespeicherte `en`-Wert für die Drift-Erkennung garantiert zum Original. Ein Schlüssel, der im Dump nicht
vorkommt, wird gemeldet und übersprungen statt still hinzugefügt; das fängt Tippfehler in Schlüsseln ab
(Exit-Code 1). Anschließend `dotnet run --project tools/ShapeCheck` zur Kontrolle.

## Fenstertexte (Milestone 3)

Alles, was BossMod direkt als Literal an ImGui gibt: Fenstertitel, Buttons, Checkboxen, Tabellenköpfe,
Tooltips. 380 nutzerseitige Literale sind entschieden — 259 deutsch, 121 bleiben englisch.

Der Transpiler **ersetzt den `ldstr`-Operanden direkt zur Patch-Zeit**. Er baut keinen Lookup ein: das
Literal liegt beim Transpilieren schon als Operand vor, also kostet die Auflösung zur Laufzeit nichts —
kein Aufwand pro Frame, kein Helfer, der sich falsch verhalten kann.

Gepatcht werden **nur Methoden, die tatsächlich eine Übersetzung haben** — die Liste kommt aus der
Sprachdatei, nicht aus einem Assembly-Scan. Das sind rund 80 statt mehrerer Tausend.

Schlüssel sind `ui/<Typ>::<Methode>/<Literal>`. Das Literal gehört in den Schlüssel, weil derselbe Text in
einer Methode ein sichtbares Label und in einer anderen eine ImGui-ID oder ein String-Vergleich sein kann —
nur das erste darf übersetzt werden. Ein Beispiel, das genau das braucht: `ModuleViewer` nutzt „Enabled" als
Spaltenkopf **und** in `EnabledColumnWidth`, um die Spaltenbreite zu messen. Würde nur der Kopf übersetzt,
wäre die Spalte falsch breit; beide Schlüssel bekommen dieselbe Übersetzung.

Als englisch markiert (121): ImGui-IDs (`ConfigTabs`, `preset_options`, `##module`), Chat-Befehle (`/bmr`,
`/bmrai`), Befehls-Token (`RADAR`, `TOGGLE`), Reflection-Namen, URLs, Texturpfade, Log-Präfixe und der
erzeugte C#-Quellcode des Quest-Battle-Gerüsts. Das ist Identität oder Protokoll, kein Anzeigetext.

**Debug- und Replay-Oberflächen (1282 Literale) sind bewusst ausgelassen** — Entwicklerwerkzeuge, die kein
Spieler öffnet. `tools/ShapeCheck --ui` listet sie, falls sie doch jemand haben will.

## Autorotation

2996 Schlüssel über 83 Module — Modulnamen, 158 Strategie-Tracks und 228 Optionen. Die Texte sind stark
formelhaft: 2996 Schlüssel bestehen aus nur 1762 verschiedenen Sätzen, und `Do not use automatically`
allein steht hinter 321 davon.

Übersetzt wurde in drei Stufen, von der sichersten zur aufwendigsten:

1. **Reine Namen** (299) — `Aegis`, `A.Anchor`, `Arms' Length`. Fähigkeitsnamen und ihre Kürzel bleiben
   englisch, wie überall sonst in diesem Projekt.
2. **Satzschablone um einen Namen** (418) — `Automatically use Bow Shock`, `Force Nastrond in next
   possible weave slot`. Die Schablone übersetzt mechanisch, der Name wird unangetastet durchgereicht.
   Eine Schablone greift nur, wenn das Objekt wirklich ein bloßer Name ist: alles mit Funktionswörtern
   oder einem eigenen nachgestellten `ASAP` landet in Stufe 3, weil die Wortstellung sonst kippt.
3. **Echte Sätze** (1045) — von Hand.

Fachvokabular folgt dem, was die Hinweis-Ebene schon gesetzt hat: Burst, Gauge, Cartridges, GCD, Combo,
Proc, DoT, Positional, Gapcloser, Slidecasting, Weave und Yalms bleiben stehen, weil deutsche Spieler sie
ausschließlich so kennen.

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

Optionen: `--bmr <pfad>`, `--dalamud <verzeichnis>`, `--seed <pfad>`, `--strict`, `--missing`, `--all`,
`--help`.

Die zweite Prüfung betrifft das IL-Patching. Kampfhinweise und Fenstertexte laufen über Harmony, und
Harmony funktioniert in Dalamuds Plugin-Kontext nur, wenn `0Harmony` außerhalb davon liegt (siehe
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)). `tools/AlcProbe` baut diese Umgebung nach — zwei collectible
Kontexte, Plugin und Zielassembly getrennt — und patcht dort jede Form, die das Plugin braucht:

```
dotnet run --project tools/AlcProbe/Host                          # muss PASS ergeben
dotnet run --project tools/AlcProbe/Host -- --as-shipped-before   # muss fehlschlagen
```

Autorotation-Schlüssel entstehen erst, wenn BossMod seine Modulregistrierung aufgebaut hat — sie lassen
sich also nicht offline ableiten. Für sie prüft `--catalogue` gegen ein frisches `/bmrtl extract`:

```
dotnet run --project tools/ShapeCheck -- --catalogue "%appdata%\XIVLauncher\pluginConfigs\BmrTranslation\extract\strings.de.json"
```

Das meldet dieselben zwei Dinge wie die abgeleiteten Prüfungen: verwaiste Schlüssel und Drift. Ein Katalog
enthält immer nur, was die Sitzung berührt hat, deshalb überspringt die Prüfung eine Ebene, die im Katalog
gar nicht vorkommt, mit Hinweis — statt sie als gelöscht zu melden.

Der erste Lauf prüft als Schritt 0 die **echte** gebaute `BmrTranslation.dll`: ob Dalamud ihre Typen
aufzählen kann, solange `0Harmony` noch nicht geladen ist. Der zweite stellt die Anordnung her, die im
Spiel jeden Patch scheitern ließ. Beides gehört dazu: ein Prüfstand, der den Fehlerfall nicht mehr
erzeugen kann, meldet irgendwann grün für die falsche Umgebung — genau das ist hier zweimal passiert.

## Nur eine Kopie gleichzeitig

Dalamud behandelt einen Dev-Plugin-Pfad und eine Installation aus dem Repository als **zwei** Plugins.
Sind beide aktiv, hängen sich beide an dieselben BossMod-Objekte — und der Schaden ist nicht kosmetisch:
die zweite Kopie liest das Deutsch der ersten und merkt es sich als „BossMods Englisch". Ihr Undo-Log
stellt beim Entladen dann Deutsch wieder her, das englische Original ist bis zum Spielneustart weg.

Sichtbar wird das an einem Fenster voller „veraltet". Das Plugin erkennt den Fall inzwischen über einen
benannten Mutex: die zweite Kopie bleibt untätig und sagt im Statusfenster, warum. Entfernt man eine der
beiden, greift die verbliebene beim nächsten Versuch von selbst wieder.

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
