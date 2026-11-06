# PCasa – verbindlicher Scheduler-Vertrag

Dieser Vertrag beschreibt **die einzige fachliche Ablaufregel**. Eine
Ausführungsspur/Worker ist lediglich technische Kapazität und hat keine eigene
Prioritätenordnung. Der Scheduler darf nicht mehrere konkurrierende
Startentscheidungen auf unterschiedliche Auftragstypen verteilen.

## 1. Ein Auftrag = ein Paket

Jeder fachliche Vorgang (GPS/Tags, Import, manuelles Scannen, Backup, Transfer,
Watcher-Folgearbeit) wird unter **einer Auftrags-ID** angenommen. Alle
notwendigen Schritte (Original, Prüfung, Katalog, Anzeige) gehören zum
selben Paket. SQLite-Journal speichert Typ, Nutzdaten, Fortschritt, Position
und Status. Der Taskmanager besitzt keine eigene Warteschlange.

## 2. Eine globale Liste

Bei Annahme erhält jedes Paket seine Standardpriorität.
`P0 = manuell`, `P1 = interaktiv`, `P2 = Benutzerfolgearbeit`,
`P3 = Hintergrund`, `P4 = ausschließlich Leerlauf`.

Innerhalb P0 gilt **LIFO**: das jüngste manuelle Paket steht vorne.
Eine ausdrücklich im Taskmanager veränderte Position/Priorität gilt
verbindlich, auch nach Neustart.

Die eine Rangfolge lautet:
`(effektive Priorität, gespeicherte Reihenfolge, Auftrags-ID)`.
`PCasaScheduler.Precedes` definiert den Vergleich. Dieser Vergleich
wird bei Aufnahme, Benutzeränderung und Startfreigabe verwendet.

## 3. Eine Ablaufsteuerung

Auf jede Aufnahme, Prioritätsänderung, Unterbrechung, Beendigung,
Stornierung oder Wiederzulassung folgt eine Neubewertung der globalen Liste.
Es werden keine unabhängigen Worker-Prioritäten verwendet.

- Ein neuer P0-Auftrag verdrängt ältere manuelle Pakete gemäß LIFO.
- Die Ausführung wird **sofort angestoßen**. Bereits begonnene atomare
  Dateischritte werden nicht unsicher abgebrochen.
- Der Worker meldet den nächsten sicheren Unterbrechungspunkt und bewahrt
  Paket-ID und bereits bestätigten Cursor.
- Nach der Unterbrechung läuft das höchste ausführbare Paket; unterbrochene
  Pakete kehren gemäß ihrer gespeicherten Position zurück.
- Blockierte NAS-I/O darf andere, unabhängige lokale manuelle Aufträge nicht
  am Start hindern. Die Worker-Kapazitäten sind nur dafür getrennt.

`PCasaScheduler.SelectNextJobUnsafe` ist die zentrale Freigabestelle.
`WakePendingWorkersUnsafe` signalisiert nach Zustandsübergängen eine
Neubewertung, **ohne** einen zweiten Auftragsbestand aufzubauen.

## 4. P4 – keine Ausnahme

`Verify catalog` hat ausschließlich P4. Es darf **nur** starten oder
fortsetzen, wenn kein Auftrag P0–P3 läuft oder wartet. Dazu zählen
ausdrücklich auch als `deferred` gespeicherte NAS-Pakete, solange sie im
Taskmanager als `Wartet` erscheinen. Die bisherige Ausnahme für offline
zurückgestellte Aufgaben widersprach der Benutzervereinbarung.

Konsequenz: Ein dauernd wartender Offline-Auftrag verhindert P4.
P4 kann erst wieder starten, wenn der höher priorisierte Auftrag erledigt,
entfernt oder ausdrücklich in einen Zustand außerhalb der wartenden Liste
versetzt wurde. Eine solche Zustandsänderung darf nicht stillschweigend
behauptet werden.

## 5. Ein Taskmanager

Der Taskmanager spiegelt Paketidentität, effektive Reihenfolge und Zustand
aus dem Scheduler. Jede Verschiebung ist ein **Steuerbefehl an denselben
Scheduler**, keine Sortierung einer unabhängigen UI-Liste.

## 6. Abnahme

Die CI muss mindestens nachweisen:

1. P0-LIFO über unterschiedliche Ausführungs-Worker.
2. P0 startet trotz blockiertem SMB-Scan; atomare Schritte bleiben sicher.
3. Benutzerreihenfolge wirkt auf tatsächlichen Start und über Neustart.
4. P4 läuft nur bei absolut leerer P0–P3-Liste, einschließlich `deferred`.
5. Die nächste freigebbare Arbeit startet nach Abschluss/Abbruch ohne
   verlorene Weckmeldungen.
6. GPS/Tags und ihre selbst erzeugten Dateiänderungen sind ein Paket.
7. Verzeichnis-Marker und Paket-Cursor überleben Unterbrechung und Neustart.

**Kein Build gilt als abgenommen, solange die Tests nicht bestanden sind.**
Die reale NAS-/VPN-Abnahme bleibt zusätzlich erforderlich.
