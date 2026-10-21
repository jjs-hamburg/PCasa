# PCasa PR #14 – Architektur-Revision (2026-10-19)

## Fortschrittsnachtrag – 20.10.2026

Dieser Abschnitt beschreibt den **neuen Stand**. Die ursprünglichen
Fehlerbefunde weiter unten dokumentieren den Ausgangszustand der Revision;
sie dürfen nicht als aktuelle Fehlerliste gelesen werden.

| Prüfgebiet | Stand im Branch `picasa-browse-cache` |
| --- | --- |
| A – doppelte Galerie | Alter `GalleryList`-Anzeige- und Ladeweg aus XAML und MainWindow entfernt; `BrowseGallerySurface` ist die einzige Galerie |
| B – Bildcache | Identischer Index behält dekodierte Seiten; verdeckte Ordner erzeugen keine großen zusammenhängenden Cache-Anforderungen |
| C – Galerieposition und Filter | Scrollwiederherstellung nutzt Bildidentität; Tag-Suche sichert ursprünglichen Ordnerbaum-Fokus und Bildanker gemeinsam; Größen-, Abstand- und Sichtbarkeitswechsel nutzen denselben Ankerpfad |
| D – Ordnerstatus | Eine Statuskomponente verwaltet Scan-Abdeckung, löst veraltete Teilbaumfehler nach erfolgreicher rekursiver Prüfung auf und bewahrt neu eintreffende Aufträge |
| E – Menü-/Theme-Darstellung | Ein neuer Windows-WPF-Smoke-Test öffnet das echte MainWindow, misst vier identische Hauptmenüabstände à 20 px, öffnet das Optionen-Popup und prüft Hell/Dunkel/System sowie Tags/Info/GPS. Andere DPI-Skalierungen und optische Abnahme am Ziel-PC bleiben offen |
| F – Aktualisierung und Nebenläufigkeit | Galerie-Refreshes besitzen eine Auftragsgeneration; nur der jüngste laufende Refresh darf nach einem `await` Ergebnisse in die Ansicht übernehmen; Info-/GPS-Seitenleiste prüft ebenfalls die Identität des letzten Leseauftrags |

**Automatisiert bestätigt (20.10.2026):** 42/42 Windows-Katalog- und
Integrationstests, Windows-Build, Windows-Paketierung und die
WPF-Oberflächentests erfolgreich (u. a. 500.000 synthetische
Galerieeinträge, Cache-Wiederverwendung, Katalogbereinigung,
Bildauswahl, Tag-Metadaten und EXIF-Erhalt). Aktueller Quellstand:
`c1e06389`. Prüfläufe:
- https://github.com/jjs-hamburg/PCasa/actions/runs/39494585231 (42/42 Integration)
- https://github.com/jjs-hamburg/PCasa/actions/runs/39494584937 (Build)
- https://github.com/jjs-hamburg/PCasa/actions/runs/39494585014 (Paketierung)
- https://github.com/jjs-hamburg/PCasa/actions/runs/39494585098 (WPF-Smoketest)

Zwei zusätzliche, zuvor unentdeckte Widersprüche sind behoben:
1. Ein Hintergrundscan, bei dem *nur Bilder entfernt* wurden, hat
   zwar den SQLite-Bestand bereinigt, aber keinen Galerie-Refresh
   ausgelöst. Der Scanner meldet jetzt `Removed` zusätzlich zu
   `Updated` und wertet beide als Katalogänderung. Getestet sind
   unveränderte Ordner, einzelne Löschungen und komplett leere Ordner.
2. Nach einem `FileSystemWatcher.Error` hat der Monitor bislang den
   möglicherweise defekten Watcher wiederverwendet. Der betroffene
   Watcher wird jetzt thread-sicher als ungültig markiert und beim
   nächsten Monitor-Refresh ersetzt. Ein injizierbares
   `FileSystemWatcher`-Objekt ermöglicht einen deterministischen
   Regressionstest mit zwei nacheinander ausgelösten Error-Ereignissen.
   Die gemeinsame Watcher-Liste wird dabei ausschließlich vom
   Monitor-Refresh verwaltet.

Der Scanner überprüft nach der vollständigen Aufzählung vor der
Katalogbereinigung erneut die Erreichbarkeit von Stamm- und Scanordner
und gibt den Abbruch-Token bis unmittelbar vor das Festschreiben der
SQLite-Löschtransaktion weiter. Drei neue, reproduzierbare
Regressionstests simulieren einen Abbruch in der finalen
Fortschrittsmeldung, einen Abbruch zu Beginn der Bereinigung und das
Verschwinden des Quellordners *nach* dem Durchlauf. In allen drei
Fällen bleiben die bisherigen Katalogbilder erhalten. Ein
späterer gesunder Scan kann tatsächlich entfernte Dateien weiterhin
bereinigen. Ein vierter Test lässt den überwachten Ordner dreimal
verschwinden und zurückkehren; der `BackgroundFolderMonitor` muss
seinen FileSystemWatcher jeweils neu aufbauen und darf keine
Katalogeinträge verlieren. Testlauf:
https://github.com/jjs-hamburg/PCasa/actions/runs/39493134090

Die Simulation verwendet lokale Testordner und beweist keine
Robustheit unter sämtlichen realen SMB-/VPN-Fehlermodi.

Zusätzlich läuft `.github/workflows/window-smoke.yml` auf einem
**isolierten Windows-GitHub-Runner**. Der erste WPF-Test öffnet das
Hauptfenster, misst die Menüs, prüft die Popup-Platzierungsfunktion,
schaltet die Designs und öffnet/schließt Tags, Info und GPS, während
ein Bild in der Galerie verankert bleibt.

Der zweite WPF-Test erstellt selbstständig **24 echte JPEG-Testbilder**
in zwei temporären Ordnern, importiert sie in einen isolierten Katalog,
setzt unterschiedliche SQLite-Tags und prüft einen vollständigen
Benutzerablauf: bewusste Auswahl eines übergeordneten Ordners,
Scrollposition merken, Tag-Suche ausführen, während der Suche in einen
anderen Ordner wechseln und Suche wieder löschen. Das ursprünglich
gewählte Verzeichnis und die ursprüngliche Bildposition werden
wiederhergestellt. Beide WPF-Durchläufe sind auf dem Quellstand
`d0491954` grün. Testlauf:
https://github.com/jjs-hamburg/PCasa/actions/runs/39491488862

**Noch offen vor Release:** visuelle Abnahme auf dem Ziel-PC für
unterschiedliche DPI-/Monitor-Konfigurationen, Menü-Hover und
Popup-Geometrie an Bildschirmrändern; Belastung mit großem echtem
Bildarchiv und reale SMB-/VPN-Abbruch-/Wiederverbindungstests. Die
synthetischen Tests ersetzen solche Hardware- und Netzwerkbedingungen
nicht. Kein Merge nach `main` und keine Release-Freigabe aufgrund
von CI-Grün allein.
---


## Verbindliche Regel
**Für jede Aufgabe und jeden veränderlichen Zustand genau eine zuständige Implementierung.**
Bestehende funktionierende Logik bleibt Referenz; keine spekulativen Patches, keine Änderungen an `main`, SQLite-Schema oder persistierten Cache-Daten ohne nachgewiesenen Bedarf.

## Geprüfte Basis
- Bestätigter funktionierender Galeriestand: `7cbdab1566`
- Bei Revisionsbeginn: `0c630be5ae1d1ee422d55a9a70c93b7c3c04e9b7`
- Git-Vergleich: 164 nachfolgende Commits, nicht alle betreffen diese Revision.
- Betroffene Dateien: `MainWindow.xaml.cs`, `MainWindow.BrowseActivation.cs`, `MainWindow.BrowseGallery.cs`, `MainWindow.GalleryHousekeeping.cs`, `Controls/BrowseGallerySurface.cs`, `Services/BrowseThumbnailPageCache.cs`, `MainWindow.FolderStatus.cs`, `MainWindow.xaml`.

## Nachgewiesene Architekturprobleme

### A. Zwei Galerie-Implementierungen (hoch)
`MainWindow.xaml.cs` enthält noch `GalleryList`, `_galleryRows`, `RebuildGalleryRows`, `ScrollToPhotoFolder`, `GalleryList_ScrollChanged` und Thumbnail-Loader für alte Zeilen. In `MainWindow.xaml` existiert eine versteckte `GalleryList`. `MainWindow.BrowseActivation.cs` deaktiviert lediglich deren Start-Handler und DataSource. Daneben arbeiten `BrowseSurface`/`BrowseScrollViewer` mit eigenem Index, Abschnittslayout und Scrollpfad.

**Einzige künftige Galerie:** `BrowseGallerySurface` mit eigenem Layout, aber ohne gesonderte zweite Auswahl-, Lade- oder Navigationsimplementierung. Vor Löschungen sämtliche Referenzen auf Legacy-Felder/Methode prüfen; keine Massendeletion auf Verdacht.

### B. Thumbnail-Pages werden bei Index-Refresh vollständig verworfen (hoch)
`MainWindow.BrowseGallery.cs` ruft bei `RefreshBrowseGalleryAsync(forceIndexReload:true)` die Indexabfrage und `_browsePageCache.SetIndex(index)` auf. `BrowseThumbnailPageCache.SetIndex` erhöht unconditionally die Generation, cancelt alle Pages, verwirft alle Bitmaps. Erzwungene Refreshes werden auch bei kleinen Import- und Hintergrundänderungen angestoßen. **Nachgewiesener unnötiger Cache-Reset**; nicht mit dauerhaft beschädigten Cache-Dateien verwechseln.

**Eine Instanz entscheidet über Indexversion/Generation.** Nur neu initialisieren, wenn sich Einträge/Tier tatsächlich geändert haben; zuerst Korrektheit und Nebenläufigkeit testen, dann optimieren.

### C. Layout-/Filter-/Scroll-Zustände konkurrieren (hoch)
`BrowseGallerySurface` hält `_index`, `_sections`, `_hiddenFolderIds`, `_spacingPreviewColumns`, `_cellGap`, `_rowGap`; `GalleryHousekeeping` hält außerdem `_forcedVisiblePhotoFolderId`. Scrollposition und aktiver Ordner liegen in `MainWindow`. Galerie-Sichtbarkeit wird durch TreeView-Expansion und Sonderausnahmen bestimmt.

**Ein Layoutmodell** liefert Spaltenzahl, Zellgeometrie, Gesamt-/Abschnittshöhen, Hit-Testing und Scrollziele. Sichtbarkeitsfilter vor Aufbau der Layoutabschnitte zentral bestimmen. Während Live-Vorschau nur eine kontrollierte Layoutänderung, keine Rekursion aus Folder-Refresh/Scroll-Ereignissen.

### D. Anzeigestatus für Ordner ist flüchtig und aggregiert veraltete Fehler (mittel/hoch)
`MainWindow.FolderStatus.cs` hält `_folderActivity` und `_folderStatusAggregate` zusätzlich zur tatsächlichen Aufgabenwarteschlange. Aggregation gewichtet `Failed` dauerhaft höher als `Ready`; ein neuer erfolgreicher Check beseitigt daher den alten Fehler eines Unterordners/Überordners nicht zwingend. Screenshot zeigt Warnzeichen bei zugänglichen lokalen Bildern; genaue Ereignisfolge nicht instrumentiert.

**Eine Statusquelle** ist der aktuelle Aufgaben-/Verfügbarkeitszustand, kein eigenständiges Verlaufsaggregat. Fehler werden an konkrete Aufträge/Ordner gebunden und durch erfolgreichen Check derselben Einheit aufgelöst.

### E. Menü-Template ohne klare Maßregel (mittel)
`MainWindow.xaml` verwendet eigens gebaute lokale `MenuItem`-Templates mit `TopLevelHeader`-Triggern und separat gesetzten Margin-/Padding-Werten. Mehrere Commits veränderten diese Werte. **Eine Template-Regel:** Beschriftungsbreite plus einheitlicher interner Abstand für alle Top-Level-Items; Drop-down-Menüs bleiben unabhängig davon.

### F. Aktualisierung bindet mehrere Operationen zusammen (hoch)
`RefreshBrowseGalleryAsync`: Ordner erneut lesen, Baum rekonstruieren, ggf. Cache-Index vollständig neu laden, `BrowseSurface.SetData`, Scroll-/Viewport-Restore, optional Prefetch. Dadurch sind selektive Import-, Ordner- und Thumbnail-Aktualisierungen nicht isoliert.

**Eine Update-Koordination:** Katalogbestand, FolderTree-Modell, Browse-Index, Cache-Pages, Layout und Auswahl als getrennt versionierte Teilzustände; nur tatsächlich geänderte Teile aktualisieren. Keine zweite Galerie-Implementation.

## Reihenfolge der Sanierung
1. **Reproduzierbare Invarianten & Tests zuerst:** Layout enthält jeden nicht ausgeblendeten Bildindex genau einmal; Abschnittshöhen monoton; `GetFolderOffset` für jeden sichtbaren Ordner; identischer Render-/HitTest-Pfad; letztes Bild erreichbar; gleicher Index => kein Page-Reset. Testfälle für 1–500.000 Bilder, wechselnde Hoch-/Querformate, Collapse/Expand, Größenänderung, Zwischenimport.
2. **Aktive Galerie konsolidieren:** Layoutmodell, Filter und Navigation mit eindeutigen Eigentümern. Keine neuen Sonderfälle `_forcedVisible...` als Dauerlösung.
3. **Cache-Lebensdauer entkoppeln:** Indexdelta und Page-Generation statt globalem Cache-Abwurf nach jedem Refresh; auf laufende Ladeaufträge achten.
4. **Ordnerstatus aus Aufgabenstatus ableiten:** veraltete Fehler gezielt beenden; offline niemals als Löschen interpretieren.
5. **Versteckte Legacy-Galerie entfernen:** erst nach Referenzanalyse und Tests. Nur der alte Anzeige- und Ladepfad darf weg, nicht Import oder Katalog.
6. **Einheitliche Menüs/Styles:** ein Template, quantitative Abstandsprüfung, drei Themes.
7. **Windows-Laufzeittest:** mehrere Auflösungen/DPI, großes Netzwerkarchiv, gleichzeitiger Import; CI-Grün allein genügt nicht.

## Nicht anfassen ohne separaten belegten Befund
- Schema und vorhandene SQLite-Daten in `%LocalAppData%\\PCasa`
- Pack-Dateiformat und existente Thumbnails
- Stabiler Scan-/Importpfad und SMB-Sicherungsregeln
- Viewer-Bildverarbeitung und unbeeinflusste Funktionen

## Abnahmekriterien
- Keine fehlenden Ordnerabschnitte oder vorzeitig endenden Scrollbereiche
- Gleichmäßig verteilte Zwischen- und Randlücken, ohne Bildüberlappung
- Bildklick zeigt zutreffenden Ordner im Baum, ohne Galerie wegzuscrollen
- Keine globale Thumbnail-Page-Invalidierung bei unverändertem Index
- Fehler-Status verschwindet nach erfolgreicher Prüfung; lokale Erreichbarkeit ohne Fehlalarm
- Hell/Dunkel/System: alle Dialogtexte und Hover lesbar
- Hauptmenü: messbar gleicher Leerraum zwischen benachbarten Beschriftungen
- Vergleich gegen stabilen Galeriestand und manuelle Windows-Sichtprüfung
