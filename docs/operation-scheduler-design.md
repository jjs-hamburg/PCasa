> **HISTORISCHER ENTWURF UND ÄNDERUNGSPROTOKOLL (21.–23.10.2026).** Die folgenden Kapitel 1–14 enthalten absichtlich damalige Planungen, Teilstände und offene Abnahmesperren. Formulierungen wie »Konzept, keine Implementierung«, »nur manuelle CI«, »Scheduler-Monitor« oder »nicht als ZIP freigeben« beschreiben **nicht** pauschal den aktuellen Stand. Die gegenwärtige Architektur steht in [`architecture.md`](architecture.md); der aktuelle, geprüfte Entwicklungszweig ist `scheduler-consistency`. Im Zweifel gelten Quelltext, Tests und der Statusnachtrag am Ende dieser Datei. Ein Scheduler-Monitor als separate UI wird **nicht** als aktuelle oder geplante Funktion geführt.

# PCasa – OperationScheduler: verbindlicher Architekturentwurf

Stand: 2026-10-21 · Arbeitszweig `picasa-browse-cache` · **Konzept, keine Implementierung**

## 1. Ziel und Geltungsbereich

Der OperationScheduler ist die **einzige Kontrollinstanz für ressourcenverbrauchende, verzögerte oder konkurrierende Arbeiten**. Er verwaltet Aufträge, Prioritäten, Ressourcen, Abbrüche, Wiederaufnahmen, fachlichen Sollzustand und nachvollziehbaren Status.

Die WPF-Oberfläche verarbeitet Eingaben, zeichnet bereits geladene Thumbnails, scrollt, positioniert Fenster und zeigt Menüs **direkt und ohne Scheduler-Wartezeit**. Sobald eine Aktion asynchronen Datei-, Datenbank-, Netzwerk-, Cache- oder Decoder-Zugriff erfordert, beauftragt sie den Scheduler. Das Zeichnen bleibt niemals von einer zentralen Arbeitswarteschlange abhängig.

Der Scheduler **enthält keine Fachlogik**: für jeden fachlichen Vorgang gibt es **genau einen Dienst**, unabhängig davon, ob Kontextmenü, Viewer, Tastatur, Ordnermanager, Dateisystem-Watcher oder ein späterer Importdialog ihn auslöst.

Kein paralleler Neben-Scheduler in einer Fachfunktion. UI-Timer für Gesten/Animationen sind zulässig, dürfen aber keine unkoordinierten Hintergrundjobs starten.

## 2. Fachlicher Zustand statt blinder FIFO-Aufträge

Die Soll-Konfiguration der überwachten Verzeichnisse und der ausgeschlossenen Unterverzeichnisse ist verbindlich. Das Dateisystem bleibt Wahrheit über die physischen Dateien; SQLite ist der lokale Katalog. Thumbnails und Pack-Dateien sind abgeleitete Daten.

Für eine überwachte Quelle führt die Steuerung einen **stabilen Quellschlüssel** und eine monoton steigende **Generation**. Der Schlüssel wird anhand normalisierter kanonischer Pfade (bei Netzlaufwerken nach Möglichkeit UNC) gebildet und bleibt auch erhalten, wenn die überwachte Zeile zwischenzeitlich aus SQLite entfernt wird. Eine neue SQLite-ID **ist keine neue Quellidentität**. Kanonische Pfadidentität, tatsächliche Erreichbarkeit und mögliche Aliase müssen ausdrücklich geprüft werden; unbekannte Netzlaufwerks-Aliase niemals blind gleichsetzen.

Manuelle Zustandsänderungen aktualisieren die Soll-Konfiguration und erhöhen die zugehörige Generation **atomar und dauerhaft** mit der fachlich wirksamen Entscheidung. Ein Auftrag erfasst Quellschlüssel und Generation. Er darf nur dann Änderungen committen oder als aktuelle Ergebnisse veröffentlichen, wenn die Generation und die relevanten Ein-/Ausschlussregeln weiterhin gelten. Der Check erfolgt **innerhalb derselben Commit-Grenze** wie die Datenänderung; ein bloßes Prüfen vor einem langen Scan schützt nicht. Alte Ergebnisse sind `Superseded` und dürfen den neueren Zustand nicht nachträglich überschreiben.

Generationen betreffen den jeweiligen Wirkungsbereich: ein Wechsel in einem Unterordner muss nicht ohne Grund sämtliche anderen Ordner invalidieren. Für Umbenennungen und verschachtelte überwachte Wurzeln sind Quellschlüssel, Zugehörigkeit und Auftragsüberlappung explizit zu definieren, bevor Code geschrieben wird.

Das Entfernen **aus PCasa** löscht keine Originale. Physisches Löschen ist eine getrennte Fachaktion: lokal Windows-Papierkorb; Netzwerkpfad nach ausdrücklicher Warnung endgültig löschen; kein stiller Fallback. Ein physisch gelöschtes Original kann durch Wiederaufnahme der Überwachung nicht wiederhergestellt werden.

## 3. Ein Auftragsmodell

Ein `OperationRequest` enthält mindestens:
- Auftrags-ID, Typ, Auslöser (manuell/automatisch), Prioritätsklasse und Erstellungszeit
- Quellschlüssel, betroffenen Pfad/Unterbaum und erwartete Generation
- benötigte Ressourcen, Abhängigkeiten, sicher erreichbare Unterbrechungspunkte
- Status, Fortschritt, Fehler und dokumentierte Ersetzungs-/Zusammenführungsregel

Zustände: `Queued`, `Running`, `YieldRequested`, `WaitingForResource`, `Paused`, `Completed`, `Canceled`, `Superseded`, `Failed`. `YieldRequested` bedeutet **noch nicht gestoppt**. Nicht abbrechbare, bereits laufende atomare Schritte enden oder rollen zurück, bevor der Scheduler widersprechende Arbeit an derselben Ressource startet.

### Prioritätsklassen

| Klasse | Beispiele | Regel |
| --- | --- | --- |
| P0 – interaktiv | Originalbild öffnen, einzelne Datei löschen, manuelle Ordnerentfernung | Start schnellstmöglich; UI nie warten lassen |
| P1 – sichtbare Daten | Thumbnails für aktuellen Viewport | vor unsichtbarem Prefetch |
| P2 – bewusst gestartete Langläufer | Ordner hinzufügen/einlesen, Ordner aktualisieren, Duplizieren | vor automatischer Hintergrundarbeit |
| P3 – automatisch | Watcher-Signale, periodische Prüfung, Tag-Backfill | unterbrechbar und zusammenführbar |
| P4 – Leerlauf | Cache-Kompaktierung, nicht dringende Bereinigung | nur bei verfügbaren Ressourcen |

Priorität richtet sich nach **Benutzerabsicht und Sichtbarkeit**, nicht nach dem aufrufenden Codepfad. Ein manuell hinzugefügter Ordner darf nicht erst bei einer periodischen Watcher-Prüfung zur Verarbeitung kommen. Lang laufende Arbeiten dürfen P0/P1 nicht dauerhaft verdrängen; niedrige Klassen benötigen umgekehrt faire Gelegenheiten zum Fortschritt.

Der Scheduler darf *mehrere* Aufträge parallel ausführen, aber nur unter klaren Ressourcenregeln: getrennte begrenzte Kapazitäten für Netzwerk-/Dateilesen, Decoder und lokalen Cache; SQLite-Leser/WAL möglichst parallel, Schreiber kurz und serialisiert; Pack-Index, Pack-Writer und Pack-Kompaktierer konsistent koordiniert. Ein langsamer SMB-Aufruf darf den WPF-Dispatcher und die gesamte Warteschlange nicht blockieren.

## 4. Koaleszierung, Abbruch und Wiederaufnahme

Neue Anforderungen ersetzen oder verschmelzen alte Anfragen nach **fachlicher Bedeutung**, Quellschlüssel, Unterbaum, Generation und Art der Arbeit. Kein pauschales Löschen aller Pending-Einträge. Beispiel: Ein manueller rekursiver Scan kann einen automatischen kleineren Scan **derselben gültigen Generation** ersetzen. Eine inzwischen ungültige Scan-Generation wird verworfen; bei geänderter Soll-Konfiguration entsteht ein neuer Auftrag.

Scanner, Cache-Aufbau und Kompaktierung müssen kleine, abgeschlossene Arbeitseinheiten und kooperative sichere Unterbrechungspunkte anbieten. Ein großer Scan oder Cache-Lauf darf nicht unter einem globalen `CacheLock` für seine gesamte Dauer laufen. Das bestehende gemeinsame Lock darf **nicht** entfernt werden, bevor Pack-/Index-Konsistenz durch eine nachweisbar sichere Transaktions- und Ressourcenstrategie ersetzt ist.

Nach Unterbrechung: unterbrochene Arbeit entweder als gültiger Auftrag wieder einreihen oder als veraltet ersetzen. Für automatische Event-Bursts ist Zusammenfassung erlaubt; **ein gültiger Zustand darf nie durch verlorene Watcher-Meldungen unerreichbar bleiben**. Reconciliation gegen Sollzustand/Dateisystem dient als Auffangmechanismus. Auftragsstatus darf niemals auf bloßen UI-Flags beruhen.

## 5. Kritischer Ablauf: Entfernen → sofort wieder hinzufügen

1. Quelle `X` ist Generation 17; Scan 17 läuft.
2. Benutzer entfernt `X` aus PCasa: Sollzustand wird Generation 18 / ausgeschlossen. Scan 17 erhält Yield-Anforderung; vor jedem weiteren Commit wird seine Generation geprüft. Die Oberfläche darf den aktuell wirksamen Ausschluss unmittelbar zeigen; Fehler werden gegen die echte Soll-Konfiguration zurückgeführt.
3. Die Katalogentfernung wird unter einer kurzen exklusiven Commit-Grenze durchgeführt. Cache-Bereinigung wird **nur als Bedarf**, nicht als unwiderrufliche Liste später zu löschender Dateien vorgemerkt.
4. Benutzer fügt `X` wieder hinzu: Sollzustand wird Generation 19 / überwacht. Pending Scan 17/Entfernung 18 werden nicht fortgeschrieben. Neuer Scan 19 erhält **manuelle Priorität**.
5. Eine ältere physische Cache-Bereinigung darf nur gegen den *jetzigen* gültigen Katalog bereinigen, unter koordinierter Sperre mit Cache-/Pack-Writern; inzwischen wieder referenzierte Daten bleiben erhalten.
6. Der Monitor zeigt eindeutig `Superseded`, `WaitingForResource`, `Running` und den aktuellen Sollzustand an.

Weitere verpflichtende Konfliktfälle:
- Entfernen während Import/Cache-Aufbau und während eine Datenbanktransaktion läuft
- Sofortiges Wiederhinzufügen vor und während der vorgemerkten Cache-Kompaktierung
- Gleichzeitiges manuelles und automatisches Scannen desselben Unterbaums
- Datei löschen/duplizieren während Scan und Gallery-/Viewer-Ladevorgängen
- Verschachtelte Überwachungswurzeln und widersprechende Unterordner-Ausschlüsse
- UNC-/VPN-Verbindungsverlust während der Aufzählung, vor Commit und nach Reconnect
- Neustart nach persistierter Solländerung, aber vor Abschluss der Arbeit

**Offline** ist niemals gleichbedeutend mit `vom Benutzer gelöscht`. Nicht erreichbare NAS-/VPN-Quellen behalten ihre Katalogeinträge bis zu sicherer Überprüfung oder expliziter Benutzeraktion.

## 6. Einmalige Fachfunktionen und Übergänge

Die bestehenden Fachimplementierungen werden vor der Migration inventarisiert. Ziel sind eindeutige Dienste für `SetFolderMonitoring`, `SetSubtreeExclusion`, `ScanFolder`, `ScanFile`, `DeleteImage`, `DuplicateImage`, `LoadOriginal`, `LoadVisibleThumbnailPage`, `RebuildMissingCache`, `ReclaimCache`. Namen sind Platzhalter, **keine Aufforderung, vorhandene bewährte Funktionen neu zu schreiben**.

UI-Entry-Points reichen Anforderungen ein und reagieren auf Ergebnis-/Zustandsereignisse. Services dürfen nicht direkt neue unabhängige Timer, Warteschlangen und `Task.Run`-Steuerungen eröffnen. Watcher melden Veränderungen, entscheiden aber nicht selbst über die Priorität. Direktes Scrollen, Hit-Testing, Zeichnen, Fensterlayout und bereits dekodierte Bitmaps bleiben im UI-/Galeriebereich.

## 7. Scheduler-Monitor v1: strikt read-only

Separates, nicht modales, größenveränderbares Fenster. Live-Snapshot aus der **gleichen** Scheduler-Zustandsquelle; keine eigene DB-/Netzwerk-Polling-Schleife.

Anzeigen: laufende/wartende/pausierende/unterbrochene Aufträge, Quelle und Pfad, Priorität, Ressourcen, Laufzeit, Fortschritt (falls messbar), Wartegrund, Generation, Supersession, letzte Abschlüsse und Fehler. Statushistorie begrenzt halten; keine unbegrenzte Protokollsammlung im Arbeitsspeicher.

**Keine** Stop-, Pause-, Start-, Prioritäts- oder Löschen-Schaltflächen in Version 1.

## 8. Schrittweise Migration, ohne neue parallele Steuerung

1. **Bestandsaufnahme und Verantwortungsmatrix abschließen.** Alle gegenwärtigen Einstiege, Timer, Locks, Statusvariablen und Workflows zuordnen; explizit den Eigentümer markieren.
2. **Scheduler-Vertrag und Sicherheitsinvarianten** fachlich prüfen. Dann erst Klassen/API-/Persistenzdesign bestimmen; weder Schema noch Pack-Format ohne konkreten Bedarf ändern.
3. **Instrumentierendes, read-only Scheduler-Skelett** und Monitor zunächst ohne Übernahme von konkurrierenden Schreib-/Scanoperationen; darf keine falsche Vollständigkeit suggerieren. Migration innerhalb eines kontrollierten Arbeitszweigs.
4. **Schreibende/konfliktfähige Operationen** und kurze Ressourcengrenzen schrittweise unter Scheduler-Hoheit stellen. Jeder migrierte Einstieg verliert seine bisherige eigene Steuerung **im selben Schritt**; keine dauerhaft parallelen Scheduler.
5. **Scanner, Cache-Arbeiten, Viewer-Originale, sichtbare Thumbnail-Pages** mit nachgewiesener Ressourcenzuteilung integrieren. Zeichnen/Scrollen nicht umbauen.
6. **Ergebnisprüfung** gegen echte Bedienabläufe: insbesondere Entfernen→Hinzufügen, Datei löschen lokal/UNC, Offline/Online, Original statt Preview, laufender Scan, große Galerie. Keine Freigabe allein wegen erfolgreichem Build.

## 9. Nichtziele / Schutzregeln

- Keine TAG-Funktionssanierung in diesem Arbeitspaket.
- Keine Neuentwicklung des bewährten BrowseGallerySurface-Renderers, der UI-Ereignisbehandlung oder des Thumbnail-Pack-Formats.
- Kein sofortiger globaler SQLite-Schemawechsel. Eventuell notwendige dauerhafte Generationen und Änderungsabsichten werden **erst nach Migrationsentwurf** präzise spezifiziert.
- Keine automatischen GitHub-Windows-Testläufe. Workflows bleiben `workflow_dispatch`; gezielte Tests nur bewusst auslösen.
- `main` unangetastet. Weder eine noch nicht abgeschlossene Dateilöschänderung noch ein grüner Build gelten als Funktionsabnahme.

## 10. Offene technische Entscheidungen vor dem ersten Implementierungscommit

1. Persistenz der generationsübergreifenden Quellidentitäten (auch nach Entfernen) und Behandlung von UNC-Aliasen/verschachtelten Wurzeln.
2. Atomarer Commit-Check mit SQLite-Katalogänderungen sowie Paket-/Thumbnail-Schreibgrenzen.
3. Scheduler-Executor-Modell, Ressourcenhierarchie, Schranken für SMB-Aufträge, Fortschritts-/Fairnessregeln.
4. Kompensation bei teilweisen Dateisystem-/Datenbankerfolgen und Neustartwiederaufnahme; keine falsche Behauptung einer global atomaren FS+SQLite-Transaktion.
5. Exakte Abnahmeszenarien und lokal ausführbarer Testmodus ohne fortlaufende CI-Kosten.

**Status:** Die Grundentscheidungen sind beschlossen; die fünf technischen Punkte sind *noch zu entwerfen* und dürfen nicht mit fertiger Implementierung verwechselt werden.


## 11. Operativer Vertrag für physische Dateioperationen (Sanierung 23.10.2026)

Der frühere Konzeptstatus oben beschreibt Ausgangsentscheidungen, nicht mehr
den aktuellen Entwicklungsstand. Die folgenden Regeln sind **verbindliche
Implementierungsinvarianten** im Branch `scheduler-consistency`.

### Ein OperationRequest und ein bestätigter Abschluss

- Der Scheduler besitzt Schlüssel, Priorität, Lifecycle **und Fortschritt**
  derselben manuellen Verschiebung. Das Fortschrittsfenster liest diese
  Fortschrittsdaten; es hält keine eigene Fortschrittsberechnung.
- `Completed` bedeutet: Die fachliche Dateibewegung ist abgeschlossen,
  der Katalogstand wurde bestätigt **und die Galerie hat eine
  Aktualisierung dieses bestätigten Stands übernommen**. Eine physisch
  bewegte Datei ohne Katalogbestätigung ist nicht `Completed`.
- Die Galerie hat **genau eine** koaleszierende Publikationsschleife.
  Zeitgleiche Import-, Filter- und Dateiänderungen dürfen die zuletzt
  bestätigte Katalogänderung nicht dauerhaft aus der Anzeige verdrängen.
  Der existierende Browse-Renderer wird nicht ersetzt.
- Die Fortschrittsanzeige erhält die Phase direkt aus dem Scheduler.
  Ein verdrängter Scan darf die sichtbar laufende Benutzeroperation
  nicht mit einer älteren Bild-/Phasenmeldung überdecken.
- Der Scanner bleibt der einzige Importpfad für **neue** Bilder.
  Unveränderte, katalogisierte Originale behalten beim Verschieben ihre
  Bild-ID, Bildmetadaten und vorhandenen Thumbnail-Pack-Verweise.
  Kopieren/Neuimport kann nach physischer Übertragung eine eigenständige,
  nachgelagerte Scanneroperation auslösen.

### Konsistenzgrenze zwischen Dateisystem und SQLite

Windows-/NAS-Dateisystemoperation und SQLite besitzen **keine gemeinsame
ACID-Transaktion**. Für jede bekannte Original-ID persistiert PCasa
deshalb *vor* der physischen Bewegung eine `file_transfer_intents`-Zeile.
Die bestätigte Katalogverschiebung löscht den Intent **innerhalb
derselben SQLite-Transaktion**, in der Pfade und Ordneridentität
umgeschrieben werden.

Beim Wiederanlauf werden diese Intents unter Scheduler-Hoheit geprüft:

- Quelle vorhanden, Ziel nicht vorhanden: Es gab noch keinen vollständigen
  Umzug; der Intent darf verworfen werden.
- Quelle nicht vorhanden, Ziel vorhanden, beide Eltern erreichbar:
  Die eindeutige Bewegung darf im Katalog nachvollzogen werden.
- Beide vorhanden, beide fehlen, Zielquelle ausgeschlossen oder NAS offline:
  **unaufgelöst** lassen; weder löschen noch stillschweigend überschreiben.
  Benutzerdiagnose und erneute Prüfung sind erforderlich.

Ein fehlgeschlagener Katalog-Commit darf **nicht** ersatzweise die alte
Bild-ID samt Cache löschen und eine neue Bildidentität erzwingen.

### Begrenzung und ehrliche Abnahme

Ein laufender nativer Decoder oder OS-/SMB-Aufruf kann eine sichere
Abbruchgrenze verzögern; der Scheduler meldet bis dahin `YieldRequested`,
nicht `Completed` oder `Canceled`. Es ist technisch falsch, jede
laufende Systemoperation als sofort hart unterbrechbar zu versprechen.

Automatisierte WPF-/SQLite-Tests kontrollieren den Vertrag, beweisen jedoch
keine erfolgreiche Bedienung am NAS. Freigabe setzt eine reale
End-to-End-Abnahme mit gleichzeitigem Import, Topaz-Filter und Verschiebung
nach ORG voraus. **Ein grüner Build allein ist ausdrücklich keine Freigabe.**

## 12. Scheduler-/UI-Eintrittsregeln und offene Abnahmesperren (23.10.2026)

### Bereits implementiert und automatisiert geprüft

- Die Galerie ermittelt fehlende Pfade ausgewählter Bilder über
  `SchedulerWorkKind.CatalogRead`; die WPF-Geste verwendet für vorhandene
  Bilder nur den aktuellen Index. Die Auswahl wird vor einer asynchronen
  Drag-Geste festgehalten und nach dem Warten erneut abgeglichen.
- Drag-over/Drop-Zielmarkierung benutzt eine schreibgeschützte Projektion der
  überwachten Wurzeln und Ausschlüsse. Die Projektion darf nie die
  autoritative Prüfung ersetzen: Der gestartete Scheduler-Auftrag prüft
  Erreichbarkeit und Monitoringstatus erneut. Einfügen fragt nicht vorab
  synchron `Directory.Exists` auf dem Dispatcher ab.
- Source-Probes bleiben im selben `PCasaScheduler`, verfügen jedoch über
  begrenzte, getrennte Kapazitäten für manuelle, interaktive und automatische
  Anforderungen. Zwei im Betriebssystem blockierte automatische SMB-Probes
  dürfen eine neue Benutzeranforderung nicht in ihrer Queue festhalten.
- Die Tests zu diesen drei Verträgen wurden im Windows-Actions-Lauf
  `40017852470` für Commit `e87d13fd2302` erfolgreich ausgeführt.
  Dies ist **keine** reale NAS-/VPN-End-to-End-Abnahme.
- Der Scheduler-Testworkflow wird nur noch über `workflow_dispatch`
  gestartet. Commits starten keinen kostspieligen automatischen Windows-Lauf.

### Verbindliche, noch offene Architekturprüfung

1. **`FolderExclusionService.ChangeAsync`**: `SetExcluded` erfolgt zurzeit
   über ein unabhängiges `Task.Run`; die spätere Katalogbereinigung ist ein
   separater Scheduler-Auftrag mit `UserAction`-Priorität. Das widerspricht
   der einheitlichen Scheduler-Hoheit über manuelle Zustandsänderungen. Vor
   dem Umbau müssen commitfeste Generationen, die unmittelbare optimistische
   UI-Projektion, Unterbrechung zwischen Entscheidung und Bereinigung sowie
   Wiederanlauf nach Neustart zusammenhängend festgelegt werden. Ein
   isoliertes Ersetzen von `Task.Run` durch `EnqueueAsync` ist **keine**
   ausreichende Sanierung.
2. **`RemoveFolderFromPcasaAsync`** liest die überwachte Wurzel noch
   synchron vor der optimistischen Galerieausblendung. Auch dieser Pfad muss
   Teil des einheitlichen manuellen Operationsvertrags werden, damit ein
   SQLite-Wartezustand die Benutzerreaktion nicht verzögert.
3. **`RefreshFolderTree`** führt bei Neuaufbau weiterhin lokale
   Repository-Abfragen auf dem Dispatcher aus. Die zugehörige konsistente
   Projektion darf nicht durch einen separaten Poller oder eine zweite
   fachliche Zustandsquelle ersetzt werden; ihr Aktualisierungsvertrag mit
   Scheduler, Katalog und Ordnerverwaltung ist vor der Migration zu klären.

**Freigabestatus:** keine vollständige Architekturabnahme. Alle offenen
Punkte bleiben gesperrt, bis ihr Codepfad analytisch begründet ist und
anschließend gezielt geprüft wurde. `main` bleibt unverändert.

## 13. Zusammenhängende Sanierung der Ordnerentscheidungen (23.10.2026)

**Stand des Arbeitszweigs:** implementiert und analytisch durchgesehen;
Windows-Kompilierung, gezielte Regressionen und reale NAS-End-to-End-Abnahme
dieser letzten Änderungen **noch nicht ausgeführt**. Dieser Abschnitt ersetzt
die unter `12 beschriebenen damaligen offenen Punkte nicht rückwirkend,
sondern dokumentiert ihre aktuelle Bearbeitung.

### Einheitliche dauerhafte Zustandsgrenze

- `FolderManagerSelectionService.ApplyExclusionSet` ist der **gemeinsame
  Transaktionsweg** für die Exklusionsregeln des Ordnermanagers und
  `ChangeExclusion` für das Kontextmenü. Exklusionssätze, Quellgeneration,
  Katalogbereinigung und vorgemerkte Cachepflege werden innerhalb **eines
  SQLite-Commits** wirksam oder vollständig zurückgerollt.
- `FolderExclusionService.ChangeAsync` benutzt ausschließlich den
  `PCasaScheduler` mit manueller Priorität. Keine `Task.Run`-Umgehung und
  kein nachgelagerter separater Löschauftrag. Die WPF-Publikation erfolgt
  vor `Completed` desselben Scheduler-Auftrags.
- `RemoveFolderFromPcasaAsync` verwendet eine vorhandene UI-Projektion
  für die unmittelbare Ausblendung; die autoritative Prüfung und physisch
  nicht löschende Katalogänderung erfolgen im Scheduler. Auch ein entfernter
  Überwachungsstamm gilt erst nach Galerieübernahme als abgeschlossen.
- Während einer noch ausstehenden Löschung bleibt die vorläufig
  ausgeblendete Teilbaum-Projektion auch bei einem neuen Galerie-/Baumaufbau
  verborgen. Bei Fehlschlag wird der Katalog über den Scheduler neu gelesen
  und die vorläufige Ausblendung aufgehoben.
- Reine TreeView-Layouts fragen nicht mehr SQLite ab. Die Konfiguration
  wird beim Laden und nach einer Ordnermanagerentscheidung einmal über die
  Katalog-Leseexecutor des Schedulers ermittelt. Der Ordnermanager
  bekommt eine schreibgeschützte Ausgangssicht und schreibt erst nach OK.
  Sein manueller Scheduler-Auftrag bleibt bis zur fertigen Galerie-
  und Ordnerbaum-Publikation aktiv; die Einstellungen werden nach dem
  Katalog-Commit unter derselben Scheduler-Hoheit gespeichert.
- »Alle scannen« verwendet die veröffentlichte Quellübersicht zur
  UI-Steuerung; der Scanner validiert selbst seine aktuelle Quelle.
  Der dauerhafte Erstimport liest seine wartenden Ordner über
  `SchedulerWorkKind.CatalogRead` und nicht über eine eigene synchrone
  WPF-Datenbankabfrage.
- Bei Start prüft ein Hintergrundauftrag im Scheduler bestehende
  Exklusionsregeln gegen den lokalen Katalog. Er beseitigt ausschließlich
  **verwaiste Katalogzeilen** aus alten, nicht atomaren Vorgängen, mit
  Generationenschutz und vorgemerkter Cachepflege. Keine Originale werden
  gelöscht; ein offline befindliches NAS wird nicht als gelöscht gewertet.

### Konkrete Prüfungen für die Freigabe

1. SQLite-Trigger bricht die Kataloglöschung nach der Exklusionsänderung
   absichtlich ab: **Regeln, Quellgeneration und Katalog müssen gemeinsam
   zurückrollen**.
2. Simulierter Neustart mit altem Zustand »Ausschluss gespeichert,
   Bilder noch katalogisiert«: Wiederanlauf bereinigt nur diese Einträge,
   erhält Originale, ist idempotent und ermöglicht anschließende
   Wiederaufnahme über den Scanner.
3. Tatsächliches WPF-Kontextmenü während `CacheLock` durch einen
   simulierten Scan belegt ist: Der Ordner verschwindet sofort, bleibt nach
   erneutem TreeView-Aufbau verborgen, und der Scheduler bestätigt
   abschließend Katalog und Galerie.
4. Ordnermanager: Abbruch schreibt nichts; OK speichert vollständige
   Auswahl; erneutes OK ohne Änderungen führt keine erneuten Migrationen aus.
5. Verschieben nach ORG während eines echten, laufenden Netzwerkscans,
   danach Auswahl, Galerie und Fortschrittsanzeige ohne Neustart prüfen.
6. VPN/NAS offline/online, Abbruch während sicherer SQLite-Grenze,
   Überwachungswurzel entfernen/neu hinzufügen sowie verschachtelte
   überwachte Wurzeln gesondert abnehmen.

**Weiterhin offen:** Ein Scheduler-Monitor mit ausführlichen
Ressourcen-/Blockiergründen; einheitliche Fortschrittsfenster-Publikation
auch für Ordnerentscheidungen; eine vollständige Auditierung der
verbleibenden UI-SQLite-Einstiege außerhalb von Ordnerbaum, Dialog,
»Alle scannen« und Erstimport; Überschneidungen mehrerer
Überwachungswurzeln und NAS-/VPN-Abnahme. Bis dahin weder ZIP zur
Benutzerabnahme noch Freigabe/Änderung an `main`.

**CI-Betrieb:** nur manuell durch `workflow_dispatch`. Es wurde durch
diese Sanierung **kein automatischer CI-Testlauf** gestartet.

## 14. Abschließende Konsolidierung und Abnahmekriterien (23.10.2026)

Der aktuelle Entwicklungszweig besitzt folgende **eine Zuständigkeit je Aufgabe**:

- **Dateien kopieren/duplizieren/verschieben:** ausschließlich
  `TransferGalleryFilesAsync`; eine separate Kopier- und
  Scanner-Implementierung im Bildkontextmenü wurde entfernt.
- **Bildpfade und Dateisysteminformationen:** bei fehlender Index-Projektion
  über `SelectedGalleryImagePathsAsync` und die Katalog-Lesespur; Netzwerk-
  Klassifikation und Dateiprobe gehören in die Scheduler-Probes und nicht
  in Tastatur- oder Kontextmenüereignisse.
- **Ordnerauswahl und -entfernung:** ein Scheduler-Auftrag kontrolliert
  SQLite-Transaktion, Quellgeneration, Galerie-/Ordnerbaum-Publikation und
  den sichtbaren Abschluss. Der Ordnermanager blockiert eine zweite
  überlappende OK-Entscheidung bis zum vollständigen Abschluss der ersten.
- **Verschachtelte überwachte Wurzeln:** `FolderExclusionRules.FindOwner`
  entscheidet für Scanner, Dateioperation und Drag-Ziel ausschließlich
  nach der längsten passenden Wurzel. Ein überwachter äußerer Ordner
  hebt keinen Ausschluss der inneren Überwachungswurzel auf.
- **Fortschritt:** `BeginVisibleManualOperation` und
  `EndVisibleManualOperation` sind dieselbe nur lesende Projektion des
  `PCasaScheduler` für Dateiübertragung, Ordnerentfernung und OK im
  Ordnermanager. Mehrere gleichzeitig ausstehende manuelle Aktionen
  können die Anzeige nicht durch den Abschluss eines älteren Auftrags
  versehentlich schließen. Die Präsentationsliste gehört allein zum
  WPF-Dispatcher; Hintergrundbenachrichtigungen werden dorthin
  transportiert.
- **Statusanzeigen:** rein kosmetische Statusmeldungen verwenden die
  bereits veröffentlichte Katalogzählung und öffnen keine eigenständige
  SQLite-Verbindung.

### Prüfungen

Drei auf einen getrennten Prüfbranch begrenzte Windows-Actions-Läufe
(`40021841906`, `40022234062` und `40022689686`) wurden gestartet,
um die architektonischen Änderungen mit realem WPF und SQLite zu prüfen.
Der letzte Lauf deckt bereits die Besitzerregel ab, aber nicht die
anschließend ergänzte gemeinsame Fortschrittspräsentation.
Für die endgültige Windows-Paketabnahme muss ein zusätzlicher
manuell begrenzter Prüfbranch-Lauf den exakten Endstand kompilieren,
die realen WPF-Smokes, die Katalogregressionen und die Paketierung
erfolgreich bestätigen.

**Grenze der maschinellen Prüfung:** Ein Windows-CI-Runner kann das reale
NAS-/VPN-Verhalten oder die Interaktion mit Topaz nicht vollständig
simulieren. Diese Umgebungsabnahme kann nur auf dem Ziel-PC erfolgen.
Der erzeugte Windows-Build bleibt daher ein **Abnahme-/Testpaket**,
keine ungeprüfte Produktionsfreigabe. `main` wird nicht verändert.

Die automatischen GitHub-Tests des Entwicklungsbranches sind weiterhin
deaktiviert. Nur der isolierte Prübranch besitzt einen absichtlich
eingeschränkten Push-Trigger, um seine gezielten Abnahmeläufe auszuführen.


## 15. Statusnachtrag – 29.10.2026: implementierter Scheduler und offene Grenzen

Die frühere Formulierung »Konzept, keine Implementierung« gilt ausschließlich für den historischen Entwurf vom 21.10.2026. Im Entwicklungszweig `scheduler-consistency` sind `PCasaScheduler`, `SchedulerTaskPackage`, `DurablePackageJournal` und die zugehörigen Repository-/WPF-Integrationen implementiert.

### Nachweisbarer Implementierungsstand

- **Eine zentrale Steuerung:** `PCasaScheduler` ist Eigentümer der Queues und begrenzten Arbeits-Lanes; Viewer-/Thumbnail-/Source-Probes verwenden getrennte Kapazitäten *innerhalb derselben* Scheduler-Instanz.
- **Prioritäten:** `ManualAction` (P0), `Interactive`, `UserAction`, `Background` und `Idle`; manuelle Pakete werden an bestätigten Schrittgrenzen unterbrochen und grundsätzlich in umgekehrter Startreihenfolge weiterbearbeitet.
- **Dauerhafte Pakete:** `scheduler_packages` speichert Typ, Payload, Priorität, Einreihung, Cursor und Retry. Wiederherstellung geschieht über registrierte Paketfabriken. Fehlende Wiederherstellungstypen dürfen nicht stillschweigend als erledigt gelten.
- **Tags:** `pending_tag_edits` enthält die offenen Originaldatei-Änderungen. ExifTool darf bei atomaren nativen Schreiboperationen nicht mitten im Ersetzen einer Datei beendet werden; erfolgreiche Schritte werden bestätigt. Bei NAS-Ausfall bleibt der unerledigte Auftrag rekonstruierbar.
- **Ordnermanager, Scans und Dateioperationen:** verwenden den gemeinsamen Scheduler, fachliche Quell-/Generationssperren und bestehende Transaktions-/Abgleichdienste.
- **Status und Vorschau:** dieselbe Scheduler-Fortschrittsprojektion bedient Statuszeile und schwebendes Fenster; Tags zeigen eine Serien-Gesamtzahl sowie Bildname und Pfad. Das Vorschaubild wird lokal aus Pack oder Einzel-Thumbnail geladen, ohne das NAS-Original nur dafür neu zu öffnen.
- **Programmende:** geordneter Scheduler-Abschluss vor dem Beenden von PCasa-eigenen ExifTool-Prozessen; Windows-Job-Object als zusätzliche Crash-Absicherung.

### Tests, noch offene Abnahme

Der [Windows-Workflow](https://github.com/jjs-hamburg/PCasa/actions/workflows/scheduler-consistency-tests.yml) läuft bei Pushes auf `scheduler-consistency` und manuell. [Lauf `40765826666`](https://github.com/jjs-hamburg/PCasa/actions/runs/40765826666) hat Katalog-/EXIF-/Tag-, WPF-, Ordner-, Übertragungs- und lokale Vorschauprüfungen sowie Paketierung erfolgreich abgeschlossen. Einzelne historische Ideen, etwa jede G01–G12-Variante unter allen NAS-/VPN-Ausfallmodi, sind dadurch **nicht automatisch vollständig bewiesen**.

Ein Test-Artefakt `PCasa-win-x64` ist verfügbar; das ist **keine** automatische Produktionsfreigabe. Reale Netzwerkunterbrechungen, echte Großbibliotheken, Drittanwendungen und diverse Monitorkonfigurationen müssen separat abgenommen werden. `main` enthält weiterhin eine ältere Codebasis; diese Aktualisierung dokumentiert den aktiven Entwicklungszweig.
