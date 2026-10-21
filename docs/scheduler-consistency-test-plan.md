# OperationScheduler – deterministische Konsistenztests (Vorbereitung)

Basis: öffentlicher PCasa-main-Ausgangsstand `bf8e6015e45389a077cfc3e899ff8f621ce8d775`. Dieser Arbeitszweig isoliert die Scheduler-Sanierung von PR #14. Dieses Dokument ist eine **Testspezifikation**, keine Behauptung bereits bestandener Tests.

## Nachgewiesene Ausgangslage
- `FolderExclusionService.ChangeAsync` persistiert die Ausschlussentscheidung, ruft danach den Benachrichtigungs-Callback auf und führt `DetachSubtree` später asynchron aus. Entscheidung und Bereinigung sind nicht dieselbe Transaktion.
- `FolderScanner.ScanDirectory` und `BrowseCacheBuilder.Build` halten `CatalogRemovalService.CacheLock` während ihrer jeweiligen Arbeit. `CatalogRemovalService.Remove` verwendet dasselbe Lock.
- `FolderScanner` schreibt Scanergebnisse bereits während der Aufzählung in mehreren Sessions; die finale Bereinigung besitzt eine eigene Transaktion.
- `CatalogRemovalService.Remove` prüft Cancellation vor Commit. Ein CancellationToken ist aber kein dauerhafter, atomar mit der Ordnerentscheidung geprüfter Generationsnachweis.
- `FolderRepository.Add` und `Remove` verwenden derzeit die Identität der `monitored_folders`-Zeile. Eine entfernte und wieder hinzugefügte Quelle erhält möglicherweise eine neue ID.

## Verbindliche Sicherheitsinvarianten
1. **Sollzustand gewinnt:** Ein Auftrag darf keinen Zustand einer neueren Quellgeneration überschreiben.
2. **Commit-Fence:** Generationsprüfung und jede Katalogänderung erfolgen unter derselben SQLite-Schreibtransaktion; die Prüfung vor einem langen Scan allein genügt nicht.
3. **Quellidentität überlebt Entfernung:** Die Quellgeneration darf nicht an eine löschbare `monitored_folders.id` gebunden sein.
4. **Offline ist kein Löschen:** Netzwerkfehler oder fehlende Erreichbarkeit berechtigen niemals zum Pruning.
5. **Keine globale Dateisystemtransaktion behaupten:** Pack-/Thumbnail-Dateien und SQLite werden durch wiederholbare, referenzgeprüfte Schritte konsistent gehalten.
6. **Wiederaufnahme:** Nach einem Neustart werden gültige Sollzustände reconciliert; alte Aufträge werden nicht blind fortgesetzt.
7. **Keine parallelen Eigentümer:** Jeder migrierte UI-/Watcher-Einstieg nutzt denselben Scheduler; die alte Auftragssteuerung wird im selben Schritt deaktiviert.

## Deterministische Testfälle (mit Gates statt Zeit-Sleeps)

| ID | Ablauf am kontrollierten Haltepunkt | Erwartung |
| --- | --- | --- |
| G01 | Scan G1 pausiert vor Katalog-Commit; Ausschluss G2; Scan fortsetzen | G1 verworfen; ausgeschlossener Unterbaum bleibt katalogfrei |
| G02 | Ausschluss G2 pausiert vor Lösch-Commit; Wiedereinschluss G3; Löschung fortsetzen | G2 verworfen; G3 kann neu einlesen |
| G03 | Stammordner entfernen und sofort identischen kanonischen Pfad hinzufügen | Neue SQLite-Zeilen-ID ändert Quellidentität nicht; alte Löschaufträge verlieren |
| G04 | Cache-Bereinigung pausiert; Quelle wieder hinzufügen und Thumbnail referenzieren | Keine inzwischen referenzierte Cache-Datei löschen |
| G05 | Scan beendet Enumeration; NAS/VPN wird vor Pruning unerreichbar | Keine Kataloglöschung; Quelle als offline markiert |
| G06 | Crash nach Sollzustands-Commit, vor Auftragsausführung | Neustart reconciliert den neuen Sollzustand |
| G07 | Crash nach teilweisem Scan-Commit | Wiederholter Scan ist idempotent; keine alten Ergebnisse nach neuem Generationwechsel |
| G08 | Manueller rekursiver Scan überholt automatischen Teilscan derselben Generation | Keine Doppelarbeit und keine verlorene Watcher-Änderung |
| G09 | Verschachtelte Überwachungswurzeln mit Unterordnerausschluss | Nur tatsächlich betroffene Generation/Teilbäume werden invalidiert |
| G10 | Zwei Pfad-Aliase auf dieselbe UNC-Quelle | Keine ungeprüfte Aliasgleichsetzung; definierte Quellidentität |
| G11 | P0-Bildöffnen während langsamer SMB-Aufzählung und Cache-Kompaktierung | UI-Dispatcher bleibt reaktiv; P0 wartet nicht hinter globalem Langzeitlock |
| G12 | Wiederholte Änderungen während Cache-/SQLite-Operationen | Monitor meldet Queued/Running/Superseded korrekt aus derselben Zustandsquelle |

## Implementierungsreihenfolge
1. Bestehende Tests unverändert als Regression-Baseline erhalten.
2. Injizierbare Gates an **fachlichen Commit-Grenzen** schaffen, nicht durch zufällige Thread-Sleeps.
3. Stabile Quellidentität und dauerhafte Generationen samt SQLite-Migration spezifizieren; Migration muss wiederholbar sein und vorhandene Kataloge bewahren.
4. Commit-Fences zuerst an der Ausschluss-/Bereinigungsgrenze, dann am Scanner und an der Cache-Bereinigung durchsetzen.
5. Erst anschließend Scheduler-Executor, Prioritäten und Monitor anschließen.
6. Bestehende Langzeitlocks nur unter nachgewiesener Pack-/Index-Konsistenz verkürzen.

**Freigabekriterium:** G01–G12 müssen reproduzierbar bestanden sein; zusätzlich sämtliche bisherigen Katalog-/WPF-Tests und manuelle SMB-/VPN-Tests. Keine automatische Release- oder Merge-Freigabe.
