# PCasa – Galerie und Bedienung

**Stand 29.10.2026 · `scheduler-consistency`.** Diese Beschreibung trennt aktuelle Bedienung von geplanten Funktionen.

## Galerie und Ordner

Die Ordnerliste links steuert die **eine durchgehend vertikal scrollbare Galerie** rechts. Bildordner erscheinen als Abschnitte; am Ende eines Ordners wird direkt in den nächsten gescrollt. Katalog und Bilder sind lokal indiziert; die Galerie virtualisiert den sichtbaren Bereich, statt für jedes Bild ein WPF-Steuerelement anzulegen.

- Klick auf einen Ordner navigiert zu dessen Galerieabschnitt; beim Scrollen über Ordnergrenzen kann die Baum-Markierung folgen.
- Auf-/Zuklappen im Baum ist zunächst eine Ansichtsentscheidung. Über **Optionen → Eingeklappte Ordner in Galerie ausblenden** kann die Galerie eingeklappte Unterbäume überspringen.
- **Alle Ordner auf-/zuklappen** und **Expand/Compress** wirken auf die Ordneransicht; der Explorer-Ansichts-Befehl ist auf den aktuellen Ausgangsordner bezogen.
- Im Ordnermanager werden überwachte Wurzeln und Unterordner-Ausschlüsse gewählt. Änderungen werden mit **OK** bestätigt, **Abbruch** verwirft die Auswahl. Ein manueller Scan ist eine priorisierte Scheduler-Aktion.
- Das Kontextmenü eines Ordners bietet Einlesen/Aktualisieren, Info, aus PCasa entfernen, Unterordner anlegen, Explorer und **separat** das physische Löschen. **Aus PCasa entfernen löscht keine Originaldateien.**

## Suche und Sortierung

Die Suchzeile steht dauerhaft getrennt unter dem Hauptmenü; ihre Suchgruppe ist rechtsbündig. Das Sortierfeld befindet sich links neben **WAS?**.

- **Sortierung pro Ordner:** Name (Standard), Aufnahmedatum, Erstellungsdatum, Änderungsdatum, Größe, Dimensionen; jeweils auf- oder absteigend. Die LAP-artige Pfeilgrafik zeigt die vereinbarte Richtung. Das gewählte Kriterium und der Tooltip wechseln mit der Sprache.
- **WAS?** schaltet zwischen Tags und Dateinamen.
- **WO?** bietet Alle, Ab hier (ausgewählter Teilbaum) und Ordner (aktueller Ordner ohne Unterordner).
- **NOT** kehrt das Suchergebnis um.
- **Galerieansicht** begrenzt bei Bedarf die Suche auf gerade in der Galerie sichtbare Ordner. **Ohne** diese Option werden auch eingeklappte Unterordner bei globaler Suche berücksichtigt.
- Mehrere Tag-Suchbegriffe lassen sich mit Kommas oder Semikolons kombinieren; die Suchlogik arbeitet auf dem lokalen Index.

## Auswahl und Dateioperationen

- Einfacher Klick markiert ein Bild; **Strg-Klick** fügt hinzu oder entfernt; **Shift-Klick** wählt den Bereich; **Strg+Shift-Klick** ergänzt einen Bereich.
- **Strg+A** wählt Bilder des aktuell ausgewählten Ordners, nicht sämtliche Abschnitte der durchscrollbaren Galerie.
- Bilder lassen sich auf Ordner im Baum oder geeignete Ziele ziehen. Das Ziel wird beim Darüberziehen markiert. Kopieren und Verschieben laufen durch die bestehenden priorisierten, wiederaufnehmbaren Dateioperationspfade; Namenskonflikte werden ausdrücklich abgefragt.
- Externe Explorer-Dateiaktionen erfordern anschließende Abgleichlogik; ein fehlendes NAS-Laufwerk wird nicht als bestätigtes Löschen behandelt.

## GPS-Arbeitsbereich

Der GPS-Bereich ist dieselbe rechte Seitenleiste wie Tags und Bildinformationen, kein eigenes Fenster. Die Breiten von Ordnerliste, Galerie und rechter Leiste werden relativ zum verfügbaren Platz verwaltet und im bestehenden `appearance.json` gespeichert. Beide Trennlinien lassen sich ziehen.

- **GPS vergrößern** erweitert die Leiste nach links und blendet die Ordnerliste vorübergehend aus. Beim Zurückschalten erscheint die Ordnerliste nur dann wieder, wenn sie vorher sichtbar war.
- Über das Ordnersymbol unten kann die Ordnerleiste unabhängig ein- oder ausgeblendet werden.
- **GPS-Vollbild** lässt die rechte Leiste innerhalb der PCasa-Arbeitsfläche alles andere überdecken; nochmals klicken oder **Esc** stellt die vorherige Aufteilung wieder her.
- Der letzte Seitenleistenmodus, die vergrößerte GPS-Ansicht und die relativen Breiten bleiben erhalten. Der kurzfristige Vollbildzustand wird nicht als Fensterstartmodus gespeichert.
- Galerieauswahl und Galeriescrollposition werden beim Wechsel der Aufteilung nicht bewusst verändert.

Die aktuelle GPS-Anzeige enthält weiterhin die gespeicherten Koordinaten und die Höhe. Eine interaktive Karte und ihre Verknüpfung mit der Bildauswahl werden getrennt umgesetzt.

## Tag-Bearbeitung über mehrere Bildauswahlen

Im Tag-Editor werden neue oder entfernte Tags zunächst **pro Bild-ID vorgemerkt**, ohne sofort SQLite oder die Originaldateien zu ändern. Beim Wechsel der Auswahl oder des Ordners und auch bei leerer Auswahl bleibt die Sitzung erhalten.

- Die Tag-Liste zeigt den wirksamen Zustand der **aktuell ausgewählten** Bilder einschließlich ihrer noch vorgemerkten Änderungen.
- Über OK/Abbruch erscheint die Gesamtzahl der betroffenen Bilder und vorgemerkten Tag-Änderungen.
- **OK** übernimmt alle Änderungen mit **einem** dauerhaft gespeicherten Scheduler-Paket. Die verschiedenen Tag-Intentionen werden in **einer** SQLite-Transaktion übernommen.
- **Abbruch** verwirft die gesamte noch unbestätigte Sitzung. Beim Programmende fragt PCasa vor dem Verwerfen nach.
- Der Wiederanlauf unterstützt neue bildbezogene `tag-edit-v2`-Pakete und bisherige `tag-edit-v1`-Pakete.

## Bilder, Ansicht und Status

- Regler für Thumbnailgröße und -abstand; optionale Dateinamen (auch fett), Thumbnail-Schatten, GPS-/Tag-/RAW-Symbole.
- Design Hell, Dunkel oder System; optional eigene JPG-Hintergrundtexturen aus dem Ordner `Texturen` neben der EXE.
- Bildbetrachter mit Navigation, Vollbild, Zoom/Fit und Panning. RAW-Dateien können betrachtet werden; die spätere **RAW-Bearbeitung** ist ausdrücklich offen.
- Ordnerstatus-Modi: Immer anzeigen, nur Aktivität/Probleme, nie anzeigen. Statussymbole erläutert die Ordnerstatus-Legende; Farben sind nicht das einzige Unterscheidungsmerkmal.
- Die Statusleiste und das schmale Fortschrittsfenster zeigen dieselbe Scheduler-Arbeit. Bei Tag-Schreibjobs erscheint der Dateiname, der Pfad und nach Möglichkeit ein **lokal** gecachtes Vorschaubild; nicht erreichbare Originaldateien werden für die Vorschau nicht extra gelesen.

## GPS-Karte und Kartenanbieter

Die GPS-Seitenleiste enthält eine eingebettete interaktive Karte auf Basis von **MapLibre GL JS** (BSD-3-Clause) und der quelloffenen **CefSharp/CEF**-Einbettung. Die Karte ist in derselben WPF-Arbeitsfläche wie Galerie/Ordnerleiste; der integrierte Browser wird erst beim Öffnen von GPS initialisiert. OpenFreeMap Liberty ist Standard, Positron und Bright sind weitere freie Stile. Die vollständigen Daten und Vorschaubilder bleiben auf dem PC; die Kartenserver erhalten nur Kachelanfragen.

- Die Kartenquelle wird über einen kompakten **„Karte“-Button** mit Aufklappmenü gewechselt, nicht über ein breites Eingabefeld. Eigene Anbieter sind als **MapLibre-Style-JSON-URL** oder **XYZ-Rasterkacheln** mit HTTPS konfigurierbar; mehrere Profile sind möglich.
- Eigene API-Schlüssel werden nur durch den Nutzer eingegeben und mit Windows-DPAPI verschlüsselt im vorhandenen `appearance.json` gespeichert; der Schlüssel gelangt zur Laufzeit zwangsläufig als Anmeldeinformation an den ausdrücklich ausgewählten Kartendienst.
- Die Karte zeigt GPS-Marker aus dem lokalen SQLite-Katalog. Ausgewählte Fotos behalten eigene, immer sichtbare Markierungen oberhalb der gruppierten Punkte des aktuellen Ordners (Clustering). Beim Galerie-Auswahlwechsel werden die Kartenauswahl und eine bereits geöffnete Fotovorschau mitgeführt. Pro Ansicht ist diese erste Implementierung auf **5.000 Marker** begrenzt. Eine weltweite Gesamtdarstellung aller katalogisierten Bilder mit räumlicher Datenabfrage bleibt Ausbauarbeit.
- Klick auf einen Marker zeigt Namen und, sofern bereits im lokalen Seiten-Cache, ein Thumbnail. Ist das Foto in der aktuellen Galerieansicht, wird es ausgewählt und kann geöffnet werden.
- Ein optionaler Filmstreifen unter der Karte zeigt die **ausgewählten GPS-Fotos** in Galeriereihenfolge, mit lokal gepackten Thumbnails. Sein Schalter ist **nur ein Filmstreifen-Icon in derselben Kopfzeile wie der Kartenanbieter**, kein eigenes Zeilenfeld. **Einfachklick hebt nur den Kartenpunkt hervor, ohne den Kartenausschnitt zu verändern**. **Doppelklick zeigt das Bild groß im GALERIEBEREICH neben der unverdeckten Karte**. Bei bereits geöffneter Bildanzeige wechselt **Einfachklick auf ein anderes Filmstreifenbild** nur das Foto und den hervorgehobenen Punkt; die Mehrfachauswahl bleibt bestehen. Ein Klick auf die große Bildanzeige stellt die Galerie-Thumbnails wieder her. Wird das Bild aus dem GPS-Vollbild geöffnet, kehrt PCasa zuvor zur geteilten Galerie/Kartenansicht zurück, damit beide sichtbar sind. Die Anzeige ist auf 160 Fotos pro Auswahl begrenzt; der Zustand wird in der Erscheinungsbildkonfiguration gespeichert. Die doppelte Copyright-Textzeile unter der Karte entfällt; die von MapLibre bereitgestellte vollständige Quellenangabe bleibt über das Info-Symbol verfügbar.
- **Filmstreifen-Optik:** eine durchgehende dunkel-anthrazitfarbene Bahn mit regelmäßigen rechteckigen **Transportlöchern oben und unten**. Thumbnails sitzen in ruhigen dunklen Rahmen. Das aktive Foto erhält nur einen helleren, dickeren Rahmen und leicht helleren Hintergrund, niemals den standardmäßigen blauen WPF-Listenblock. Hover ist schwächer als die Auswahl. Die Markierung ist auch ohne Farbdifferenzierung sichtbar. Die Perforation ist unabhängig von der Thumbnail-Virtualisierung und scrollt nicht pro Einzelbild; WPF-Templates ändern nur die Optik, nicht die Filmstreifen-Bedienung.
- „Google Maps“ ist ein **experimenteller Webmodus** im selben CefSharp-Browserfenster, ohne Google-Cloud-Projekt oder API-Schlüssel. Der Standardaufruf verwendet die **Koordinatensuche mit einer Ortsmarkierung**, weil die reine Google-Kartenansicht in der schmalen eingebetteten Webansicht große Vorschlags-, Wetter- und Home/Work-Fenster über der Karte eingeblendet hat. Die Google-Seitenleiste lässt sich nicht zuverlässig per dokumentiertem URL-Parameter unterdrücken. Google-Maps-URLs unterstützen keine beliebige Liste von Fotokoordinaten als Markierungen: Bei Mehrfachauswahl wird automatisch zur **PCasa-Fotokarte** mit allen Markern zurückgeschaltet und die Google-Schaltfläche ist im Mehrfachmodus deaktiviert. Google kann den normalen Webzugang innerhalb eines eingebetteten Browsers einschränken; das ist keine offizielle Embed-API.
- Im GPS-Vollbild öffnet ein Klick auf einen Kartenmarker oder ein Doppelklick auf ein Filmstreifenbild ein großes Foto **über** der Karte. Ein einfacher Klick auf das Foto schließt diese Anzeige und stellt dasselbe Karten-Vollbild wieder her. Der Bilddecoder und sämtliche Originaldateizugriffe laufen ausschließlich über den bestehenden Scheduler (ViewerRead).
- Attribution für OpenFreeMap/OpenMapTiles/OpenStreetMap oder eigene Anbieter bleibt sichtbar. Die JavaScript-Bibliothek wird derzeit in einer fixierten Version über ein öffentliches CDN geladen; Karte und Kacheln benötigen deshalb Internetzugang. Eine vollständig lokal gebündelte JS-Auslieferung ist noch offen.

**Nicht als aktuelle Galerie-Funktion ausgeben:** fertige Bildbearbeitung, GPS-Kartenansicht, externe Plugin-Menüs oder eine abgeschlossene NAS/VPN-Lastabnahme.

Siehe [Architektur](architecture.md), [Anforderungen/Status](requirements.md) und [Sprachverwaltung](localization.md).
