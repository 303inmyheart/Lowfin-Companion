# Lowfin Companion 0.7.0

**Lowfin-eigener Code, Dokumentation und Gestaltung wurden zu 100 % mit KI erstellt. Zusätzlich enthält diese Version angepasste Discord-Cover-Komponenten.**

> **Persönlich getestet:** Als Projektbetreiber habe ich alle Funktionen selbst ausgiebig getestet.

<img src="assets/lowfin-companion.png" alt="Lowfin Companion" width="260" />

Eigenständiges Quellcode-Repository: [Lowfin-Companion](https://github.com/303inmyheart/Lowfin-Companion). [English README](README.md).

Optionales Jellyfin-Plugin für **Jellyfin 12.2 oder neuer** und Lowfin ab **0.10.0**.
Es verwendet Jellyfins normale Benutzeranmeldung. Zusätzliche API-Keys sind nicht nötig.

**Neu in 0.7.0:** Dashboard-Seitenleisteneintrag mit dem originalen Lowfin-L, neu gegliederte Einstellungen und optionale Discord Rich Presence mit Nutzerzuordnung, verschlüsselten Tokens, Discord-Cover-Pipeline und Live-TV-Programmdaten. Discord und Bild-Uploads sind standardmäßig ausgeschaltet. Einrichtung und Backup-Hinweise stehen weiter unten.

## Installation

1. Für manuelle Installation das Release-Paket mit `-manual.zip` verwenden; das Paket ohne diesen Zusatz ist für Jellyfins Katalog-Installer. Jellyfin beenden.
2. Den gesamten aktuellen Ordner `Lowfin.Companion_0.7.0.0` in Jellyfins bestehenden `plugins`-Ordner kopieren. Die DLL, `meta.json` und `lowfin-companion.png` müssen direkt in diesem Unterordner liegen. Beim Update den bisherigen Lowfin-Companion-Ordner aus `plugins` herausnehmen und als Sicherung aufbewahren, damit nur die neue Version geladen wird. Die separaten Daten unter `lowfin-companion` beibehalten.
3. Sicherstellen, dass der Jellyfin-Prozess die Dateien lesen und seinen Datenordner beschreiben kann.
4. Jellyfin neu starten. Unter Dashboard → Plugins erscheint **Lowfin Companion** mit dem Bild.
5. Lowfin neu öffnen. Auf Film- und Folgendetailseiten erscheint **Smart Completion**. Die allgemeine Regel für lange Videos liegt unter Einstellungen → Wiedergabe.

Bei Docker ist der Plugin-Ordner üblicherweise `/config/plugins`; entscheidend ist jedoch der tatsächlich konfigurierte Jellyfin-Datenordner. Die ZIP enthält denselben fertigen Plugin-Unterordner. Kein Repository-Manifest und keine Veröffentlichung erforderlich.

## Funktionen

- Regeln pro Jellyfin-Benutzer und Film/Folge: Jellyfin-Standard, eigener Prozentwert, verbleibende Zeit oder niemals automatisch als gesehen markieren.
- Ohne individuell gewählte Regel und ohne passende aktivierte Langvideo-Regel ist Companion für das Medium inaktiv: native Jellyfin-Wiedergabe, keine zusätzliche Fortsetzposition. Die ausdrückliche Auswahl „Jellyfin-Standard“ deaktiviert Companion; die separate Fortsetzoption kann bewusst wieder eingeschaltet werden.
- Regeln sind nicht an einen Fernseher gebunden. Derselbe Jellyfin-Benutzer bekommt sie auf anderen Geräten mit Lowfin 0.10.0 beim nächsten Wiedergabestart ebenfalls. Andere Benutzer erhalten sie nicht.
- Unabhängige Fortsetzposition, auch wenn Jellyfin das Medium bereits als gesehen markiert.
- Optionale benutzerspezifische Regel für lange Videos, beispielsweise ab vier Stunden.
- Normale Jellyfin-Playback-Ereignisse bleiben erhalten. Es werden keine globalen Completion-Einstellungen geändert.
- Wiedergabe-IDs, fortlaufende Meldungsnummern und manuelle Sperren verhindern, dass alte Meldungen neuere Positionen überschreiben.
- Bibliotheksbezogene Fortsetzlisten werden im Server abgegrenzt. Jellyfins Startseiten-Ausschlüsse werden in globalem Weiterschauen respektiert, ohne die Fortsetzposition zu löschen. In der ausdrücklich geöffneten Bibliothek und der Detailseite bleibt sie verfügbar.

Die Einstellungen werden in `<Jellyfin-Datenpfad>/lowfin-companion/<Benutzer-ID>/<Medien-ID>.json` gespeichert. Diesen Ordner zusammen mit den Jellyfin-Daten sichern. Das Entfernen des Plugins löscht diese Daten nicht.

## Grenzen der ersten Version

- Die individuellen Regeln gelten für Wiedergabe über den Companion-Gateway in Lowfin. Andere Clients behalten Jellyfins normales Verhalten; ein Start in einem anderen Client beendet die vorherige Lowfin-Zuständigkeit.
- Bereits verlorene historische Fortsetzpositionen können nicht rekonstruiert werden. Bereits manuell als gesehen markierte Titel werden nicht automatisch ungesehen gemacht.
- Tracearr und andere Tracker verwenden weiterhin ihre eigenen Abschlussregeln. Lowfins Schwellenwerte ändern diese nicht.
- Ein externer Fortschrittsreset, der exakt denselben nativen Wert erneut schreibt, ist über Jellyfins Ereignisschnittstelle nicht von einem reinen Metadaten-Update unterscheidbar. Der ausdrückliche Companion-Knopf „Fortschritt zurücksetzen“ ist eindeutig.
- Nach einem mehrdeutigen Fehler bei einer Startmeldung wird ein Start nicht doppelt ausgeführt. Bei einem Serverabsturz genau zwischen Companion-Speicherung und Jellyfin-Verarbeitung kann dadurch eine Startmeldung fehlen. Eine atomare Transaktion über beide Speicher existiert nicht.
- Mindestversion ist Jellyfin 12.2; neuere Versionen werden nicht allein aufgrund ihrer Versionsnummer gesperrt. Künftige API-Änderungen können weiterhin eine Anpassung erfordern.

## Prüfung

Automatisierte Plugin- und Client-Tests plus Integrationstest mit einem isolierten echten Jellyfin 12.2.0 und zwölfstündiger Videodatei: Anmeldung, Discovery, individuelle Completion, unabhängige Fortsetzliste, reguläre Session-Meldungen, doppelte Meldungen, konkurrierende manuelle Änderungen und unveränderte globale Schwellenwerte. Auch meta.json und Plugin-Bild werden im echten Testserver geladen.

Das Paket wurde nicht automatisch auf deinem produktiven Server installiert.

**Dieses Projekt wurde zu 100 % mit KI erstellt.**

## Inkrementeller Precache ab Lowfin 0.10.0

Das Plugin sammelt Metadatenänderungen in einem atomar gespeicherten Journal (bis zu 4096 Ereignisse). Lowfin wartet bei aktiver Lowfin-Oberfläche einschließlich Wiedergabe auf neue Änderungen und übernimmt kleine Pakete direkt in den bestehenden Cache. Keine zusätzliche Anmeldung; jeder Abruf nutzt die bestehenden Jellyfin-Rechte. Die Option „Bibliothek automatisch vorladen“ steuert auch diese Anbindung.

Ein erster vollständiger Abgleich ist für einen leeren oder noch nicht vollständig aufgebauten Cache weiterhin nötig. Ein bereits gespeicherter vollständiger Cache wird seit Lowfin 0.10.8 auch bei fehlendem Änderungsmarker übernommen; eine mögliche Verlaufslücke wird ausdrücklich angezeigt. Danach entfällt bei kompatiblem, verfügbarem Plugin der normale 24-Stunden-Vollabgleich. Ein ausgeschalteter TV holt Änderungen anhand seines gespeicherten Markers nach. Geänderte Bilder werden nur bei neuen Bildreferenzen vorgeladen; die Nachlade-Warteschlange überlebt einen App-Abbruch. Fehlgeschlagene Bilder werden mit sechs Stunden Abstand erneut versucht.

Nach Jellyfin-Neustart, abgelaufenem Journal, gelöschten Medien sowie Struktur- oder Rechteänderungen kann das Plugin einen Reset melden. Lowfin behält den Cache, übernimmt den neuen Marker und zeigt eine Verlaufslücke an; es startet keinen automatischen Vollabgleich. Verpasste Änderungen lassen sich mit der aktuellen API nicht zuverlässig rekonstruieren. Bei Bedarf kann der Nutzer manuell vollständig aktualisieren. Ohne erkanntes kompatibles Plugin bleibt der bisherige zeitgesteuerte Abgleich erhalten; eine vorübergehende Verbindungsstörung allein schaltet einen bereits erkannten Companion nicht auf Vollabgleich um. Live-TV-Bilder werden weiterhin ausschließlich bei Bedarf vom Server geladen.

## Serverseitige API-Anbieter ab Companion 0.3.0

Unter **Jellyfin Dashboard → Plugins → Lowfin Companion → Einstellungen** können Administratoren OMDb-, MDBList- und TMDB-Schlüssel sowie Tracearr-Adresse, Public-API-Token und Jellyfin-Server-ID konfigurieren. Erst Tracearr-Adresse/Token speichern, dann die Serverliste laden, den passenden Jellyfin-Server auswählen und erneut speichern. Schlüssel liegen in Jellyfins regulärer Plugin-Konfiguration; diese Konfiguration darf nur von Administratoren gelesen/geschrieben werden. Clients erhalten nur Bewertungen und ihre eigenen Statistiken, niemals Anbieter-Schlüssel.

Die Admin-Auswahl **Allow server providers for all Jellyfin users** gibt die Anbieter global frei. Wird sie ausgeschaltet, dürfen nur ausdrücklich ausgewählte Nutzer die serverseitigen Anbieter nutzen. Die Freigabe gilt gemeinsam für die Anbieter; Lowfin wählt pro Gerät die benötigten Anbieter und Einzelbewertungen. Nicht freigegebene Nutzer behalten ihre lokale API-Konfiguration. Keine automatische Übertragung vorhandener Client-Schlüssel. Die serverseitige Rechteprüfung gilt unabhängig davon, welche Optionen der Client anzeigt.

Lowfin 0.10.0 blendet bei verfügbarer Freigabe lokale Schlüssel-, TXT-Import- und Tracearr-Verbindungsfelder aus. Mit inkrementellem Cache entfällt auch das normale Stundenintervall in der Oberfläche. Device-spezifische Cachebudgets und die An/Aus-Schalter bleiben.

Der **Bewertungscache auf dem Client bleibt standardmäßig an**. Der neue Schalter in API-Einstellungen deaktiviert Lesen und Schreiben des lokalen Bewertungscaches, ohne gespeicherte Werte zu löschen. Nach dem Einschalten sind sie wieder verfügbar. Bewertungen werden weiterhin nur bei Bedarf abgefragt, nicht beim Bibliotheks-Precache. Tracearr liefert ausschließlich abgeschlossene eindeutige Wiedergaben des zugeordneten Jellyfin-Nutzers; Serienzählung bleibt der bisherige vollständige Seriendurchlauf.

## DVD- und 5.1-Tags (0.4)

Unter Dashboard → Plugins → Lowfin Companion gibt es drei Schalter: Cover-Tags aktivieren (standardmäßig aus), DVD anzeigen und 5.1 anzeigen. DVD steht für eine SD-Videospur bis 1024 × 576 Pixel, nicht für eine verifizierte physische DVD. 5.1 erscheint, wenn mindestens eine Audiospur genau sechs Kanäle enthält; es bezeichnet nicht zwingend die beim Start gewählte Spur. Stereo und 7.1 werden nicht als 5.1 gekennzeichnet.

Die neutralen, halbtransparenten Tags werden in Lowfin unten rechts über den sichtbaren Film-/Folgenrahmen gezeichnet. Sie skalieren mit dem UI-Rahmen und seiner Auflösung, nicht mit den Bilddateien. So bleiben sie auch bei von 4:3 auf 16:9 zugeschnittenen Folgenbildern vollständig sichtbar. Serien-/Staffelcover zeigen DVD, wenn mehr als die Hälfte der verfügbaren Folgen SD/DVD-Auflösung haben; Gleichstand und unbekannte Auflösungen begründen keine Mehrheit. Logos, Backdrops und Senderbilder bleiben unverändert. Quelldateien, Jellyfin-Bildschnittstellen und persistenter Covercache werden nicht verändert.

Lowfin ab 1.2.5 unterstützt zusätzlich die Mehrheitsregel und ungecachte Folgenbewertungen. Ab 1.2.3 übernimmt Lowfin die drei Schalter bei der vorhandenen Plugin-Erkennung. Die neue authentifizierte API liefert nur kompakte Tag-Metadaten für bis zu 64 sichtbare IDs und prüft deren Benutzerzugriff. Der Client lädt in kleinen Batches, speichert die Ergebnisse kontoabhängig und verwirft sie bei gemeldeten Metadatenänderungen. Kein zusätzliches Bibliotheksscannen, keine native Bildverarbeitung auf dem Server und kein Rebuild des Covercaches. Andere Clients zeigen diese Lowfin-Overlays nicht an.

## Folgenbewertungen (0.4.1)

Lowfin 1.2.5 lädt IMDb über OMDb und TMDB für die konkrete Folge. Die Bewertung der Serie wird nicht als Folgenbewertung verwendet. Fehlt die eigene externe Folgen-ID, werden Serien-ID, Staffelnummer und Folgennummer verwendet. Fehlende Bewertungen bleiben ausgeblendet. MDBList wird für Folgen nicht als Film-/Serienabfrage verwendet. Die Ergebnisse werden weder im Companion noch im Lowfin-Bewertungscache gespeichert; bereits laufende gleiche Abfragen werden zusammengefasst und die geöffnete Ansicht behält ihre Anzeige. Eine neu geöffnete Ansicht fragt erneut ab.

## Persistenter Tag-Index und HDR (0.5)

Der Companion hält DVD-/5.1-/HDR-Werte sowie benutzerspezifische DVD-Mehrheiten für Serien und Staffeln in `<Jellyfin-Datenpfad>/lowfin-companion/badge-index.json` vor. Start und Wiederabgleich erfolgen im Hintergrund; Anfragen aus Lowfin rechnen keine Medien-/Mehrheitswerte mehr live aus. Die normale Berechtigungsprüfung bleibt erhalten.

Jellyfin-Ereignisse aktualisieren gezielt betroffene Einträge und Eltern. Änderungen von Benutzerrechten erneuern die betreffenden Mehrheiten. Zusätzlich ist die Aufgabe **Lowfin · Update cover tag index** mit einem 24-Stunden-Intervall unter geplanten Aufgaben registriert und dort manuell ausführbar. Nach Serverausfall wird der gespeicherte Index sofort wiederverwendet und im Hintergrund abgeglichen. Der Index wird nur bei aktivierten Cover-Tags gepflegt. Änderungen werden spätestens alle 30 Sekunden und beim geordneten Beenden atomar gespeichert; nach einem Absturz schließt der Startabgleich die Lücke.

Das Plugin-Dashboard zeigt Bereitschaft, Anzahl, letzten Vollabgleich, letzte Änderung und nächsten Prüftermin. Während des ersten Aufbaus lädt Lowfin 1.2.6 fehlende Tag-Daten automatisch nach; unbekannte Werte werden nicht dauerhaft als null gespeichert. Coverdateien und Bewertungen sind von diesem Index getrennt. Folgenbewertungen bleiben ungecacht.

Der HDR-Schalter im Dashboard erlaubt HDR10, HDR10+, HLG und Dolby Vision als Inhaltskennzeichnung. Lowfin zeigt den HDR-Tag **nur bei aktivierter HDR-Wiedergabe auf dem jeweiligen Gerät** (standardmäßig aus). Er übernimmt nicht die Wiedergabeentscheidung und schaltet HDR nicht selbst ein. Der lokale Tag-Sichtbarkeitsschalter verbirgt weiterhin alle Tags. Serien-/Staffelmehrheiten betreffen weiterhin nur DVD.

### Index-Diagnose ab 0.6.0

Die geplante Aufgabe meldet Fortschritt für Medienprüfung, benutzerspezifische Serien-/Staffelauswertung und Speichern. Bei einem parallelen Hintergrundlauf wartet sie auf dessen Ergebnis, statt einen zweiten vollständigen Lauf zu starten. Im Jellyfin-Log nach `Lowfin cover index` suchen: Start, Abschluss, Fehler, Abbruch und alle 15 Sekunden Phase, Prozentwert und Zähler werden auf Information-Level protokolliert. Auch während einer langsamen Datenbankabfrage bleibt damit der zuletzt erreichte Stand erkennbar. Die Plugin-Einstellungen zeigen diese Werte beim Aktualisieren des Indexstatus ebenfalls. Ein erster Aufbau kann bei vielen Staffeln und Benutzern länger dauern; bestehende vollständige Indexdaten bleiben nutzbar.

## Lokal bauen · ohne GitHub Actions

Voraussetzungen: .NET SDK 10 und Python 3.10 oder neuer.

```bash
dotnet test tests/Lowfin.Companion.Tests/Lowfin.Companion.Tests.csproj -c Release
dotnet build src/Lowfin.Companion/Lowfin.Companion.csproj -c Release
python3 tools/package.py
```

Alternativ `bash tools/build.sh`; ein abweichender SDK-Pfad kann über `DOTNET` angegeben werden. Alle Ausgaben landen im ignorierten Ordner `artifacts/`. Im Repository gibt es absichtlich keinen automatischen GitHub-Workflow. Lokal gebaute Pakete werden direkt als GitHub-Releases hochgeladen.

## Automatische Plugin-Updates vorbereiten

Den folgenden Link unter **Jellyfin Dashboard → Plugins → Repositorys** eintragen:

```text
https://raw.githubusercontent.com/303inmyheart/Lowfin-Companion/main/manifest.json
```

Danach Lowfin Companion im Katalog installieren. Jellyfins Plugin-Aktualisierungsaufgabe kann neue kompatible Versionen darüber laden; nach Installation oder Update Jellyfin neu starten. Der normale GitHub-Projektlink ist kein Manifest-Link. Falls eine frühere manuelle Installation in ihrer `meta.json` noch `autoUpdate: false` enthält, bleiben automatische Updates dafür deaktiviert. Bei gestopptem Jellyfin dieses Feld auf `true` setzen (andere Werte beibehalten) oder das aktuelle manuelle Paket übernehmen. Neue Kataloginstallationen und das aktuelle manuelle Paket erlauben automatische Updates.

`tools/make_repository.py` bereitet Manifest, flaches Jellyfin-Installationspaket, Bild und Prüfsummen lokal vor. Es lädt nichts hoch:

```bash
python3 -m unittest discover -s tools -p 'test_*.py'
python3 tools/make_repository.py --base-url https://DEIN-HOST/lowfin-companion/ --output artifacts/feed
```

Die Host-Adresse im Beispiel ist ein Platzhalter für eigenes Hosting. Der veröffentlichte Feed verwendet öffentliche GitHub-Release-Dateien. Für spätere Updates denselben Ausgabeordner verwenden, damit ältere Versionen im Manifest erhalten bleiben. Das Feed-Paket enthält die DLL direkt an der ZIP-Wurzel, während das manuelle Installationspaket den oben beschriebenen Versionsordner enthält. Das aktuelle Manifest verweist auf die exakten Pakete des jeweiligen GitHub-Releases. Jellyfins Plugin-Aktualisierungsaufgabe übernimmt dann die Prüfung; zum Laden eines Updates ist ein Server-Neustart nötig.

## Dashboard und Discord-Präsenz ab 0.7.0

Der Companion erscheint in der Dashboard-Seitenleiste mit dem originalen Lowfin-L ohne Schriftzug. Ein eingebetteter Font ergänzt genau dieses Symbol über Jellyfins Branding-CSS; vorhandenes eigenes CSS bleibt erhalten. Das Symbol ist abschaltbar. Die Einstellungen sind in Übersicht, Cover-Tags/Index, Bewertungsanbieter, Tracearr und Discord gegliedert.

Discord ist standardmäßig deaktiviert. Der Administrator ordnet Jellyfin-Nutzer explizit Discord-Tokens zu und konfiguriert die Anwendungs-ID. Filme, Folgen, Musik, Bücher und optional Live-TV werden unterstützt. Live-TV zeigt Sender und aktuelle Sendung; gültige EPG-Zeiten liefern die Zeitachse. Ohne Sendungsdaten wird der Sendername wiederholt. Vorhandene Wiedergabemeldungen und Gesehen-Regeln bleiben unverändert.

Diese Betriebsart nutzt einen Discord-Nutzertoken am Gateway und kein offizielles Desktop-RPC. Discord untersagt automatisierte Benutzerkonten. Tokens liegen verschlüsselt in der Server-Plugin-Konfiguration; die Schlüssel werden getrennt im Companion-Datenordner gespeichert. Tests verbinden sich nicht mit echten Discord-Konten.

Discord wird direkt im Companion konfiguriert; eine Standalone-Importfunktion gibt es nicht. Bereits im Companion gespeicherte Zuordnungen bleiben beim Update erhalten. Ein separates Discord-Präsenz-Plugin vor Aktivierung des Companions deaktivieren oder entfernen. Bild-Uploads sind separat zustimmungspflichtig per Option: Erst nach Aktivierung werden quadratische PNGs beziehungsweise unveränderte Originalbilder beim Fallback zum eingestellten HTTPS-Bilddienst und deren öffentliche URLs an Discord gesendet. Ohne Uploads werden nur konfigurierte öffentliche Ersatzbilder verwendet.

Die obere Jellyfin-Versionssperre entfällt; Mindestversion bleibt 12.2. Tests laufen gegen die APIs von 12.2 und 12.3, der isolierte vollständige Server-Ladetest gegen 12.2. Dies ist keine Garantie für beliebige zukünftige API-Änderungen. Das Paketwerkzeug aktualisiert das Repository-Manifest mit URL und MD5 des exakten flachen Installations-ZIPs; ältere Manifest-Versionen bleiben erhalten.

Der Gateway-Verbindungsaufbau entspricht dem bereitgestellten Originalcode: Endpunkt-Erkennung ohne erzwungene v9-Version, passende Client-Header, bis zu 32 MiB große fragmentierte READY-Nachrichten und Senden der vorgemerkten Präsenz nach READY. Discord-Close-Codes und Größenüberschreitungen werden ohne Token oder rohe Gateway-Nachrichten protokolliert.

### Verschlüsselte Discord-Tokens und Backups

Bestehende Klartext-Tokens werden beim Start automatisch migriert. Neue Tokens werden vor dem Speichern mit .NET Data Protection verschlüsselt und an den jeweiligen Jellyfin-Nutzer gebunden. In der Plugin-XML und beim Admin-Konfigurationsabruf steht danach nur der verschlüsselte Wert. Ein leeres Token-Feld behält den gespeicherten Token; zum Ändern einen neuen eingeben, zum Löschen die Zuordnung entfernen. Bei einem Wechsel des zugeordneten Jellyfin-Nutzers ist die erneute Eingabe nötig.

Der dauerhafte Schlüsselring liegt unter `<Jellyfin-Datenordner>/lowfin-companion/secret-keys/` und muss vertraulich mitgesichert werden. Unter Unix ist der Ordner auf 0700 beschränkt; erzeugte Schlüsseldateien erhalten 0600. Container benötigen dafür ein dauerhaftes Datenvolume. Fehlt der Schlüssel, muss er aus dem Backup wiederhergestellt oder der Token erneut eingegeben werden. Nicht entschlüsselbare Werte bleiben erhalten und werden nicht als Discord-Token versendet.

Das schützt vor dem bloßen Auslesen einer kopierten Konfigurationsdatei, nicht vor vollständigem Serverzugriff: Der lokale Schlüsselring ist unter Linux ohne zusätzliches Zertifikat/Masterpasswort nicht selbst verschlüsselt. Wer Konfiguration und Schlüsselring besitzt, kann die Tokens entschlüsseln. Während einer Verbindung muss der Token im Arbeitsspeicher verfügbar sein. Alte Backups mit Klartext-Tokens werden nicht verändert. Niemals echte Konfigurationen oder Schlüssel in Git ablegen.

### Discord-Cover-Verarbeitung

Für Discord-Cover kombiniert der Companion die Bildaufbereitung mit einem dauerhaften Upload-Cache und der Registrierung externer Bilder bei Discord. Folgen nutzen zuerst das Staffelcover, danach das Seriencover und schließlich das Folgenbild. Lokale Dateien und Bild-URLs werden unterstützt. Das Poster wird proportional auf ein 512 × 512 PNG mit unscharfem Hintergrund gesetzt; falls das Rendern fehlschlägt, wird das Originalbild verwendet. Der Upload deklariert PNG/JPEG passend zu den tatsächlichen Bytes. Erfolgreiche Uploads werden 150 Minuten persistent wiederverwendet, fehlgeschlagene Uploads lösen eine 15-Minuten-Pause aus. Erst danach wird die öffentliche URL bei Discord registriert.

Der Upload-Schalter muss aktiv sein. Fehlende Bilder werden bei laufenden Fortschrittsmeldungen frühestens nach zwei Minuten erneut versucht; unveränderte Bilder erzeugen keine zusätzliche Präsenzmeldung. Bildquelle, Upload-Fehler und erfolgreiche Discord-Registrierung sind nun getrennt im Log erkennbar. Details zur Herkunft stehen in THIRD_PARTY.md.
