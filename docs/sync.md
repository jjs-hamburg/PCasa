# PCasa – optionale Mehrclient-Synchronisierung (Konzept, nicht implementiert)

**Stand 29.10.2026.** Diese Seite hält eine **geplante Architektur** fest. Die derzeit lauffähige PCasa-Anwendung besitzt einen eigenständigen lokalen Katalog, **aber keine betriebsbereite zentrale Mehrclient-Synchronisierung**. Im aktiven Branch wird der Ordnermanager mit `serverDatabaseConfigured: false` geöffnet; die Server-Sync-Option erscheint daher derzeit nicht als nutzbare Einrichtung.

## Verbindliche Leitlinien für eine spätere Umsetzung

1. **Local first:** Jeder Client muss mit seinem eigenen SQLite-Katalog und lokalen Thumbnail-Dateien ohne Server arbeiten können.
2. **Dateisystem als Wahrheit:** Der Server darf Originalbilder nicht zu Datenbankobjekten machen, keine Bildordner selbst scannen und keine lokale Dateisystemberechtigung umgehen.
3. **Explizites Opt-in:** Nur ausdrücklich zur Synchronisierung freigegebene **Netzwerk-/Serverordner** sollen später teilnehmen; lokale Ordner bleiben lokal.
4. **Kanonische Identität:** Gemeinsame Netzwerkbilder benötigen konsistente UNC-Pfadidentitäten; lokale Laufwerksbuchstaben können zwischen Clients unterschiedlich sein.
5. **Geteilte Daten:** Katalogmetadaten, Tags/GPS und Thumbnails sollen bei Bedarf ausgetauscht werden, **nicht** die Originalbilddateien.
6. **Kein zweiter Jobplaner:** Datenabgleich, Konflikte, Wiederholung und Datei-/Katalogänderungen müssen die bestehende Scheduler-/Generationsarchitektur respektieren.

## Vorgesehene Bedienung (noch nicht verfügbar)

Wenn ein Server eingerichtet ist, soll der Ordnermanager eine Option für die Synchronisierung geeigneter überwachter Netzwerkordner zeigen. Lokale Quellen wären ausgegraut. Die Auswahl eines Netzwerkordners zur Überwachung allein **soll nicht** automatisch einen Serverabgleich aktivieren.

Die ursprünglich vorgesehene manuelle Synchronisierung eines Ordners oder Teilbaums ist ebenfalls noch keine implementierte Funktion. Der aktuelle Befehl zum **manuellen Einlesen/Aktualisieren** ist ein lokaler Dateisystemscan und darf nicht mit Server-Synchronisation verwechselt werden.

## Offene Architekturentscheidungen

- Serverdatenbank, Transport, Authentifizierung, Berechtigungen und Verschlüsselung
- Konfliktmodell bei konkurrierenden Tag-Änderungen, Umbenennungen und Löschungen
- Revisions-/Generationsabgleich zwischen Clients und Wiederanlauf nach langer Offline-Zeit
- Übertragung und Prüfung referenzierter Cache-Packs ohne doppelte Fachzuständigkeiten
- Migrations- und Kompatibilitätsregeln für bestehende lokale Kataloge

**Nicht aus den bereits vorhandenen Schemafeldern oder UI-Platzhaltern ableiten, dass der Abgleich funktioniert.** Die aktuelle Datenhaltung ist unter [`database.md`](database.md) beschrieben.
