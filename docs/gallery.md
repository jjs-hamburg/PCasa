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

**Nicht als aktuelle Galerie-Funktion ausgeben:** fertige Bildbearbeitung, GPS-Kartenansicht, externe Plugin-Menüs oder eine abgeschlossene NAS/VPN-Lastabnahme.

Siehe [Architektur](architecture.md), [Anforderungen/Status](requirements.md) und [Sprachverwaltung](localization.md).
