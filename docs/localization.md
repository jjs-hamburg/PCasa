# PCasa – zentrale Sprachverwaltung Deutsch / Englisch

**Stand 29.10.2026 · implementiert in `scheduler-consistency`.** Deutsch (`de`) ist der Ausgangskatalog; Englisch (`en`) verwendet die Datei `src/PCasa.App/Resources/english.json`, die in die Anwendung eingebettet wird.

## Eine Quelle, ein Übersetzungsweg

`src/PCasa.App/Services/UiLanguage.cs` ist der **einzige Eigentümer** des Sprachzustands, der Katalogsuche und der Änderungsbenachrichtigung. Es dürfen keine eigenständigen Wörterbücher oder Sprachumschalter in Galerie, Viewer, Tag-Editor, Fortschrittsdialog oder Ordnermanager eingeführt werden.

- WPF/XAML benutzt `{loc:Tr Key='Ordner'}`. Die Markup-Extension bindet an `UiLanguage.Revision` und aktualisiert offene Fenster nach Sprachwechsel.
- Zur Laufzeit generierte C#-Texte verwenden `UiLanguage.T("...")` bzw. `UiLanguage.Format("...", args)`.
- **Laufzeitwerte**, die im Code gesetzt werden, müssen bei Sprachänderung erneut publiziert werden. Beispiel: `MainWindow.GallerySorting.cs` aktualisiert das ausgewählte Sortiermerkmal und den Tooltip über `UiLanguage`, statt ein zuvor übersetztes XAML-Feld wieder mit deutschem Text zu überschreiben.
- Unbekannte Übersetzungsschlüssel fallen auf den deutschen Originaltext zurück und dürfen niemals als leere Beschriftung verschwinden.

## Was ausdrücklich nicht übersetzt wird

Benutzer-Tags, Bilddateinamen, Ordner- und UNC-Pfade, EXIF-Nutzdaten, SQLite-Werte, Scheduler-Paket-IDs und andere maschinenlesbare Identifikatoren sind **Daten**, keine UI-Texte. Platzhalter mit benutzergegebenen Werten dürfen erst **nach** der Übersetzung des Formatstrings eingefügt werden.

## Sprachwechsel und Persistenz

**Optionen → Sprache → Deutsch / English** schaltet ohne Programmneustart um. Der Sprachcode liegt im vorhandenen `%LOCALAPPDATA%\PCasa\appearance.json` als `AppearanceState.Language`. Alte Profile ohne diesen Eintrag starten auf Deutsch. Eine separate Spracheinstellungsdatei ist nicht vorgesehen.

Neue sichtbare Beschriftungen gehören zentral in die deutsche Ausgangsschreibweise und, falls abweichend, in den englischen Katalog. Nicht jeder denkbare Laufzeit-/Fehlertext ist bereits lückenlos übersetzt; unvollständige Katalogeinträge sind durch den deutschen Fallback sichtbar.

## Tests und Qualitätsgrenze

- Katalogtest: Sprachwechsel, Formatstrings, unbekannte Tags/Pfade.
- Echter Windows-WPF-Test: Menü, Suchleiste und **Sortierfeld inklusive ausgewähltem Kriterium, Tooltip und Einträgen** wechseln de → en → de; Spracheinstellung bleibt gespeichert.
- Bestehende EXIF-, Tag-, Scheduler-, Ordner- und Dateitestfälle bleiben unverändert gültig.

Die Lokalisierung ist **ausschließlich Darstellung**. Sie darf weder Schedulerprioritäten, Dateipfade, Tag-Daten, Datenbank-Schema noch Galerie-Layout verändern.
