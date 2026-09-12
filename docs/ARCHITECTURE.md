# Architektur

## Grundidee

BossMod Reborn ist nicht lokalisierbar gebaut: es gibt keine Ressourcendateien, keine `CultureInfo`-Nutzung
und kein Lokalisierungsframework. Sichtbarer Text entsteht an vier sehr unterschiedlichen Stellen, und
genau diese Aufteilung bestimmt die Milestones.

| Schicht | Wo der Text liegt | Zugriff |
| --- | --- | --- |
| 1. Konfiguration | Attributinstanzen in `GeneratedConfigMetadata` | Datenmutation per Reflection |
| 2. Autorotation | `RotationModuleDefinition`, `StrategyConfig`, `StrategyOption` | Datenmutation per Reflection |
| 3. Kampfhinweise | String-Literale in `Modules/` und `Components/` | Harmony nötig |
| 4. Restliche UI | ImGui-Aufrufe in 87 Dateien | Harmony-Transpiler nötig |

Milestone 1 deckt Schicht 1 und 2 vollständig ab. Das ist der Teil, der ohne IL-Manipulation auskommt —
und damit der Teil, der ein BossMod-Update mit hoher Wahrscheinlichkeit übersteht.

## Warum Schicht 1 und 2 ohne Hooks funktionieren

BossMods Source-Generator (`BossMod.SourceGen/ConfigGenerator.cs`) erzeugt pro Konfigurationsklasse eine
`ConfigTypeMetadata` und darin pro Feld eine **eigene** Attributinstanz:

```csharp
metadata.Add(typeof(BossModuleConfig), new ConfigTypeMetadata(typeof(BossModuleConfig), new ConfigDisplayAttribute { ... }, new ConfigFieldMetadata[]
{
    new ConfigFieldMetadata("EnableRadar", typeof(bool), true, getter, setter, null, new PropertyDisplayAttribute("Enable radar"), ...),
}));
```

`ConfigUI` liest `Label` und `Tooltip` bei **jedem Frame** aus diesen Attributen. Wer die Instanzen
umschreibt, hat die Oberfläche übersetzt — ohne einen einzigen Hook. Und weil der Generator pro Feld eine
frische Instanz erzeugt, kann eine Änderung nicht auf ein anderes Feld durchschlagen.

Zwei Eigenschaften machen das zusätzlich sicher:

* **Serialisierung nutzt andere Member.** Konfigurationswerte werden über Feldnamen gespeichert, Presets
  über `InternalName` (`Autorotation/Preset.cs`). Anzeigetext ist konsequent davon getrennt. Eine
  Übersetzung kann daher keine gespeicherten Einstellungen beschädigen.
* **`ConfigFieldMetadata` wird mutiert, nicht ersetzt.** `ConfigTypeMetadata` hält vier Referenzen auf
  dieselben Feld-Objekte (`Fields`, `SerializableFields`, `DisplayFields`, `FieldsByName`). Neue Instanzen
  einzusetzen hieße, alle vier konsistent zu halten; eine In-Place-Änderung umgeht das Problem.

## Die drei Fallen

### `readonly`-Felder

Fast aller Anzeigetext sitzt in `readonly`-Feldern — auch die Backing-Fields von `{ get; }`-Properties
(`<Label>k__BackingField`). `Reflect.Set` schreibt sie über eine `DynamicMethod` mit `stfld`, weil der JIT
`initonly` dort nicht durchsetzt; `FieldInfo.SetValue` ist der Rückfallweg.

### `EnumMetadata.DisplayNames` zeigt oft auf `Names`

`EnumMetadata` setzt `DisplayNames = displayNames ?? names` — bei Enums ohne abweichende Anzeigenamen ist
es **dasselbe Array**. Und `GeneratedEnumMetadata.Parse` vergleicht beim Deserialisieren gegen `Names`.
Ein Schreibzugriff auf das gemeinsame Array würde also das Laden der Konfiguration zerstören.
`EnumMetadataPatcher` kopiert deshalb zuerst und zeigt `DisplayNames` auf die Kopie.

Zweitens sind die Tabellen `Lazy<EnumMetadata>`. Alle zu erzwingen würde große ID-Enum-Tabellen aufbauen,
die niemand braucht — deshalb wird nur übersetzt, was schon materialisiert ist, und alle 2 Sekunden
nachgesehen.

### `ConfigUI` cacht zwei Dinge

`ConfigUI` kopiert im Konstruktor die Abschnittsnamen in `UINode.Name` und legt seine Tab-Namen an; danach
liest es die Metadaten dafür nie wieder. Übersetzt man nur die Metadaten, bleiben Baum und Tab-Leiste
englisch.

Der Weg zur lebenden `ConfigUI`-Instanz führt **nicht** über BossMods Plugin-Objekt (das wäre Reflection in
Dalamuds `PluginManager`), sondern über zwei öffentliche Angelpunkte: `BossMod.Service.WindowSystem` ist
`public static`, und das Konfigurationsfenster ist ein `UISimpleWindow`, dessen Draw-Delegate als `Target`
genau die `ConfigUI` trägt.

Tab-Namen sind bei BossMod gleichzeitig Identität: `UITabs` vergleicht sie für die ImGui-ID und für
`ConfigUI.ShowTab`. Sie werden daher als `"Einstellungen###Settings"` geschrieben — die ImGui-ID bleibt
stabil — und ein kleiner Wrapper um das Draw-Delegate bildet das ausstehende `ShowTab` auf den übersetzten
Namen ab, damit die „springe zu Tab"-Aufrufe weiter funktionieren.

Das Fenster ist *detached* und wird beim Schließen verworfen, taucht also bei jedem Öffnen als neues Objekt
auf. Der Sweep behandelt das.

## Rückbau ist Pflicht

Wir verändern Objekte, die BossMod gehören. Ohne Undo-Log bliebe die Oberfläche nach einem Entladen dieses
Plugins halb deutsch, bis das Spiel neu startet. `PatchSession` schreibt zu jeder Änderung die
Rücksetzfunktion mit; `Dispose`, `/bmrtl off` und `/bmrtl reload` spielen sie in umgekehrter Reihenfolge
zurück.

## Der Extraktor

`/bmrtl extract` parst **keinen** Quellcode. `PatchSession` protokolliert jeden Schlüssel, an dem die
Patcher vorbeigelaufen sind — übersetzt oder nicht — samt gefundenem englischem Text. Der Katalog kommt
damit aus derselben Traversierung wie die Anwendung. Das schließt die typische Fehlerquelle
quelltextgeschürfter Übersetzungsdateien aus: Schlüssel, die zur Laufzeit nie treffen.

Nebeneffekt: der englische Text stammt aus der **installierten** BossMod-Version, nicht aus einem
Arbeitsbaum, der davon abweichen kann.

## Der Shape-Checker

`tools/ShapeCheck` liest die installierte `BossModReborn.dll` über einen `MetadataLoadContext` — also nur
Metadaten, ohne die Assembly auszuführen und ohne Dalamud-Prozess. Das macht es zu einem Werkzeug, das man
nach jedem BossMod-Update laufen lassen kann, statt im Spiel zu merken, dass nichts mehr übersetzt wird.

Drei Prüfungen:

* **Strukturen.** Jeder Typ, jedes Feld, jedes Backing-Field und jede Methode, die die Patcher per
  Reflection erreichen — gruppiert nach der abhängigen Datei. Zusätzlich wird die `readonly`-Eigenschaft
  mitgemeldet, weil sie entscheidet, ob `Reflect.Set` einen Setter emittieren muss.
* **Verwaiste Schlüssel.** `SeedChecks` baut aus den Attribut-Blobs dieselben Schlüssel nach, die
  `ConfigMetadataPatcher` zur Laufzeit erzeugt, und meldet jeden Eintrag in `de.json`, den es dort nicht
  gibt. Exit-Code 1.
* **Drift.** Existiert der Schlüssel noch, aber der mitgeschriebene `en`-Wert weicht vom aktuellen Text ab,
  ist die Übersetzung überholt. Warnung; mit `--strict` ein Fehler.

Ein Detail zur Attribut-Auswertung: Attribute mit optionalen Konstruktorparametern tragen im Metadaten-Blob
**alle** Argumente positionsweise, mit eingesetzten Standardwerten. `PropertyDisplay`s Tooltip ist damit
schlicht `ConstructorArguments[2]`; benannte Argumente muss man nur für `ConfigDisplay.Name` betrachten, weil
das eine setzbare Property und kein Konstruktorparameter ist. Leere Standardwerte werden verworfen — sonst
würde der Checker Schlüssel erwarten, die die Laufzeit (die leere Strings überspringt) nie erzeugt.

Beide Erkennungen sind negativ getestet: ein manipuliertes `de.json` mit einem umbenannten Schlüssel und
einem verfälschten `en`-Wert liefert genau eine Fehlermeldung und eine Warnung, Exit-Codes 1 bzw. 0 (ohne
`--strict`) und 1 (mit).

## Drift

Schlüssel sind ortsabgeleitet, nie textabgeleitet. Ändert BossMod eine Formulierung, bleibt der Schlüssel
gleich und die Übersetzung greift weiter — aber der mitgeschriebene `en`-Wert passt nicht mehr, und
`/bmrtl` meldet den Eintrag als *stale*. Eine Umbenennung eines Feldes oder Typs hingegen lässt den
Schlüssel verwaisen; er erscheint dann in `missing.de.json`.

Geprüft gegen **BossMod Reborn 7.5.6.5**: alle Typ-, Feld- und Backing-Field-Namen sowie alle 918
Einträge der mitgelieferten `de.json` wurden gegen die installierte Assembly verifiziert, nicht nur gegen
den Quellcode — per `tools/ShapeCheck`. Die Konfigurationsebene ist damit vollständig entschieden: 542 der
913 ableitbaren Strings übersetzt, 371 bewusst englisch, 0 offen.

### Warum „bewusst englisch" ein eigener Zustand ist

Raid-Notation wie `MT/R1 N, OT/R2 S` oder `LPDU (global): M1>M2>MT>OT>R1>R2>H1>H2` darf nicht übersetzt
werden — Guides und Partyfinder sind englisch, eine Eindeutschung würde den Abgleich erschweren. Ohne eine
Möglichkeit, „entschieden, bleibt englisch" auszudrücken, würden diese 371 Schlüssel dauerhaft im
Fehlend-Zähler stehen und die Zahl damit unbrauchbar machen. Ein leeres `"de"` im Sprachfile ist deshalb
ein eigener Zustand, den Laufzeit und Prüfwerkzeug getrennt zählen.

Die erste Klassifikation war zu grob: pauschal „alle `group`/`preset`/`combo`/`order`-Werte bleiben
englisch". Vier davon trugen echte Erklärungsprosa hinter einem Notations-Präfix
(`2+0: first towers are soaked by short color …`, `CCW (leftmost, if facing outside)`). Ein Suchlauf über
die als englisch markierten Werte nach erklärenden Wörtern hat sie gefunden; sie sind jetzt übersetzt. Eine
Regel nach Schlüsselform ist ein guter erster Filter, aber kein Ersatz dafür, die Werte anzusehen.

Eine Lehre aus dem ersten Batch: der Dump für `tools/merge-translations.py` war zunächst zeilenbasiert
(TSV) und hat die eingebetteten Zeilenumbrüche in BossMods Tooltips zu Leerzeichen geplättet. Damit wich der
gespeicherte `en`-Wert vom Original ab, und die Drift-Erkennung meldete zwei frisch übersetzte Einträge
sofort als veraltet — ein Format, das die zu prüfenden Daten beschädigt, ist als Prüfgrundlage wertlos. Der
Dump ist deshalb JSON.

## Milestone 2 und 3

**Milestone 2 — Kampfhinweise.** 543 eindeutige feste und 238 interpolierte `hints.Add(...)`-Literale. Das
braucht Harmony. Tragfähiger Ansatzpunkt sind die öffentlichen `BossModule.CalculateHintsForRaidMember`
und `CalculateGlobalHints` (Postfix auf der zurückgegebenen Liste) statt der Einzeiler
`TextHints.Add` — die inlinet der JIT zu wahrscheinlich weg. Interpolierte Hinweise brauchen
Mustervergleich mit Platzhaltern statt 1:1-Nachschlagen. Offene Vorfrage: Verhalten von `Lib.Harmony`
unter `net10.0` und Patch-Zeitpunkt früh genug vor dem ersten JIT der aufrufenden Methoden.

**Milestone 3 — restliche UI.** 87 Dateien, darunter 314 `TextUnformatted`, 162 `Button`, 72 `Checkbox`.
Praktikabel nur über einen Harmony-Transpiler, der in den UI-Typen jedes `ldstr` durch `ldstr` +
`Translate(string)` ersetzt, mit Rückfall auf das Original bei fehlendem Eintrag.

Beide Schichten sind invasiver als Milestone 1 und sollten getrennt schaltbar bleiben.

## Schriftzeichen

Kein Problem: der Arena-MSDF-Atlas von BossMod deckt `U+0020`–`U+024F` ab
(`Framework/Fonts/arena-text.charset`), Umlaute und ß sind enthalten.
