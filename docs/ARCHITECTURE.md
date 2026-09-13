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

### Der Katalog ist die Wahrheit darüber, ob ein Schlüssel überhaupt greift

Die statische Ableitung in `ShapeCheck` kann Schlüssel erzeugen, die es zur Laufzeit nicht gibt — und das
merkt sie selbst nicht, weil sie beide Seiten des Vergleichs liefert. Genau das war passiert: die
Ableitung lief über *alle* Typen der Assembly, und Enum-Member sind Felder mit `PropertyDisplay`. Also
entstanden 119 Schlüssel der Form `cfg/<EnumTyp>/<Member>/label`. `ConfigMetadataPatcher` läuft aber über
`GeneratedConfigMetadata`, und darin stehen Config-**Klassen**; ein Enum ist dort nie ein Schlüssel.

Aufgefallen ist das erst beim Abgleich mit einem `/bmrtl extract`: 119 Schlüssel aus `de.json`, an denen
die Patcher in einer echten Sitzung kein einziges Mal vorbeigekommen sind. Alle 119 hatten einen lebenden
`enum/`-Zwilling, 57 davon mit **abweichender** Übersetzung — derselbe Text zweimal übersetzt, wobei die
tote Variante nie sichtbar wurde und deshalb auch niemandem auffallen konnte.

Die Ableitung überspringt Enums jetzt; die toten Schlüssel sind entfernt. Die Lehre ist allgemeiner: eine
Prüfung, die ihre eigene Erwartung erzeugt, bestätigt nur sich selbst. Der Laufzeit-Katalog ist die
einzige Quelle, die sagen kann, ob ein Schlüssel tatsächlich einen String erreicht.

## Milestone 2: Kampfhinweise

Die einzige Ebene, die IL-Patching braucht. Hinweise sind String-Literale in über 800 Aufrufstellen und
werden jeden Frame neu berechnet — es gibt keine Datenstruktur zum Umschreiben.

### Erst prüfen, dann bauen — und der Prüfstand, der das Falsche prüfte

Ob Harmony unter .NET 10 und über die ALC-Grenze funktioniert, war die Frage, an der der ganze Milestone
hing. Das lässt sich ohne Spiel beantworten: eine Stellvertreter-Assembly in einen eigenen
`AssemblyLoadContext` laden — so wie Dalamud es mit jedem Plugin tut — und patchen. Geprüft wird in
`tools/AlcProbe`, mit Harmony 2.4.2 auf .NET 10.0.5:

1. Postfix auf einer öffentlichen Methode, die eine `List<T>`-Ableitung zurückgibt
2. Postfix auf einer Methode, die **vorher schon** JIT-kompiliert wurde
3. Postfix auf einem Override einer virtuellen Property
4. Override-Erkennung per Assembly-Scan und Patch jedes Treffers
5. Transpiler, der `ldstr` ersetzt, inklusive `__originalMethod` (Milestone 3)
6. `UnpatchAll` stellt das Original wieder her (nötig für `/bmrtl off` und das Entladen)

Die erste Fassung dieses Prüfstands lief grün — und im Spiel scheiterte anschließend **jeder einzelne**
Patch: 64 Hinweis-Ziele und 75 UI-Ziele, alle mit `NotSupportedException: Resolving to a collectible
assembly is not supported`. Der Prüfstand hatte den `AssemblyLoadContext` mit `isCollectible: false`
angelegt. Dalamud legt ihn collectible an, damit Plugins ohne Spielneustart entladbar sind — und genau
diese eine Eigenschaft entscheidet. Grün war also eine Aussage über eine Konfiguration, die es nicht gibt.

Die Ursache liegt nicht bei Harmony selbst, sondern darin, **wo** Harmony liegt: MonoMod lässt beim Bauen
eines Patches einen Proxy-Typ erzeugen, der von `System.Reflection.Emit.ILGenerator` erbt und dabei
HarmonyLibs eigenen `ILGeneratorShim` **über seinen Namen** auflösen muss. Namensauflösung auf eine
collectible Assembly verweigert die Runtime. Kein Versionsproblem: 2.3.3 bis 2.4.2 scheitern identisch,
ebenso jedes `MONOMOD_DMDType`-Backend.

### Harmony wohnt außerhalb des Plugins

`0Harmony.dll` liegt deshalb **nicht** neben dem Plugin (`ExcludeAssets="runtime"`), sondern als
eingebettete Ressource *im* Plugin und wird beim Start in den Default-Kontext geschoben
(`Interop/HarmonyBootstrap.cs`). Danach findet der Plugin-Kontext nichts, was die Referenz erfüllt, fällt
auf den Default-Kontext zurück und bindet an diese Kopie. Patch-Methoden, Transpiler und BossMod selbst
bleiben collectible — nur Harmony zieht um, und das ist das Minimum, das funktioniert. Preis: `0Harmony`
bleibt nach dem Entladen des Plugins geladen. Eine Assembly, und unvermeidbar.

### Kein Harmony-Typ in einem Feld

Damit war das Plugin immer noch nicht ladbar — aus einem zweiten, unabhängigen Grund. Dalamud ruft
`Module.GetTypes()` auf der Plugin-Assembly auf, um die `IDalamudPlugin`-Implementierung zu finden, und
zwar **bevor** es irgendetwas konstruiert. Einen Typ zu laden löst dessen **Feldtypen** auf. Ein Feld
`private Harmony? _harmony` verlangt also `0Harmony` zu einem Zeitpunkt, an dem der Bootstrap noch gar
nicht gelaufen ist — `ReflectionTypeLoadException`, und das ganze Plugin lädt nicht.

Betroffen waren drei Stellen, zwei davon sichtbar (`HintPatcher`, `UiPatcher`) und eine nicht: `Transpile`
war ein Iterator, und der Compiler erzeugt daraus eine State-Machine mit Feldern vom Typ `CodeInstruction`.
Ein einzelner generierter Typ, den niemand geschrieben hat, reichte aus.

Die Felder heißen jetzt `object?` mit einer typisierten Property davor; `Transpile` baut eine Liste, statt
zu yielden. Methodenrümpfe werden erst beim JIT aufgelöst — also lange nach dem Bootstrap —, deshalb darf
Harmony dort beliebig genannt werden. Nur Felder und Signaturen von Feldern sind tabu.

### Was den Zustand hält

Drei Dinge, statt eines Kommentars:

* Der Build bricht ab, wenn `0Harmony.dll` neben dem Plugin landen würde (`ExcludeAssets` entfernt).
* `AlcProbe` Schritt 0 lädt die **echte** `BmrTranslation.dll` in einen collectible Kontext ohne
  `0Harmony` und zählt die Typen. Das ist die Stufe, die zweimal übersehen wurde.
* `AlcProbe --as-shipped-before` stellt die alte Anordnung her und erwartet den Fehlschlag;
  `--non-collectible` führt vor, wie die falsche Grünmeldung zustande kam.

### Warum die zusammenführenden Methoden

`BossComponent.TextHints.Add` sieht wie der natürliche einzige Funnel aus, ist aber ein Expression-Body-
Einzeiler, den der JIT in seine Aufrufer inlinet — und ein Patch auf einer inlinten Methode läuft nie. Die
`Calculate*`-Methoden iterieren über die Komponenten und sind keine Inlining-Kandidaten.

Virtuelle Member brauchen jeden Override einzeln: ein Patch auf der Basis-Deklaration fängt eine Unterklasse
nicht ab, die sie überschreibt. Daher der Assembly-Scan für `ZoneModule.CalculateGlobalHints` und die 59
`PrePullHints`-Overrides.

### Die Falle bei `PrePullHints`

Alle 59 Overrides geben `_prePullHints` zurück — ein **Feld**, kein neu erzeugtes Array. Ein
In-Place-Schreiben hätte BossMods Hinweis-Arrays dauerhaft überschrieben, ohne Rückweg. Der Postfix klont
deshalb, bevor er schreibt. Dass es ein Feld ist, stand nicht in der Signatur; das kam erst beim Nachsehen
im Quellcode heraus.

### Textabgeleitete Schlüssel — die eine Ausnahme

Hinweise haben keinen Ort. Es gibt nichts Stabiles zum Verankern, also ist der Schlüssel der englische Text
selbst (`hint/Stay together!`). Preis: eine Umformulierung stromaufwärts verwaist den Schlüssel, statt Drift
zu melden — der neue Wortlaut erscheint dann als neu beobachteter Hinweis in `/bmrtl extract`.

### Interpolierte Hinweise: beobachten statt aufzählen

220 Aufrufe sind interpoliert und existieren in der Assembly nicht als einzelnes Literal. Sie brauchen
geordnete Regex-Regeln (`$hintPatterns`), und die Regeln müssen gegen real gesehene Hinweise geschrieben
werden. Deshalb protokolliert der `HintTranslator` jeden Hinweis, der durchläuft.

Zwei Eigenschaften, die aus dem Per-Frame-Aufruf folgen: ein Fehlschlag muss so billig sein wie ein Treffer
(einmal erfolglos gegen Dictionary und alle Muster geprüft, wird das Ergebnis gemerkt), und die
Beobachtungsliste ist begrenzt.

Ein Nebenfund beim Extrahieren: `$"..."` **ohne** Platzhalter kompiliert zu einem gewöhnlichen String. Alle
`$`-Aufrufe zu überspringen hätte 16 feste Hinweise stillschweigend verloren.

## Milestone 3: Fenstertexte

### Der Transpiler ersetzt, statt nachzuschlagen

Der erste Entwurf war, jedes `ldstr` durch `ldstr` + `Translate(string)` zu ersetzen — also einen Lookup pro
Literal pro Frame zu emittieren. Beim Prototypen fiel auf, dass das unnötig ist: der Transpiler hat das
Literal zur Patch-Zeit **schon als Operanden**. Er ersetzt es einfach. Damit gibt es zur Laufzeit keinen
Aufwand, keinen Helfer und nichts, was sich falsch verhalten kann. Voraussetzung ist, dass Harmony dem
Transpiler die gepatchte Methode mitgibt (`__originalMethod`) — das prüft AlcProbe mit.

### Die Patch-Liste kommt aus der Sprachdatei

Nicht aus einem Assembly-Scan. Übersetzte Schlüssel nennen ihre Methode, also ist die Menge der zu
patchenden Methoden genau die Menge der Methoden mit Übersetzung — rund 80 statt mehrerer Tausend. Das hält
JIT-Kosten und Wirkungsradius klein und macht ein „alles patchen und hoffen" unnötig.

### Warum das Literal in den Schlüssel gehört

`ui/<Typ>::<Methode>/<Literal>`. Derselbe Text kann in einer Methode ein sichtbares Label und in einer
anderen eine ImGui-ID oder ein String-Vergleich sein; nur das erste darf übersetzt werden. Und das Literal
statt eines Index im Schlüssel heißt: ein neues Literal in einer Methode verschiebt nicht alle anderen
Schlüssel darin.

Ein Fall, der genau das braucht: `ModuleViewer` nutzt „Enabled" als Spaltenkopf **und** in
`EnabledColumnWidth`, um die Spaltenbreite zu messen. Nur den Kopf zu übersetzen hätte eine falsch
bemessene Spalte ergeben. Beide Schlüssel bekommen dieselbe Übersetzung.

`::` trennt Typ von Methode, weil ein Konstruktor buchstäblich `.ctor` heißt und ein `.` als Trenner
ambig wäre.

### Der Extraktor liest IL, keinen Quellcode

`tools/ShapeCheck --ui` liest die installierte Assembly mit Mono.Cecil: die Literale einer Methode sind ihre
`ldstr`-Operanden, und ob eine Methode UI zeichnet, entscheidet sich daran, ob sie ImGui aufruft. Damit ist
die Kandidatenmenge exakt statt geraten — kein Quellbaum, keine Regexe, und automatisch passend zur
installierten Version. Ergebnis: 1662 Literale in 277 Methoden, davon 380 nutzerseitig.

### Zwei Fallen beim Zusammenführen

Mehrzeilige Literale tragen **CRLF**, weil BossMods Quelldateien CRLF haben. Der erste Merge-Durchlauf
schrieb `
` und traf die Schlüssel nicht — der Diff sah dabei identisch aus, weil der Unterschied
unsichtbar ist. Und Literale, die auf ein Leerzeichen enden (`"AI: "`, `"New "`), verlieren es, wenn die
Batch-Zeile getrimmt wird; dafür gibt es die Anführungszeichen-Form. Beides hat der Abgleich gegen den Dump
gefunden, nicht ein Blick auf den Text.

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
* **Kampfhinweise und Fenstertexte.** Deren Schlüssel enthalten den englischen Text, also werden sie gegen
  die Literale der Assembly geprüft. Dabei ist eine Falle: Literale liegen UTF-16LE im #US-Heap, aber nichts garantiert einen
  geraden Byte-Offset — die Datei nur ab Offset 0 zu dekodieren verliert jedes Literal auf einem ungeraden.
  Der erste Anlauf meldete deshalb 267 von 636 Hinweisen als verschwunden. Geprüft werden jetzt beide
  Ausrichtungen.
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

Alle drei Milestones sind umgesetzt. Was offen bleibt: die Debug- und Replay-Oberflächen (1282 Literale),
bewusst ausgelassen, weil es Entwicklerwerkzeuge sind. `tools/ShapeCheck --ui` listet sie.

## Schriftzeichen

Kein Problem: der Arena-MSDF-Atlas von BossMod deckt `U+0020`–`U+024F` ab
(`Framework/Fonts/arena-text.charset`), Umlaute und ß sind enthalten.
