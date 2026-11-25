# PCasa – Scheduler-Vertrag Alpha.63

**Stand 25.11.2026.** Ersetzt den früheren P0–P4-Vertrag.

| Fachliche Priorität | Aufträge |
| --- | --- |
| P0 | Manuelle Mehrfachaufträge und Einlesen (auch Erstimport), LIFO für neue manuelle Aufträge |
| P1 | Automatische Bibliotheksaktualisierung, erkannte Dateisystemänderungen |
| P2 | Kontinuierlicher Hintergrundcheck und Leerlauf-/Precache-Arbeit |

ManualAction=0, Background=3 und Idle=4 sind die kompatiblen internen Enum-Werte. Interactive und UserAction bleiben technische Werte, keine zusätzlichen fachlichen Taskmanager-Klassen.

Eine fachliche Anforderung besitzt eine Identität. Gespeicherte Rangfolge und Taskmanager-Position steuern den Start. Worker-Kapazitäten sind technische Ressourcen, kein unabhängiger Auftragsbestand. Sichere Unterbrechungspunkte schützen atomare Dateischritte und bestätigte Katalog-Commits.

Einleseaufträge und angenommene Mehrfachaufträge behalten Identität, Rang und bestätigten Fortschritt über Neustart. Gewöhnliche Pausen werden beim Start aufgehoben; die globale Pause des kontinuierlichen Checks bleibt erhalten. Ordnerfortschritt bleibt beim Löschen eines Einleseauftrags bestehen.

Einzelbildaktionen und Datenbankwartung liegen außerhalb der fachlichen Warteschlange. Mehrfach-Schreibvorgänge werden am bestätigten Dateischritt geparkt und nach Neustart fortgesetzt. Der Beenden-Dialog prüft nur nicht fortsetzbare Einzelvorgänge und ungespeicherte Änderungen; angenommene persistente Pakete benötigen keine Bestätigung. Bereits begonnene atomare Schritte müssen sicher abgeschlossen werden. Recovery-Receipts und unbestätigte Tag-Projektionen bleiben zur Integrität erhalten.

Genau eine permanente Bestandsprüfungs-Zeile: Pause/Fortsetzen, keine Löschung oder Rangänderung. Erreichbare, vollständig eingelesene Wurzeln werden nach dauerhafter Prüfsequenz ausgewählt. Keine starre Altersfrist. Höhere ausführbare Arbeit hat Vorrang. Nicht erreichbare Quellen dürfen andere erreichbare Quellen nicht dauerhaft blockieren. Die Inventur dekodiert keine Bildpixel und erzeugt keine Thumbnails, sondern übergibt erkannte Änderungen an die reguläre Bibliotheksaktualisierung.

Abnahme: Identität, LIFO, reale Taskmanager-Reihenfolge, Unterbrechung, Katalogfortschritt nach Joblöschung, Neustart-Pausen, faire Inventur, Offline-Schutz und Beenden mit offenen Schreibvorgängen. Zusätzlich realer NAS-/VPN-Praxistest.
